using System.Text.Json;

namespace Aether.Ledger;

/// <summary>
/// 账户状态（重放中维护）：币余额 + 计价币余额（原子单位，按资产分账）+ 发送序号
/// （nonce 随每笔**发出**交易递增——以太坊模型；nonce 单流覆盖全部资产）。
/// </summary>
public sealed record LedgerAccountState(ulong BalanceAtoms, ulong QuoteBalanceAtoms, ulong Nonce)
{
    /// <summary>记账减法（转账 from 侧——nonce +1）。调用方保证余额充足（验证器前置）。</summary>
    public LedgerAccountState Debit(uint asset, ulong atoms)
        => asset == LedgerSupply.Assets.Coin
            ? this with { BalanceAtoms = BalanceAtoms - atoms, Nonce = Nonce + 1 }
            : this with { QuoteBalanceAtoms = QuoteBalanceAtoms - atoms, Nonce = Nonce + 1 };

    /// <summary>记账加法（转账 to 侧——nonce 不动）。余额上限（≤ 各资产总量）结构上保证无溢出。</summary>
    public LedgerAccountState Credit(uint asset, ulong atoms)
        => asset == LedgerSupply.Assets.Coin
            ? this with { BalanceAtoms = BalanceAtoms + atoms }
            : this with { QuoteBalanceAtoms = QuoteBalanceAtoms + atoms };

    public ulong BalanceOf(uint asset)
        => asset == LedgerSupply.Assets.Coin ? BalanceAtoms : QuoteBalanceAtoms;
}

/// <summary>
/// 区块构建器（引擎侧——节点/测试共用）：给定父块、待打包交易、出块人钱包，产出合法区块。
/// 逐笔按当前状态验证（验签/地址派生/nonce/余额——**非法交易入块前即拒**），按通过序打包；
/// nonce 连续性由状态机保证（同账户多笔须按 nonce 顺序提交）。
/// </summary>
public static class LedgerBlockBuilder
{
    /// <summary>构建下一块（height = parent.Height + 1；timestamp 由调用方给——单出块人无共识时间）。</summary>
    public static (LedgerBlock Block, IReadOnlyList<LedgerTransaction> Accepted, IReadOnlyList<(LedgerTransaction Tx, string Reason)> Rejected)
        Build(LedgerGenesis genesis, LedgerBlock? parent, IEnumerable<LedgerTransaction> pending,
            LedgerWallet producer, ulong timestampUnix, IReadOnlyDictionary<string, LedgerAccountState> state)
    {
        var previousHash = parent is null ? genesis.Hash : parent.Hash;
        var height = (parent?.Height ?? 0) + 1;
        var chainId = genesis.ChainId;

        var accepted = new List<LedgerTransaction>();
        var rejected = new List<(LedgerTransaction, string)>();
        var working = new Dictionary<string, LedgerAccountState>(state, StringComparer.Ordinal);

        foreach (var tx in pending)
        {
            if (LedgerValidator.ValidateTransaction(tx, genesis, working) is { } error)
            {
                rejected.Add((tx, error));
                continue;
            }
            accepted.Add(tx);
            // 立即推进工作状态——同块内同账户后续交易按推进后的 nonce/余额验证（按资产分账）
            working[tx.From] = working[tx.From].Debit(tx.Asset, tx.AmountAtoms);
            working[tx.To] = working.TryGetValue(tx.To, out var existing)
                ? existing.Credit(tx.Asset, tx.AmountAtoms)
                : new LedgerAccountState(0, 0, 0).Credit(tx.Asset, tx.AmountAtoms);
        }

        // txRoot = SHA256(全部交易哈希拼接)——无 Merkle 树（本量级重放即验证），包含证明力等价
        var txHashes = accepted.Select(t => Convert.FromHexString(t.Hash)).SelectMany(b => b).ToArray();
        var txRoot = LedgerCodec.Sha256Hex(txHashes);

        var headerPayload = LedgerCodec.HeaderPayload(chainId, height, previousHash, timestampUnix, txRoot);
        var signature = producer.Sign(headerPayload);

        var block = new LedgerBlock
        {
            ChainId = chainId,
            Height = height,
            PreviousHash = previousHash,
            TimestampUnix = timestampUnix,
            TransactionRoot = txRoot,
            Transactions = accepted,
            ProducerSignature = Convert.ToBase64String(signature),
        };
        return (block, accepted, rejected);
    }
}

/// <summary>链文件 IO：JSON lines（第一行创世，其后每行一块）——可复制、可 diff、人可读的「全部历史」。</summary>
public static class LedgerChainFile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,      // 一行一块（链文件格式契约）
    };

    /// <summary>写出链文件（创世 + 全部区块，按高度序）。</summary>
    public static void Write(string path, LedgerGenesis genesis, IEnumerable<LedgerBlock> blocks)
    {
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine(JsonSerializer.Serialize(genesis, Json));
        foreach (var block in blocks)
            writer.WriteLine(JsonSerializer.Serialize(block, Json));
    }

    /// <summary>读入链文件（返回创世 + 区块列表，按文件序——顺序校验归验证器）。
    /// 旧单资产链（缺 QuoteTotalSupplyAtoms/Asset 字段）在此明确拒绝——批 6 双资产是硬分叉。</summary>
    public static (LedgerGenesis Genesis, IReadOnlyList<LedgerBlock> Blocks) Read(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0)
            throw new InvalidDataException($"链文件为空：{path}");
        LedgerGenesis genesis;
        try
        {
            genesis = JsonSerializer.Deserialize<LedgerGenesis>(lines[0])
                ?? throw new InvalidDataException("创世行反序列化为 null。");
        }
        catch (JsonException)
        {
            throw new InvalidDataException(
                $"链文件为旧单资产协议（缺 QuoteTotalSupplyAtoms）——批 6 双资产为硬分叉。删除链文件后重启生成新链：{path}");
        }
        var blocks = new List<LedgerBlock>(lines.Count - 1);
        foreach (var line in lines.Skip(1))
            blocks.Add(JsonSerializer.Deserialize<LedgerBlock>(line)
                ?? throw new InvalidDataException("区块行反序列化为 null。"));
        return (genesis, blocks);
    }
}
