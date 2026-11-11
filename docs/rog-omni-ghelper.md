# ROG OMNI（0B05:1ACE）电量协议静态分析

## 1. 结论摘要

- 可以提出鼠标和部分键盘的「仅查询、不改配置」读取器候选，但现有证据不足以确认真实设备可用；不能只把 `1ACE` 加进现有 PID 表。（`PeripheralsProvider.cs:636-775`、`794-805`）
- 鼠标候选：`MI_02&Col03`、报告 `03`、64 字节、`12 07`；电量 `[5]`，充电 `[10] > 0`。（`Mouse/AsusMouse.cs:782-803`；各 OMNI 型号见 2.2）
- 键盘候选：`MI_02&Col02`、报告 `02`、64 字节、`12 01`；Azoth / Scope II 96 电量 `[6]`，Falchion RX LP 为 `[11]`，充电均为 `[9] == 1`。（`Keyboard/AsusKeyboard.cs:409-444`；型号见 2.3）
- 配对表查询在 `MI_02&Col01` 发送 `01 A0 00 00`，按下游 PID 选型号；该集合的 usage page / usage、完整报告长度及回复头均未确认。（`PeripheralsProvider.cs:638-678`、`729-775`）
- 还缺逐报告描述符、底层传输及冷启动独立查询证明、离线/休眠实包；只有 #472 有官方电量数值，#506 没有。（`issue-472.txt:119-188`；`issue-506.txt:81-95`）
- 不能直接复用完整连接流程：Azoth 可条件性写 OLED 时钟，键盘连接后可写灯光；这些不属于电量查询。（`Keyboard/Models/Azoth.cs:103-152`；`PeripheralsProvider.cs:312-320`）

## 2. 七个问题的逐条回答

### 引用与字节约定

G-Helper 文件引用均相对于 `.ai/evidence/g-helper/0bda6b55317f/app/Peripherals/`；issue 文件相对于 `.ai/evidence/rog-omni/issues/`。`[n]` 一律为源码数组的零基下标，`[0]` 包含源码传入的 report ID；不是从命令回显后再计数。`00 × n` 表示精确重复 n 个零字节。型号表中的「64」是源码应用缓冲区长度，不冒充用户设备的描述符证明。

本分析只读给定证据和仓库实现，没有编译、运行 G-Helper，没有访问 HID，也没有联网。源码中 `Write` / `Read` 的调用事实与底层 HID API 的证明分开陈述：快照不包含 `WindowsUsbProvider` 或 `AnimeMatrix.Communication.Device` 的实现，不能确定继承的 `Write` 最终使用哪个 Windows API。（`Mouse/AsusMouse.cs:571-574`、`628-653`；`Keyboard/AsusKeyboard.cs:26`、`159-186`、`335-346`；快照清单 `SHA256SUMS.txt`）

### 2.1 识别：接收器、集合与下游设备

G-Helper 枚举 VID `0B05`；鼠标检测入口先执行 `DedectOmniMouse`，后者也识别键盘。它不是把 `1ACE` 当成某一固定鼠标型号。（`PeripheralsProvider.cs:17`、`476-483`、`636-678`）

| 用途 | G-Helper 路径筛选 | usage page / usage 的证据 | 请求首字节 | 源码长度事实 |
| --- | --- | --- | --- | --- |
| 配对表 | `MI_02&Col01` | 两者均未确认；不能把余下的 `FF02` 或 `FFC0` 自动配给它 | `01` | 直接 `HidStream.Write` 一个 4 字节数组；分配 64 字节接收缓冲区，未核对实际返回长度 |
| 鼠标命令 | `MI_02&Col03` | 注释称 `FF01`；usage 未确认 | `03` | OMNI 型号返回 64；公共请求函数按该长度扩展数组 |
| 键盘命令 | `MI_02&Col02` | 注释称 `FF00`；usage 未确认 | `02` | OMNI 型号返回 64；公共请求函数按该长度扩展数组 |

依据：`PeripheralsProvider.cs:638-673`、`681-708`；`Mouse/AsusMouse.cs:628-653`；`Keyboard/AsusKeyboard.cs:335-346`；型号定义见 2.2、2.3。鼠标和键盘确实走不同集合；上述注释没有证明这些集合的 usage 必为 `0001`，issue 中的 `FF01:0001` / `FF00:0001` 也未逐路径绑定到 Col03 / Col02。

识别交换与解析：

1. 在 Col01 上发出的完整应用调用数组是 `01 A0 00 00`。这里没有 `Array.Resize`，故不能宣称实际 Output 报告就是 4 字节，也不能自行补成 64 字节。读写超时各设为 2000 ms；源码直接读一次，无重试循环。（`PeripheralsProvider.cs:666-678`）
2. 接收缓冲区从 `[5]` 起按 4 字节步进。每项前两字节为小端 PID：低字节在 `[offset]`，高字节在 `[offset+1]`；PID 为 `0000` 即停止。每项后两字节含义未确认，回复 `[0..4]` 含义、回显规则、实际 report ID、有效项数和实际报告长度均未确认。日志只打印 12 字节，不等于协议只有三项；循环实际以缓冲区长度为界。（`PeripheralsProvider.cs:671-678`、`729-764`）
3. 鼠标、键盘各独立扫描并取第一个受支持 PID。没有已证实的「设备类型字段」；类型来自两张 PID 映射表。未知 PID 被跳过，不能猜类型或电量布局；不能据此支持同一接收器后的任意多个鼠标或任意多个键盘。（`PeripheralsProvider.cs:729-775`、`794-805`）
4. Col01、Col03 通过 `OmniInstance` 配对：取路径按 `#` 分隔的第三段，去掉其最后一个 `&` 后缀再比较；键盘 Col02 也按此比较。这是源码路径关联方法，不是已经取得的 USB parent 证据。另一个实现限制是 Col03 缺失时直接返回，连键盘配对表也不查。（`PeripheralsProvider.cs:638-656`、`681-687`）
5. G-Helper 在鼠标连接前另发 `03 7D 20 02`，以回复 `[5] == 01` 设置宿主对象的 `Booster` 属性。没有看到它修改硬件的明确代码，但仅凭名字与赋值不能确认命令纯查询；也没有证明省略它后冷接收器必然回答电量。最小方案排除它。（`PeripheralsProvider.cs:700-712`；`Mouse/Models/KerisIIOrigin.cs:31-39`）

因此，「G-Helper 先查询配对设备」是已确认的调用顺序；「电量协议必须先查配对表才能工作」未确认。配对表至少用于避免把不同键盘型号的 `[6]` / `[11]` 混用，但没有实包能证明其初始化必要性。（`PeripheralsProvider.cs:671-712`；`Keyboard/Models/Falchion.cs:27-31`）

### 2.2 鼠标电量

#### 请求、回复和现有实现的区别

所有本节 OMNI 鼠标继承同一套电量命令与解析。源码发送的完整 64 字节数组为 `03 12 07` 加 `00 × 61`，即 `[0]=03`、`[1]=12`、`[2]=07`、`[3..63]=00`。常规成功回复要求前三字节回显 `03 12 07`；电量取 `[5]`，充电取 `[10] > 0`。电量命令的默认 `matchLength` 为 3，不要求其他字节回显。（`Mouse/AsusMouse.cs:614`、`628-653`、`676-691`、`782-803`、`844-857`；OMNI 构造与包长见下表）

| 项目 | 当前 `engine/providers/asus.py` | G-Helper OMNI 鼠标 |
| --- | --- | --- |
| 接口 / 集合 | interface 0，usage page 至少 `FF00` | `MI_02&Col03`，注释称 `FF01`，usage 未确认 |
| 请求 | `00 12 07` + 62 个零，共 65 字节 | `03 12 07` + 61 个零，共 64 字节 |
| 电量 | 回显 `12 07` 起点为 m，取 `m+4` | 带 ID 数组 `[5]`，同样等于回显起点 `1+4` |
| 充电 | `m+9 != 0` | 带 ID 数组 `[10] > 0`，等于 `1+9` |
| ID 兼容 | 只接受回显直接位于 `[0]` 或前置 `00` | 命令回显通常要求首字节为 `03` |

依据：现有实现 `engine/providers/asus.py:40-51`、`95-131`、`158-175`、`200-205`；G-Helper `PeripheralsProvider.cs:683-684`、`Mouse/AsusMouse.cs:630`、`676-678`、`782-803`。所以本批 OMNI 鼠标**没有电量/充电字段的额外偏移变化**；变化是路由、非零 report ID 与应用包长。现有 `_offset` 不能直接接受 `03 12 07`。仓库既有记录也明确排除了 OMNI，且没有 ASUS 硬件验收。（`engine/upstream-protocols.md:1118-1122`）

#### 逐型号与配对 PID

下表覆盖七个引用 `1ACE` 的鼠标文件中的全部九个 OMNI 类；均为 `0B05:1ACE` / `MI_02&Col03` / `03`，电量均继承 `[5]` 的百分比，充电均继承 `[10] > 0`。没有一行覆盖了将电量乘 25 的方法，不能因为某个原型名称相近而套用旧型号的 `0..4` 档位。共同解析依据：`Mouse/AsusMouse.cs:787-803`；配对 PID 依据：`PeripheralsProvider.cs:794-805`。

| OMNI 类 / 型号 | 配对表中的 PID（不是接收器 PID） | 应用包长 | 电量 / 充电 / 尺度 | 型号与包长依据 |
| --- | --- | --- | --- | --- |
| KerisIIOriginOmni | `1C0E` | 64，继承父类 | `[5]` / `[10] > 0` / 百分比 | `Mouse/Models/KerisIIOrigin.cs:4-12`、`119-136` |
| KerisIIOriginKJPOmni | `1D4E` | 64，同上 | 同上 | `Mouse/Models/KerisIIOrigin.cs:119-123`、`150-159` |
| HarpeAceAimLabEditionOmni | `1A94` | 64，本类覆盖 | 同上 | `Mouse/Models/HarpeAceAimLab.cs:4-12`、`157-171` |
| HarpeAceExtremeOmni | `1B68`、`1B69` | 64，本类覆盖 | 同上 | `Mouse/Models/HarpeAceAimLab.cs:191-209` |
| KerisAceIIOmni | `1B1A`、`1B18` | 64，本类覆盖 | 同上 | `Mouse/Models/KerisIIAce.cs:4-12`、`104-118` |
| StrixImpactIIIWirelessOmni | `1AD7` | 64，本类覆盖 | 同上 | `Mouse/Models/StrixImpactIIIWireless.cs:4-9`、`100-103` |
| HarpeAceMiniOmni | `1B65` | 64，继承父类 | 同上 | `Mouse/Models/HarpeAceMini.cs:4-13`、`110-125` |
| KerisWirelssAimpointOmni（源码拼写） | `1A68`、`1A6A` | 64，本类覆盖 | 同上 | `Mouse/Models/KerisWirelssAimpoint.cs:4-16`、`121-135` |
| GladiusIIIAimpointOmni | `1A72` | 64，本类覆盖 | 同上 | `Mouse/Models/GladiusIIIAimpoint.cs:4-16`、`155-169` |

这些是源码选用的布局，不是九款设备的硬件测量。继承的 `ParseBattery` 没有限定上限 100；百分比范围校验只能作为候选读取器的拒绝规则，不是已有 OMNI 实包结论。（`Mouse/AsusMouse.cs:787-794`）

#### 回复判定的细节

- `FF AA`：初次读后若 `[1]=FF`、`[2]=AA`，`WriteForResponse` 返回错误数组，不是返回 null；`ParseBattery` 因不匹配 `12 07` 返回 `-1`，随后不发布有效电量。（`Mouse/AsusMouse.cs:595-598`、`656-664`、`787-803`、`854-867`）
- 空回复：源码实际只测 `[1]`、`[2]`、`[3]` 是否都为零，并非逐字节验证「整包全零」；满足即返回 null。真正全零也会命中，但不能把这个分支视为已验证的离线帧格式。（`Mouse/AsusMouse.cs:667-674`）
- 非匹配报告：继续读直到回显吻合；该内层循环没有次数上限，而且其后每一包没有重新走前面的错误/空包分支。超时最多触发三次发送尝试，I/O 异常或对象关闭则断连并返回 null；不能把整个函数描述为有严格总时限。（`Mouse/AsusMouse.cs:637-713`）

### 2.3 键盘电量

公共命令为 `reportId 12 01`，扩展到型号的 `USBPacketSize()`。在 OMNI 上是完整 `02 12 01` + `00 × 61`；普通类通常是 `00 12 01` + `00 × 62`，应用长度 65。公共解析只检查 `[1..2] == 12 01`，没有像鼠标那样确认回复 `[0]` 的回显，也没有处理其他报告的读循环。`FF AA` 返回 null；其他回复交给 `ReadBattery` 判断。（`Keyboard/AsusKeyboard.cs:302-357`、`409-444`）

成功回复的公共字段为：电量默认 `[6]`；充电仅当 `[9] == 01`，不是「任何非零」；`[7]` 是休眠超时设置，只有值 `0..4` 或 `FF` 才记录，它不是电量或在线状态。低电量告警阈值不从该回复读取。（`Keyboard/AsusKeyboard.cs:399-444`）

| 型号 / 路线 | 非 OMNI 源码路径与应用长度 | OMNI 配对 PID / 类 | OMNI 路径、ID、长度 | 电量 / 充电及依据 |
| --- | --- | --- | --- | --- |
| Azoth、Azoth Wireless | `MI_01`、ID `00`、65 | `1A85` / AzothOmni | `MI_02&Col02`、`02`、64 | `[6]` 百分比；`[9] == 1`。`Keyboard/Models/Azoth.cs:3-24`、`192-200`、`232-246` |
| Azoth Extreme | `MI_01`、ID `00`、65 | `1B42` / AzothExtremeOmni | 继承 AzothOmni | 同上。`Keyboard/Models/Azoth.cs:162-177`、`249-256` |
| Azoth Extreme SE | `MI_01`、ID `00`、65 | `1CF1` / AzothExtremeSEOmni | 继承 AzothExtremeOmni | 同上。`Keyboard/Models/Azoth.cs:180-188`、`259-264` |
| Falchion / Falchion Wireless / Falchion RX | 前两者 `MI_01`；RX 默认构造未固定路径，可走公共候选搜索；ID `00`、65 | `1B06` / FalchionRXLowProfileOmni | `MI_02&Col02`、`02`、64 | **`[11]` 才是百分比**，`[6]` 注释为 `0..10` gauge，不用它换算；充电仍 `[9] == 1`。`Keyboard/Models/Falchion.cs:3-31`、`39-63`、`115-129` |
| Strix Scope II 96 Wireless | `MI_01`、ID `00`、65 | `1AB0` / StrixScopeII96WirelessOmni | `MI_02&Col02`、`02`、64 | `[6]` 百分比；`[9] == 1`。`Keyboard/Models/StrixScopeII.cs:41-63`、`93-107` |
| Strix Scope II 96 RX Wireless | `MI_01`、ID `00`、65 | `1B7A` / StrixScopeII96RXWirelessOmni | 继承上一行 | 同上。`Keyboard/Models/StrixScopeII.cs:76-89`、`110-120` |

表内公共 65、ID 默认值及充电规则依据：`Keyboard/AsusKeyboard.cs:35`、`51-54`、`302-309`、`411-444`；全部键盘配对映射依据：`PeripheralsProvider.cs:767-777`。这些 OMNI 子类没有覆盖电量解析方法，因此已列型号经 OMNI 时只是集合、ID、长度变化，字段不变。

需要分开的例外：

- 名为 `StrixScopeII` / `StrixScopeIIRX` 的普通类并不是表里的 96 Wireless 类；它们继承公共 `HasBattery=false`，不能因为文件同名就声称有无线电量。（`Keyboard/Models/StrixScopeII.cs:3-39`；`Keyboard/AsusKeyboard.cs:69-72`）
- Falchion Ace / Ace HFX 明确 `HasBattery=false`，不能并入 Falchion RX LP OMNI。（`Keyboard/Models/Falchion.cs:81-112`）
- `AzothX`、`AzothXWireless` 确有类，PID 为 `1C24`、`1C25`，路径 `MI_01` / ID `00`；但配对映射没有这两个 PID，也没有 Azoth X OMNI 子类。夜魔 X 通过 `1ACE` 的覆盖未确认。（`Keyboard/Models/Azoth.cs:204-229`；`PeripheralsProvider.cs:767-777`）
- 无固定路径时，公共键盘搜索优先 page `FF00`，再尝试其他最大 Output 长度至少 64 的集合，多候选时发 `reportId 12 00` 探测。这是上游的宽泛 fallback，不是本任务建议的安全开设备条件；OMNI 显式设置路径时会绕过该描述符筛选。（`Keyboard/AsusKeyboard.cs:107-135`、`159-207`；`PeripheralsProvider.cs:685-692`）

### 2.4 离线、休眠、未配对：哪些不是 0%

没有任何所给 issue 提供 OMNI 原始请求/回复，因此下表是 G-Helper 的处理逻辑，不是关机、休眠、未配对三种硬件状态的已验证一一对应。（`issue-472.txt`、`issue-506.txt` 及其余十二份文本；源码依据逐行列出）

| 观察 / 值 | G-Helper 行为 | 能否解释为 0% |
| --- | --- | --- |
| 鼠标 `[1..3]` 全零 | 返回 null，`ReadBattery` 立即返回，不清旧读数 | 不能。`Mouse/AsusMouse.cs:667-674`、`851-852` |
| 鼠标 `FF AA` | 解析为电量 `-1`、非充电，标记未就绪，不发电量更新事件 | 不能。`Mouse/AsusMouse.cs:656-664`、`787-803`、`854-873` |
| 鼠标有效回显，电量 0 | 日常 `ReadBattery` 以 `Battery > 0` 判就绪，连「0 且充电」也不发电量更新 | 不能直接发布。`Mouse/AsusMouse.cs:854-873` |
| 鼠标同步时 `Battery <= 0` 且不充电 | 注释称可能只有接收器、鼠标关机或睡眠，标记未就绪并中断同步 | 不能；这只是可能原因，不区分三种状态。`Mouse/AsusMouse.cs:728-739` |
| 键盘 `FF AA` / 通信异常 | 返回 null，电量读取立即返回；仍能枚举到接收器时不一定断连 | 不能。`Keyboard/AsusKeyboard.cs:312-357`、`420-421` |
| 键盘无 `12 01` 回显（包括全零） | Battery 为 `-1`，Charging=false，未就绪 | 不能。`Keyboard/AsusKeyboard.cs:423-437` |
| 键盘有效 `12 01`，电量字段 0 | 按回显而非电量判断就绪，源码会保留 0 并发更新事件 | 源码允许，真实 OMNI 的 0 是空电还是离线占位未确认；不能套用鼠标规则。`Keyboard/AsusKeyboard.cs:423-440` |
| 配对表 PID `0000` | 停止扫描；未识别出型号就不创建对应对象 | 这是列表终止，不是 0% 或已证实的在线位。`PeripheralsProvider.cs:729-764` |

另一个容易混淆的细节：鼠标 `SynchronizeDevice` 只在「不充电且非正电量」时返回，随后会 `SetDeviceReady(true)`；因此「0 且充电」在同步流程和日常 `ReadBattery` 的就绪处理不同。现有 `asus.py` 又只拒绝「0 且不充电」，三者不能笼统说成同一条规则。（`Mouse/AsusMouse.cs:728-739`、`859-867`；`engine/providers/asus.py:119-131`）

连接层最多三次同步尝试，间隔各 250 ms；OMNI 键盘仍未就绪则被跳过并 Dispose。鼠标没有相同的 OMNI 未就绪跳过条件。后台常规电量刷新至少间隔 20 s，未就绪设备会再次同步。这些都没有给出真实睡眠/未配对帧，也不能用旧 Battery 属性值证明本轮读到电量。（`PeripheralsProvider.cs:174-193`、`232-246`、`280-294`）

### 2.5 命令安全性：查询与状态写入分离

这里「只读读取器」指**允许发送查询请求，但不修改配置/灯光/配对等状态**，不是绝对零写入。下表是识别与电量路径上逐条应用写入；「源码作为查询」不等于已证明固件绝不唤醒或改变内部状态。

| 写入 | 出现位置 / 是否保留在最小候选中 | 安全性判断与依据 |
| --- | --- | --- |
| `01 A0 00 00`，Col01；完整报告长度未确认 | 用于首次识别；完整交换条件尚不齐备 | 源码作为配对表查询，没有配对动作；回复只用于选 PID。`PeripheralsProvider.cs:666-678`、`729-775` |
| `03 12 07` + `00 × 61`，Col03 | 鼠标电量，保留 | 源码作为电量/电源设置读取，不调用设置函数。`Mouse/AsusMouse.cs:782-803`、`844-891` |
| `02 12 01` + `00 × 61`，Col02 | 键盘电量，保留 | 源码作为电量/休眠设置读取，不调用设置函数。`Keyboard/AsusKeyboard.cs:409-444` |
| `03 7D 20 02`，Col03；直接短数组写入 | 上游发现鼠标时会发；最小候选不发 | 只看见读取 Booster 属性的用途，纯查询语义、完整报告长度及可否省略均未确认。`PeripheralsProvider.cs:700-712` |
| `02 12 00` + `00 × 61` | 多候选接口探测、读取键盘 profile；非最小电量所需 | 源码作为版本/配置读取，不能沿用它跨集合探测。`Keyboard/AsusKeyboard.cs:190-207`、`815-834` |
| `02 12 12` + `00 × 61` | 连接后的布局读取；不发 | 源码作为工厂布局查询，不参与电量解析。`Keyboard/AsusKeyboard.cs:360-371`；`PeripheralsProvider.cs:312-320` |
| `03 12 12 02` | 只在注释掉的串号读取代码中；不发 | 不能当作实际执行或必需握手。`PeripheralsProvider.cs:714-720` |

公共鼠标 `SynchronizeDevice` 先读电量，再读 profile、DPI、轮询率、灯光等；这些额外读取不在最小候选中。公共键盘同步只读电量；`RestoreProfile` 只是读宿主配置到属性，并未发切换 profile 命令。（`Mouse/AsusMouse.cs:728-750`；`Keyboard/AsusKeyboard.cs:210-215`、`290-300`、`810-813`）

#### 🔴 必须排除的状态写入

每行红色标记表示会改变设备状态，而不是查询。前六行尤其容易因复用完整连接流程而混入；后续行用于区分邻近 setter 与 getter。以下都不被 `ReadBattery` 直接调用；从调用关系看可以只发查询而不发它们，但冷设备在省略完整初始化时能否正确回复仍未确认，不能给出硬件保证。

| 状态写入（参数按源码，不列为可执行方案） | 为什么危险 / 不发是否仍有查询路径 | 依据 |
| --- | --- | --- |
| <span style="color:red">🔴 `02 63 00 00 00 <format> <yearLE:2> <month> <day> <hour> <minute>`，余量补零</span> | 设置 OLED 时间；Azoth `Connect` 在 OledClock 启用时会调用，且设定定时器继续写。独立电量查询不调用它 | `Keyboard/Models/Azoth.cs:103-143`、`148-152` |
| <span style="color:red">🔴 `02 6A 00 00 00 01 01`，余量补零</span> | AMOLED 时钟屏启用；时钟流程的 `ShowAmoledScreen(1,true)` 首条写入；不是电量前提 | `Keyboard/Models/Azoth.cs:44-47`、`109-111`、`249-256` |
| <span style="color:red">🔴 `02 6A 01 00 00 01`，余量补零</span> | 同一流程选择时钟屏；不是电量前提 | `Keyboard/Models/Azoth.cs:44-47`、`109-111` |
| <span style="color:red">🔴 `02 51 2C ...`（含分块灯效）</span> | 写键盘灯效；连接后或就绪变化时可能同步/恢复灯光。电量查询不需要 | `PeripheralsProvider.cs:312-320`、`421-424`；`Keyboard/AsusKeyboard.cs:615-687`、`850-860`、`888-895` |
| <span style="color:red">🔴 `02 C0 81 <count> 00 ...`</span> | 写逐键颜色；恢复 Direct 灯光可能走此路径。电量查询不需要 | `Keyboard/AsusKeyboard.cs:753-780`、`888-895` |
| <span style="color:red">🔴 `02 50 55`，余量补零</span> | 保存键盘灯光；Aura 同步成功后调用。电量查询不需要 | `Keyboard/AsusKeyboard.cs:789-793`、`850-860` |
| <span style="color:red">🔴 `02 69 00 00 00 <enable>`</span> | OLED 开关，不是电量查询 | `Keyboard/Models/Azoth.cs:57-64` |
| <span style="color:red">🔴 `02 68 00 00 00 <brightness>`</span> | OLED 亮度，不是电量查询 | `Keyboard/Models/Azoth.cs:67-76` |
| <span style="color:red">🔴 `02 61 00 00 00 <index>`</span> | OLED 动画选择，不是电量查询 | `Keyboard/Models/Azoth.cs:79-88` |
| <span style="color:red">🔴 `02 6A 01 00 00 00`</span> | AMOLED 动画流程可追加选择屏幕 0，不是电量查询 | `Keyboard/Models/Azoth.cs:44-47`、`79-88` |
| <span style="color:red">🔴 `02 51 00 00 00 <profile>`</span> | 真正切换键盘 profile；不能与 `12 00` 查询混淆 | `Keyboard/AsusKeyboard.cs:837-847` |
| <span style="color:red">🔴 `02 51 37 00 00 <lowBatteryWarning>`</span> | 改键盘低电量阈值，不是读取 | `Keyboard/AsusKeyboard.cs:399-406` |
| <span style="color:red">🔴 `02 51 38 00 00 <powerOff>`</span> | 改键盘休眠超时，不是读取 | `Keyboard/AsusKeyboard.cs:399-406` |
| <span style="color:red">🔴 `03 50 02 <profile>`</span> | 切换鼠标 profile；电量查询不调用 | `Mouse/AsusMouse.cs:933-935`、`966-983` |
| <span style="color:red">🔴 `03 50 03`</span> | Flush / 保存鼠标设置；电量查询不调用 | `Mouse/AsusMouse.cs:2249-2258` |
| <span style="color:red">🔴 `03 51 37 00 00 <powerOff> 00 <warning>`</span> | 改鼠标省电/阈值；电量查询不调用 | `Mouse/AsusMouse.cs:824-841` |
| <span style="color:red">🔴 `03 51 31 09 00 <dpiProfile>`</span> | 切 DPI 档；电量查询不调用 | `Mouse/AsusMouse.cs:1386-1388` |
| <span style="color:red">🔴 `03 51 31 0A 00 <count>`</span> | 改 DPI 档数；电量查询不调用 | `Mouse/AsusMouse.cs:1381-1384` |
| <span style="color:red">🔴 `03 51 31 <profile-1> 00 <dpiLE:2> [RGB]`</span> | 修改 DPI 参数；电量查询不调用 | `Mouse/AsusMouse.cs:1424-1442` |
| <span style="color:red">🔴 `03 51 28 ...`</span> | 修改鼠标灯光，含直接颜色流；电量查询不调用 | `Mouse/AsusMouse.cs:2067-2088`、`2190-2210` |

这些是与连接/读取流程及易混淆设置相关的风险清单，不是 G-Helper 所有配置功能的协议目录；不允许据表外命令继续探测。已检查的识别、电量及相关型号连接路径没有明确要求配对、刷固件、唤醒或开启心跳的命令；这些操作的字节未确认，不编造，也不加入最小交换。底层 Provider 不在快照内，是否有隐式初始化，以及查询是否会触发固件唤醒，仍未确认。（`PeripheralsProvider.cs:636-727`；`Mouse/AsusMouse.cs:486-489`、`728-750`、`844-891`；`Keyboard/AsusKeyboard.cs:210-215`、`290-300`、`416-444`；`Keyboard/Models/Azoth.cs:148-152`）

### 2.6 用户描述符对照与缺口

issue 中给的是按设备汇总的 `usage page:usage` 列表，不是 Report Descriptor。它们能证明出现过下列集合标识，不能证明某个 Col 的报告类型、ID 或长度：

| 文本中的 1ACE 集合组合 | issue 依据 | 与 G-Helper 的关系 |
| --- | --- | --- |
| `FF00:0001 FF01:0001 FF02:0001 FFC0:0001` | `issue-156.txt:29`、`issue-168.txt:27`、`issue-216.txt:28`、`issue-245.txt:27`、`issue-246.txt:27,61`、`issue-320.txt:27`、`issue-492.txt:28`、`issue-501.txt:36`；`issue-506.txt:112-113` | 出现了候选键盘/鼠标 page，但没有 MI/Col 的逐项对应 |
| `FF00:0001 FF01:0001`，另有 `0059:0001` | `issue-131.txt:28`（0059 重复两次）、`issue-472.txt:31,68,103,147,209`、`issue-483.txt:27` | 不能要求必须还存在 `FF02` / `FFC0`；也不能认定 0059 就是配对表集合 |
| `FF00:0001 FF01:0001 FF02:0001`，另有 `0059:0001` | `issue-76.txt:27`、`issue-517.txt:28` | 集合组合不同，原因未确认 |
| `FF00:0001 FF01:0001 FF02:0001`，没有列出 0059 / FFC0 | `issue-506.txt:44-45,65-66` | 同一 issue 后来的汇总变为含 FFC0；没有足够证据解释变化 |

G-Helper 的明确通道路由仍只是注释所说 Col02=`FF00`、Col03=`FF01`；Col01 的 page / usage 未确认。`FF02:0001`、`FFC0:0001` 没有被源码确认是本次应发送电量的集合，不能作为 fallback。（`PeripheralsProvider.cs:638-673`、`683-687`）

#472 的硬件 ID 有 `MI_01`，但同一段标的是 `UP:0001_U:0002` / 系统鼠标；不是 `MI_02` 厂商命令集合的路径。它既不能证明 G-Helper 路由错误，也不能证明该机器具有所需 `MI_02&Col01/02/03`。（`issue-472.txt:169-178`）

14 份文本均未给出目标集合的 Input / Output / Feature 报告长度或逐 ID 报告表。具体缺少：

- 每个物理接收器的完整路径、USB parent、接口号、Col 号与 page / usage 的一一对应；#506 重复的两行 `1ACE` 无法据此判断物理数量或归属。（`issue-506.txt:44-45,112-113`）
- Col01 的 Output `01` 完整长度、Input 的实际 ID / 完整长度、回复头与每项剩余两字节；64 字节接收数组不提供这些证明。（`PeripheralsProvider.cs:671-678`、`729-764`）
- Col03 的 Output / Input `03` 是否确为含 ID 的 64 字节，Col02 的 Output / Input `02` 是否如此；以及底层调用是否确走 Input / Output 而非某种封装的 Feature。（`Mouse/AsusMouse.cs:571-574`、`628-653`；`Keyboard/AsusKeyboard.cs:159-186`、`335-346`）
- Feature 有无相关报告、ID / 长度均未确认。给定电量调用点没有显式 `GetFeature` / `SetFeature`，不能因为报告长度相近就改走 Feature。（同上；`SHA256SUMS.txt`）

### 2.7 十四份反馈中的下游型号

只把用户实际写出的型号或诊断显示的设备列出；没有下游信息就写未提供。同机其他厂商设备、独立耳机和 Bluetooth 卡片不自动算 OMNI 下游。唯一明确的中英文型号对照包括 #472 的 Harpe Ace、#501 的 Azoth X、#506 的 Keris II Origin；其余中文简称不强行等同上游某个英文类。（`issue-472.txt:165-188`；`issue-501.txt:12-15`；`issue-506.txt:83-95`）

| issue | 用户型号 / 关联限制 | 鼠标或键盘 | 官方电量数值对照 | 给定 G-Helper 快照中的型号文件与 OMNI 匹配情况 |
| --- | --- | --- | --- | --- |
| #76 | 未明确写 OMNI 下游；描述请求适配狼蛛 SC580，诊断另有 `ROG STRIX SCOPE II 96 RX WL` Bluetooth 卡片 | 卡片为键盘；接收器后设备未确认 | 无；37% 是本程序 Bluetooth 读数 | `Keyboard/Models/StrixScopeII.cs:76-120` 有对应 96 RX Wireless / OMNI 类，但卡片不能证明绑定 OMNI。依据 `issue-76.txt:10,21,27-28` |
| #131 | 未提供；描述是 CHERRY MW5500，另有独立 DELTA II | OMNI 下游类别未确认 | 无 | 无从选择 OMNI 型号文件。MW5500 不能归到 1ACE。依据 `issue-131.txt:10,27-29` |
| #156 | 未提供；描述只是反馈公开提示 | 未确认 | 无 | 无从选择 OMNI 型号文件；已显示的 MCHOSE / Xbox 不是下游证明。依据 `issue-156.txt:10,21-29` |
| #168 | ROG 夜魔 | 键盘 | 无 | 有 `Keyboard/Models/Azoth.cs` 家族文件；未给英文型号/PID，具体变体及 OMNI 映射未确认。依据 `issue-168.txt:10,27` |
| #216 | ROG 夜魔 | 键盘（同系列简称，具体变体未确认） | 无 | 同上，不指定到某个 Azoth 类。依据 `issue-216.txt:10,28` |
| #245 | 夜魔 x，接收器连接 | 键盘 | 无 | 借 #501 的中英文对照，`Keyboard/Models/Azoth.cs:204-229` 有 Azoth X / X Wireless；**没有 X OMNI 映射**。依据 `issue-245.txt:10,27`、`issue-501.txt:12` |
| #246 | ROG 夜魔 x，2.4G；设备 1.00.25、接收器 7.00.08 | 键盘 | 无，版本号不是电量 | 同上。依据 `issue-246.txt:10,42-44,61`；映射缺口见 `PeripheralsProvider.cs:767-777` |
| #320 | ROG 夜魔 | 键盘（具体变体未确认） | 无 | 有 Azoth 家族文件，具体 OMNI 类未确认。依据 `issue-320.txt:10,27` |
| #472 | ROG 龙鳞 Ace Aim Lab 合作版 / ROG Harpe Ace Aim Lab Edition，Omni 2.4G | 鼠标 | **有**：先补充 77%，后明确 Gear Link 为 76%；不是同时采集的协议对照包 | `Mouse/Models/HarpeAceAimLab.cs:157-171` 有 Omni 类，配对 PID `1A94`。依据 `issue-472.txt:119-126,165-188`；`PeripheralsProvider.cs:799` |
| #483 | ROG 夜魔 ex | 键盘（英文变体未确认） | 无 | 有 Azoth / Extreme / Extreme SE 家族类，但没有中文 ex 到具体类的证据，不能直接等同 Extreme。依据 `issue-483.txt:10,27`；`Keyboard/Models/Azoth.cs:162-188,232-264` |
| #492 | ROG 夜魔 20 周年；同帖还有 ATK ZERO | 键盘；ATK 为另一鼠标 | 无 | 有 Azoth 家族类，但未查到明确同名 20 周年类/映射；不能推成 Extreme SE。依据 `issue-492.txt:10,28-30`；`Keyboard/Models/Azoth.cs:162-264` |
| #501 | ROG 夜魔 X / ROG Azoth X；ROG Pelta 用另一个接收器 | 键盘；耳机非本次 OMNI 下游证明 | 无 | `Keyboard/Models/Azoth.cs:204-229` 有 X / X Wireless；不在 OMNI 键盘映射。依据 `issue-501.txt:12-15,36-37`；`PeripheralsProvider.cs:767-777` |
| #506 | ROG 月刃 2 RGB / **ROG KERIS II ORIGIN**；ROG 游侠 2 RX **98** 无线版；DELTA II 为独立接收器 | 鼠标 + 键盘；哪一台 1ACE 对应哪台设备未确认 | **无**；只写「官方界面需要分别显示电量」 | `Mouse/Models/KerisIIOrigin.cs:125-136` 有 Omni 类；`Keyboard/Models/StrixScopeII.cs:76-120` 只有 **96** RX 类，未查到明确 98 型号，不能擅改用户型号。依据 `issue-506.txt:83-95,112-114` |
| #517 | ROG 龙鳞 ace；EVO80 有独立 `36B0:3002` 接收器 | 鼠标；EVO80 键盘不能归到 OMNI | 无 | `Mouse/Models/HarpeAceAimLab.cs` 有 Aim Lab / Extreme 类；简称没有明确变体，不能直接指定配对 PID。依据 `issue-517.txt:10,28-29` |

表内「有文件」不等于已证明该用户设备被支持。特别是 X、ex、20 周年、98 和仅写 ace 的简称，需要真实配对 PID 与准确型号对应，不能仅凭同家族命令相似而扩大覆盖。（`PeripheralsProvider.cs:767-777`、`794-805`；上述 issue）

## 3. 建议的最小只读交换

这是供后续决策的**条件化方案，不是可立即执行的设备探测步骤**。未知项仍为未确认，尤其管理集合与底层传输未齐；本任务没有打开设备或验证下列交换。

### 3.1 打开数据读写句柄前的描述符门槛

先由元数据发现证明准确 VID:PID=`0B05:1ACE`、物理 USB parent 及唯一目标路径，再证明逐报告结构；只凭产品字符串、厂商 page 或集合最大容量不放行。仓库已有 access 0 元数据读取及逐报告长度证明可作设计参考，但本任务没有增加 OMNI 发现分支，也不运行这些代码。（`engine/topology.py:249-268`；`engine/battery_hid.py:227-256`、`270-318`）

| 候选路径 | 打开命令句柄前必须证明的集合 | 必须证明的报告类型 / ID / 长度（包含 ID 槽） | 当前证据状态 |
| --- | --- | --- | --- |
| 管理 | 同一 parent 的 `MI_02&Col01`；page / usage **未确认** | 源码调用数组首字节 `01`；Output `01` 的完整长度 **未确认**；Input ID / 长度 **未确认**；底层明确 `HidStream.Write/Read` | **不能放行**。不能猜为 `FF02:0001`、`FFC0:0001` 或 `0059:0001`，不能把读缓冲区 64 当逐报告长度。依据 `PeripheralsProvider.cs:638-678` |
| 鼠标 | 同一 parent 的 `MI_02&Col03`，候选 `FF01:0001`；需实际描述符把路径与 usage 绑定 | 候选 Output `03/64`、Input `03/64`，均须逐 ID 证明，并确认上游底层确为 Input/Output 路线 | 源码给出 ID / 应用长度和 FF01 注释，issue 只给 FF01:0001 汇总；真实报告结构 **未确认**。依据 `PeripheralsProvider.cs:683-684`；2.2 型号表 |
| 键盘 | 同一 parent 的 `MI_02&Col02`，候选 `FF00:0001`；需实际描述符绑定 | 候选 Output `02/64`、Input `02/64`，均须逐 ID 证明，且确认底层传输 | 同上；还需可识别的配对 PID，以区分 `[6]` 与 `[11]`。依据 `PeripheralsProvider.cs:685-692,767-775`；2.3 型号表 |

后两行是根据应用调用拟定的严格候选门槛，不是宣称描述符一定长这样。若实际描述符表明相反方向、别的 ID / 长度、混合字段无法完整证明，或 Feature 才是实际传输，则停止并补证据，不改为「试一试」其他集合。现有 `byte_report_sizes` 要求完整、无别名的字节数组等条件，不拿最大长度推断每个 ID。（`engine/battery_hid.py:227-256`、`270-318`）

管理集合的必要元数据目前不齐，因而无法仅凭这些证据完成自动发现型读取器的全部门槛。不能绕过它，按通用接收器名或猜测键盘型号去发电量请求。（`PeripheralsProvider.cs:671-678`、`729-775`；`Keyboard/Models/Falchion.cs:27-31`）

### 3.2 门槛满足后的最小交换候选

| 次序 | 写入与读回 | 解析 / 停止规则 | 超时与重试上限 |
| --- | --- | --- | --- |
| 1. 型号识别，仅拓扑建立或变化时 | Col01 / 请求 ID 槽 `01`；已知调用数组只有 `01 A0 00 00`。完整实际请求长度、其余字节以及期望回复 ID / 头 **未确认** | 在确认实际长度和回复归属规则前**不执行**；之后按 `[5+4n]` 小端 PID、`0000` 终止，其他字段仍不猜。未知 PID 不产生电量查询 | 上游读/写各 2000 ms、一次写一次读，无重试。候选同样最多 1 次（重试 0），不沿用宽泛异常后的自动探测。`PeripheralsProvider.cs:666-678,729-764` |
| 2. 鼠标（仅已识别的表内 PID） | Col03；完整 `03 12 07` + `00 × 61`，64 字节。候选成功回复为完整、已证明长度的 `03 12 07 ...` | 每一包先校验实际长度、ID、回显，再取 `[5]` 百分比、`[10] > 0`；`FF AA`、空包、0、越界或其他回显都无读数；`1..100` 范围是保守拒绝策略，不是新增协议事实 | 建议 IO 超时 300 ms，最多 1 次写 + 1 次读、重试 0；首个不匹配报告即本轮无读数，避免无限读。300 来自公共 `USBTimeout`；上游原有超时发送上限是 3 次，而非本方案的要求。`Mouse/AsusMouse.cs:561-568,637-713,782-803` |
| 3. 键盘（仅已识别的六类 PID） | Col02；完整 `02 12 01` + `00 × 61`，64 字节。候选成功回复 `02 12 01 ...` | 每一包确认实际长度、ID 与回显；Azoth / Scope 取 `[6]`，Falchion RX LP 取 `[11]`；充电仅 `[9]==01`；错误、空包、未知型号或超出 `0..100` 不发读数 | 建议 IO 超时 300 ms，最多 1 次写 + 1 次读、重试 0；上游公共函数也没有发送重试循环。`Keyboard/AsusKeyboard.cs:302-357,409-444`；`Keyboard/Models/Falchion.cs:27-31` |

这里新增的拒绝策略和次数上限是**软件安全建议**，不是固件协议事实或成功率保证。特别是键盘有效回显且电量为 0：源码允许，但缺少离线区分证明；在硬件补证前建议将该样本记为「未确认 / 无读数」，而不是宣称已确认 0%。鼠标「0 且充电」也先不发布，以免掩盖上游两条就绪规则的差异。（`Keyboard/AsusKeyboard.cs:423-440`；`Mouse/AsusMouse.cs:728-739,854-873`）

同一物理接收器的三个通道建议串行，所有丢弃、读缓冲清理也必须有上限；最小版本可不做 Drain，并对首个非匹配包直接失败。非零 ID 不沿用现有 `asus.py` 对 ID 0 的去头兼容。长度校验应依据真实返回字节数，不能用预分配的零缓冲区替代。上游管理代码未检查 `stream.Read` 返回长度，公共电量代码也未显式证明短读/补零行为，因此这些是待实现的防护要求，不是已验证特性。（`PeripheralsProvider.cs:671-673,705-708`；`Mouse/AsusMouse.cs:630-653`；`Keyboard/AsusKeyboard.cs:335-346`；`engine/providers/asus.py:95-102`）

**允许的查询白名单仅是已证明条件下的配对表、鼠标电量、键盘电量三种。** 不发 Booster、串号、profile 探测、灯光、OLED、DPI、配对、唤醒、固件或心跳设置；不开事件监听的其他集合。上游事件监听会遍历多个集合并无限等待，不属于本最小交换。（`PeripheralsProvider.cs:666-720`；`PeripheralEventListener.cs:23-45`；状态写入见 2.5）

## 4. 风险与未确认项

| 项目 | 未确认内容 / 与简报概述的差异 | 所需补证与当前结论 |
| --- | --- | --- |
| 描述符与路由 | Col01 page / usage、请求完整长度、回复 ID / 长度未知；鼠标/键盘的逐 ID 长度也未取得 | 完整描述符、逐路径 parent / MI / Col / usage 绑定；不能用 64 字节应用数组放行。`PeripheralsProvider.cs:638-687`；十四份 issue |
| 底层传输 | 所给证据只覆盖 Peripherals；继承的 USB Provider / Device 未包含，底层 Input/Output/Feature API 与短数组补齐行为未确认 | 后续如要落实实现，需另行提供这部分静态证据；本任务不越界补抓源码。`SHA256SUMS.txt`；`Mouse/AsusMouse.cs:571-574`；`Keyboard/AsusKeyboard.cs:26,167-186` |
| 独立查询与副作用 | 省略 `03 7D 20 02`、完整 Connect / 同步后能否冷读，查询会不会唤醒设备未确认；Azoth 完整连接可写时钟 | 独立查询与完整初始化分离的对照证据；不默认初始化安全。`PeripheralsProvider.cs:700-712`；`Keyboard/Models/Azoth.cs:103-152` |
| 离线、休眠、未配对 | 没有三类状态的实包，列表剩余字节不知含义；键盘 0 与鼠标 0 的处理不同 | 同一已知型号在线 / 充电 / 休眠 / 关机 / 未配对样本及实际读长度；当前不能把失败当 0%。2.4 所引源码 |
| 型号覆盖 | 配对 PID 是型号识别依据；X、ex、20 周年、RX 98 与简称 ace 不都能明确映射 | 型号与配对 PID 对照；夜魔 X 只有非 OMNI 类，RX **98** 不能擅改为 **96**。`PeripheralsProvider.cs:767-805`；`issue-501.txt:12`；`issue-506.txt:87-95` |
| 回复归属与边界 | 管理回复没有回显检查；键盘只验命令；鼠标错包循环无次数上限，缺少跨应用关联/序号事实 | 不能照搬成生产解析保证；需要有限尝试、实际长度校验、同 parent 串行。`PeripheralsProvider.cs:671-678`；`Mouse/AsusMouse.cs:656-713`；`Keyboard/AsusKeyboard.cs:342-357,423-428` |
| 集合概述不是固定拓扑 | 不只有 #131 缺 FF02 / FFC0，#472、#483 也如此；#506 前后集合汇总不同。#472 的 `MI_01` 是系统鼠标信息，不是厂商通道证明 | 按实际描述符判断，不因概述简化而选错集合。`issue-131.txt:28`；`issue-472.txt:31,169-178`；`issue-483.txt:27`；`issue-506.txt:44-45,112-113` |
| 反馈数量与排名 | 确有 14 份含 1ACE 的 open issue 文本，但 #76 / #131 主要请求别的品牌，#156 无具体型号，不能都算 14 次明确的 OMNI 不支持反馈；「被反馈最多」没有其他设备统计可复核 | 文档以「14 份含 1ACE 的证据」表述，不据此排序。`issue-76.txt:1-10,27`；`issue-131.txt:1-10,28`；`issue-156.txt:1-10,29` |
| 官方电量对照 | 本版简报对 #506「没有官方电量数值」的说明与证据一致；#472 有 77% 后 76%，但无对应原始电量包 | 不把 #506 版本号或需求句当电量，不把 #472 当已验证解析。`issue-472.txt:119-188`；`issue-506.txt:81-95` |
| 摘要与源码细节 | 鼠标仍用公共 `[5]` / `[10]`，并非所有 OMNI 型号都改字段；七个鼠标文件实际还含 KJP、Extreme 共九个 OMNI 类。真正不同的键盘字段包括 Falchion `[11]` | 已按源码扩展表内事实，没有扩大文件修改范围。2.2、2.3 所引型号定义 |

这些差异不要求突破简报边界，故按证据记录并完成文档；没有需要绕过边界才能完成的部分。硬件适配、真实读数、真实用户集合与描述符长度均未验证；不作版本支持承诺。

## 5. 证据来源与核验

- 上游：`seerge/g-helper`，提交 `0bda6b55317f67c0ad68f3b5366a0e12a9096c53`；来源记录为 `.ai/evidence/g-helper/0bda6b55317f/SOURCE.txt`。源码及许可证快照只作文本阅读，没有构建或运行。
- 许可证：随证据提供的 `LICENSE` 为 **GPL-3.0**。本文件用自己的话记录协议事实与行号，不移植实现，不大段复制源码；十六进制请求是分析所需的协议数据。
- 完整性：对 `SHA256SUMS.txt` 列出的 **55 个文件**逐个计算 SHA-256，全部匹配；该清单没有替代 issue 内容真实性或硬件结果的证明。
- 实际用于 OMNI 分析的 G-Helper 文件：`PeripheralsProvider.cs`、`PeripheralEventListener.cs`、`Mouse/AsusMouse.cs`；`Mouse/Models/KerisIIOrigin.cs`、`HarpeAceAimLab.cs`、`KerisIIAce.cs`、`StrixImpactIIIWireless.cs`、`HarpeAceMini.cs`、`KerisWirelssAimpoint.cs`、`GladiusIIIAimpoint.cs`；`Keyboard/AsusKeyboard.cs`；`Keyboard/Models/Azoth.cs`、`Falchion.cs`、`StrixScopeII.cs`。
- 用户反馈：`issue-76.txt`、`issue-131.txt`、`issue-156.txt`、`issue-168.txt`、`issue-216.txt`、`issue-245.txt`、`issue-246.txt`、`issue-320.txt`、`issue-472.txt`、`issue-483.txt`、`issue-492.txt`、`issue-501.txt`、`issue-506.txt`、`issue-517.txt`，全部只读。
- 仓库对照：`engine/providers/asus.py`、`engine/upstream-protocols.md:1118-1122`、`engine/topology.py`、`engine/battery_hid.py`。它们只用于说明现状与描述符证明方式，不补写未知 OMNI 字节或暗示已有 OMNI 运行时门槛。

项目记忆仅用于任务背景与边界提醒；本文协议结论均来自以上当前工作树证据，没有以历史记忆替代源码或 issue。
