# 架构 v2：声明式方案族

> 状态：第一阶段已落地（引擎侧）。作者 Claude，2026-11-21。

## 1. 目标

把"一个型号写一段代码"改为"一个方案族写一份数据"。新型号只增加数据：识别规则加只读读法。数据由一个经过审计的解释器执行，以后还可以经签名的型号数据包下发，不需要发新版。

## 2. 三层结构

| 层 | 内容 | 位置 |
|---|---|---|
| 方案族 | 识别规则（VID/PID 或整品牌、厂商集合、报告 ID 与长度证明）和只读读法 | `engine/families/builtin/<id>.json` |
| 能力 | 目前只有电量和充电状态；DPI 等写操作**不进入**数据，仍在代码中单独审查 | `engine/families/spec.py`（`TRANSPORTS`） |
| 型号表 | 方案族内逐项列出的设备行 | 同一 JSON 的 `devices` |

## 3. 组成

- `families/spec.py`：严格的 JSON 解析与校验。未知字段、类型或范围错误，整份方案族都会被拒绝。安全约束写在解析器里：
  - 只有 `output`、`feature`、`featureGet`、`listen` 四种读法；
  - 没有前置或收尾的会话帧；
  - 请求的首字节必须是已声明的报告 ID，长度不能超过已证明的报告长度；
  - 只接受厂商自定义 usage page，不会碰键盘、鼠标等标准集合；
  - 必须写明来源、版本、许可证和证据。
- `families/runner.py`：在已打开的设备上执行一次读法，按字节规则校验回复（帧头、电量位置和范围、充电位、休眠位）。请求被原样回显时不算读数；I/O 错误记为异常。
- `families/memory.py`：读法记忆，参照 BrimDeck 的读取安全策略重新实现：
  - 设备首次出现时最多试三轮；
  - 记住可用的读法，以后只用这一种；
  - 出现异常的读法会被停用，两种读法都停用后不再读这个设备；
  - 长期没有回应时重新试一次；
  - 用户可以关闭单个设备的读取。
- `families/processes.py`：厂商软件正在运行时让路（`standAsideFor`）。
- `families/__init__.py`：注册表。负责加载和校验方案族，提供供应商 ID 预筛、拓扑层报告证明请求（`wants_caps`），并按物理设备选出唯一可读的集合。
- `providers/families.py`：唯一的通用读取器 `FamiliesProvider`，已在 `server.PROVIDERS` 中注册。

## 4. 与现有引擎的接入点

- `server.py`：注册 `families` 读取器，`PROVIDER_VIDS["families"]` 由数据生成。
- `topology.py`：对方案族声明的身份和厂商集合计算逐报告字节数（`battery_caps`），只读。
- `known_ids.py`：直接从方案族 JSON 解析已列出的设备（不导入代码），供分诊和 issue 工具使用。
- `device_pack.py`：`families` 加入可禁用的读取器范围。
- `compatibility.json`：新增 `families` 读取器条目（C 级）。

## 5. 试读族（整品牌识别）与宿主接口（C1）

方案族可以用 `probeVendorIds` 声明整品牌识别。这类方案族只有在试读开关打开时才生效。按维护者 2026-11-21 的决定，试读**默认开启**，用户可以在设置里关闭。

**开关：** `server.py --probe-families on|off`（默认 `on`）。管理进程本身不读设备，所以不改动进程级的开关，而是在发给读取子进程的每条命令（包括拓扑命令）里带上 `probeFamilies`，由子进程调用 `families.set_probe_enabled`。子进程重启后也不会丢失这个设置。

**读法记忆：** 记忆的键是 `<方案族 id>|unit-<sha256 前 24 位>`，由 `families.memory_key(physical)` 生成，不保存原始设备路径。管理进程持有权威副本，流程如下：

1. 每次 families 查询时，管理进程把副本放进 `familyMemory` 发给子进程；
2. 子进程先 `restore`，再读设备，最后把 `snapshot` 放进响应返回；
3. 管理进程合并返回的快照。

快照里包含 `silentPolls`，这样两个子进程交替读同一设备时，长期无回应后重新试读的逻辑仍然有效。为避免完整快照超过宿主每行 512 KiB 的上限，最多记录 256 个设备。

**完整快照** 会附带 `probeFamilies` 和 `familyMemory`（只在 `complete` 时附带），供宿主写入 `family-memory.json`。

**stdin 命令** `{"command":"family_memory","op":...,"requestId":n}`，回复带 `"reply":"family_memory"`：

| op | 参数 | 作用 |
|---|---|---|
| `get` | — | 返回当前快照 |
| `set` | `familyMemory` | 启动时恢复持久化的快照，格式错误的条目会被丢弃 |
| `disable` | `deviceId`（卡片的公开 ID） | 停止读取这个设备，回复中带 `memoryKey` |
| `enable` | `memoryKey` | 恢复读取。设备被停用后不再出现卡片，所以按 `memoryKey` 恢复 |

用户的停用或恢复操作优先于子进程返回的快照：点击之前已经开始的那一轮读取，不会把用户的选择覆盖掉。

## 6. 下一步

已完成（第二阶段）：

- **同一只鼠标的有线与接收器合并（T61）**：`scripts/WirelessLayout.cs` 中加入 `NativeWiredTwins` 配对表（目前只有 A9 Air+ 接收器 `373B:1278` 对应有线 `373B:1293`）。合并条件：
  - 有线连接**唯一**；
  - 接收器读数已经过期，或者有线正在充电。

  合并后保留原生卡片的名称、位置和 DPI，电量和充电状态取当前的那份读数。两份都是当前读数、而且有线没有充电时，判断为两只鼠标，保持两张卡。拔线后自动回到接收器读数。
- **空闲退避（T15，#588）**：`server.py` 的 `_idle_backoff` 规则：设备级读取器连续两轮找到设备却没有读数，之后的自动轮询依次间隔 60、120、300 秒；有读数、强制同步、定向刷新、拓扑变化时立即恢复。被动和系统读取器不参与退避。

后续：
1. 型号数据包扩展：允许签名数据包携带方案族 JSON（只能新增族或设备行，解析规则与内置相同）。
2. ~~读法记忆持久化~~：引擎侧已完成（第 5 节，C1），宿主侧由 Codex 接入（B5 第 3.1 节）。
3. 按 B2-9 的盘点结果，批量把 BrimDeck、OpenMouse 等项目的协议转换为方案族。
4. 通用连接去重（A9 充电双卡，T61）和统一轮询退避（T15）。
5. 引擎对外接口整理为 JSON-RPC 2.0。

## 7. 首个方案族：XIBERIA

`36F9:F008`（T7，上游已做实机验证）、`F016`、`F002`、`F001`。协议来自 BrimDeck（Apache-2.0），BrimDeck 的来源是 Voltide（MIT），Voltide 的来源是官方 XIBERIA HUB：通过输出报告 `0x55` 发送 `55 65 00 00`，回复 `55 65 00 <电量>`。候选集合为 FF90:0001 或 FF82:0001，必须在同一接收器上唯一，并且证明报告 `0x55` 的输入、输出都是 64 字节。目前是 C 级，还没有本项目的实机回复。

## 8. 第三阶段：规格扩展与 BrimDeck 首批移植

**规格扩展（仍然只有读取，不放开任何写操作）：**

- `reply.checksum`：对字节区间求和后取低 8 位，必须等于指定值；
- `reply.spans`：一段字节必须落在指定范围内（例如设备型号必须是可打印 ASCII）；
- `reply.variants`：按 `when` 条件选择字段布局，并支持 `require`、`asleep`、`chargingAlways`（例如容源接收器按配对状态读取键盘或鼠标的字段）；
- `read.gate`：发送读法之前，先用同一传输方式问一次是否在线。在线才继续；离线记为休眠；没有回答则后续什么都不发；
- 方案族级 `interface`（限定接口号）和 `preferProductName`（共享接收器显示设备自报的产品名）；
- 拓扑层对方案族集合也计算 Feature 报告长度（只读的 HidP caps）。

**首批移植（来源均为 BrimDeck f1510d0，Apache-2.0；二次来源均为 MIT 项目）：**

| 方案族 | 设备 | 读法 |
|---|---|---|
| `lofree_flow` | 05AC:024F，仅产品名 `2.4G Wireless Receiver`、接口 1、FF02:0002、报告 08/17 | 先问在线（03），再读电量（04），17 字节求和须为 0x55 |
| `eweadn_beiyin` | 372E 的 1027/1029/103D/105E/106E/107B/107C/108A/108D | Feature 03/64，状态字节低 7 位为电量，bit7 为充电，0 表示休眠 |
| `rongyuan_checksum` | AJAZZ AK870、CAROTMAS Mars68、VGN Neon75 Air/Turbo/Extreme | Feature 0/65 F7 状态查询（带校验） |
| `rongyuan` | MonsGeek/Akko 驱动列表中的 60 个接收器 | 同上，裸 F7 |
| `rongyuan_mouse` | 4 个鼠标接收器 | 同上，读取鼠标字段 |

所有与现有读取器重叠的 ID 都已排除。05AC:024F 被很多键盘共用，因此不进入分诊索引（`known_ids.SHARED_UNINDEXED`）。
