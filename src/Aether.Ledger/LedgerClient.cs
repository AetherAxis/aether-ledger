using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Aether.Ledger;

/// <summary>
/// 链节点 RPC 客户端（引擎库内——批 3 交易所桥与测试共用）。一行请求一行应答（与节点协议同款；
/// 每调用一连接（getBalance/submitTx 的调用频率是秒级——连接开销可忽略，换来零连接状态管理）。
/// <b>签名在调用方</b>（钱包在客户端侧）——本客户端只传输已签名交易，无私钥面。
/// </summary>
public sealed class LedgerClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;

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

    /// <summary>请求/应答核心（一连接一请求；err ⇒ 抛 LedgerClientException——错误串原样透传）。</summary>
    private async Task<JsonElement> CallAsync(string op, object? arg, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(_host, _port, ct);
        using var stream = client.GetStream();
        var request = JsonSerializer.Serialize(new LedgerRpcRequest(op,
            arg is null ? null : JsonSerializer.SerializeToElement(arg, LedgerJson.Options)), LedgerJson.Options);
        var bytes = Encoding.UTF8.GetBytes(request);
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);

        // 读一行应答
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n == 0) break;
            var chunk = Encoding.UTF8.GetString(buf, 0, n);
            sb.Append(chunk);
            if (chunk.Contains('\n')) break;
        }
        var response = JsonSerializer.Deserialize<LedgerRpcClientResponse>(sb.ToString(), LedgerJson.Options)
            ?? throw new LedgerClientException("应答反序列化为 null");
        if (!response.Ok)
            throw new LedgerClientException(response.Err ?? "(无错误信息)");
        return response.Result is { } element ? element
            : throw new LedgerClientException($"操作 {op} 无 result（ok=true 但空应答——协议错误）");
    }

    public void Dispose() { /* 每调用一连接——无长驻资源 */ }
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
