# 架构 v2：声明式方案族

> 状态：第一阶段已落地（引擎侧）。作者 Claude，2026-11-21。

## 1. 目标

把"一个型号写一段代码"改为"一个方案族写一份数据"。新型号只增加数据：识别规则加只读读法。数据由一个经过审计的解释器执行，以后还可以经签名的型号数据包下发，不需要发新版。

## 2. 三层结构

| 层 | 内容 | 位置 |
|---|---|---|
| 方案族 | 识别规则（VID/PID 或整品牌、厂商集合、报告 ID 与长度证明）和只读读法 | `engine/families/builtin/<id>.json` |
| 能力 | 电量、充电状态、DPI 读取（第 10 节）；DPI 切换等写操作**不进入**数据，只能通过逐个审查的代码适配器实现（第 11 节） | `engine/families/spec.py`（`TRANSPORTS`） |
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
1. ~~型号数据包扩展~~：已完成（第 9 节，C2）。
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

## 9. 型号数据包携带方案族（C2，loader 2）

**为什么要升 loader：** 已安装的客户端只认 `loader: 1`，数据包遇到未知字段就整包拒绝。如果直接在 v1 数据包里加 `families`，所有旧客户端都会丢掉整个数据包。所以带方案族的数据包使用 **loader 2**，单独发布：

| 文件 | 内容 | 读取方 |
|---|---|---|
| `device-pack-v1-latest.json`、`device-pack-v1-<版本>.json` | `entries` 和 `disabled`，与现在相同 | 旧客户端；新客户端在 v2 不可用时回退到这里 |
| `device-pack-v2-latest.json`、`device-pack-v2-<版本>.json` | 同一来源、同一 `packVersion`，`loader: 2`，额外带 `families` | 新客户端 |

**校验分工：**

- **结构（schema）：** `loader` 只能是 1 或 2；`families` 最多 64 项，每项必须是对象（`x-family-spec`）；loader 1 带 `families` 会被整包拒绝。C# 宿主和引擎用同一份 schema，共用契约语料 `tests/fixtures/device-pack-contract.json`。
- **语义（引擎，逐行拒绝）：** 每一行先过 `spec.parse_family`，安全规则与内置方案族完全相同。以下任一情况会拒绝该行：
  - 与内置方案族 ID 相同；
  - 包内 ID 重复；
  - 设备已被内置读取器或内置方案族占用（`known_ids` 索引加 `builtin_ids`）；
  - 设备与本包的 `entries` 冲突；
  - 设备与包内其他方案族冲突；
  - `probeVendorIds` 覆盖了已有读取器的品牌（`probe_vendor_claimed`）——远程数据包不能对已有品牌开启整品牌试读，这类方案族必须作为内置方案族走审查。
- **生效：** 子进程收到新的数据包快照后，调用 `families.set_pack_families`。内置方案族始终优先，不会被覆盖。管理进程的厂商预筛由 `_provider_vids("families")` 合并数据包方案族的厂商。`disabled` 也可以指向 `families` 读取器的具体 VID/PID。
- **诊断：** `devicePack.families` 列出已生效的方案族 ID，被拒绝的行写在 `rejected` 里，例如 `families[3]: builtin_id_conflict`。

## 10. DPI 读取（C3，只读）

鼠标方案族（`kind: "mouse"`）可以声明 `dpi`。只有在本轮电量读数成功后，才用同一个打开的句柄读取 DPI。DPI 读取失败不会影响电量读数；连续 3 次失败后，本次运行内不再读取这个设备的 DPI。DPI 读数会以 `canSet: false` 发布，写入只能通过 C4 的适配器。

```json
"dpi": {
  "exchanges": [
    {"name": "table", "transport": "output", "request": "55 70",
     "reply": {"match": [{"at": 0, "eq": "55"}, {"at": 1, "eq": "70"}]}}
  ],
  "stages": {"exchange": "table", "count": {"at": 2}, "first": 4, "stride": 2, "width": 2},
  "currentStage": {"exchange": "table", "at": 3},
  "stageSource": "onboard"
}
```

| 字段 | 规则 |
|---|---|
| `exchanges` | 1–3 个，按顺序各发送一次。传输方式只能是 `output`、`feature`、`featureGet`，没有 `listen`，也没有 `gate`。请求的校验规则与电量读法相同（首字节必须是已声明的报告 ID，长度不超过已证明的报告长度）。回复只做外形校验：`match`，可选 `length`、`spans`、`checksum`。任何一个问答没有得到有效回复，这一轮就没有 DPI 读数 |
| `stages` | `count` 可以是固定值（1–8），也可以是回复中的一个字节（`{"at", "mask"}`）。第 i 档的值位于 `first + i × stride`；`width` 为 1 或 2；`endian` 为 `little` 或 `big`；`mask` 为 2 位或 4 位十六进制；`scale` 为 1–100。每档的值必须在 50–65535 之间 |
| `current` / `currentStage` | 二选一。`current` 是直接读到的 DPI 值，取值规则与 `stages` 相同；`currentStage` 是档位序号（`base` 为 0 或 1），必须落在档位表内 |
| `stageSource` | `onboard` 表示设备上的档位表（默认），`quick` 表示预设列表 |

编码复杂到 JSON 写不出来的设备（例如数值跨字节拼接、XY 分轴），由 C4 适配器的 `read_stages` 在代码里实现。

## 11. DPI 切换适配器（C4）

DPI 写操作**永远不进入 JSON**。每个允许切换 DPI 的方案族，配一个经过审查的小模块 `engine/families/dpi/<family_id>.py`，并在 `families/dpi/__init__.py` 的 `ADAPTERS` 中登记后才生效。只放文件不登记，不会生效。数据包下发的方案族永远用不了适配器：框架要求方案族对象必须就是内置方案族本身。

**适配器只需要写协议相关的部分：**

```python
REVISION = 1          # 可选，改协议时加一，旧令牌随之失效
MAX_WRITES = 1        # 可选，1 或 2；select_stage 最多发送的写报告数
MAX_QUERY_WRITES = 2  # 可选，0–4；read_stages 用来提问的写报告数上限

def select_stage(dev, info, index):
    """发送"切换到第 index 档（从 0 开始）"的命令。可以读回复；不能自己重试。"""

def read_stages(dev, info):   # 可选；不提供时使用方案族 JSON 的 dpi 块（第 10 节）
    return {"current": 1600, "stages": [400, 800, 1600, 3200]}
```

**安全规则由框架统一执行，适配器无法绕过：**

1. 只有板载档位表（`stageSource: onboard`）、档位值互不重复、当前值在档位表内时才可写；目标值必须是已有档位；
2. 令牌 = sha256（设备身份五元组 + 方案族 + 适配器 + 修订号 + 完整档位表），不包含当前值，因此切档后令牌不变；
3. 写入前重新读取：令牌必须一致，当前值必须等于用户看到的值；目标值等于当前值时不发送；
4. 适配器拿到的是计数包装后的设备对象（原始句柄不作为属性暴露），`select_stage` 写入超过 `MAX_WRITES` 次、`read_stages` 写入超过 `MAX_QUERY_WRITES` 次时直接抛异常，多余的报告不会发出；
5. 写入后读回：当前值等于目标值、档位表不变，才算成功；否则记为"结果未知"，DPI 能力立即下线，必须重新读取后才能再次操作；
6. 厂商软件正在运行时（`standAsideFor`）拒绝切档（`dpi_vendor_software`），不与官方工具抢设备；
7. 失败不重试，只在用户点击时执行。管理进程的 `set_dpi` 对身份、令牌、在线状态、新鲜度（90 秒）的检查保持不变，`families` 与 `pulsar` 一样只允许板载档位。

**接入点：**

- `server.DPI_PROVIDERS["families"]` 只在 `ADAPTERS` 非空时出现，值为这些方案族的厂商 ID；
- 读取器在电量读数之后发布 DPI 读数（`dpi`）和内部目标（`dpi_target`）；
- 模块级 `providers.families.set_device_dpi` 要求清单中恰好有一个集合与目标身份完全一致，并且该集合满足方案族的设备与报告证明；
- C# 宿主 `WirelessDpi.CanWrite` 放行 `families`，条件与 `pulsar` 相同，只允许板载档位。

**审查清单（每个适配器 PR 都要 Claude 审查，不能自行合并）：**

- 协议来源与许可证；
- `select_stage` 只发送切档命令，不涉及保存到闪存、配置文件或灯光；
- 不 `import hid`/`ctypes`，不重新打开 `info["path"]`，只通过传入的 `dev` 收发；
- 写报告数与 `MAX_WRITES` 一致；
- 回放测试覆盖：成功、读回不一致、令牌过期、当前值已变化。

