using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Aether.Ledger;

/// <summary>节点 env（`AETHER_LEDGER_*`）——与 FrontHostOptions/SimHostOptions 同款「env → record」惯例。</summary>
public sealed record LedgerNodeOptions(string Host, int Port, string ChainFilePath, uint ChainId, ulong BlockIntervalSec)
{
    public const string AddrEnvVar = "AETHER_LEDGER_ADDR";
    public const string DbEnvVar = "AETHER_LEDGER_CHAIN_FILE";
    public const string ChainIdEnvVar = "AETHER_LEDGER_CHAIN_ID";
    public const string IntervalEnvVar = "AETHER_LEDGER_BLOCK_INTERVAL_SEC";

    /// <summary>默认：127.0.0.1:17805（对前置/交易所桥——同机部署即默认）；链文件 <c>~/.aether/ledger.chain</c>；
    /// chainId 77；60s 出块间隔。坏值严格抛（启动即失败，好过静默错链）。</summary>
    public static LedgerNodeOptions FromEnvironment()
    {
        var (host, port) = ParseListen(Environment.GetEnvironmentVariable(AddrEnvVar));
        var chainFile = Environment.GetEnvironmentVariable(DbEnvVar);
        if (string.IsNullOrWhiteSpace(chainFile))
            chainFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "ledger.chain");
        var chainId = uint.TryParse(Environment.GetEnvironmentVariable(ChainIdEnvVar), out var cid) ? cid : 77;
        var interval = ulong.TryParse(Environment.GetEnvironmentVariable(IntervalEnvVar), out var iv) && iv >= 1 ? iv : 60;
        return new LedgerNodeOptions(host, port, chainFile, chainId, interval);
    }

    private static (string, int) ParseListen(string? addr)
    {
        if (string.IsNullOrWhiteSpace(addr)) return ("127.0.0.1", 17805);
        var colon = addr.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(addr[(colon + 1)..], out var port) || port is < 1 or > 65535)
            throw new FormatException($"{AddrEnvVar} 格式非法：\"{addr}\"（应为 host:port，如 127.0.0.1:17805）");
        return (addr[..colon], port);
    }
}

/// <summary>
/// 链节点——引擎之上的常驻服务（批 2）：TCP JSON-RPC（每连接一读循环，请求-应答帧；**无推送**——链上无事件流，
/// 查询型 API）+ 内存池（验签过、待打包）+ 出块循环（有交易即出 / <see cref="LedgerNodeOptions.BlockIntervalSec"/>
/// 心跳空块——锚定新鲜度）+ 链文件持久化（**每块 append 一行**——重启 = 全量重放恢复，链文件永远是最真相）。
/// <para>创世策略：链文件不存在 ⇒ 用 env chainId + 随机 treasury/producer 种子**生成新链**（种子打印到日志——
/// 丢失即丢失全部供给，保管等级=链本身）；存在 ⇒ 读取并重放（创世哈希 = 链身份，跨重启不变）。</para>
/// <para>单出块人（诚实边界：可审查可检测/不可伪造/不可增发）；无自动重连重启语义（客户端重连重查即可）。</para>
/// </summary>
public sealed class LedgerNode : IDisposable
{
    private readonly LedgerNodeOptions _options;
    private readonly string _logPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, LedgerAccountState> _state = new(StringComparer.Ordinal);
    private readonly List<LedgerBlock> _blocks = new();
    private readonly List<LedgerTransaction> _mempool = new();
    private LedgerGenesis? _genesis;
    private LedgerWallet? _producer;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _blockLoop;
    private readonly CancellationTokenSource _cts = new();

    public LedgerNode(LedgerNodeOptions options, string? logPath = null)
    {
        _options = options;
        _logPath = logPath ?? options.ChainFilePath + ".log";
    }

    /// <summary>创世哈希（链身份——跨重启不变）。</summary>
    public string GenesisHash => _genesis!.Hash;

    /// <summary>当前高度（创世后 0；每块 +1）。</summary>
    public ulong Height => (ulong)_blocks.Count;

    /// <summary>实际监听端点（端口 0 = 内核临时分配后取回——测试回环用）。</summary>
    public IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    /// <summary>初始化：加载或生成链 + 恢复状态 + 启动监听与出块循环。</summary>
    public void Start()
    {
        if (File.Exists(_options.ChainFilePath))
        {
            var (loadedGenesis, loadedBlocks) = LedgerChainFile.Read(_options.ChainFilePath);
            _genesis = loadedGenesis;
            _blocks.Clear();
            _blocks.AddRange(loadedBlocks);
            var result = LedgerValidator.Replay(_genesis!, _blocks);
            if (!result.Ok)
                throw new InvalidDataException($"链文件验证失败（拒绝启动在坏链上）：{result.Error}");
            foreach (var (address, account) in result.Accounts)
                _state[address] = account;
            Log($"链已加载：创世 {_genesis.Hash[..16]}… | 高度 {Height} | 交易 {result.TransactionsProcessed} 笔 | 账户 {_state.Count} 个（全量重放恢复）");
        }
        else
        {
            // 新链生成：treasury/producer 种子随机 + 打印（保管等级 = 链本身；丢失 treasury 种子 = 丢失全部供给）
            _producer = LedgerWallet.Create();
            var treasury = LedgerWallet.Create();
            _genesis = LedgerGenesis.Create(_options.ChainId, treasury.Address, _producer);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.ChainFilePath))!);
            LedgerChainFile.Write(_options.ChainFilePath, _genesis, Array.Empty<LedgerBlock>());
            _state[treasury.Address.Hex] = new LedgerAccountState(_genesis.TotalSupplyAtoms, _genesis.QuoteTotalSupplyAtoms, 0);
            Log($"新链已生成：创世 {_genesis.Hash[..16]}… | chainId={_options.ChainId} | treasury 拿满 币 {LedgerSupply.Format(_genesis.TotalSupplyAtoms)} + 计价币 {LedgerSupply.Format(_genesis.QuoteTotalSupplyAtoms)}");
            Log($"  ⚠ treasury 种子（hex，丢失 = 丢失全部供给）：{Convert.ToHexString(treasury.Seed).ToLowerInvariant()}");
            Log($"  ⚠ producer 种子（hex，丢失 = 无法出块，可从备份换 producer 重建链头）：{Convert.ToHexString(_producer.Seed).ToLowerInvariant()}");
        }
        // 加载路径的 producer 种子从环境变量来（重启续链：链文件只存公钥）
        if (_producer is null)
        {
            var producerSeed = Environment.GetEnvironmentVariable("AETHER_LEDGER_PRODUCER_SEED");
            if (string.IsNullOrWhiteSpace(producerSeed))
                throw new InvalidOperationException(
                    "重启已有链须设 AETHER_LEDGER_PRODUCER_SEED（hex 种子——生成链时日志打印过；无它无法出块）");
            _producer = new LedgerWallet(Convert.FromHexString(producerSeed));
            if (Convert.ToBase64String(_producer.PublicKey) != _genesis.ProducerPublicKey)
                throw new InvalidOperationException("AETHER_LEDGER_PRODUCER_SEED 与链创世的出块人公钥不符（创世出块人唯一）。");
        }

        var addr = IPAddress.TryParse(_options.Host, out var ip) ? ip : IPAddress.Loopback;
        _listener = new TcpListener(addr, _options.Port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _blockLoop = Task.Run(() => BlockLoopAsync(_cts.Token));
        Log($"链节点已就绪：{_options.Host}:{_options.Port}（JSON-RPC）| 链文件 {_options.ChainFilePath} | 出块间隔 {_options.BlockIntervalSec}s");
    }

    /// <summary>停止：停监听 + 停出块 + flush（链文件本就逐块落盘——flush 是把最后一块的行写完整）。</summary>
    public async Task StopAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_acceptLoop is not null) { try { await _acceptLoop; } catch { } }
        if (_blockLoop is not null) { try { await _blockLoop; } catch { } }
        Log("链节点已停止（链文件为最真相——独立验证器随时可复核）。");
    }

    // —— JSON-RPC 面（请求 {op, arg} → 应答 {ok, result|err}；每连接一读循环串行分发） ——

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => ServeConnectionAsync(client, ct), ct);
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var response = HandleRequest(line);
                await writer.WriteLineAsync(response);
            }
        }
        catch { /* 连接断开——正常 */ }
        finally { try { client.Dispose(); } catch { } }
    }

    /// <summary>单请求处理（串行——节点无并发面；op 见 <see cref="LedgerOps"/>）。</summary>
    private string HandleRequest(string line)
    {
        try
        {
            var request = JsonSerializer.Deserialize<LedgerRpcRequest>(line, Json);
            if (request?.Op is null)
                return Err("请求缺 op 字段");
            if (_genesis is null)
                return Err("节点未完成启动（创世未加载）");   // 监听在 Start() 尾部才开——运行到不了；守卫消 CS8602（_genesis 在 Start 内赋值）
            switch (request.Op)
            {
                case LedgerOps.GetInfo:
                    return Ok(new { genesisHash = _genesis.Hash, chainId = _genesis.ChainId, height = Height, mempool = _mempool.Count, totalSupplyAtoms = _genesis.TotalSupplyAtoms, quoteTotalSupplyAtoms = _genesis.QuoteTotalSupplyAtoms });

                case LedgerOps.GetBalance:
                {
                    var address = request.Arg?.GetPropertyCaseInsensitive("address")?.GetString();
                    if (string.IsNullOrWhiteSpace(address)) return Err("getBalance 缺 address");
                    lock (_gate)
                    {
                        _state.TryGetValue(address, out var account);
                        return Ok(new { address, balanceAtoms = account?.BalanceAtoms ?? 0, quoteBalanceAtoms = account?.QuoteBalanceAtoms ?? 0, nonce = account?.Nonce ?? 0 });
                    }
                }

                case LedgerOps.SubmitTx:
                {
                    var tx = request.Arg?.Deserialize<LedgerTransaction>(Json);
                    if (tx is null) return Err("submitTx 交易字段非法");
                    string? error;
                    lock (_gate)
                    {
                        error = LedgerValidator.ValidateTransaction(tx, _genesis!, _state);
                        if (error is null)
                        {
                            // mempool 防重：同 nonce 只留最新（旧的是同笔重发或被顶掉的乱序）
                            _mempool.RemoveAll(m => m.From == tx.From && m.Nonce == tx.Nonce);
                            _mempool.Add(tx);
                        }
                    }
                    return error is null ? Ok(new { accepted = true, txHash = tx.Hash }) : Err(error);
                }

                case LedgerOps.GetBlock:
                {
                    var heightStr = request.Arg?.GetPropertyCaseInsensitive("height")?.GetRawText();
                    if (!ulong.TryParse(heightStr, out var height)) return Err("getBlock 缺 height");
                    LedgerBlock? block;
                    lock (_gate) block = height <= Height ? _blocks[(int)height - 1] : null;
                    return block is null ? Err($"高度 {height} 不存在（当前 {Height}）") : Ok(block);
                }

                case LedgerOps.GetTx:
                {
                    var hash = request.Arg?.GetPropertyCaseInsensitive("hash")?.GetString();
                    if (string.IsNullOrWhiteSpace(hash)) return Err("getTx 缺 hash");
                    LedgerTransaction? tx = null; LedgerBlock? block = null;
                    lock (_gate)
                        (tx, block) = FindTransaction(hash);
                    return tx is null ? Err($"交易 {hash[..Math.Min(16, hash.Length)]}… 不存在") : Ok(new { blockHeight = block!.Height, transaction = tx });
                }

                case LedgerOps.ExportChain:
                    lock (_gate) return Ok(new { exported = true, path = _options.ChainFilePath, genesisHash = _genesis.Hash, height = Height, note = "链文件即全部历史——Aether.Ledger.Verify 可独立复核" });

                default:
                    return Err($"未知操作：{request.Op}");
            }
        }
        catch (Exception ex)
        {
            return Err($"请求处理异常：{ex.Message}");
        }
    }

    private (LedgerTransaction?, LedgerBlock?) FindTransaction(string hash)
    {
        foreach (var block in _blocks)
            foreach (var tx in block.Transactions)
                if (tx.Hash == hash) return (tx, block);
        return (null, null);
    }

    // —— 出块循环：有交易即出 + 间隔心跳空块 ——

    private async Task BlockLoopAsync(CancellationToken ct)
    {
        var lastBlockAt = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { break; }

            List<LedgerTransaction> pending;
            lock (_gate)
            {
                if (_mempool.Count == 0
                    && Environment.TickCount64 - lastBlockAt < (long)_options.BlockIntervalSec * 1000)
                    continue;
                pending = new List<LedgerTransaction>(_mempool);
            }

            // 出块（Builder 逐笔重验——与 RPC 入口同一套规则；nonce 顺序排列：同账户按 nonce 升序）
            pending.Sort((a, b) => a.From == b.From ? a.Nonce.CompareTo(b.Nonce) : string.CompareOrdinal(a.From, b.From));
            LedgerBlock? parent = null;
            lock (_gate) if (_blocks.Count > 0) parent = _blocks[^1];
            var (block, _, rejected) = LedgerBlockBuilder.Build(
                _genesis!, parent, pending, _producer!, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), _state);

            lock (_gate)
            {
                // 提交：状态推进 + 块登记 + mempool 清空 + 追加落盘（每块一行——链文件永远是最真相）
                foreach (var tx in block.Transactions)
                {
                    _state[tx.From] = _state[tx.From].Debit(tx.Asset, tx.AmountAtoms);
                    _state[tx.To] = _state.TryGetValue(tx.To, out var toState)
                        ? toState.Credit(tx.Asset, tx.AmountAtoms)
                        : new LedgerAccountState(0, 0, 0).Credit(tx.Asset, tx.AmountAtoms);
                }
                _blocks.Add(block);
                _mempool.RemoveAll(m => block.Transactions.Any(t => t.Hash == m.Hash));
                AppendBlockToFile(block);
                foreach (var (tx, reason) in rejected)
                    Log($"内存池交易被出块剔除（不应发生——入池已验证）：from={tx.From[..12]}… {reason}");
            }
            lastBlockAt = Environment.TickCount64;
            if (block.Transactions.Count > 0)
                Log($"出块 #{block.Height}：{block.Transactions.Count} 笔交易（{block.Transactions.Sum(t => (decimal)t.AmountAtoms) / LedgerSupply.AtomsPerCoin:N2} 币）");
        }
    }

    /// <summary>逐块 append（链文件格式 = JSON lines——重启恢复 = 读取+重放，天然增量）。</summary>
    private void AppendBlockToFile(LedgerBlock block)
    {
        try
        {
            using var writer = new StreamWriter(_options.ChainFilePath, append: true);
            writer.WriteLine(JsonSerializer.Serialize(block, new JsonSerializerOptions()));
        }
        catch (Exception ex)
        {
            Log($"⚠ 链文件追加失败（内存态继续——但重启将丢此块）：{ex.Message}");
        }
    }

    private static string Ok(object result)
        => JsonSerializer.Serialize(new LedgerRpcResponse(true, result, null));

    private static string Err(string error)
        => JsonSerializer.Serialize(new LedgerRpcResponse(false, null, error));

    private void Log(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [LEDGER] {msg}";
        Console.WriteLine(line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logPath))!);
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch { /* 日志失败不打断节点 */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}

/// <summary>节点 RPC 操作名（请求 <c>{op, arg}</c>）。</summary>
public static class LedgerOps
{
    public const string GetInfo = "getInfo";
    public const string GetBalance = "getBalance";
    public const string SubmitTx = "submitTx";
    public const string GetBlock = "getBlock";
    public const string GetTx = "getTx";
    public const string ExportChain = "exportChain";
}

/// <summary>RPC 帧（请求）。</summary>
public sealed record LedgerRpcRequest(string? Op, JsonElement? Arg);

/// <summary>RPC 帧（应答）。</summary>
public sealed record LedgerRpcResponse(bool Ok, object? Result, string? Err);

internal static class JsonElementExtensions
{
    /// <summary>大小写不敏感取属性（JSON 元素便捷——与 System.Text.Json 默认大小写敏感互补）。</summary>
    public static JsonElement? GetPropertyCaseInsensitive(this JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }
}
