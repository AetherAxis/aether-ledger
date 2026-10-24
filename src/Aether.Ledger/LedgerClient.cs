using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Aether.Ledger;

/// <summary>
/// 链节点 RPC 客户端（引擎库内——批 3 交易所桥与测试共用）。一行请求一行应答（与节点协议同款）。
/// <para>
/// <b>长连接复用</b>（2026-10-24）：节点侧是「每连接一读循环串行分发」（<c>LedgerNode.ServeConnectionAsync</c>），
/// 客户端却曾每调用新建 <see cref="TcpClient"/>。桥启动要全量重扫链（一个块一次 RPC），链高 2 万级时
/// = 2 万次握手 + 2 万个 TIME_WAIT 端口，把 Windows 默认 16384 个动态端口打光 ⇒ <c>SocketException 10048</c>
/// ⇒ 交易所崩溃 ⇒ 服务自动重启再重扫一遍，永久起不来（2026-10-24 服务器现场）。改为单连接串行（信号量互斥），
/// 断链时丢弃旧连接重连一次。
/// </para>
/// <b>签名在调用方</b>（钱包在客户端侧）——本客户端只传输已签名交易，无私钥面。
/// </summary>
public sealed class LedgerClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TcpClient? _client;      // 长连接（null = 未连接/已断开）
    private NetworkStream? _stream;
    private byte[] _pending = [];    // 上一次读取超出一行末尾的余量（协议一行一应答，正常为空）

    public LedgerClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>节点信息（创世哈希/高度/内存池/总量）。</summary>
    public async Task<LedgerNodeInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var result = await CallAsync(LedgerOps.GetInfo, null, ct);
        return new LedgerNodeInfo(
            result.GetProperty("genesisHash").GetString()!,
            result.GetProperty("chainId").GetUInt32(),
            result.GetProperty("height").GetUInt64(),
            result.GetProperty("mempool").GetInt32(),
            result.GetProperty("totalSupplyAtoms").GetUInt64(),
            result.GetProperty("quoteTotalSupplyAtoms").GetUInt64());
    }

    /// <summary>查账户（余额 + nonce；未知地址返回 0/0）。</summary>
    public async Task<LedgerAccountState> GetBalanceAsync(string address, CancellationToken ct = default)
    {
        var result = await CallAsync(LedgerOps.GetBalance, new { address }, ct);
        return new LedgerAccountState(
            result.GetProperty("balanceAtoms").GetUInt64(),
            result.GetProperty("quoteBalanceAtoms").GetUInt64(),
            result.GetProperty("nonce").GetUInt64());
    }

    /// <summary>提交已签名交易（验签/nonce/余额不过 ⇒ CtpBusinessException 风格 err）。
    /// 入池即回（交易在下一个出块间隔内进块）。</summary>
    public async Task SubmitTxAsync(LedgerTransaction tx, CancellationToken ct = default)
    {
        await CallAsync(LedgerOps.SubmitTx, tx, ct);
    }

    /// <summary>查块（高度）。</summary>
    public async Task<LedgerBlock> GetBlockAsync(ulong height, CancellationToken ct = default)
    {
        var result = await CallAsync(LedgerOps.GetBlock, new { height }, ct);
        return result.Deserialize<LedgerBlock>(LedgerJson.Options)!;
    }

    /// <summary>按哈希查交易（含所在块高度）。</summary>
    public async Task<(ulong BlockHeight, LedgerTransaction Tx)> GetTxAsync(string hash, CancellationToken ct = default)
    {
        var result = await CallAsync(LedgerOps.GetTx, new { hash }, ct);
        return (result.GetProperty("blockHeight").GetUInt64(),
            result.GetProperty("transaction").Deserialize<LedgerTransaction>(LedgerJson.Options)!);
    }

    /// <summary>请求/应答核心（长连接串行；断链重连一次；err ⇒ 抛 LedgerClientException——错误串原样透传）。</summary>
    private async Task<JsonElement> CallAsync(string op, object? arg, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            try
            {
                return await CallOnceAsync(op, arg, ct);
            }
            catch (OperationCanceledException)
            {
                // 半途取消 ⇒ 连接上可能残留半个应答，直接丢弃（下次重连）
                DropConnection();
                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                // 节点重启/连接被回收：丢掉旧连接，重连一次再试（业务错误 LedgerClientException 不在此列）
                DropConnection();
                return await CallOnceAsync(op, arg, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonElement> CallOnceAsync(string op, object? arg, CancellationToken ct)
    {
        var stream = await EnsureConnectedAsync(ct);
        var request = JsonSerializer.Serialize(new LedgerRpcRequest(op,
            arg is null ? null : JsonSerializer.SerializeToElement(arg, LedgerJson.Options)), LedgerJson.Options);
        var bytes = Encoding.UTF8.GetBytes(request + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);

        var response = JsonSerializer.Deserialize<LedgerRpcClientResponse>(await ReadLineAsync(stream, ct), LedgerJson.Options)
            ?? throw new LedgerClientException("应答反序列化为 null");
        if (!response.Ok)
            throw new LedgerClientException(response.Err ?? "(无错误信息)");
        return response.Result is { } element ? element
            : throw new LedgerClientException($"操作 {op} 无 result（ok=true 但空应答——协议错误）");
    }

    /// <summary>取长连接（没有就建；已建直接复用——一次启动 2 万次 RPC 只握手一次）。</summary>
    private async Task<NetworkStream> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_stream is not null)
            return _stream;
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(_host, _port, ct);
        _client = client;
        _stream = client.GetStream();
        _pending = [];
        return _stream;
    }

    /// <summary>读一行应答（跨包拼接；超出的余量留给下一次——协议一行一应答，正常只会有 0 字节余量）。</summary>
    private async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        while (true)
        {
            var idx = Array.IndexOf(_pending, (byte)'\n');
            if (idx >= 0)
            {
                var line = Encoding.UTF8.GetString(_pending, 0, idx);
                _pending = _pending[(idx + 1)..];
                return line;
            }
            var buf = new byte[64 * 1024];
            var n = await stream.ReadAsync(buf, ct);
            if (n == 0)
                throw new IOException("链节点关闭了连接（应答未读完）");
            var merged = new byte[_pending.Length + n];
            Buffer.BlockCopy(_pending, 0, merged, 0, _pending.Length);
            Buffer.BlockCopy(buf, 0, merged, _pending.Length, n);
            _pending = merged;
        }
    }

    private void DropConnection()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
        _pending = [];
    }

    public void Dispose()
    {
        DropConnection();
        _gate.Dispose();
    }
}

/// <summary>节点信息快照。</summary>
public sealed record LedgerNodeInfo(string GenesisHash, uint ChainId, ulong Height, int MempoolCount, ulong TotalSupplyAtoms, ulong QuoteTotalSupplyAtoms);

/// <summary>客户端侧错误（节点 err 原样透传——验签失败/nonce 不符/余额不足等拒因都在里面）。</summary>
public sealed class LedgerClientException(string message) : Exception(message);

/// <summary>客户端侧应答帧（result 为 JsonElement——各操作自行反序列化）。</summary>
public sealed record LedgerRpcClientResponse(bool Ok, JsonElement? Result, string? Err);

internal static class LedgerJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}
