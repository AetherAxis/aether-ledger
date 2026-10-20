using System.Text.Json;

namespace Aether.Ledger.Tests;

/// <summary>
/// 链引擎夹具：确定性测试链——固定种子的三个钱包（treasury/出块人/alice/bob）+ 创世 + 可追加的区块列表。
/// 种子固定 ⇒ 全链确定性（重放测试的「同文件同状态」天然成立）。
/// </summary>
internal static class LedgerHarness
{
    public const uint ChainId = 0x0A11CE;   // 任意测试链 id

    /// <summary>固定种子钱包（32B——同种子恒同钱包）。</summary>
    public static LedgerWallet Wallet(string label)
        => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(label)));

    /// <summary>建标准测试链：treasury 拿满 21M（创世），初始状态。</summary>
    public static (LedgerGenesis Genesis, LedgerWallet Treasury, LedgerWallet Producer) NewChain()
    {
        var treasury = Wallet("treasury-seed");
        var producer = Wallet("producer-seed");
        var genesis = LedgerGenesis.Create(ChainId, treasury.Address, producer);
        return (genesis, treasury, producer);
    }

    /// <summary>创世后的初始状态（treasury 满额，nonce 0）。</summary>
    public static Dictionary<string, LedgerAccountState> InitialState(LedgerGenesis genesis)
        => new(StringComparer.Ordinal)
        {
            [genesis.TreasuryAddress] = new LedgerAccountState(genesis.TotalSupplyAtoms, genesis.QuoteTotalSupplyAtoms, 0),
        };

    /// <summary>签名一笔 treasury → to 的转账并构建含它的区块（单交易块）。</summary>
    public static (LedgerBlock Block, IReadOnlyList<(LedgerTransaction Tx, string Reason)> Rejected) TransferBlock(
        LedgerGenesis genesis, LedgerBlock? parent, LedgerWallet producer, LedgerWallet from, LedgerAddress to,
        ulong amountAtoms, ulong timestamp = 1_000, IReadOnlyDictionary<string, LedgerAccountState>? state = null)
    {
        var current = state ?? StateAfter(genesis, parent is null ? Array.Empty<LedgerBlock>() : new[] { parent });
        var nonce = current.TryGetValue(from.Address.Hex, out var s) ? s.Nonce + 1 : 1;
        var tx = LedgerTransaction.Sign(ChainId, from, to, amountAtoms, nonce);
        var (block, _, rejected) = LedgerBlockBuilder.Build(genesis, parent, new[] { tx }, producer, timestamp, current);
        return (block, rejected);
    }

    /// <summary>重放到某区块为止的状态（便捷）。</summary>
    public static Dictionary<string, LedgerAccountState> StateAfter(LedgerGenesis genesis, IReadOnlyList<LedgerBlock> blocks)
    {
        var result = LedgerValidator.Replay(genesis, blocks);
        Assert.True(result.Ok, $"夹具链非法：{result.Error}");
        return new Dictionary<string, LedgerAccountState>(result.Accounts, StringComparer.Ordinal);
    }
}

/// <summary>
/// 链引擎批 1 的验收钉子（五条规则 + 守恒不变量 + 确定性 + 链文件往返 + 篡改拒绝）。
/// 「不丢」性质在本批成立：链文件 + 验证器 = 不需要任何活进程的余额真相。
/// </summary>
public class LedgerEngineTests
{
    [Fact]
    public void 创世_treasury拿满21M_总量守恒()
    {
        var (genesis, _, _) = LedgerHarness.NewChain();

        var result = LedgerValidator.Replay(genesis, Array.Empty<LedgerBlock>());

        Assert.True(result.Ok);
        Assert.Equal(LedgerSupply.TotalSupplyAtoms, result.Accounts[genesis.TreasuryAddress].BalanceAtoms);
        Assert.True(result.SupplyConserved);
        Assert.Equal(0UL, result.Accounts[genesis.TreasuryAddress].Nonce);
    }

    [Fact]
    public void 创世总量篡改_增发即拒绝()
    {
        var (genesis, _, _) = LedgerHarness.NewChain();
        var forged = genesis with { TotalSupplyAtoms = LedgerSupply.TotalSupplyAtoms * 2 };

        var result = LedgerValidator.Replay(forged, Array.Empty<LedgerBlock>());

        Assert.False(result.Ok);
        Assert.Contains("总量", result.Error);
    }

    [Fact]
    public void 转账链_余额与nonce正确_守恒逐块保持()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        var bob = LedgerHarness.Wallet("bob");

        // 块1：treasury → alice 100 币；块2：alice → bob 30 币
        var (b1, r1) = LedgerHarness.TransferBlock(genesis, null, producer, treasury, alice.Address, 100_000_000UL, 1_000);
        Assert.Empty(r1);
        var state1 = LedgerHarness.StateAfter(genesis, new[] { b1 });
        var (b2, r2) = LedgerHarness.TransferBlock(genesis, b1, producer, alice, bob.Address, 30_000_000UL, 2_000, state1);
        Assert.Empty(r2);

        var result = LedgerValidator.Replay(genesis, new[] { b1, b2 });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(70_000_000UL, result.Accounts[alice.Address.Hex].BalanceAtoms);
        Assert.Equal(30_000_000UL, result.Accounts[bob.Address.Hex].BalanceAtoms);
        Assert.Equal(LedgerSupply.TotalSupplyAtoms - 100_000_000UL, result.Accounts[genesis.TreasuryAddress].BalanceAtoms);
        Assert.Equal(1UL, result.Accounts[alice.Address.Hex].Nonce);
        Assert.Equal(0UL, result.Accounts[bob.Address.Hex].Nonce);       // 只收不发 nonce 不动
        Assert.True(result.SupplyConserved);
    }

    [Fact]
    public void 重放攻击_同笔交易第二次被拒_nonce守门()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        var tx = LedgerTransaction.Sign(LedgerHarness.ChainId, treasury, alice.Address, 1_000_000UL, 1);
        var (b1, _) = LedgerHarness.TransferBlock(genesis, null, producer, treasury, alice.Address, 1_000_000UL, 1_000);
        // 同一笔（nonce=1）再进块 2 —— treasury 的 nonce 已推进到 1 ⇒ 期望 2
        var state1 = LedgerHarness.StateAfter(genesis, new[] { b1 });
        var (b2, _, rejected) = LedgerBlockBuilder.Build(genesis, b1, new[] { tx }, producer, 2_000, state1);

        Assert.Single(rejected);
        Assert.Contains("nonce", rejected[0].Reason);
        Assert.Empty(b2.Transactions);
    }

    [Fact]
    public void 跨链重放_他链签名交易被拒()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        // 签给另一条链（chainId 不同）——载荷含 chainId ⇒ 本链验证必拒
        var tx = LedgerTransaction.Sign(0xDEAD, treasury, alice.Address, 1_000_000UL, 1);

        var (block, _, rejected) = LedgerBlockBuilder.Build(genesis, null, new[] { tx }, producer, 1_000, LedgerHarness.InitialState(genesis));

        Assert.Single(rejected);
        Assert.Contains("跨链", rejected[0].Reason);
    }

    [Fact]
    public void 篡改交易金额_重放被拒_守恒暴露()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        var (b1, _) = LedgerHarness.TransferBlock(genesis, null, producer, treasury, alice.Address, 100_000_000UL, 1_000);
        // 篡改：金额翻倍（直接构造一条假交易——nonce 相同但金额不同 ⇒ 余额不足/txRoot 不匹配必触发其一）
        var forgedTx = LedgerTransaction.Sign(LedgerHarness.ChainId, treasury, alice.Address,
            genesis.TotalSupplyAtoms, 1);   // 金额 = 全部供给 ⇒ treasury 余额恰好够——但 txRoot 会对不上（真块里没有它）
        var blocks = new List<LedgerBlock> { b1 };
        var state = LedgerHarness.StateAfter(genesis, blocks);
        var (forgedBlock, _, rejected) = LedgerBlockBuilder.Build(genesis, b1, new[] { forgedTx }, producer, 2_000, state);
        // 单独验证：假块本身合法（余额恰够）——但拼进原链后 txRoot/链接会暴露任何「事后改块」
        // 这里钉的是直接场景：篡改 b1 内的交易（改 JSON 后重放）
        var tampered = JsonSerializer.Deserialize<LedgerBlock>(
            JsonSerializer.Serialize(b1).Replace("100000000", "200000000"))!;
        var result = LedgerValidator.Replay(genesis, new[] { tampered });

        Assert.False(result.Ok);    // 签名载荷对不上（金额变了）⇒ 验签失败
    }

    [Fact]
    public void 篡改区块链接_prevHash断链被拒()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        var (b1, _) = LedgerHarness.TransferBlock(genesis, null, producer, treasury, alice.Address, 100_000_000UL, 1_000);
        var state1 = LedgerHarness.StateAfter(genesis, new[] { b1 });
        var (b2, _) = LedgerHarness.TransferBlock(genesis, b1, producer, treasury, alice.Address, 50_000_000UL, 2_000, state1);
        // 篡改 b2 的 prevHash（指向假哈希）——重放必拒
        var tampered = b2 with { PreviousHash = new string('a', 64) };

        var result = LedgerValidator.Replay(genesis, new[] { b1, tampered });

        Assert.False(result.Ok);
        Assert.Contains("prevHash", result.Error);
    }

    [Fact]
    public void 伪造出块人签名_被拒()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        var (b1, _) = LedgerHarness.TransferBlock(genesis, null, producer, treasury, alice.Address, 100_000_000UL, 1_000);
        // 另一个钱包冒名出块（对同一 header 载荷用自己的种子签名）
        var impostor = LedgerHarness.Wallet("impostor");
        var sig = Convert.ToBase64String(impostor.Sign(b1.HeaderPayload));
        var forged = b1 with { ProducerSignature = sig };

        var result = LedgerValidator.Replay(genesis, new[] { forged });

        Assert.False(result.Ok);
        Assert.Contains("出块人", result.Error);
    }

    [Fact]
    public void 余额不足_拒入块()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        var tx = LedgerTransaction.Sign(LedgerHarness.ChainId, treasury, alice.Address,
            genesis.TotalSupplyAtoms + 1, 1);   // 超过全部供给

        var (block, _, rejected) = LedgerBlockBuilder.Build(genesis, null, new[] { tx }, producer, 1_000, LedgerHarness.InitialState(genesis));

        Assert.Single(rejected);
        Assert.Contains("余额不足", rejected[0].Reason);
    }

    [Fact]
    public void 链文件往返_同文件同状态_独立验证器可用()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        var (b1, _) = LedgerHarness.TransferBlock(genesis, null, producer, treasury, alice.Address, 100_000_000UL, 1_000);
        var state1 = LedgerHarness.StateAfter(genesis, new[] { b1 });
        var (b2, _) = LedgerHarness.TransferBlock(genesis, b1, producer, treasury, alice.Address, 50_000_000UL, 2_000, state1);

        var path = Path.Combine(Path.GetTempPath(), $"ledger-{Guid.NewGuid():N}.chain");
        try
        {
            LedgerChainFile.Write(path, genesis, new[] { b1, b2 });
            var (readGenesis, readBlocks) = LedgerChainFile.Read(path);

            var result = LedgerValidator.Replay(readGenesis, readBlocks);
            Assert.True(result.Ok, result.Error);
            Assert.Equal(150_000_000UL, result.Accounts[alice.Address.Hex].BalanceAtoms);
            // 独立验证器入口（控制台同一逻辑）
            var verifyResult = LedgerVerifier.VerifyFile(path);
            Assert.True(verifyResult.Ok);
            Assert.Contains("链验证 OK", LedgerVerifier.Summarize(verifyResult));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void 同种子恒同钱包_地址确定性派生()
    {
        var seed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var w1 = new LedgerWallet(seed);
        var w2 = new LedgerWallet(seed);

        Assert.Equal(w1.Address, w2.Address);
        Assert.Equal(w1.PublicKey, w2.PublicKey);
        Assert.Equal(w1.Address, LedgerAddress.FromPublicKey(w1.PublicKey));
    }

    [Fact]
    public void 链文件被篡改一字节_验证器拒绝()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        var (b1, _) = LedgerHarness.TransferBlock(genesis, null, producer, treasury, alice.Address, 100_000_000UL, 1_000);

        var path = Path.Combine(Path.GetTempPath(), $"ledger-tamper-{Guid.NewGuid():N}.chain");
        try
        {
            LedgerChainFile.Write(path, genesis, new[] { b1 });
            // 文件级篡改：翻转一个 hex 字符（金额域）
            var text = File.ReadAllText(path);
            var idx = text.IndexOf("100000000", StringComparison.Ordinal);
            Assert.True(idx >= 0, "夹具金额未找到");
            var tampered = text.Remove(idx, 9).Insert(idx, "100000001");
            File.WriteAllText(path, tampered);

            var result = LedgerVerifier.VerifyFile(path);
            Assert.False(result.Ok);   // 签名/txRoot 至少其一暴露
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void memo超64字节_签名入口拒绝()
    {
        var (genesis, treasury, _) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");

        Assert.Throws<ArgumentException>(() =>
            LedgerTransaction.Sign(LedgerHarness.ChainId, treasury, alice.Address, 1UL, 1,
                new string('x', 65)));
    }

    [Fact]
    public void 计价币转账_余额按资产分账_币余额不动()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice-dual");

        // treasury 给 alice 转 100 AQ（asset=Quote）
        var tx = LedgerTransaction.Sign(LedgerHarness.ChainId, treasury, alice.Address,
            100_000_000UL, 1, null, LedgerSupply.Assets.Quote);
        var (block, _, rejected) = LedgerBlockBuilder.Build(genesis, null, new[] { tx }, producer, 1_000,
            LedgerHarness.InitialState(genesis));
        Assert.Empty(rejected);
        var state = LedgerHarness.StateAfter(genesis, new[] { block });

        Assert.Equal(100_000_000UL, state[alice.Address.Hex].QuoteBalanceAtoms);   // AQ 到账
        Assert.Equal(0UL, state[alice.Address.Hex].BalanceAtoms);                  // 币余额不动
        var result = LedgerValidator.Replay(genesis, new[] { block });
        Assert.True(result.Ok, result.Error);
        Assert.True(result.QuoteSupplyConserved);                                  // AQ 守恒
        Assert.Equal(LedgerSupply.QuoteTotalSupplyAtoms - 100_000_000UL, state[genesis.TreasuryAddress].QuoteBalanceAtoms);
    }

    [Fact]
    public void 资产字段篡改_签名失效被拒()
    {
        // 签名绑资产：合法币转账改成计价币转账（同金额同 nonce）⇒ 签名载荷变了 ⇒ 验签拒绝——防跨资产重放。
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice-tamper");
        var tx = LedgerTransaction.Sign(LedgerHarness.ChainId, treasury, alice.Address, 1_000_000UL, 1);
        var forged = tx with { Asset = LedgerSupply.Assets.Quote };
        var (_, _, rejected) = LedgerBlockBuilder.Build(genesis, null, new[] { forged }, producer, 1_000,
            LedgerHarness.InitialState(genesis));
        Assert.NotEmpty(rejected);   // 验签失败（载荷含 asset）
    }

    [Fact]
    public void 张冠李戴_公钥与地址不符被拒()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        var bob = LedgerHarness.Wallet("bob");
        // 用 treasury 的密钥签名，但 From 填 bob 的地址（对 bob 地址的伪装）
        var payload = LedgerCodec.TransactionPayload(LedgerHarness.ChainId, LedgerSupply.Assets.Coin, bob.Address, alice.Address, 1UL, 1, null);
        var tx = new LedgerTransaction
        {
            ChainId = LedgerHarness.ChainId,
            Asset = LedgerSupply.Assets.Coin,
            From = bob.Address.Hex,
            To = alice.Address.Hex,
            AmountAtoms = 1UL,
            Nonce = 1,
            PublicKey = Convert.ToBase64String(treasury.PublicKey),   // treasury 的公钥
            Signature = Convert.ToBase64String(treasury.Sign(payload)),
        };

        var (block, _, rejected) = LedgerBlockBuilder.Build(genesis, null, new[] { tx }, producer, 1_000, LedgerHarness.InitialState(genesis));

        Assert.Single(rejected);
        Assert.Contains("地址派生", rejected[0].Reason);
    }

    [Fact]
    public void 同块内同账户多笔_nonce连续可打包()
    {
        var (genesis, treasury, producer) = LedgerHarness.NewChain();
        var alice = LedgerHarness.Wallet("alice");
        var bob = LedgerHarness.Wallet("bob");
        var txs = new[]
        {
            LedgerTransaction.Sign(LedgerHarness.ChainId, treasury, bob.Address, 10_000_000UL, 1),
            LedgerTransaction.Sign(LedgerHarness.ChainId, treasury, bob.Address, 20_000_000UL, 2),
            LedgerTransaction.Sign(LedgerHarness.ChainId, treasury, alice.Address, 5_000_000UL, 3),
        };

        var (block, accepted, rejected) = LedgerBlockBuilder.Build(
            genesis, null, txs, producer, 1_000, LedgerHarness.InitialState(genesis));

        Assert.Empty(rejected);
        Assert.Equal(3, accepted.Count);
        var result = LedgerValidator.Replay(genesis, new[] { block });
        Assert.True(result.Ok, result.Error);
        Assert.Equal(30_000_000UL, result.Accounts[bob.Address.Hex].BalanceAtoms);
        Assert.Equal(5_000_000UL, result.Accounts[alice.Address.Hex].BalanceAtoms);
        Assert.Equal(3UL, result.Accounts[genesis.TreasuryAddress].Nonce);
    }
}
