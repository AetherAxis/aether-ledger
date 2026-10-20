using System.Text;

namespace Aether.Ledger;

/// <summary>重放结果：全部余额真相（不需要任何活进程即可重建）。</summary>
public sealed record LedgerReplayResult
{
    public required bool Ok { get; init; }
    public required string? Error { get; init; }               // 失败原因（哪一条规则、哪一块）
    public required LedgerGenesis? Genesis { get; init; }
    public required ulong BlocksProcessed { get; init; }
    public required ulong TransactionsProcessed { get; init; }
    public required IReadOnlyDictionary<string, LedgerAccountState> Accounts { get; init; }

    /// <summary>币守恒（Σ 币余额 ≡ 创世币总量——第一不变量，逐块保持）。</summary>
    public bool SupplyConserved
        => Accounts.Values.Aggregate(0UL, (acc, a) => acc + a.BalanceAtoms) == (Genesis?.TotalSupplyAtoms ?? 0);

    /// <summary>计价币守恒（批 6：Σ AQ 余额 ≡ 创世 AQ 总量——同一不变量的第二资产面）。</summary>
    public bool QuoteSupplyConserved
        => Accounts.Values.Aggregate(0UL, (acc, a) => acc + a.QuoteBalanceAtoms) == (Genesis?.QuoteTotalSupplyAtoms ?? 0);
}

/// <summary>
/// 验证器——**五条协议规则的机器强制**（验证 = 全量重放，本量级秒级免费）。这是「不丢」性质的实现核心：
/// 链文件 + 本验证器 = 不需要原节点、不需要原交易所、不需要任何活进程的余额真相。
/// <list type="number">
/// <item><b>创世</b>：分配合计 ≡ 总量常量（21M 整）。</item>
/// <item><b>链接</b>：每块 prevHash = 前块 header 哈希、height 连续、出块人签名验签通过（出块人 = 创世指定）。</item>
/// <item><b>交易验签</b>：签名对 from 公钥有效，且 SHA256(pubkey)[0..20] == from（地址派生校验）。</item>
/// <item><b>nonce 顺序</b>：from.nonce + 1 精确匹配（防重放——同链）+ ChainId 匹配（防跨链重放）。</item>
/// <item><b>守恒</b>：from.balance ≥ amount；只有转账类型存在（无铸造）⇒ 结构上不可能增发。</item>
/// </list>
/// </summary>
public static class LedgerValidator
{
    /// <summary>重放整条链（创世 + 全部区块）→ 终态。任一规则失败即终止并返回原因。</summary>
    public static LedgerReplayResult Replay(LedgerGenesis genesis, IReadOnlyList<LedgerBlock> blocks)
    {
        // —— 规则 1：创世（双资产——两套总量常量各自校验） ——
        if (genesis.TotalSupplyAtoms != LedgerSupply.TotalSupplyAtoms)
            return Fail(genesis, 0, 0, $"创世总量 {genesis.TotalSupplyAtoms} ≠ 协议常量 {LedgerSupply.TotalSupplyAtoms}（增发/改供给 = 另一条链）");
        if (genesis.QuoteTotalSupplyAtoms != LedgerSupply.QuoteTotalSupplyAtoms)
            return Fail(genesis, 0, 0, $"创世计价币总量 {genesis.QuoteTotalSupplyAtoms} ≠ 协议常量 {LedgerSupply.QuoteTotalSupplyAtoms}");

        var state = new Dictionary<string, LedgerAccountState>(StringComparer.Ordinal)
        {
            [genesis.TreasuryAddress] = new LedgerAccountState(genesis.TotalSupplyAtoms, genesis.QuoteTotalSupplyAtoms, 0),
        };
        var producerPub = Convert.FromBase64String(genesis.ProducerPublicKey);
        var previousHash = genesis.Hash;
        ulong expectedHeight = 1;

        foreach (var block in blocks)
        {
            // —— 规则 2：链接 ——
            if (block.Height != expectedHeight)
                return Fail(genesis, block.Height, 0, $"高度断链：期望 {expectedHeight}，实际 {block.Height}");
            if (block.ChainId != genesis.ChainId)
                return Fail(genesis, block.Height, 0, $"区块 chainId {block.ChainId} ≠ 创世 {genesis.ChainId}");
            if (block.PreviousHash != previousHash)
                return Fail(genesis, block.Height, 0, $"prevHash 断链：期望 {previousHash[..16]}…，实际 {block.PreviousHash[..16]}…（历史被改写或分叉）");
            if (!LedgerSignature.Verify(producerPub, block.HeaderPayload, Convert.FromBase64String(block.ProducerSignature)))
                return Fail(genesis, block.Height, 0, "出块人签名验签失败（非创世指定出块人）");
            // txRoot 复核（打包时写错/被篡改在此暴露）
            var txHashes = block.Transactions.Select(t => Convert.FromHexString(t.Hash)).SelectMany(b => b).ToArray();
            if (LedgerCodec.Sha256Hex(txHashes) != block.TransactionRoot)
                return Fail(genesis, block.Height, 0, "txRoot 与交易哈希拼接不匹配（块内交易被增删/篡改）");

            // —— 规则 3/4/5：逐笔交易 ——
            ulong txCount = 0;
            foreach (var tx in block.Transactions)
            {
                if (ValidateTransaction(tx, genesis, state) is { } error)
                    return Fail(genesis, block.Height, 0, $"高度 {block.Height} 交易 #{txCount}（from={tx.From[..12]}… nonce={tx.Nonce}）：{error}");
                state[tx.From] = state[tx.From].Debit(tx.Asset, tx.AmountAtoms);
                state[tx.To] = state.TryGetValue(tx.To, out var toState)
                    ? toState.Credit(tx.Asset, tx.AmountAtoms)
                    : new LedgerAccountState(0, 0, 0).Credit(tx.Asset, tx.AmountAtoms);
                txCount++;
            }

            previousHash = block.Hash;
            expectedHeight++;
        }

        // —— 规则 5 的全局复核：Σ 余额 ≡ 各资产总量（双面） ——
        var sum = state.Values.Aggregate(0UL, (acc, a) => acc + a.BalanceAtoms);
        if (sum != genesis.TotalSupplyAtoms)
            return Fail(genesis, expectedHeight - 1, 0, $"守恒破坏：Σ 币余额 {sum} ≠ {genesis.TotalSupplyAtoms}");
        var quoteSum = state.Values.Aggregate(0UL, (acc, a) => acc + a.QuoteBalanceAtoms);
        if (quoteSum != genesis.QuoteTotalSupplyAtoms)
            return Fail(genesis, expectedHeight - 1, 0, $"守恒破坏：Σ 计价币余额 {quoteSum} ≠ {genesis.QuoteTotalSupplyAtoms}");

        return new LedgerReplayResult
        {
            Ok = true,
            Error = null,
            Genesis = genesis,
            BlocksProcessed = (ulong)(blocks.Count == 0 ? 0 : blocks.Max(b => b.Height)),
            TransactionsProcessed = (ulong)blocks.Sum(b => b.Transactions.Count),
            Accounts = state,
        };
    }

    /// <summary>单笔交易按当前状态验证（构建器与重放共用同一套规则——同一逻辑保证「入块即合法」与「重放必通过」）。
    /// 返回 null = 合法；返回错误串 = 拒因。</summary>
    public static string? ValidateTransaction(LedgerTransaction tx, LedgerGenesis genesis,
        IReadOnlyDictionary<string, LedgerAccountState> state)
    {
        // 规则 4（跨链/跨资产）：chainId + asset 必须合法
        if (tx.ChainId != genesis.ChainId)
            return $"chainId {tx.ChainId} ≠ 本链 {genesis.ChainId}（跨链重放）";
        if (tx.Asset is not (LedgerSupply.Assets.Coin or LedgerSupply.Assets.Quote))
            return $"未知资产 id {tx.Asset}（合法：{LedgerSupply.Assets.Coin}=币 / {LedgerSupply.Assets.Quote}=计价币）";

        // 地址形状
        if (tx.From.Length != LedgerAddress.HexLength || tx.To.Length != LedgerAddress.HexLength)
            return "地址形状非法";

        // 规则 3：验签 + 地址派生（公钥哈希前 20B 必须等于 from——张冠李戴防护）
        byte[] publicKey;
        byte[] signature;
        try
        {
            publicKey = Convert.FromBase64String(tx.PublicKey);
            signature = Convert.FromBase64String(tx.Signature);
        }
        catch (FormatException)
        {
            return "公钥/签名 base64 非法";
        }
        if (!LedgerSignature.Verify(publicKey, tx.SignedPayload, signature))
            return "签名验签失败";
        if (LedgerAddress.FromPublicKey(publicKey).Hex != tx.From)
            return "地址派生不匹配（SHA256(pubkey)[0..20] ≠ from）";

        // 规则 4（同链重放）：nonce 精确匹配
        if (!state.TryGetValue(tx.From, out var fromState))
            return $"未知账户（无余额且 nonce 不从 1 起）——from={tx.From[..12]}… nonce={tx.Nonce}";
        if (tx.Nonce != fromState.Nonce + 1)
            return $"nonce {tx.Nonce} ≠ 期望 {fromState.Nonce + 1}（重放或乱序）";

        // 规则 5：余额充足（按资产分账）+ 转账守恒（amount > 0；溢出结构上不可能——余额 ≤ 各资产总量）
        if (tx.AmountAtoms == 0)
            return "转账量必须 > 0";
        if (fromState.BalanceOf(tx.Asset) < tx.AmountAtoms)
            return $"余额不足（资产 {tx.Asset}）：{fromState.BalanceOf(tx.Asset)} < {tx.AmountAtoms}";

        return null;   // 合法
    }

    private static LedgerReplayResult Fail(LedgerGenesis genesis, ulong height, ulong txs, string error)
        => new()
        {
            Ok = false,
            Error = $"[高度 {height}] {error}",
            Genesis = genesis,
            BlocksProcessed = height,
            TransactionsProcessed = txs,
            Accounts = new Dictionary<string, LedgerAccountState>(),
        };
}

/// <summary>
/// 验证链文件的入口逻辑（控制台/测试共用）：读文件 → 重放 → 摘要输出。
/// 独立于节点/交易所存在——「链文件 + 此验证器 = 全部真相」。
/// </summary>
public static class LedgerVerifier
{
    public static LedgerReplayResult VerifyFile(string chainFilePath)
    {
        var (genesis, blocks) = LedgerChainFile.Read(chainFilePath);
        return LedgerValidator.Replay(genesis, blocks);
    }

    /// <summary>人类可读摘要（验证器控制台输出/测试断言信息用）。</summary>
    public static string Summarize(LedgerReplayResult result)
    {
        if (!result.Ok)
            return $"链验证 FAIL：{result.Error}";
        var sb = new StringBuilder();
        sb.AppendLine($"链验证 OK：创世哈希 {result.Genesis!.Hash[..16]}… | 高度 {result.BlocksProcessed} | 交易 {result.TransactionsProcessed} 笔 | 账户 {result.Accounts.Count} 个");
        sb.AppendLine($"币供给守恒：{(result.SupplyConserved ? "✓" : "✗")} Σ 币余额 = {LedgerSupply.Format(result.Accounts.Values.Aggregate(0UL, (acc, a) => acc + a.BalanceAtoms))}");
        sb.AppendLine($"计价币守恒：{(result.QuoteSupplyConserved ? "✓" : "✗")} Σ AQ = {LedgerSupply.Format(result.Accounts.Values.Aggregate(0UL, (acc, a) => acc + a.QuoteBalanceAtoms))}");
        foreach (var (address, account) in result.Accounts.OrderByDescending(kv => kv.Value.BalanceAtoms))
            sb.AppendLine($"  {address}  币 {LedgerSupply.Format(account.BalanceAtoms),20}  AQ {LedgerSupply.Format(account.QuoteBalanceAtoms),20}  nonce={account.Nonce}");
        return sb.ToString().TrimEnd();
    }
}
