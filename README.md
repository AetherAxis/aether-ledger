# Aether Ledger

虚拟币链引擎——原生代币账本（对标比特币的固定供给 + Ed25519 密钥所有权；中心化出块、资产真自持）。
用户为「AI agent 经济」自建的资产层：链 = 资产层（独立于任何交易所存活），交易所 = venue 层。

## 组成

| 项目 | 说明 |
|---|---|
| `src/Aether.Ledger` | 链引擎纯库：账户/转账签名/区块/五条验证规则/创世供给/JSON-lines 链文件/节点（TCP JSON-RPC + 内存池 + 出块循环）/桥（交易所托管服务）。唯一 NuGet 依赖 `BouncyCastle.Cryptography`（**纯托管** Ed25519 确定性签名——2026-10-27 从 NSec/libsodium 换过来，签名字节不变，见 `LedgerEd25519ParityTests`；换的原因：libsodium 的 iOS 切片缺 NSec 需要的符号，导致移动端无法链接） |
| `tests/Aether.Ledger.Tests` | 纯链测试（引擎规则 + 节点回环 + 重启恢复/末行截断自愈/落盘失败停写），24 例 |
| `doc/ledger-runbook.md` | 运维手册（部署/验证/备份种子/发币/充值/交易/提币） |

节点/验证器宿主（原 `apps/LedgerNode`、`apps/LedgerVerify`）已移至 aether-ctp-hosts 仓（组合根归一，2026-10-20）。

## 核心设计

- **固定供给**：总量 21M × 10⁸ 原子单位，创世发给 treasury（`const` 不可配——改供给 = 改创世哈希 = 另一条链）
- **密钥所有权**：32 字节种子 = 全部财富与身份；Ed25519 确定性签名
- **签名载荷**含 chainId（防跨链重放）+ nonce（防同链重放）
- **验证** = 全量重放：五条规则（创世总量/链接+出块人验签/交易验签+地址派生/nonce/余额守恒）
- **诚实边界**：单出块人可审查（可检测）不可伪造不可增发；理论上可重写历史（无外部共识）

## 构建与测试

```bash
dotnet test aether-ledger.slnx
```

## 本仓库的来历

2026-10-19 从 Aether monorepo（`~/Desktop/Aether`）抽取为第一个独立仓库试点。
**边界**：三个可执行宿主（节点 / 钱包 CLI / 独立验证器）都在 ⑯ `aether-coin-hosts`
（`../aether-coin-hosts/Aether.Ledger.*`，2026-10-27 从 ⑧ 拆出）；钱包 CLI 另引 ④ `Aether.Exchange.Rpc` 做提现；
钱包 GUI 在 ⑰ `aether-coin-desktop`（2026-10-28 拆出）。
`deploy-ledger` 也随宿主住 ⑯（源 = ⑯ 宿主 + 本仓）——**本仓的改动改由 ⑯ 的 workflow 送达服务器上的 :17805**。
桥/对账/提现的跨域胶水（`LedgerCashRail`）留在 ⑧ 的交易所宿主。抽取时的完整决策记录见工作区根 `MIGRATION.md`。

## 运行

可执行宿主（`Aether.Ledger.Node` 节点 / `Aether.Ledger.Verify` 独立验证器）在 ⑯ `aether-coin-hosts`
（2026-10-20 曾归一进 ⑧ 组合根，2026-10-27 随虚拟币线拆出；工作区同级 `../aether-coin-hosts/`）：

```bash
# 节点（默认 127.0.0.1:17805，链文件 ~/.aether/ledger.chain）
dotnet run --project ../aether-coin-hosts/Aether.Ledger.Node

# 独立验证（退出码 0=有效 / 2=无效）
dotnet run --project ../aether-coin-hosts/Aether.Ledger.Verify -- <链文件路径>

# 新链生成时 treasury/producer 种子打印进 node 日志——务必抄录（丢失 = 丢失链）
```
