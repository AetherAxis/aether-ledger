# Aether Ledger

虚拟币链引擎——原生代币账本（对标比特币的固定供给 + Ed25519 密钥所有权；中心化出块、资产真自持）。
用户为「AI agent 经济」自建的资产层：链 = 资产层（独立于任何交易所存活），交易所 = venue 层。

## 组成

| 项目 | 说明 |
|---|---|
| `src/Aether.Ledger` | 链引擎纯库：账户/转账签名/区块/五条验证规则/创世供给/JSON-lines 链文件/节点（TCP JSON-RPC + 内存池 + 出块循环）/桥（交易所托管服务）。唯一依赖 `NSec.Cryptography`（Ed25519 确定性签名） |
| `tests/Aether.Ledger.Tests` | 纯链测试（引擎规则 + 节点回环），22 例 |
| `doc/ledger-runbook.md` | 运维手册（部署/验证/备份种子/发币/充值/交易/提币） |

节点/验证器宿主（原 `apps/LedgerNode`、`apps/LedgerVerify`）已移至 aether-apps 仓（组合根归一，2026-10-20）。

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
**边界**：`LedgerWallet` CLI（依赖交易所 RPC 客户端）与桥/对账/提现的跨域集成测试**留在 monorepo**——
等交易所 SDK 独立成包后迁入。抽取时的完整决策记录见工作区根 `MIGRATION.md`。

## 运行

可执行宿主（`Aether.Ledger.Node` 节点 / `Aether.Ledger.Verify` 独立验证器）已随组合根归一移至
aether-apps 仓（2026-10-20；工作区同级 `../aether-apps/`）：

```bash
# 节点（默认 127.0.0.1:17805，链文件 ~/.aether/ledger.chain）
dotnet run --project ../aether-apps/Aether.Ledger.Node

# 独立验证（退出码 0=有效 / 2=无效）
dotnet run --project ../aether-apps/Aether.Ledger.Verify -- <链文件路径>

# 新链生成时 treasury/producer 种子打印进 node 日志——务必抄录（丢失 = 丢失链）
```
