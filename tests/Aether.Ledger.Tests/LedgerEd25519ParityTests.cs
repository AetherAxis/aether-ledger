using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Aether.Ledger.Tests;

/// <summary>
/// 签名实现从 NSec（libsodium 原生库）换成纯托管 BouncyCastle 后，**签名字节必须逐字节不变**——
/// Ed25519 是 RFC 8032 确定性签名（同一密钥 + 同一消息恒出同一 64 字节），所以换实现不产生
/// 任何链上迁移：历史交易验签、已签名交易重放、公钥/地址派生全部原样成立。
/// <para>本测试就是那条承诺的守门员：NSec 只作为对照实现存在于测试侧，生产闭包无原生依赖，
/// 因此 Android / iOS / 桌面 / 服务器同一份代码都能签能验（iOS 原生库缺符号的链接问题消失）。</para>
/// </summary>
public class LedgerEd25519ParityTests
{
    /// <summary>多种种子（含边界值 + RFC 8032 §7.1 测试向量），覆盖标量/点的边界路径。</summary>
    public static TheoryData<string> SeedHexes => new()
    {
        // RFC 8032 TEST 1 种子
        "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
        // RFC 8032 TEST 2 种子
        "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
        "0000000000000000000000000000000000000000000000000000000000000000",
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
        "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20",
    };

    private static byte[] Seed(string hex) => Convert.FromHexString(hex);

    /// <summary>公钥派生逐字节一致（地址 = SHA256(pubkey)[0..20]，因此地址也必然一致）。</summary>
    [Theory]
    [MemberData(nameof(SeedHexes))]
    public void 公钥与地址_与NSec一致(string seedHex)
    {
        var seed = Seed(seedHex);
        var nsecKey = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        var nsecPublic = nsecKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var wallet = new LedgerWallet(seed);

        Assert.Equal(nsecPublic, wallet.PublicKey);
        Assert.Equal(LedgerAddress.FromPublicKey(nsecPublic), wallet.Address);
    }

    /// <summary>签名逐字节一致——空消息、短消息、跨块长消息都签一遍。</summary>
    [Theory]
    [MemberData(nameof(SeedHexes))]
    public void 签名_与NSec逐字节一致(string seedHex)
    {
        var seed = Seed(seedHex);
        var nsecKey = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        var wallet = new LedgerWallet(seed);

        var payloads = new[]
        {
            Array.Empty<byte>(),
            Encoding.UTF8.GetBytes("aether"),
            Encoding.UTF8.GetBytes(new string('x', 4096)),
            RandomNumberGenerator.GetBytes(37),
        };

        foreach (var payload in payloads)
        {
            var expected = SignatureAlgorithm.Ed25519.Sign(nsecKey, payload);
            var actual = wallet.Sign(payload);
            Assert.Equal(expected, actual);
            // 另一方的实现也要能验：新签名过 NSec 验签、旧签名过新验签。
            Assert.True(SignatureAlgorithm.Ed25519.Verify(nsecKey.PublicKey, payload, actual));
            Assert.True(LedgerSignature.Verify(wallet.PublicKey, payload, expected));
        }
    }

    /// <summary>真实交易载荷（链上实际签的东西）也要逐字节一致——不只是裸字节数组。</summary>
    [Fact]
    public void 交易载荷签名_与NSec一致()
    {
        var seed = Seed("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var wallet = new LedgerWallet(seed);
        var to = LedgerAddress.FromPublicKey(SHA256.HashData(Encoding.UTF8.GetBytes("counterparty"))[..LedgerAddress.Size]);
        var payload = LedgerCodec.TransactionPayload(chainId: 7, asset: 1, wallet.Address, to,
            amount: 123_456_789, nonce: 42, memo: "换库回归");

        var nsecKey = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        Assert.Equal(SignatureAlgorithm.Ed25519.Sign(nsecKey, payload), wallet.Sign(payload));
        Assert.True(LedgerSignature.Verify(wallet.PublicKey, payload, wallet.Sign(payload)));
    }

    /// <summary>验签的失败路径：改一个字节、换公钥、长度不对——一律 false，不崩。</summary>
    [Fact]
    public void 验签失败路径_返回false而不抛异常()
    {
        var wallet = LedgerWallet.Create();
        var payload = Encoding.UTF8.GetBytes("tamper");
        var signature = wallet.Sign(payload);

        var tampered = (byte[])signature.Clone();
        tampered[0] ^= 0xFF;

        Assert.False(LedgerSignature.Verify(wallet.PublicKey, payload, tampered));
        Assert.False(LedgerSignature.Verify(wallet.PublicKey, Encoding.UTF8.GetBytes("tamper!"), signature));
        Assert.False(LedgerSignature.Verify(LedgerWallet.Create().PublicKey, payload, signature));
        Assert.False(LedgerSignature.Verify(wallet.PublicKey, payload, signature[..63]));
        Assert.False(LedgerSignature.Verify(wallet.PublicKey[..31], payload, signature));
        // 非曲线点的公钥（全 0xFF）也必须是 false 而不是异常。
        Assert.False(LedgerSignature.Verify(new byte[32].Select(_ => (byte)0xFF).ToArray(), payload, signature));
    }
}
