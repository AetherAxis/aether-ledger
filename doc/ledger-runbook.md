# Aether Ledger 链侧运维手册（Runbook）

> **定位**：链引擎自身的运维——节点部署/启动、验证、种子备份、链上资产操作。
> 交易所侧（充值/提币桥、现货映射）与桌面端玩法**不在本仓范围**——完整经济流程见 Aether monorepo 的
> `doc/ledger-runbook.md` 与 CLAUDE.md「虚拟币链引擎」节。
> ⚠ 本文档不含任何真实种子/密钥/服务器地址——它们只存在于你的离线备份里。

---

## 0. 全景（30 秒）

**两种链上资产**：币（AEX，21M 稀缺资产）+ 计价币（AQ，210 亿，报价侧）——创世时 treasury 同拿两样。
钱包 = 32 字节随机数种子 + 数学推导（Ed25519 公钥 → SHA256 前 20 字节 = 地址），**纯本地创建、零网络、
无需注册**（比特币同款语义：链只在有人往地址转币时才"知道"它）。

**三把钥匙**：
| 钥匙 | 权力 | 丢失后果 |
|---|---|---|
| `treasury 种子` | 花 21M 币 + 210 亿 AQ 供给 | 丢失 = 丢失全部供给 |
| `producer 种子` | 出块权 | 无法出块（可换 producer 重建链头） |
| `custody 种子` | 托管钱包（交易所场景才需要） | 托管资产不可提 |

**硬分叉语义**：协议升级后旧链文件不兼容——删除 `ledger.chain` 重启生成新链（旧链资产作废）。

## 1. 节点启动与验证

```bash
# 本地起节点（默认 127.0.0.1:17805，链文件 ~/.aether/ledger.chain）
dotnet run --project apps/LedgerNode

# 新链生成时 treasury/producer 种子打印进 node 日志——⚠ 务必当场抄录到离线位置（密码管理器/加密盘）
# 保管等级 = 财富本身。treasury 种子建议同时存成本地文件供 CLI 使用。

# 独立验证（零活进程依赖的余额真相——「不丢」性质的载体；退出码 0=有效 / 2=无效）
dotnet run --project apps/LedgerVerify -- ~/.aether/ledger.chain
```

节点环境变量：

| 变量 | 默认 | 说明 |
|---|---|---|
| `AETHER_LEDGER_ADDR` | `127.0.0.1:17805` | RPC 监听地址（绑 `0.0.0.0` 即公网——Bitcoin 节点公开是常态，17805 无秘密） |
| `AETHER_LEDGER_CHAIN_FILE` | `~/.aether/ledger.chain` | 链文件路径 |
| `AETHER_LEDGER_PRODUCER_SEED` | （生成路径不需要） | **加载已有链必须设**——与磁盘链同一条命的 producer hex（见 §4 排障） |

RPC op（一行请求一行应答，每连接串行，无推送）：`getInfo` / `getBalance` / `submitTx` / `getBlock` /
`getTx` / `exportChain`。

## 2. 链上资产操作

链上操作（发币/查余额/转账）经节点 RPC——monorepo 的 `Aether.Ledger.Wallet` CLI 是现成操作面
（`new` / `info` / `balance` / `transfer`，币数支持 8 位小数，`--asset coin|quote`）：

```bash
# 新钱包（本地零网络）
wallet new --out alice.seed          # 种子 hex（⚠ 丢了没人能找回）+ 40 位 hex 地址

# 国库发资产（你用 treasury 种子签名；出块约 0.5-1s）
wallet transfer --seed @treasury.seed --to <alice地址> --amount 5                # 币
wallet transfer --seed @treasury.seed --to <alice地址> --amount 500 --asset quote # AQ

# 验证
wallet balance <alice地址>           # 双显示：币 + AQ
wallet info                          # 高度 + chainId + 总供给
```

## 3. 备份与恢复纪律（「永不删链」的底气）

- **链文件 = append-only JSON lines**——任何时候 `Copy-Item` 一份即完整快照，独立验证器可复核任何副本。
- **恢复** = 拷回链文件 + producer seed 在 env——重启节点即重放恢复（余额/nonce 续排）。
- **灾备三件套**：链文件定期副本 + treasury/producer 两枚种子离线 + `LedgerVerify`。
- **删链只对空链合法**：链一旦有真实余额，删除 = 销毁全部链上资产（链文件就是账本本身，不是缓存；
  钱包种子不死但新链上余额归零）。空链判据：`getInfo` 心跳空块之外无任何交易。

## 4. 排障实录（形态 → 根因 → 修法）

| 现象 | 根因 | 修法 |
|---|---|---|
| 节点反复重启（~5-8s 循环），日志反复「链已加载」**永无**「链节点已就绪」 | **producer 种子缺失/不匹配**：加载已有链必须有 env `AETHER_LEDGER_PRODUCER_SEED`（生成路径不需要——所以「新链已生成」后能就绪、重启后反而崩） | env 补上**与磁盘链同一条命**的 producer hex → 重启 |
| 升级后节点崩溃 + 日志报「旧单资产协议」 | 协议硬分叉：旧链文件不兼容 | 删链重启生成新链（⚠ 旧链资产作废——确认是测试链再删） |
| 「对暗号」：日志里多组「新链已生成」种子，哪对是真的 | 每条「新链已生成：创世 XXXX…」都带一对新种子——**只有创世哈希与当前链一致的那对是活链正主**，其余是被删链的遗物 | RPC `getInfo` 的 `genesisHash` 就是暗号；对上后取「最后一组」种子 |

**删链自愈**：崩溃循环中删掉链文件，下一轮拉起走「生成」路径（随机 producer、不需要 env）自愈就绪——
只对空链合法（见 §3）。

## 附：Windows 服务形态

节点宿主支持服务化（`AddWindowsService()`——服务名如 `AetherLedgerNode`）：SCM 启动 →
ServiceLifetime、服务停止 = 优雅关停（同步 flush 落盘）；交互运行仍 console。服务级 env 经注册表
`Environment`（MultiString）配置——同上表三个变量。
