namespace Aether.Ledger;

/// <summary>
/// 链供给规则——**协议的第一不变量**（设计骨架）：全链余额合计 ≡ <see cref="TotalSupplyAtoms"/>，创世之后永真。
/// 21,000,000 币 × 10⁸ 原子单位（比特币式聪）；u64 上限 1.8×10¹⁹ ⇒ 余量四个数量级。
/// 刻意 <see langword="const"/> 而非配置：供给规则活在创世块里 = 任何增发都必然产生不同的创世哈希 = 一条不同的链。
/// </summary>
public static class LedgerSupply
{
    /// <summary>资产 id（批 6 双资产协议）：0 = 币（AEX——21M 稀缺资产）；1 = 计价币（AQ——报价/计价侧）。
    /// 资产 id 进交易签名载荷（防跨资产重放）+ 状态/守恒按资产分账。</summary>
    public static class Assets
    {
        public const uint Coin = 0;
        public const uint Quote = 1;
    }

    /// <summary>小数位（比特币式 8 位）。</summary>
    public const int Decimals = 8;

    /// <summary>原子单位/币。</summary>
    public const ulong AtomsPerCoin = 100_000_000UL;

    /// <summary>总量 21M 币（原子单位）——创世分配合计必须精确等于此值。</summary>
    public const ulong TotalSupplyAtoms = 21_000_000UL * AtomsPerCoin;

    /// <summary>
    /// 计价币（AQ）总量 210 亿（原子单位）——批 6 双资产（2026-10-08，方案 C「币安式」）：
    /// 交易对两边都是真资产（可充可提、都住链上），交易所永不发钱。AQ 是计价/报价侧
    /// （场内现金 = AQ 的托管凭证），由国库分发（运营者卖/送），8 位小数同币。
    /// </summary>
    public const ulong QuoteTotalSupplyAtoms = 21_000_000_000UL * AtomsPerCoin;

    /// <summary>显示格式化（验证器输出用）。</summary>
    public static string Format(ulong atoms)
        => (atoms / AtomsPerCoin).ToString("N0") + "." + (atoms % AtomsPerCoin).ToString("D8");
}
