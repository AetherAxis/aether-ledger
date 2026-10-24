using System.Text.Json;

namespace Aether.Ledger.Tests;

/// <summary>节点级测试夹具：进程内起节点（临时端口 + 临时链文件）+ 客户端回环。</summary>
internal sealed class LedgerNodeLoopback : IDisposable
{
    private readonly LedgerNode _node;
    private readonly string _chainFile;
    private readonly string? _producerSeed;
    private bool _keepFiles;

    public LedgerClient Client { get; }
    /// <summary>实际监听端口（临时端口 0 = 内核分配——LedgerWalletToolTests 把它写进工具的节点地址 env）。</summary>
    public int Port { get; private init; }
    public string ChainFile => _chainFile;
    public LedgerGenesis Genesis => LedgerChainFile.Read(_chainFile).Genesis;

    private LedgerNodeLoopback(LedgerNode node, LedgerClient client, string chainFile, string? producerSeed)
    {
        _node = node;
        Client = client;
        _chainFile = chainFile;
        _producerSeed = producerSeed;
    }

    /// <summary>启动新链回环节点（临时端口 0 + 临时链文件；捕获生成日志里的 treasury/producer 种子供测试用）。</summary>
    public static async Task<LedgerNodeLoopback> StartNewAsync(string label)
    {
        var chainFile = Path.Combine(Path.GetTempPath(), $"ledger-node-{label}-{Guid.NewGuid():N}.chain");
        var logFile = chainFile + ".log";
        var options = new LedgerNodeOptions("127.0.0.1", 0, chainFile, 77, 60);
        var node = new LedgerNode(options, logFile);
        node.Start();
        var port = node.LocalEndpoint?.Port ?? throw new InvalidOperationException("节点未绑定端口");
        // 种子（生成的种子打印在日志里——测试从那里回收）
        var log = await File.ReadAllTextAsync(logFile);
        // 种子必须回收成功，否则后续签名断言会以「空种子」这种难以定位的方式失败。
        var treasurySeed = ExtractSeed(log, "treasury")
            ?? throw new InvalidOperationException($"节点日志中未找到 treasury 种子：{logFile}");
        var producerSeed = ExtractSeed(log, "producer");
        return new LedgerNodeLoopback(node, new LedgerClient("127.0.0.1", port), chainFile, producerSeed)
        {
            TreasurySeed = treasurySeed,
            Port = port,
        };
    }

    /// <summary>生成链时日志里的 treasury 种子（hex）——测试直接用它签名转账。</summary>
    public string TreasurySeed { get; private init; } = "";

    /// <summary>生成链时的 producer 种子（重启续链要用同一个——链文件只存公钥）。</summary>
    public string ProducerSeed => _producerSeed ?? "";

    /// <summary>节点已判定链文件落盘失败并停止出块。</summary>
    public bool IsAppendFaulted => _node.IsAppendFaulted;

    /// <summary>节点累计接受的 TCP 连接数（断言客户端长连接复用）。</summary>
    public long AcceptedConnections => _node.AcceptedConnections;

    /// <summary>在**已有**链文件上起节点 = 生产重启路径：producer 种子经 <c>AETHER_LEDGER_PRODUCER_SEED</c> 提供
    /// （env 是跨进程通道，这里同步设置/复位——Start() 在返回前同步完成，不留窗口）。
    /// 不解析种子日志（已有链不再打印种子），<see cref="TreasurySeed"/> 为空是预期的。</summary>
    public static LedgerNodeLoopback StartOnExisting(string chainFile, string producerSeed)
    {
        var previous = Environment.GetEnvironmentVariable("AETHER_LEDGER_PRODUCER_SEED");
        Environment.SetEnvironmentVariable("AETHER_LEDGER_PRODUCER_SEED", producerSeed);
        try
        {
            var node = new LedgerNode(new LedgerNodeOptions("127.0.0.1", 0, chainFile, 77, 60), chainFile + ".log");
            node.Start();
            var port = node.LocalEndpoint?.Port ?? throw new InvalidOperationException("节点未绑定端口");
            return new LedgerNodeLoopback(node, new LedgerClient("127.0.0.1", port), chainFile, producerSeed)
            {
                Port = port,
            };
        }
        finally
        {
            Environment.SetEnvironmentVariable("AETHER_LEDGER_PRODUCER_SEED", previous);
        }
    }

    /// <summary>停止节点但**保留**链文件/日志（重启恢复测试要在同一文件上再起一个节点）。</summary>
    public void StopKeepFiles()
    {
        _keepFiles = true;
        try { _node.StopAsync().GetAwaiter().GetResult(); } catch { }
    }

    private static string? ExtractSeed(string log, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            log, $@"⚠ {label} 种子（hex[^：]*）：([0-9a-f]{{64}})");
        return match.Success ? match.Groups[1].Value : null;
    }

    public void Dispose()
    {
        try { _node.StopAsync().GetAwaiter().GetResult(); } catch { }
        try { _node.Dispose(); } catch { }
        if (_keepFiles) return;
        try { File.Delete(_chainFile); } catch { }
        try { File.Delete(_chainFile + ".log"); } catch { }
    }
}

/// <summary>
/// 批 2 节点验收：RPC 面（getInfo/getBalance/submitTx/getBlock/getTx/exportChain）+
/// 出块循环（有交易出块 + 心跳）+ 链文件持久化（逐块 append、重启 = 全量重放恢复、独立验证器复核）。
/// </summary>
public class LedgerNodeTests
{
    // 每个测试方法各自 `using var sim = await NewSimAsync(...)`：xunit 每个方法一个类实例，
    // 不需要再留一个从未使用的字段。
    private static async Task<LedgerNodeLoopback> NewSimAsync(string label)
        => await LedgerNodeLoopback.StartNewAsync(label);

    [Fact]
    public async Task 新链生成_treasury满额_信息与余额查询()
    {
        using var sim = await NewSimAsync("info");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));

        var info = await sim.Client.GetInfoAsync();
        var balance = await sim.Client.GetBalanceAsync(treasury.Address.Hex);

        Assert.Equal(LedgerSupply.TotalSupplyAtoms, balance.BalanceAtoms);
        Assert.Equal(0UL, balance.Nonce);
        Assert.Equal(77U, info.ChainId);
        Assert.Equal(0UL, info.Height);       // 尚无交易——未出块
        Assert.True(info.GenesisHash.Length == 64);
    }

    [Fact]
    public async Task 提交交易_出块进链_余额与nonce推进()
    {
        using var sim = await NewSimAsync("transfer");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var alice = LedgerHarness.Wallet("node-alice");
        var tx = LedgerTransaction.Sign(77, treasury, alice.Address, 100_000_000UL, 1, "节点转账测试");

        await sim.Client.SubmitTxAsync(tx);
        // 等出块（有交易即出——出块循环 500ms 轮询）
        await WaitForAsync(async () => (await sim.Client.GetInfoAsync()).Height >= 1);

        var treasuryBalance = await sim.Client.GetBalanceAsync(treasury.Address.Hex);
        var aliceBalance = await sim.Client.GetBalanceAsync(alice.Address.Hex);
        Assert.Equal(LedgerSupply.TotalSupplyAtoms - 100_000_000UL, treasuryBalance.BalanceAtoms);
        Assert.Equal(1UL, treasuryBalance.Nonce);
        Assert.Equal(100_000_000UL, aliceBalance.BalanceAtoms);

        // getBlock / getTx
        var block = await sim.Client.GetBlockAsync(1);
        Assert.Equal(tx.Hash, Assert.Single(block.Transactions).Hash);
        var (height, found) = await sim.Client.GetTxAsync(tx.Hash);
        Assert.Equal(1UL, height);
        Assert.Equal("节点转账测试", found.Memo);
    }

    [Fact]
    public async Task 客户端长连接_连续200次调用只握手一次()
    {
        using var sim = await NewSimAsync("reuse");
        for (var i = 0; i < 200; i++)
            Assert.Equal(77U, (await sim.Client.GetInfoAsync()).ChainId);

        // 连接数 = 1：每调用新建 TcpClient 的旧实现这里是 200（桥全量重扫 2 万块 = 2 万次握手 + 2 万 TIME_WAIT，
        // Windows 默认 16384 个动态端口被打光 ⇒ SocketException 10048 ⇒ 交易所崩溃重启循环）
        Assert.Equal(1L, sim.AcceptedConnections);
    }

    [Fact]
    public async Task 业务错误不断链_同一连接继续可用()
    {
        using var sim = await NewSimAsync("err-keep");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var alice = LedgerHarness.Wallet("node-err-keep");
        var bad = LedgerTransaction.Sign(77, treasury, alice.Address, 1UL, 2);   // nonce 跳号

        await Assert.ThrowsAsync<LedgerClientException>(() => sim.Client.SubmitTxAsync(bad));
        Assert.Equal(77U, (await sim.Client.GetInfoAsync()).ChainId);

        // 拒因是节点正常应答（err 帧），不是连接失败——不该触发重连
        Assert.Equal(1L, sim.AcceptedConnections);
    }

    [Fact]
    public async Task 提交非法交易_验签拒因透传()
    {
        using var sim = await NewSimAsync("reject");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var alice = LedgerHarness.Wallet("node-alice");
        // nonce=2 跳号（当前 0 ⇒ 期望 1）
        var bad = LedgerTransaction.Sign(77, treasury, alice.Address, 1UL, 2);

        var ex = await Assert.ThrowsAsync<LedgerClientException>(() => sim.Client.SubmitTxAsync(bad));
        Assert.Contains("nonce", ex.Message);
    }

    [Fact]
    public async Task 重启恢复_同链文件再起节点_身份高度余额复原且能续块()
    {
        var sim = await NewSimAsync("persist");
        // ① 出块后独立复核（先不信节点：验证器只读链文件）
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var alice = LedgerHarness.Wallet("node-restart");
        var tx = LedgerTransaction.Sign(77, treasury, alice.Address, 250_000_000UL, 1);
        await sim.Client.SubmitTxAsync(tx);
        await WaitForAsync(async () => (await sim.Client.GetInfoAsync()).Height >= 1);

        var verify = LedgerVerifier.VerifyFile(sim.ChainFile);
        Assert.True(verify.Ok, verify.Error);
        Assert.Equal(250_000_000UL, verify.Accounts[alice.Address.Hex].BalanceAtoms);
        Assert.True(verify.SupplyConserved);

        var infoBefore = await sim.Client.GetInfoAsync();
        var chainFile = sim.ChainFile;
        var producerSeed = sim.ProducerSeed;
        sim.StopKeepFiles();   // 停服但保留链文件 = 进程被杀后磁盘上留下的东西

        // ② 同链文件再起节点（producer 种子经 env——生产重启的既定通道，测它而不是绕开它）
        using var node2 = LedgerNodeLoopback.StartOnExisting(chainFile, producerSeed);
        var infoAfter = await node2.Client.GetInfoAsync();
        Assert.Equal(infoBefore.GenesisHash, infoAfter.GenesisHash);    // 链身份跨重启不变
        Assert.Equal(infoBefore.Height, infoAfter.Height);              // 全量重放恢复高度
        Assert.Equal(250_000_000UL, (await node2.Client.GetBalanceAsync(alice.Address.Hex)).BalanceAtoms);
        var (height, found) = await node2.Client.GetTxAsync(tx.Hash);
        Assert.Equal(1UL, height);                                      // 交易索引在重放时重建
        Assert.Equal(tx.Hash, found.Hash);

        // ③ 续块：producer 私钥来自种子，新块签名必须被验证器接受（treasury nonce 已推进到 1 ⇒ 下笔用 2）
        var bob = LedgerHarness.Wallet("node-restart-bob");
        await node2.Client.SubmitTxAsync(LedgerTransaction.Sign(77, treasury, bob.Address, 1_000_000UL, 2));
        await WaitForAsync(async () => (await node2.Client.GetInfoAsync()).Height >= 2);
        var verify2 = LedgerVerifier.VerifyFile(chainFile);
        Assert.True(verify2.Ok, verify2.Error);                        // 重启前 1 块 + 重启后 1 块同链可验
        Assert.Equal(2UL, verify2.BlocksProcessed);
    }

    [Fact]
    public async Task 末行被截断_重放丢弃半行并截短文件_之后仍可续块()
    {
        var sim = await NewSimAsync("truncated-tail");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var alice = LedgerHarness.Wallet("node-truncated");
        await sim.Client.SubmitTxAsync(LedgerTransaction.Sign(77, treasury, alice.Address, 10_000_000UL, 1));
        await WaitForAsync(async () => (await sim.Client.GetInfoAsync()).Height >= 1);

        var chainFile = sim.ChainFile;
        var producerSeed = sim.ProducerSeed;
        sim.StopKeepFiles();
        // 模拟「进程在 write 中途被杀」：文件尾多出半行合法 JSON 前缀（无换行）
        File.AppendAllText(chainFile, "{\"chainId\":77,\"height\":2,\"prev");
        var lengthBefore = new FileInfo(chainFile).Length;

        using var node2 = LedgerNodeLoopback.StartOnExisting(chainFile, producerSeed);
        Assert.Equal(1UL, (await node2.Client.GetInfoAsync()).Height);       // 半行被丢弃，已确认块仍在
        Assert.True(new FileInfo(chainFile).Length < lengthBefore);           // 半行被截掉（不是留在文件里）
        // 续块后整条链仍可被独立验证（半行没变成中段损坏）
        await node2.Client.SubmitTxAsync(LedgerTransaction.Sign(77, treasury, alice.Address, 1_000_000UL, 2));
        await WaitForAsync(async () => (await node2.Client.GetInfoAsync()).Height >= 2);
        var verify = LedgerVerifier.VerifyFile(chainFile);
        Assert.True(verify.Ok, verify.Error);
        Assert.Equal(2UL, verify.BlocksProcessed);
    }

    [Fact]
    public async Task 链文件不可写_停止出块_内存态不前进()
    {
        if (OperatingSystem.IsWindows()) return;   // 用 POSIX 权限位制造落盘失败
        var sim = await NewSimAsync("append-fault");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var alice = LedgerHarness.Wallet("node-fault");
        File.SetUnixFileMode(sim.ChainFile, UnixFileMode.UserRead);   // 只读 ⇒ 下一次 append 必失败
        try
        {
            await sim.Client.SubmitTxAsync(LedgerTransaction.Sign(77, treasury, alice.Address, 1_000_000UL, 1));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!sim.IsAppendFaulted && sw.ElapsedMilliseconds < 5000) await Task.Delay(50);

            Assert.True(sim.IsAppendFaulted);                          // 落盘失败被识别（不是只记日志继续跑）
            Assert.Equal(0UL, (await sim.Client.GetInfoAsync()).Height);   // 内存态不前进（重启不会「丢」块）
            Assert.Single(File.ReadAllLines(sim.ChainFile));           // 文件里只有创世——无半块
        }
        finally
        {
            File.SetUnixFileMode(sim.ChainFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task 客户端错误透传_未知地址查余额返回零()
    {
        using var sim = await NewSimAsync("unknown");
        var nobody = LedgerHarness.Wallet("nobody");

        var balance = await sim.Client.GetBalanceAsync(nobody.Address.Hex);

        Assert.Equal(0UL, balance.BalanceAtoms);
        Assert.Equal(0UL, balance.Nonce);
    }

    /// <summary>轮询等待谓词（出块是异步循环——不能同步断言）。</summary>
    private static async Task WaitForAsync(Func<Task<bool>> predicate, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await predicate()) return;
            await Task.Delay(50);
        }
        Assert.Fail("等待超时（出块循环未在预期时间内完成）");
    }
}
