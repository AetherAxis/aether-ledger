using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Aether.Ledger;

/// <summary>
/// 链上地址：SHA256(公钥) 前 20 字节，显示为 40 个小写 hex 字符（镜像比特币地址构造）。
/// 地址是公钥的哈希承诺——验证交易时必须重算 <c>SHA256(pubkey)[0..20] == from</c>，
/// 防止「拿别人公钥签名但填自己地址」的张冠李戴。值语义（hex 字符串承载相等性）。
/// </summary>
public readonly record struct LedgerAddress
{
    public const int Size = 20;
    public const int HexLength = Size * 2;

    /// <summary>40 字符小写 hex（规范形）。</summary>
    public string Hex { get; }

    private LedgerAddress(string hex) => Hex = hex;

    /// <summary>从公钥派生地址（SHA256 前 20 字节）。</summary>
    public static LedgerAddress FromPublicKey(ReadOnlySpan<byte> publicKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(publicKey, hash);
        return FromBytes(hash[..Size]);
    }

    /// <summary>从 20 字节原始地址构造。</summary>
    public static LedgerAddress FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"地址须 {Size} 字节，实际 {bytes.Length}。");
        return new LedgerAddress(Convert.ToHexString(bytes).ToLowerInvariant());
    }

    /// <summary>从 40 字符 hex 构造（校验长度与 hex 合法性；不接受大写——规范形小写）。</summary>
    public static LedgerAddress Parse(string hex)
    {
        if (hex.Length != HexLength || !hex.All(IsLowerHex))
            throw new ArgumentException($"地址须 {HexLength} 个小写 hex 字符，实际 \"{hex}\"。");
        return new LedgerAddress(hex);
    }

    /// <summary>原始 20 字节（签名载荷编码用）。</summary>
    public byte[] ToBytes() => Convert.FromHexString(Hex);

    public override string ToString() => Hex;

    private static bool IsLowerHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');
}

/// <summary>
/// 钱包密钥：**32 字节种子 = 全部财富与身份**（Ed25519 从种子确定性派生——同种子恒同密钥，
/// 备份 = 备份 32 字节）。刻意的胶水层：NSec 的 Key 类型不进公开 API（链类型零 NSec 泄漏，
/// 序列化/验证只碰 byte[]）。
/// </summary>
public sealed class LedgerWallet
{
    /// <summary>Ed25519 种子（32B）——这就是私钥；泄漏 = 财富易主，保管等级 = 钱本身。</summary>
    public byte[] Seed { get; }

    /// <summary>Ed25519 公钥（32B）。</summary>
    public byte[] PublicKey { get; }

    /// <summary>派生地址（SHA256(pubkey)[0..20]）。</summary>
    public LedgerAddress Address { get; }

    private readonly NSec.Cryptography.Key _key;

    /// <summary>从 32 字节种子恢复钱包（同种子恒同钱包——确定性派生）。</summary>
    public LedgerWallet(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
            throw new ArgumentException($"种子须 32 字节，实际 {seed.Length}。");
        Seed = seed.ToArray();
        _key = NSec.Cryptography.Key.Import(
            NSec.Cryptography.SignatureAlgorithm.Ed25519, Seed, NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
        PublicKey = _key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        Address = LedgerAddress.FromPublicKey(PublicKey);
    }

    /// <summary>生成新钱包（密码学随机种子）。</summary>
    public static LedgerWallet Create()
    {
        Span<byte> seed = stackalloc byte[32];
        RandomNumberGenerator.Fill(seed);
        return new LedgerWallet(seed);
    }

    /// <summary>签名任意字节（交易载荷签名用；Ed25519 自带哈希——直接签原文）。</summary>
    public byte[] Sign(ReadOnlySpan<byte> payload)
        => NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(_key, payload.ToArray());
}

/// <summary>验签静态入口（验证器用——只需公钥，不需钱包）。</summary>
public static class LedgerSignature
{
    /// <summary>Ed25519 验签（64B 签名，32B 公钥）。</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        var alg = NSec.Cryptography.SignatureAlgorithm.Ed25519;
        var pub = NSec.Cryptography.PublicKey.Import(alg, publicKey.ToArray(), NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        return alg.Verify(pub, payload.ToArray(), signature.ToArray());
    }
}

/// <summary>规范字节编码（签名/哈希的载荷——字段定序，无歧义）。public：测试与上层签名构造需同一编码。</summary>
public static class LedgerCodec
{
    /// <summary>交易签名载荷：chainId | asset | from | to | amount | nonce | memo——chainId 防跨链重放、
    /// asset 防跨资产重放（批 6：同一金额的另一资产交易不能重放本签名）。⚠ 载荷形状变更 = 硬分叉（旧链不兼容）。</summary>
    public static byte[] TransactionPayload(uint chainId, uint asset, LedgerAddress from, LedgerAddress to,
        ulong amount, ulong nonce, string? memo)
    {
        var memoBytes = Encoding.UTF8.GetBytes(memo ?? "");
        var buf = new byte[4 + 4 + LedgerAddress.Size * 2 + 8 + 8 + memoBytes.Length];
        var off = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), chainId); off += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), asset); off += 4;
        from.ToBytes().AsSpan().CopyTo(buf.AsSpan(off)); off += LedgerAddress.Size;
        to.ToBytes().AsSpan().CopyTo(buf.AsSpan(off)); off += LedgerAddress.Size;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off), amount); off += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off), nonce); off += 8;
        memoBytes.AsSpan().CopyTo(buf.AsSpan(off));
        return buf;
    }

    /// <summary>区块头哈希载荷：chainId | height | prevHash | timestamp | txRoot。</summary>
    public static byte[] HeaderPayload(uint chainId, ulong height, string previousHash,
        ulong timestamp, string transactionRoot)
    {
        var prev = Convert.FromHexString(previousHash);
        var root = Convert.FromHexString(transactionRoot);
        var buf = new byte[4 + 8 + 32 + 8 + 32];
        var off = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), chainId); off += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off), height); off += 8;
        prev.AsSpan().CopyTo(buf.AsSpan(off)); off += 32;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off), timestamp); off += 8;
        root.AsSpan().CopyTo(buf.AsSpan(off));
        return buf;
    }

    /// <summary>SHA256 → 小写 hex（64 字符）。</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
