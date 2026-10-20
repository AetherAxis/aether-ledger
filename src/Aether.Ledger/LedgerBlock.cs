using System.Text.Json.Serialization;

namespace Aether.Ledger;

/// <summary>
/// 创世块（height 0，链的**身份**）：chainId、总量常量、初始归属（treasury 地址拿满全部供给——
/// 发行模式：国库经订单簿卖出，价格从第一笔成交开始就是市场定的）、出块人公钥。
/// 供给规则活在创世里 ⇒ 任何增发/改归属都必然改变创世哈希 ⇒ 一条不同的链 ⇒ 验证器直接拒绝。
/// </summary>
public sealed record LedgerGenesis
{
    public required uint ChainId { get; init; }
    public required ulong TotalSupplyAtoms { get; init; }
    /// <summary>计价币（AQ）总量（批 6 双资产）——与币同规则：创世全给 treasury、无铸造、守恒恒真。
    /// ⚠ 字段缺失 = 旧单资产链文件 ⇒ 反序列化直接拒绝（硬分叉，见 LedgerChainFile 的友好报错）。</summary>
    public required ulong QuoteTotalSupplyAtoms { get; init; }
    public required string TreasuryAddress { get; init; }   // 40 hex
    public required string ProducerPublicKey { get; init; } // base64(32B)

    /// <summary>创世哈希（SHA256(规范编码)，64 hex）——区块 1 的 prevHash，链身份。</summary>
    [JsonIgnore]
    public string Hash
    {
        get
        {
            var payload = GenesisPayload();
            return LedgerCodec.Sha256Hex(payload);
        }
    }

    private byte[] GenesisPayload()
    {
        var treasury = LedgerAddress.Parse(TreasuryAddress).ToBytes();
        var producer = Convert.FromBase64String(ProducerPublicKey);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(ChainId);
        w.Write(TotalSupplyAtoms);
        w.Write(QuoteTotalSupplyAtoms);
        w.Write(treasury.Length);
        w.Write(treasury);
        w.Write(producer.Length);
        w.Write(producer);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>构造创世（校验：两资产分配合计 ≡ 各自总量常量——分配即 treasury 全拿，恒真）。</summary>
    public static LedgerGenesis Create(uint chainId, LedgerAddress treasury, LedgerWallet producer)
        => new()
        {
            ChainId = chainId,
            TotalSupplyAtoms = LedgerSupply.TotalSupplyAtoms,
            QuoteTotalSupplyAtoms = LedgerSupply.QuoteTotalSupplyAtoms,
            TreasuryAddress = treasury.Hex,
            ProducerPublicKey = Convert.ToBase64String(producer.PublicKey),
        };
}

/// <summary>
/// 区块：header（哈希链——历史不可静默改写的根）+ 交易列表 + 出块人签名。
/// **最小可验证形态**：无 Merkle 树、无 state root——本量级验证 = 全量重放（秒级、免费）；
/// txRoot = SHA256(全部交易哈希拼接) 保留包含证明力（重算即验证）。等链长大再补 state root 是纯增量升级。
/// 空块合法（60s 心跳：锚定新鲜度——外部世界知道链还活着）。
/// </summary>
public sealed record LedgerBlock
{
    public required uint ChainId { get; init; }
    public required ulong Height { get; init; }
    public required string PreviousHash { get; init; }      // 64 hex（height 1 = 创世哈希）
    public required ulong TimestampUnix { get; init; }      // 出块人时间戳（单出块人：无共识时间，诚实边界）
    public required string TransactionRoot { get; init; }   // 64 hex
    public required IReadOnlyList<LedgerTransaction> Transactions { get; init; }
    public required string ProducerSignature { get; init; } // base64(64B)——出块人对 header 哈希签名

    /// <summary>header 哈希载荷（规范编码：chainId | height | prevHash | timestamp | txRoot）。</summary>
    [JsonIgnore]
    public byte[] HeaderPayload => LedgerCodec.HeaderPayload(ChainId, Height, PreviousHash, TimestampUnix, TransactionRoot);

    /// <summary>区块哈希（64 hex）——下一块的 prevHash。</summary>
    [JsonIgnore]
    public string Hash => LedgerCodec.Sha256Hex(HeaderPayload);
}
