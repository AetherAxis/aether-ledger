# Aether Ledger 设计说明

> 本文件写「怎么设计 / 为什么这么设计 / 还能往哪升级」。工作区边界与迁移决策以根 `MIGRATION.md` 为准。

## 1. 定位与边界

`aether-ledger` 是链资产层：原生代币账本 + 节点 RPC + 交易所托管桥，只回答余额归属与转账合法性；交易所可停摆，链文件与验证器仍是余额真相。

本仓只保留引擎纯库、纯链测试与运维手册；宿主、CLI、部署脚本及桥/跨域测试按 `MIGRATION.md` 留在 `aether-apps`/monorepo，不引用交易所、桌面、AI。

## 2. 项目组成

| 项目 | 职责 | 关键类型 | 出向依赖 |
|---|---|---|---|
| `src/Aether.Ledger` | 纯库：账户/签名、交易/区块、创世供给、五条规则、链文件、节点 RPC、桥 | `Address/Wallet/Signature/Codec`、`Transaction`、`Genesis/Block`、`AccountState/BlockBuilder/ChainFile`、`Validator`、`Node/Client`、`Bridge` | 仅 `NSec.Cryptography` 26.4.0，零 ProjectReference |
| `tests/Aether.Ledger.Tests` | 纯链测试 22 例（17+5） | `LedgerEngineTests`、`LedgerNodeTests`、`LedgerHarness`、`LedgerNodeLoopback` | 引用引擎；xunit 2.9.3/Test.Sdk 18.11.0/runner 4.0.1/coverlet 10.1.0 |

## 3. 关键设计决策

### 3.1 固定供给 + Ed25519 + 中心化出块
- **决策**：AEX 21M、AQ 210 亿创世全给 treasury，供给 `const`；只有转账；32B 种子经 Ed25519 派生；单一 producer 签块。
- **理由**：供给在创世，增发即换链；种子即身份；单出块人免共识。
- **代价**：producer 可审查、理论可重写历史；种子丢失=丢供给/无法出块。

### 3.2 链 = 资产层，独立于交易所存活
- **决策**：引擎零项目引用；链文件+验证器=无需活进程的余额真相；交易所经 RPC/桥消费。
- **理由**：venue 故障不影响资产归属，离线可复核。
- **代价**：宿主/CLI/测试分散，需跨仓同步。

### 3.3 JSON-lines 链文件作为唯一持久化
- **决策**：首行创世、其后一行一块，逐块 append；重启重放。
- **理由**：可复制/diff/人读、增量、副本可验证。
- **代价**：append 失败仅记日志；文件线性增长，无索引。

### 3.4 验证 = 全量重放，无 Merkle/state root
- **决策**：五条规则（创世总量、链接+出块人验签、交易验签+地址派生、nonce/chainId/asset、守恒）；`txRoot`=哈希拼接。
- **理由**：当前规模秒级重放；提交与出块共用校验。
- **代价**：`getTx` 全链扫描；无轻客户端/证明。

### 3.5 桥：custody 对账，IOU 从链推导
- **决策**：桥持 custody 密钥；充值=`To=custody`+`memo=账户键` 的 AQ 转账，提现=custody 签名；IOU 仅内存、启动重扫；可选 `WithdrawHook` 先扣 venue。2026-10-10 起只收付 AQ。
- **理由**：无第二真相，重启自愈；memo 使链上记录自描述。
- **代价**：custody 泄漏=资产可转走；无 memo 充值不归账；500ms 轮询无退避。

## 4. 运行与验证

```bash
dotnet build aether-ledger.slnx -c Release
dotnet test  aether-ledger.slnx -c Release --no-build
```

节点在 `../aether-apps/Aether.Ledger.Node`（默认 `127.0.0.1:17805` / `~/.aether/ledger.chain`）；env 为 `AETHER_LEDGER_ADDR/CHAIN_FILE/CHAIN_ID/BLOCK_INTERVAL_SEC`，已有链须设 `PRODUCER_SEED`。验证器 `Aether.Ledger.Verify <链文件>`，0=有效/2=无效。RPC：`getInfo/getBalance/submitTx/getBlock/getTx/exportChain`，一行一答、无推送。CI：ubuntu + dotnet 10.0.x，注入 GitHub Packages 凭据后 Release build/test。本次仅静态核对，未跑 build/test；22 例与 10-23 的 22/22 见 `README/MIGRATION`。

## 5. 升级记录（2026-10-24）

| 项 | 变更前 | 变更后 | 依据 / 验证 |
|---|---|---|---|
| `Microsoft.Extensions.Hosting` | 10.0.10 | 10.0.13 | props diff；本仓未引用（宿主在 apps） |
| `Hosting.WindowsServices` | 10.0.10 | 10.0.13 | 同上 |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | 18.11.0 | 测试栈升级 |
| `xunit.runner.visualstudio` | 3.1.4 | 4.0.1 | 同上 |
| `coverlet.collector` | 6.0.4 | 10.1.0 | 同上 |
| `xunit` | 2.9.3 | 2.9.3（不变） | 配套 |
| 告警修复① | treasury 种子可能 null | `?? throw InvalidOperationException($"...种子：{logFile}")` | NodeTests diff；带日志路径 |
| 告警修复② | 未使用 `_sim` + 空 `Dispose`/`IDisposable` | 删除，各测试 `using var sim` | 同上 |
| NU1507 | 基线曾出现 | 重新还原后消失 | 旧 obj 缓存；assets `logs` 空，非配置缺陷 |

## 6. 待升级项

| # | 优先级 | 项目 | 现状证据 | 建议 | 代价 / 风险 |
|---|---|---|---|---|---|
| 1 | P1 | 链文件追加可靠性 | 追加异常仅日志「重启将丢此块」（`LedgerNode.cs:304-315`） | flush/fsync；失败停写或只读；校验末行 | 出块略慢 |
| 2 | P1 | 重启恢复测试名不副实 | 只调 `VerifyFile`，未二次启动（`LedgerNodeTests.cs:133-155`） | 保留链文件，用 producer 种子起第二节点 | 测试变慢；夹具需改 |
| 3 | P2 | mempool 被拒交易不移除 | 只按接受 hash 清理，rejected 仅日志（`LedgerNode.cs:292-295`） | 移出或标记失败；加上限 | 防误删同 nonce 替换 |
| 4 | P2 | 无 state root/交易索引 | `txRoot`=哈希拼接；`getTx` 全链扫描（`LedgerNode.cs:247-253`） | 加高度→交易哈希索引，后加 state root | 索引需与 append 同步 |
| 5 | P2 | 桥状态与轮询 | IOU 全内存、启动全量重扫；500ms 无退避（`LedgerBridge.cs:75-92,166-185`） | checkpoint；指数退避与健康指标 | checkpoint 有第二真相风险 |
| 6 | P2 | 桥/跨域测试缺位 | csproj 注释与 `MIGRATION` §7：Bridge 测试留 monorepo | apps 建集成测试：归账/钩子/重扫 | 需临时链，跨仓维护 |
| 7 | P3 | 远程导出/分页缺失 | `exportChain` 只回本地 path；`getBlock` 单高度（`LedgerNode.cs:234-235`） | 范围导出/分页与流式 | 协议扩展；需限流 |
| 8 | P3 | 单出块人无外部共识 | README/runbook 明示可审查、可重写 | 多签或 checkpoint 锚定 | 复杂度高 |

## 7. 本轮升级落地（2026-10-24）

§6 的 1–4 项已落地（都在本源仓内可验证，无需 E2/前置）：

- **链文件写可靠**（第 1 项）：`AppendBlockToFile` 改为 `FileStream` 写完 `Flush(flushToDisk: true)`（POSIX fsync / Windows FlushFileBuffers），并返回 bool。提交顺序改为**先落盘再推进内存**——落盘成功而进程此刻崩，重启重放即得同一状态；落盘失败则内存态不前进、出块循环停（`IsAppendFaulted`，只读服务），不再「内存有块、文件没有」。
- **末行截断自愈**（第 1 项「校验末行」）：`LedgerChainFile.ReadWithValidLength` 在字节层切行，末尾半行能解析则收、不能则丢，并返回最后一个完整记录的字节长度；`Start` 据此把文件 `SetLength` 截短——否则半行留在文件中间，**下一次**重启就变成无法容忍的中段损坏。中间坏行仍抛（真损坏要让人看见）。
- **重启恢复测试名副其实**（第 2 项）：原「重启恢复」只跑验证器。现在 `重启恢复_同链文件再起节点_身份高度余额复原且能续块` 真的停服→同链文件再起节点（producer 种子走 `AETHER_LEDGER_PRODUCER_SEED`，即生产重启通道）→ 断言创世哈希/高度/余额/`getTx` 复原，并续出第 2 块后由独立验证器复核。夹具新增 `StopKeepFiles()` / `StartOnExisting()`。
- **mempool 被拒交易**（第 3 项）：**不**立即删除（瞬态拒因下一块可能成立），按连续拒绝计数，连续 3 块仍被拒才剔出并记日志；同 nonce 替换时同步清计数。另加 `MaxMempool = 10000` 背压（满则明确拒收）。
- **交易索引**（第 4 项）：`_txIndex`（hash → 高度）重放时建、出块时加，`getTx` 不再全链扫描。
- 新增测试：末行截断后仍可续块且整链可验；链文件只读时停止出块且内存态不前进（POSIX 权限位注入，Windows 跳过）。

**未做（有意）**：第 5 项桥 checkpoint（引入第二真相，须与 `LedgerBridge` 的轮询/重扫语义一起设计）、第 6 项桥/跨域集成测试（需临时链 + 跨仓）、第 7 项远程导出/分页（协议扩展 + 限流）、第 8 项多签/外部共识（复杂度高，非本轮范围）。
