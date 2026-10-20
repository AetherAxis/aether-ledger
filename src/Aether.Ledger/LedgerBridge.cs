using System.Text.Json;

namespace Aether.Ledger;

/// <summary>
/// 提币前置钩子（venue 接线——交易所宿主的 <c>LedgerSpotLink</c> 实现）：桥签名出金<b>之前</b>先扣减
/// venue 侧资产。返回 <c>(false, 原因)</c> = venue 侧不足/币通道已退役 ⇒ 拒提（IOU 不动、不签名）。
/// 扣减成功但链上提交失败的崩溃窗口由「链是唯一真相」重扫自愈（AQ IOU 从链上重扫推导）。
/// <para><b>2026-10-10 币通道退役</b>：桥只处理计价币（AQ）——coin 的充提通道整体拆除
/// （交易所单资产化，只收付 AQ；coin 留在链上钱包），asset=Coin 一律拒。</para>
/// </summary>
public interface ILedgerWithdrawHook
{
    /// <summary>asset：<see cref="LedgerSupply.Assets.Quote"/>（扣场内现金）；Coin 已退役（拒）。</summary>
    Task<(bool Ok, string? Error)> TryBurnForWithdrawalAsync(string accountKey, ulong amountAtoms, uint asset, CancellationToken ct = default);
}

/// <summary>
/// 交易所桥（批 3；批 6 双资产；**2026-10-10 币通道退役**——只收付计价币 AQ）：链侧**托管服务**——
/// 交易所进程持有的 custody 密钥对 + AQ 充值侦听 + AQ 提现签名。
/// <para>
/// **IOU 状态完全从链可推导**（无独立持久化——重启 = 全量重扫链重建）：
/// <list type="bullet">
/// <item>充 AQ：链上 tx <c>To == custody 地址</c> + <c>memo = 内部账户键</c> + <c>asset=Quote</c> ⇒ 该账户 IOU +atoms</item>
/// <item>提 AQ：链上 tx <c>From == custody 地址</c> + <c>memo = 内部账户键</c> ⇒ 该账户 IOU −atoms</item>
/// </list>
/// 提现时立即更新内存态（链上确认由下一轮 poll 自洽——临时超前无害，重启后链是唯一真相）。
/// </para>
/// <para>
/// **coin 的充值跳过**（币通道退役——诚实边界：链上余额仍在 custody，但不再归账进交易所；
/// 想变现链上 coin 走链上转账/未来的兑换通道，不经过交易所）。
/// </para>
/// <para>
/// 归账靠 <b>memo</b>：充值签名 tx 带 memo = 内部账户键。无 memo 的 AQ 充值跳过（无法归账——诚实边界，
/// 链上余额仍在）。**签名在桥**（从 custody 出金须 custody 私钥——桥持有它）；充值的签名在调用方钱包侧。
/// <b>诚实边界</b>：桥的密钥即交易所的托管责任——泄漏 = 托管 AQ 可被转走。
/// </para>
/// </summary>
public sealed class LedgerBridge : IDisposable
{
    private const string CoinRetired = "币通道已退役（2026-10-10 拆除——交易所单资产化只收付计价币 AQ；coin 留在链上钱包）";

    private readonly LedgerClient _client;
    private readonly LedgerWallet _custody;
    private readonly Dictionary<string, ulong> _quoteBalances = new(StringComparer.Ordinal);     // 计价币 IOU（asset=Quote）
    private ulong _custodyNonce;   // custody 的下一个出金 nonce（单流；重启从链重扫恢复——coin 历史出金也推进它）
    private ulong _scannedHeight;  // 已处理到的最大块高
    private Timer? _pollTimer;
    private readonly object _gate = new();
    private readonly Action<string> _log;

    /// <summary>托管地址（custody 钱包的链上地址）。</summary>
    public LedgerAddress CustodyAddress => _custody.Address;

    /// <summary>桥初始化是否完成（StartAsync 后为 true——充值侦听与提现须在初始化后调用）。</summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// 提现前置钩子（venue 接线）：设了它，提现闸门 = venue 侧可用现金（真实交易所语义），扣减成功才
    /// 借记 + 签名；未设 = 纯 IOU 闸门（批 3 语义）。StartAsync 前设置。
    /// </summary>
    public ILedgerWithdrawHook? WithdrawHook { get; set; }

    public LedgerBridge(LedgerClient client, LedgerWallet custodyWallet, Action<string>? log = null)
    {
        _client = client;
        _custody = custodyWallet;
        _log = log ?? (msg => Console.WriteLine($"[LEDGER-BRIDGE] {msg}"));
    }

    /// <summary>
    /// 启动：全量重扫链（重建 AQ IOU 状态 + custody nonce）+ 启动轮询。
    /// 链是唯一真相——桥的内存态重启后从这里恢复。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var info = await _client.GetInfoAsync(ct);
        _log($"桥启动：链创世 {info.GenesisHash[..12]}… | 当前高度 {info.Height} | custody {CustodyAddress.Hex[..12]}…");

        // 全量重扫：逐块处理，重建 AQ IOU + custody nonce（coin 历史出金也推进 nonce——单流）
        for (ulong h = 1; h <= info.Height; h++)
        {
            var block = await _client.GetBlockAsync(h, ct);
            ProcessBlock(block);
        }
        _scannedHeight = info.Height;
        _log($"链重扫完成：高度 {info.Height} | 计价币 IOU {_quoteBalances.Count} 个 | custody 出金 nonce={_custodyNonce}");

        // 轮询（500ms——链出块间隔最小也是秒级，500ms 足够灵敏）
        _pollTimer = new Timer(async _ => await PollAsync(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        IsReady = true;
    }

    /// <summary>查账户计价币 IOU 余额（原子单位；未知账户 = 0）——场内现金的铸造目标。</summary>
    public Task<ulong> GetQuoteBalanceAsync(string accountKey, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_quoteBalances.TryGetValue(accountKey, out var balance) ? balance : 0UL);
    }

    /// <summary>列出全部计价币 IOU 余额（快照）——对账用。</summary>
    public IReadOnlyDictionary<string, ulong> GetAllQuoteBalances()
    {
        lock (_gate) return new Dictionary<string, ulong>(_quoteBalances, StringComparer.Ordinal);
    }

    /// <summary>
    /// 提现（AQ）：检查余额 → 借记 → custody 签名链上转账 → 提交节点。返回链上交易哈希。
    /// memo = accountKey（链上可推导——重启重扫自洽）。asset=Coin 拒（币通道已退役）。
    /// <para>闸门二态（<see cref="WithdrawHook"/>）：设钩子 = venue 侧闸门（可用现金——「浮盈/保证金锁定
    /// 不可提」的真实语义；借记 clamp 到 0 不为负，与重扫 debit 同语义）；未设 = 纯 IOU 闸门（批 3 语义）。</para>
    /// </summary>
    public async Task<string> WithdrawAsync(string accountKey, LedgerAddress to, ulong amountAtoms,
        uint asset = LedgerSupply.Assets.Quote, CancellationToken ct = default)
    {
        if (!IsReady) throw new InvalidOperationException("桥未初始化（须先 StartAsync）。");
        if (amountAtoms == 0) throw new ArgumentException("提现量必须 > 0。");
        if (asset != LedgerSupply.Assets.Quote) throw new LedgerClientException(CoinRetired);

        // venue 钩子先扣减（签名之前——扣不掉就不签：venue 不欠这个账户那么多可提现金）
        if (WithdrawHook is { } hook)
        {
            var (ok, error) = await hook.TryBurnForWithdrawalAsync(accountKey, amountAtoms, asset, ct);
            if (!ok)
                throw new LedgerClientException(
                    $"venue 拒绝提现：{error ?? "可用现金不足"}（账户 {accountKey}，提现 {LedgerSupply.Format(amountAtoms)}）");
        }

        ulong currentBalance;
        lock (_gate)
        {
            _quoteBalances.TryGetValue(accountKey, out currentBalance);
            if (WithdrawHook is null && currentBalance < amountAtoms)
                throw new LedgerClientException($"IOU 余额不足：账户 {accountKey} 余额 {currentBalance} < 提现 {amountAtoms}");
            // 借记（clamp 到 0——钩子模式下已为 0 的「转入后提现」不 underflow；
            // 链上确认由下一轮 poll 自洽——临时超前无害，重扫 debit 同款 clamp）
            _quoteBalances[accountKey] = currentBalance >= amountAtoms ? currentBalance - amountAtoms : 0UL;
            _custodyNonce++;
        }

        // custody 签名出金（memo = accountKey——链上可推导归账）
        var tx = LedgerTransaction.Sign(
            await GetChainIdAsync(ct), _custody, to, amountAtoms, _custodyNonce, accountKey, asset);
        await _client.SubmitTxAsync(tx, ct);
        _log($"提现（计价币）：账户 {accountKey} → {to.Hex[..12]}… {LedgerSupply.Format(amountAtoms)}（tx {tx.Hash[..12]}… nonce={_custodyNonce}）");
        return tx.Hash;
    }

    /// <summary>充 AQ 的便捷入口（调用方钱包签名 → 提交节点；桥只负责侦听——本方法不归账，归账由 poll 完成）。
    /// 调用方（agent/测试/钱包页）用 wallet.Sign 转 AQ 到 custody 地址 + memo = 自己的账户键。
    /// asset=Coin 拒（币通道已退役）。</summary>
    public static async Task DepositAsync(LedgerClient client, LedgerWallet agentWallet, LedgerAddress custodyAddress,
        ulong amountAtoms, string accountKey, uint asset = LedgerSupply.Assets.Quote, CancellationToken ct = default)
    {
        if (asset != LedgerSupply.Assets.Quote)
            throw new LedgerClientException("币通道已退役（2026-10-10 拆除——交易所单资产化只收付计价币 AQ；coin 留在链上钱包）");
        var info = await client.GetInfoAsync(ct);
        // agent 侧 nonce 须从节点查（agent 可能不在本进程）
        var account = await client.GetBalanceAsync(agentWallet.Address.Hex, ct);
        var nonce = account.Nonce + 1;
        var tx = LedgerTransaction.Sign(info.ChainId, agentWallet, custodyAddress, amountAtoms, nonce, accountKey, asset);
        await client.SubmitTxAsync(tx, ct);
    }

    // —— 内部：轮询与块处理 ——

    private async Task PollAsync()
    {
        try
        {
            var info = await _client.GetInfoAsync();
            if (info.Height > _scannedHeight)
            {
                for (ulong h = _scannedHeight + 1; h <= info.Height; h++)
                {
                    var block = await _client.GetBlockAsync(h);
                    ProcessBlock(block);
                }
                _scannedHeight = info.Height;
            }
        }
        catch (Exception ex)
        {
            _log($"轮询异常（链节点不可达？下次重试）：{ex.Message}");
        }
    }

    /// <summary>处理一个块：AQ 充值（To==custody + memo + asset=Quote ⇒ credit AQ IOU）与提现确认
    ///（From==custody + memo ⇒ 幂等跳过——提现时已即时借记，重扫路径才真正 debit；nonce 推进不分资产——
    /// coin 的历史出金也推进 custody nonce（单流））。</summary>
    private void ProcessBlock(LedgerBlock block)
    {
        foreach (var tx in block.Transactions)
        {
            if (tx.To == CustodyAddress.Hex)
            {
                if (tx.Asset != LedgerSupply.Assets.Quote)
                {
                    // coin 充值：币通道已退役——不归账（诚实边界：链上余额仍在 custody，但不再进交易所）
                    if (!string.IsNullOrWhiteSpace(tx.Memo) || tx.AmountAtoms > 0)
                        _log($"⚠ coin 充值跳过（币通道已退役）：{LedgerSupply.Format(tx.AmountAtoms)} from={tx.From[..12]}… ——币留链上，不再归账");
                    continue;
                }
                // 充 AQ：memo = 归账键
                var accountKey = tx.Memo;
                if (string.IsNullOrWhiteSpace(accountKey))
                {
                    _log($"⚠ 无 memo 充值（无法归账——链上余额在，IOU 不记）：from={tx.From[..12]}… {LedgerSupply.Format(tx.AmountAtoms)}");
                    continue;
                }
                lock (_gate)
                {
                    _quoteBalances.TryGetValue(accountKey, out var balance);
                    _quoteBalances[accountKey] = balance + tx.AmountAtoms;
                }
                _log($"充值（计价币）：账户 {accountKey} +{LedgerSupply.Format(tx.AmountAtoms)}（from={tx.From[..12]}…）");
            }
            else if (tx.From == CustodyAddress.Hex)
            {
                // 提现（我们签的出金）：正常运行路径已即时借记——重扫路径（IsReady=false）才真正 debit
                if (tx.Asset == LedgerSupply.Assets.Quote && tx.Memo is { Length: > 0 } memo)
                {
                    lock (_gate)
                    {
                        if (!IsReady)
                        {
                            _quoteBalances.TryGetValue(memo, out var balance);
                            _quoteBalances[memo] = balance >= tx.AmountAtoms ? balance - tx.AmountAtoms : 0;
                        }
                    }
                }
                // custody 出金 nonce 推进（单流——coin 历史出金也推进，重扫后 nonce 才能续上不撞号）
                lock (_gate) _custodyNonce = Math.Max(_custodyNonce, tx.Nonce);
            }
        }
    }

    private async Task<uint> GetChainIdAsync(CancellationToken ct = default)
    {
        var info = await _client.GetInfoAsync(ct);
        return info.ChainId;
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
    }
}
