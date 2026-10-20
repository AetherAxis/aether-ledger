using System.Text;
using System.Text.Json.Serialization;

namespace Aether.Ledger;

/// <summary>
/// 链上交易——**只有一种类型：转账**（无铸造：供给规则活在创世块，结构上不可能增发）。
/// 签名 = Ed25519(种子, <see cref="SignedPayload"/>)——载荷含 chainId（防跨链重放）、
/// nonce（防同链重放：合法当且仅当 <c>nonce == from 账户当前 nonce + 1</c>，以太坊模型）。
/// memo ≤ 64B UTF-8：今天充值归账（交易所知道记哪个内部账户），未来效用钩子。
/// JSON 序列化（链文件 = JSON lines）；签名/公钥走 base64。
/// </summary>
public sealed record LedgerTransaction
{
    public required uint ChainId { get; init; }
    /// <summary>资产 id（<see cref="LedgerSupply.Assets"/>：0=币 AEX / 1=计价币 AQ）。入签名载荷（防跨资产重放）。</summary>
    public required uint Asset { get; init; }
    public required string From { get; init; }        // 40 hex
    public required string To { get; init; }          // 40 hex
    public required ulong AmountAtoms { get; init; }
    public required ulong Nonce { get; init; }
    public string? Memo { get; init; }                // ≤64B UTF-8
    public required string PublicKey { get; init; }   // base64(32B)——验证需公钥（地址只是其哈希前 20B）
    public required string Signature { get; init; }   // base64(64B)

    /// <summary>memo 的 UTF-8 字节上限（设计定案：64B——归账够用 + 效用钩子留口）。</summary>
    public const int MaxMemoBytes = 64;

    /// <summary>签名载荷（规范编码：chainId | asset | from | to | amount | nonce | memo）。</summary>
    [JsonIgnore]
    public byte[] SignedPayload => LedgerCodec.TransactionPayload(
        ChainId, Asset, LedgerAddress.Parse(From), LedgerAddress.Parse(To), AmountAtoms, Nonce, Memo);

    /// <summary>交易哈希（SHA256(载荷+公钥+签名) 的 64 hex）——绑定签名在内的完整交易，
    /// 供区块 txRoot 与交易包含证明。</summary>
    [JsonIgnore]
    public string Hash
    {
        get
        {
            var payload = SignedPayload;
            var pub = Convert.FromBase64String(PublicKey);
            var sig = Convert.FromBase64String(Signature);
            var all = new byte[payload.Length + pub.Length + sig.Length];
            payload.AsSpan().CopyTo(all);
            pub.AsSpan().CopyTo(all.AsSpan(payload.Length));
            sig.AsSpan().CopyTo(all.AsSpan(payload.Length + pub.Length));
            return LedgerCodec.Sha256Hex(all);
        }
    }

    /// <summary>签名并构造交易（钱包侧入口；asset 默认币——兼容绝大多数调用点）。memo 超 64B UTF-8 在此拒绝。</summary>
    public static LedgerTransaction Sign(uint chainId, LedgerWallet wallet, LedgerAddress to,
        ulong amountAtoms, ulong nonce, string? memo = null, uint asset = LedgerSupply.Assets.Coin)
    {
        if (memo is not null && Encoding.UTF8.GetByteCount(memo) > MaxMemoBytes)
            throw new ArgumentException($"memo 超 {MaxMemoBytes}B UTF-8（实际 {Encoding.UTF8.GetByteCount(memo)}B）。");
        var payload = LedgerCodec.TransactionPayload(
            chainId, asset, wallet.Address, to, amountAtoms, nonce, memo);
        var signature = wallet.Sign(payload);
        return new LedgerTransaction
        {
            ChainId = chainId,
            Asset = asset,
            From = wallet.Address.Hex,
            To = to.Hex,
            AmountAtoms = amountAtoms,
            Nonce = nonce,
            Memo = memo,
            PublicKey = Convert.ToBase64String(wallet.PublicKey),
            Signature = Convert.ToBase64String(signature),
        };
    }
}
