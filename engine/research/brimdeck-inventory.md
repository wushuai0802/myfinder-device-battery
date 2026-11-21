# BrimDeck 协议库盘点

基线 fcbbba977cb591ff6597f7f827c106df913d021f。BrimDeck 固定提交 f1510d09677492a13a31d26bc37c3509da1b818f。静态阅读 Brands 目录 52 个源码文件及 DeviceProtocols.cs。输入档案 SHA-256 0bdd21448ba6159137970ce046ee24f4aabd7585d4fd30979ddef5b87518ce6d。T00 共 797 条开放反馈，使用精确设备 ID，按 issue 去重。

BrimDeck 项目 Apache-2.0，具体协议还遵循其原来源许可。台账固定了原许可证。文档中的帧构造通过符号、常量与调用处复核，实机证据仅来自上游明示采集。未注来源的路径须核对具体原文。 [BrimDeck LICENSE](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/LICENSE) · [BrimDeck 第三方说明](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck/ThirdPartyNotices.txt) · [B2-1 台账](https://github.com/wushuai0802/myfinder-device-battery/pull/997)

## 共用协议调度

Output 写报告等 Input。Feature Set 后 Get。FeatureListen 写 Feature 后等 Input。OutputListen 发触发/唤醒帧后收 Input。Listen 只收 Input。OutputGetInput 含发出前排空和定时读取，FeatureGet 只获取 Feature。默认 TimeoutMs 600、Attempts 2、GapMs 30，个别读法及 Gate/Then 有独立条件。Before 和 After 可能进出特定会话，须和电量读取分开判断。 [BrimDeck 的调度契约](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/BrandFamilies.cs#L18-L65)

## BrandFamily 与各 BrandWay

以下按源文件列出家族、形状规则和每种 Way。VID-only 规则不会按该 VID 把全部 T00 反馈计入优先级。精确 ID 使用源码表和明确注册参数，缺少精确身份时计数显示待定。

### Audeze / AKG / Skullcandy

识别与报告形状涉及 Page L58 measured ? s.UsagePage == 0xFF13 : HeadsetPages.Is(s, 0xFF13) · Collection L60 Page(s, measured) && s.Usage == 0x0001 && s.InputLength >= ReportLength && s.OutputLength >= ReportLength · TakesAudeze L62 s.Vendor == Audeze && s.Product switch { Maxwell or MaxwellXbox or MaxwellCable => Collection(s, measured: true), Maxwell2 or Maxwell2Xbox => Collection(s, measured: false), _ => false } · TakesAkg L69 s.Vendor == Harman && s.Product == AkgN9 && Collection(s, measured: true) && s.Interface is 3 or -1 · TakesSkullcandy L70 s.Vendor == Skullcandy && s.Product switch { Plyr => Collection(s, measured: true), CrusherPlyr => Collection(s, measured: false), _ => false } · NoHeadset L76 s.Product != MaxwellCable && s.Name.Trim().EndsWith("Dongle", StringComparison.OrdinalIgnoreCase) · L30 public const int Audeze = 0x3329, Harman = 0x0ECB, Skullcandy = 0x34F0; · L31 public const int Maxwell = 0x4B19, MaxwellXbox = 0x4B18, MaxwellCable = 0x4B1A, Maxwell2 = 0x4B29, Maxwell2Xbox = 0x4B28; · L32 public const int AkgN9 = 0x20D9, Plyr = 0x3210, CrusherPlyr = 0x5310;。 [AirohaRace.cs L59](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L59)

- 二次来源 Headroom（MIT）
- 实机和捕获线索 battery command and the refusal), HaloBattery's audeze.py and skullcandy.py (MIT; measured on Windows on a Maxwell dongle [AirohaRace.cs L15](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L15) · 4B19, its cable 4B1A and an Xbox dongle 4B18, and a Skull-HQ capture of a PLYR dongle answering 70 %), Headroom's [AirohaRace.cs L16](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L16) · docs/hardware/akg.md (MIT; an N9 Hybrid on Windows answering 50 % and 100 %). [AirohaRace.cs L18](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L18)
- 本项目已知 ID 读取器 audeze。
- 核对到的精确身份 0ECB:20D9, 3329:4B18, 3329:4B19, 3329:4B1A, 3329:4B28, 3329:4B29, 34F0:3210, 34F0:5310
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| name | OutputGetInput | Request() · Request L35 BrandFamilies.Frame(ReportLength, 0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C, 0x00) | parse ?? Battery · Battery L40 r[0]，r[2]，!= 0x80，null，r[1]，!= 0x00 | 1000 ms，2 次 · ReplyId ReplyId · GapMs 50 | [AirohaRace.cs L81](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L81) |
| audeze.race, akg.race, skullcandy.race | 继承本文件 Way | 见各分支工厂 | 按挂接的解析器 | 沿用工厂；Model/with 可覆盖 Attempts 和 Gate | [AirohaRace.cs L80](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L80) |

与模型选择相关的分支可从 [AirohaRace.cs L83](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L83) 复核。

### Alienware

识别与报告形状涉及 Takes L22 shape.Vendor == Vendor && shape.Product == Receiver && Page(shape.UsagePage, 0xFF00) && shape.Usage == 0x0001 && shape.Interface == 1 && shape.FeatureLength == 65 && shape.InputLength >= 5 · L13 public const int Vendor = 0x413C, Receiver = 0x211D;。 [Alienware.cs L22](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Alienware.cs#L22)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 sends (USB captures): feature report 0 set to 0E A1 and zeros; the receiver answers with an input report 0E A1 <percent> [Alienware.cs L6](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Alienware.cs#L6)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 413C:211D
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "alienware.battery" | FeatureListen | Request() · Request L14 BrandFamilies.Frame(65, 0x00, 0x0E, 0xA1) | Battery · Battery L16 r[0]，== 0x0E，r[1]，== 0xA1，r[2]，r[3]，== 0x04，null | 800 ms，2 次 · 去前导零 | [Alienware.cs L28](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Alienware.cs#L28) |

与模型选择相关的分支可从 [Alienware.cs L25](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Alienware.cs#L25) 复核。

### ASTRO

识别与报告形状涉及 Takes L19 s.Vendor == Vendor && s.Product == Gen5 && s.UsagePage == 0xFF32 && s.Usage == 0x0074 && s.OutputLength >= 64 && s.InputLength >= 64 · L14 public const int Vendor = 0x046D, Gen5 = 0x0B1C;。 [AstroA50.cs L20](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AstroA50.cs#L20)

- 二次来源 HeadsetControl（GPL-3.0）, HaloBattery（MIT）
- 实机和捕获线索 changes. HeadsetControl's logitech_astro_a50.hpp (GPL; facts only; verified on a base station) and HaloBattery's astro.py [AstroA50.cs L7](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AstroA50.cs#L7)
- 本项目已知 ID 读取器 astro。
- 核对到的精确身份 046D:0B1C
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "astro.battery" | Output | Request() · Request L15 BrandFamilies.Frame(6, 0x02, 0x0C, 0x03, 0x00, 0x06, 0x0C) | Battery · Battery L17 r[0]，== 0x02，r[1]，== 0x0C，r[2]，r[3]，== 0x00，r[4]，== 0x06，r[6]，r[8]，null | 1000 ms，2 次 | [AstroA50.cs L25](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AstroA50.cs#L25) |

与模型选择相关的分支可从 [AstroA50.cs L23](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AstroA50.cs#L23) 复核。

### ASTRO A50 Gen 4

识别与报告形状涉及 Takes L23 s.Vendor == Vendor && s.Product == Base && HeadsetPages.Is(s, 0xFF32) && s.Usage == 0x0074 && s.Interface is 6 or -1 && s.OutputLength >= 64 && s.InputLength >= 64 · L15 public const int Vendor = 0x9886, Base = 0x002C;。 [AstroA50Gen4.cs L24](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AstroA50Gen4.cs#L24)

- 二次来源 HeadsetControl（GPL-3.0）
- 实机和捕获线索 7 charging, bits 0 to 6 the percent. HeadsetControl's logitech_astro_a50_gen4.hpp (GPL; facts only; verified on Linux, the [AstroA50Gen4.cs L9](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AstroA50Gen4.cs#L9)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 9886:002C
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "astro.gen4" | Output | Request(Battery) · Request L17 BrandFamilies.Frame(64, 0x02, command) | Level · Level L22 r[3]，null | 1000 ms，2 次 · Gate Request(Status) | [AstroA50Gen4.cs L30](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AstroA50Gen4.cs#L30) |

与模型选择相关的分支可从 [AstroA50Gen4.cs L27](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AstroA50Gen4.cs#L27) 复核。

### ASUS Headset

识别与报告形状涉及 Takes L32 s.Vendor == Vendor && s.Product is Dongle or Cable && HeadsetPages.Vendor(s) && s.FeatureLength >= 64 · L16 public const int Vendor = 0x0B05, Dongle = 0x18D6, Cable = 0x18D7;。 [AsusStrixGo.cs L33](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AsusStrixGo.cs#L33)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 文件内未找到明确测量记录。
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 0B05:18D6, 0B05:18D7
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "asus-headset.status" | Feature | Request() · Request L17 BrandFamilies.Frame(64, 0xFF, 0x08, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x03, 0x52, 0x01) | Battery(s.Product == Cable) · Battery L19 r[0]，!= 0xFF，null | 600 ms，2 次 · GapMs 35 | [AsusStrixGo.cs L37](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AsusStrixGo.cs#L37) |

与模型选择相关的分支可从 [AsusStrixGo.cs L35](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AsusStrixGo.cs#L35) 复核。

### ATK Headset

识别与报告形状涉及 Takes L48 s.Vendor == Vendor && Receivers.Any(d => d.Product == s.Product) && HeadsetPages.Is(s, 0xFF90) && s.Usage == 0x0001 && s.InputLength >= 64 && s.OutputLength >= 64 · L19 public const int Vendor = 0x373B, N9V2Ultimate = 0x1262; · L47 public static readonly (int Product, string Name)[] Receivers = [(N9V2Ultimate, "ATK N9 V2 Ultimate"), (0x12D4, "ATK N9 V2 PRO"),… · L49 private static bool Takes(HidShape s) => s.Vendor == Vendor && Receivers.Any(d => d.Product == s.Product) && HeadsetPages.Is(s, 0…。 [AtkHeadsets.cs L49](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkHeadsets.cs#L49)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 taken. Not measured by us. [AtkHeadsets.cs L15](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkHeadsets.cs#L15) · as the Ultimate's; not measured by us. [AtkHeadsets.cs L46](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkHeadsets.cs#L46)
- 本项目已知 ID 读取器 atk_headset。
- 核对到的精确身份 373B:1262, 373B:12D4, 373B:12D6
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "atk-headset.state" | Output | Request() · Request L20 BrandFamilies.Frame(4, 0x55, 0x65, 0x01, 0x00) | StateBattery · StateBattery L33 r[0]，!= 0x55，null，r[1]，== 0x0A，!= 0x65，r[3]，== 0x00，== 0x1A，== 0x04，r[2]，== 0x03 | 1000 ms，2 次 | [AtkHeadsets.cs L54](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkHeadsets.cs#L54) |

与模型选择相关的分支可从 [AtkHeadsets.cs L52](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkHeadsets.cs#L52) 复核。

### ATK HFD keyboard / Bytech keyboard / ATK Nearlink mouse / ATK Evision V2 keyboard / ATK HECHI mouse

识别与报告形状涉及 Hfd L81 s.Vendor == AtkVendor && HfdProducts.Contains(s.Product) && MouseShapes.Collection(s, 0xFF60, 0x61) && s.OutputLength >= 6 · Bytech L83 BytechReceivers.Contains((s.Vendor, s.Product)) && MouseShapes.Collection(s, 0xFF60, 0x61) && s.OutputLength >= 64 · Nearlink L84 s.Vendor == AtkVendor && NearlinkProducts.Contains(s.Product) && MouseShapes.Collection(s, 0xFFB2, 0x01) && s.OutputLength >= 10 · Yogo L85 s.Vendor == AtkVendor && YogoProducts.Contains(s.Product) && MouseShapes.Collection(s, 0xFF60, 0x61) && s.OutputLength is 33 or 65 && s.InputLength >= 11 · L34 public const int AtkVendor = 0x373B, VgnVendor = 0x391D;。 [AtkOfficial.cs L82](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L82)

- 二次来源 ATK/VGN 官方驱动
- 实机和捕获线索 their other readers here do not cover. None is measured by us; each goes only to the collection the driver opens and only [AtkOfficial.cs L4](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L4)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 373B:108B, 373B:108C, 373B:111E, 373B:111F, 373B:1147, 373B:1148, 373B:114D, 373B:114E, 373B:1151, 373B:1152, 373B:1156, 373B:1179, 373B:1196, 373B:119B, 373B:119C, 373B:11B5, 373B:11FF, 373B:1200, 373B:1231
- 不移植含状态切换的序列。Yogo 读取前发送启动报告，需确认回退和单独读取。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "atk.hfd" | Output | HfdRequest() · HfdRequest L37 BrandFamilies.Frame(33, 0x00, 0x02, 0x96, 0x24) | HfdBattery · HfdBattery L38 r[0]，== 0x00，r[1]，== 0x02，r[2]，== 0x96，r[3]，== 0x24，r[4]，r[5]，null | 600 ms，2 次 | [AtkOfficial.cs L91](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L91) |
| "bytech.battery" | Output | BytechRequest(id, id) · BytechRequest L45 BrandFamilies.Frame(64, reportId, 0x87, 0x00, 0x00, 0x01, 0x00, 0x00)，frame[63] = (byte)(0xFF - sum % 256) | BytechBattery · BytechBattery L52 r[1]，== 0x87，r[7]，r[8]，null | 600 ms，2 次 | [AtkOfficial.cs L94](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L94) |
| "atk.nearlink" | Output | NearlinkRequest(0x05) · NearlinkRequest L56 BrandFamilies.Frame(64, reportId, 0x00, 0x00, 0x1A) | NearlinkBattery · NearlinkBattery L57 r[0]，r[3]，== 0x1A，r[8]，r[9]，null | 600 ms，2 次 | [AtkOfficial.cs L97](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L97) |
| "atk.nearlink-cable" | Output | NearlinkRequest(0x04) · NearlinkRequest L56 BrandFamilies.Frame(64, reportId, 0x00, 0x00, 0x1A) | NearlinkBattery · NearlinkBattery L57 r[0]，r[3]，== 0x1A，r[8]，r[9]，null | 600 ms，2 次 | [AtkOfficial.cs L98](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L98) |
| "atk.evision-v2" | Output | YogoFrame(YogoPower, YogoPowerSequence, s.OutputLength) · YogoFrame L62 BrandFamilies.Frame(outputLength >= 65 ? 65 : 33, 0x00, 0xAA, command, 0x00, 0x00, 0x00, sequence, … | YogoBattery · YogoBattery L65 r[0]，== 0xAA，r[2]，!= 0xFF，r[5]，r[6]，== 0x00，r[8]，r[9]，null | 1500 ms，2 次 · GapMs 50 · Before [YogoFrame(YogoStart, YogoStartSequence, s.OutputLength)] · 去前导零 | [AtkOfficial.cs L102](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L102) |
| "atk.hechi" | Output | HechiRequest() · HechiRequest L71 BrandFamilies.Frame(64, 0x0B, 0x13) | s.Product == 0x1147 ? HechiCableBattery : HechiBattery · HechiCableBattery L73 Hechi(r, receiver: false) | 800 ms，2 次 | [AtkOfficial.cs L108](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L108) |

与模型选择相关的分支可从 [AtkOfficial.cs L89](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L89) 复核。

### AULA mouse / AULA headset

识别与报告形状涉及 Sc380Collection L43 shape.Vendor == Sc380Vendor && shape.Product == Sc380Receiver && MouseShapes.Collection(shape, 0xFF02, 0x02) && shape.FeatureLength >= 9 · L9Collection L46 shape.Vendor == L9Vendor && shape.Product == L9Receiver && MouseShapes.Collection(shape, 0xFFA8, 0x02) && shape.InputLength >= 19 && shape.OutputLength >= 3 · L19 public const int Sc380Vendor = 0x258A, Sc380Receiver = 0x0138, L9Vendor = 0x38A6, L9Receiver = 0x28FD;。 [Aula.cs L44](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Aula.cs#L44)

- 二次来源 OpenMouse（AGPL-3.0）
- 实机和捕获线索 first). From OpenMouse's AULA SC380 Pro module (AGPL; facts only, from AULA's utility, verified on a receiver on [Aula.cs L10](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Aula.cs#L10) · answers, whose 04 means full there (AulaA9). Not measured by us. [Aula.cs L61](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Aula.cs#L61)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 258A:0138, 38A6:001D, 38A6:28FD
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "aula.sc380" | Feature | Sc380Request() · Sc380Request L20 BrandFamilies.Frame(8, 0x09, 0x00, 0x09, 0x01, 0x01, 0x00, 0x01, 0x00)，frame[1] = (byte)sum | Sc380 · Sc380 L29 r[0]，== 0x09，r[2]，r[3]，== 0x01，r[4]，r[5]，== 0x00，r[6]，r[7]，r[8]，null | 600 ms，2 次 | [Aula.cs L52](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Aula.cs#L52) |
| "aula.l9" | Output | L9Request() · L9Request L39 BrandFamilies.Frame(2, 0x0B, 0x81) | L9 · L9 L41 r[0]，== 0x0A，r[1]，== 0x81，r[17]，r[18]，null | 600 ms，2 次 | [Aula.cs L53](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Aula.cs#L53) |
| "aula.a9" | Output | BrandFamilies.Frame(64, 0x55, 0x65, 0x00) | AulaA9 · AulaA9 L32 r[0]，!= 0x55，r[1]，!= 0x65，r[3]，== 0x00，null，== 0x1A，r[2] | 1000 ms，2 次 | [Aula.cs L65](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Aula.cs#L65) |

与模型选择相关的分支可从 [Aula.cs L49](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Aula.cs#L49) 复核。

### BETOP

识别与报告形状涉及 new("BETOP", shape => shape.Vendor == Vendor && shape.UsagePage is 0x00FF or 0xFF00 && shape.Usage == 0x0003 && shape.InputLength >= 5, L24 · L16 public const int Vendor = 0x20BC;。 [Betop.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Betop.cs#L1)

- 二次来源 betop-battery（MIT）, Voltide（MIT）
- 实机和捕获线索 reports it as 0x00FF (both sources measured that), so either is taken. It takes output report 0x02 with command 0x15, zeros [Betop.cs L7](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Betop.cs#L7) · beitong.py (MIT; KP40D, from a capture of the official client). Command 0x22 writes the settings and is never sent. [Betop.cs L12](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Betop.cs#L12)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 按配置函数检查，缺明确可独立统计的 VID:PID。
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "betop.status" | Output | Request() · Request L17 BrandFamilies.Frame(2, 0x02, 0x15) | Battery · Battery L19 r[0]，== 0x02，r[1]，== 0x15，r[3]，== 0x00，r[4]，== 0x50，r[2]，null | 800 ms，2 次 | [Betop.cs L25](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Betop.cs#L25) |

与模型选择相关的分支可从 [Betop.cs L22](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Betop.cs#L22) 复核。

### Corsair Void

识别与报告形状涉及 new("Corsair Void", shape => shape.Vendor == Vendor && Products.Contains(shape.Product) && UsagePages.Is(shape.UsagePage, 0xFFC5) && shape.Usage == 0x0001, L29 · L14 public const int Vendor = 0x1B1C, Product = 0x0A55; · L15 public static readonly int[] Products = [Product, 0x0A0C, 0x0A2B, 0x1B23, 0x1B25, 0x1B27, 0x0A14, 0x0A16, 0x0A1A, 0x0A51, 0x0A75,… · L29 new("Corsair Void", shape => shape.Vendor == Vendor && Products.Contains(shape.Product) && UsagePages.Is(shape.UsagePage, 0xFFC5)…。 [CorsairVoid.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/CorsairVoid.cs#L1)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 not measured by us. [CorsairVoid.cs L10](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/CorsairVoid.cs#L10)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 1B1C:0A0C, 1B1C:0A14, 1B1C:0A16, 1B1C:0A1A, 1B1C:0A2B, 1B1C:0A38, 1B1C:0A4F, 1B1C:0A51, 1B1C:0A55, 1B1C:0A75, 1B1C:1B23, 1B1C:1B25, 1B1C:1B27
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "corsair.void" | Output | Request() · Request L16 [0xC9, 0x64] | Battery · Battery L18 r[0]，!= 0x64，r[3]，!= 0xB1，null，r[4]，r[2] | 500 ms，2 次 | [CorsairVoid.cs L30](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/CorsairVoid.cs#L30) |

与模型选择相关的分支可从 [CorsairVoid.cs L27](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/CorsairVoid.cs#L27) 复核。

### DARKRIM

识别与报告形状涉及 Takes L43 s.Vendor == Vendor && s.Product == Shadow && HeadsetPages.Is(s, 0xFF02) && s.Usage == 0x0002 && s.Interface is 3 or -1 && s.OutputLength >= 65 && s.InputLength >= 65 · L15 public const int Vendor = 0x3654, Shadow = 0x6901;。 [Darkrim.cs L44](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darkrim.cs#L44)

- 二次来源 device-battery-viewer（MIT）
- 实机和捕获线索 vigil-watt's darkrim.md and DarkrimCodec.cs (MIT; measured: FE 00 11 00 03 00 00 03 00 01 01 5A 98 B4, 90 %). [Darkrim.cs L11](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darkrim.cs#L11)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 3654:6901
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "darkrim.basicinfo" | Output | Request() · Request L16 BrandFamilies.Frame(65, 0x00, 0xFE, 0x00, 0x10, 0x00, 0x02, 0x00, 0x00, 0x03, 0x00, 0x09, 0x01, 0x0… | Battery · Battery L29 r[0]，!= 0xFE，r[2]，!= 0x11，null，r[7]，r[end]，r[end + 1]，r[9]，!= 0x01，r[10] | 1000 ms，2 次 · 去前导零 | [Darkrim.cs L49](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darkrim.cs#L49) |

与模型选择相关的分支可从 [Darkrim.cs L47](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darkrim.cs#L47) 复核。

### Darmoshark

识别与报告形状涉及 Telink L56 shape.Vendor == TelinkVendor && shape.Product is Receiver or M3Cable or N3Cable && MouseShapes.Collection(shape, 0x008C, 0x01) && shape.FeatureLength >= 21 · Nordic L59 shape.Vendor == NordicVendor && shape.Product is M3sReceiver or M3sCable && MouseShapes.Collection(shape, 0xFF0A, 0x01) && shape.InputLength >= 13 && shape.OutputLength >= 65 · L28 public const int TelinkVendor = 0x248A, NordicVendor = 0x1915; · L29 public const int Receiver = 0xFF30, M3Cable = 0xFF12, N3Cable = 0xFF10, M3sReceiver = 0x0723, M3sCable = 0x073C;。 [Darmoshark.cs L57](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darmoshark.cs#L57)

- 二次来源 darmoshark-m3-configurator（MIT）
- 实机和捕获线索 identity layout, measured on an M3) and, facts only, Asuka-Liu/M3-N87-battery-viewer (no license; FF30 interface 1, [Darmoshark.cs L15](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darmoshark.cs#L15) · page 0x8C, the percent offset and the bond read first, measured on an M3 receiver), TheWebDog/quiet-battery (its own [Darmoshark.cs L16](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darmoshark.cs#L16)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 1915:0723, 1915:073C, 248A:FF10, 248A:FF12, 248A:FF30
- 不移植含状态切换的序列。有绑定/收发前置路径，需先单独证明其影响。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "darmoshark.identity" | Feature | IdentityRequest() · IdentityRequest L30 BrandFamilies.Frame(21, 0x51, 0x06) | shape.Product == Receiver ? ReceiverIdentity : Identity · ReceiverIdentity L36 r[0]，== 0x51，r[1]，== 0x03，r[7]，== 0x00，null | 600 ms，2 次 · GapMs 120 · FeatureLength 21 · Before [BondRequest()] | [Darmoshark.cs L65](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darmoshark.cs#L65) |
| "darmoshark.power" | Output | PowerRequest(shape.Product == M3sReceiver) · PowerRequest L39 BrandFamilies.Frame(65, 0x00, (byte)(receiver ? 0x41 : 0x01), 0x00, 0x81, 0x01)，frame[64] = (byte)(0xA1 - sum) | Power · Power L49 r[0]，!= 0x01，r[3]，null，r[6]，== 0x15，r[7]，== 0x19，r[11] | 800 ms，2 次 · 去前导零 | [Darmoshark.cs L67](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darmoshark.cs#L67) |

与模型选择相关的分支可从 [Darmoshark.cs L62](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darmoshark.cs#L62) 复核。

### EDIFIER

识别与报告形状涉及 Takes L24 s.Vendor == Vendor && s.Product == HaloSpace && HeadsetPages.Is(s, 0xFF14) && s.Usage == 0x0001 && s.InputLength >= 64 && s.OutputLength >= 64 · L13 public const int Vendor = 0x2D99, HaloSpace = 0xE049;。 [Edifier.cs L25](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Edifier.cs#L25)

- 二次来源 HeadsetControl（GPL-3.0）
- 实机和捕获线索 (GPL; facts only; captured on the headset: 2F BB EC D0 00 01 86 FE, 35 %). [Edifier.cs L9](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Edifier.cs#L9)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 2D99:E049
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "edifier.battery" | Output | Request() · Request L14 BrandFamilies.Frame(7, 0x2E, 0xAA, 0xEC, 0xD0, 0x00, 0x00, 0x66) | Battery · Battery L16 r[0]，!= 0x2F，r[1]，!= 0xBB，r[2]，!= 0xEC，r[3]，!= 0xD0，r[4]，!= 0x00，r[5]，!= 0x01，null，r[i]，r[6]，r[7] | 800 ms，2 次 | [Edifier.cs L30](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Edifier.cs#L30) |

与模型选择相关的分支可从 [Edifier.cs L28](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Edifier.cs#L28) 复核。

### 8BitDo

识别与报告形状涉及 Collection L20 shape.Vendor == Vendor && Pads.Contains(shape.Product) && shape.UsagePage == 0x0001 && shape.Usage is 0x0004 or 0x0005 && shape.InputLength >= 15 · L17 public const int Vendor = 0x2DC8; · L19 public static readonly int[] Pads = [0x6000, 0x6100, 0x6001, 0x6101, 0x6003, 0x6006, 0x6009, 0x6012, 0x202F]; · L21 public static bool Collection(HidShape shape) => shape.Vendor == Vendor && Pads.Contains(shape.Product) && shape.UsagePage == 0x0…。 [EightBitDo.cs L21](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/EightBitDo.cs#L21)

- 二次来源 SDL（zlib）
- 实机和捕获线索 hardware). Over Bluetooth the report is 0x01, but the brand families are not read over Bluetooth. [EightBitDo.cs L13](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/EightBitDo.cs#L13)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 2DC8:202F, 2DC8:6000, 2DC8:6001, 2DC8:6003, 2DC8:6006, 2DC8:6009, 2DC8:6012, 2DC8:6100, 2DC8:6101
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "8bitdo.report" | Listen | 空帧表（只读输入） | Battery · Battery L23 r[0]，!= 0x04，null，r[14] | 500 ms，1 次 | [EightBitDo.cs L33](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/EightBitDo.cs#L33) |

与模型选择相关的分支可从 [EightBitDo.cs L31](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/EightBitDo.cs#L31) 复核。

### EWEADN mouse

识别与报告形状涉及 Hub L62 shape.Vendor == ReceiverVendor && shape.Product == Receiver \|\| shape.Vendor == CableVendor && shape.Product == Cable · InfoCollection L64 MouseShapes.Collection(shape, 0xFF01, 0x10) && shape.OutputLength >= 33 && shape.InputLength >= 16 · IdentifyCollection L65 MouseShapes.Collection(shape, 0xFF02, 0x00) && shape.OutputLength == 33 && shape.InputLength == 33 · BeiyinShape L67 MouseShapes.Collection(shape, 0xFF00, 0x01) && shape.FeatureLength >= 64 · BeiyinCollection L69 shape.Vendor == BeiyinVendor && BeiyinReceivers.Contains(shape.Product) && BeiyinShape(shape) · L27 public const int ReceiverVendor = 0x089D, Receiver = 0x062F, CableVendor = 0x088D, Cable = 0x062E, BeiyinVendor = 0x372E, BeiyinR…。 [Eweadn.cs L63](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Eweadn.cs#L63)

- 二次来源 vigil-watt（MIT）, MouseDock（MIT）
- 实机和捕获线索 HaloBattery's eweadn.py (MIT; measured on an S9, its answer 30 00 01 0B 4D 31 35 36 .. 46 01: M156, 70 %, and the [Eweadn.cs L11](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Eweadn.cs#L11) · MouseDock's AulaBatteryProtocol (MIT; measured on an SC580 receiver, M137 at 82 %) sends it. The device id is not used [Eweadn.cs L13](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Eweadn.cs#L13) · 107C, 108A and 108D, which EWEADN's web driver (its beiying SDK) speaks to the same way; not measured by us. The SDK's [Eweadn.cs L22](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Eweadn.cs#L22)
- 本项目已知 ID 读取器 eweadn, eweadn_s9pro。
- 核对到的精确身份 088D:062E, 089D:062F, 372E:1026
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "eweadn.beiyin" | Feature | BeiyinRequest() · BeiyinRequest L44 BrandFamilies.Frame(64, 0x03, 0x00, 0x50, 0x00, 0x02, 0x4F, 0x81)，frame[1] = (byte)sum | Beiyin · Beiyin L53 r[0]，!= 0x03，r[1]，!= 0x50，r[2]，!= 0x00，r[3]，!= 0x0B，r[4]，!= 0x02，r[5]，!= 0x01，null，r[6] | 600 ms，2 次 · GapMs 20 · FeatureLength 64 | [Eweadn.cs L75](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Eweadn.cs#L75) |
| "eweadn.info" | Output | InfoRequest() · InfoRequest L28 BrandFamilies.Frame(33, 0x00, 0x30) | parse | 600 ms，2 次 · 去前导零 | [Eweadn.cs L77](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Eweadn.cs#L77) |
| "eweadn.identify" | Output | IdentifyRequest() · IdentifyRequest L31 BrandFamilies.Frame(33, 0x00, 0x30, 0x00, 0x01) | parse | 600 ms，2 次 · 去前导零 | [Eweadn.cs L78](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Eweadn.cs#L78) |

与模型选择相关的分支可从 [Eweadn.cs L81](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Eweadn.cs#L81) 复核。

### FIFINE

识别与报告形状涉及 Takes L16 s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF90) && s.Usage == 0x0001 && s.OutputLength >= 64 && s.InputLength >= 4 · L12 public const int Vendor = 0x3142;。 [Fifine.cs L17](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Fifine.cs#L17)

- 二次来源 HeadsetControl（GPL-3.0）
- 实机和捕获线索 headset is off or out of range. HeadsetControl's fifine_ht6.hpp (GPL; facts only: from a capture of FIFINE's SoundLink, [Fifine.cs L6](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Fifine.cs#L6) · verified on an HT6, 3142:00C3). The query read back (55 65 01 00) is no reading: its status is 0. [Fifine.cs L7](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Fifine.cs#L7)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 3142:00C3
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "fifine.state" | Output | Request() · Request L13 BrandFamilies.Frame(4, 0x55, 0x65, 0x01, 0x00) | Battery · Battery L15 AtkHeadsets.StateBattery(r) | 1000 ms，2 次 | [Fifine.cs L21](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Fifine.cs#L21) |

与模型选择相关的分支可从 [Fifine.cs L19](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Fifine.cs#L19) 复核。

### Finalmouse

识别与报告形状涉及 Takes L57 shape.Vendor == Vendor && shape.Product != UltralightXCable && MouseShapes.Collection(shape, 0xFF00, 0x01) && shape.InputLength >= 7 · L21 public const int Vendor = 0x361D, UltralightXDongle = 0x0100, UltralightXCable = 0x0102;。 [Finalmouse.cs L58](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Finalmouse.cs#L58)

- 二次来源 OpenMouse（AGPL-3.0）
- 实机和捕获线索 UltralightX's dongle (0100) measured by HaloBattery never answered command 38, so its reading is the voltage's (coarse, [Finalmouse.cs L10](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Finalmouse.cs#L10) · 0x04. From HaloBattery's finalmouse.py (MIT; measured on a 361D:0100 dongle: 0x1064 = 4196 mV, 78 %), MouseDock's [Finalmouse.cs L15](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Finalmouse.cs#L15)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 361D:0100
- 需核对。VID 与 FF00 集合筛选覆盖大量运输，必须限制固件和实际报告。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "finalmouse.status" | Output | Request(BatteryStatus) · Request L23 BrandFamilies.Frame(4, 0x04, 0x02, (byte)(0x80 \| command), 0x00) | Status · Status L31 null | 500 ms，1 次 | [Finalmouse.cs L65](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Finalmouse.cs#L65) |
| "finalmouse.voltage" | Output | Request(BatteryVoltage) · Request L23 BrandFamilies.Frame(4, 0x04, 0x02, (byte)(0x80 \| command), 0x00) | Voltage · Voltage L36 null，Coarse | 500 ms，2 次 | [Finalmouse.cs L66](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Finalmouse.cs#L66) |

与模型选择相关的分支可从 [Finalmouse.cs L61](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Finalmouse.cs#L61) 复核。

### Flydigi

识别与报告形状涉及 Pad L29 !NotPads.Contains(product) · Collection L30 shape.Vendor == Vendor && Pad(shape.Product) && shape.UsagePage is 0xFFA0 or 0xA0FF && shape.Usage == 0x0001 && shape.InputLength >= 32 · WindowsPlaceholder L56 remaining == 1000 && full == 1000 && (vendor == Vendor \|\| vendor == MicrosoftVendor && product == Xbox360Product && hidName is { } text && (text.Contains("flydigi", StringComparison.OrdinalIgn… · ReadOverHid L59 vendor == Vendor && Pad(product) · L22 public const int Vendor = 0x37D7; · L23 public const int Apex6 = 0x2502; · L55 public const int MicrosoftVendor = 0x045E, Xbox360Product = 0x028E;。 [Flydigi.cs L31](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Flydigi.cs#L31)

- 二次来源 SDL（zlib）
- 实机和捕获线索 on the vendor collection usage page 0xFFA0, usage 1 (measured on Windows in HaloBattery #191: the Vader 5 Pro's interface 1; [Flydigi.cs L8](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Flydigi.cs#L8) · a Vader 5 Pro) agrees on × 20 for the rest (OpenFlydigi measured 05 there on an Apex 5 on its dongle). The seven steps [Flydigi.cs L16](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Flydigi.cs#L16)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 37D7:2502
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "flydigi.info" | Output | Request(numbered) · Request L33 [numbered ? (byte)0x03 : (byte)0x00, .. Info] | r => Battery(r, apex6) · Battery L36 r[0]，== 0x5A，r[at]，!= 0x5A，r[at + 1]，!= 0xA5，r[at + 2]，!= 0x01，null，r[at + 5]，r[at + 11]，Coarse | 600 ms，2 次 | [Flydigi.cs L68](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Flydigi.cs#L68) |

与模型选择相关的分支可从 [Flydigi.cs L62](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Flydigi.cs#L62) 复核。

### GameSir

识别与报告形状涉及 StatusCollection L23 shape.Vendor == Vendor && shape.UsagePage is 0xFF7A or 0x7AFF && shape.Usage == 0x0001 && shape.InputLength >= 65 && shape.OutputLength >= 65 · HeartbeatCollection L26 shape.Vendor == Vendor && shape.UsagePage is 0xFFF0 or 0xF0FF && shape.Usage == 0x0040 && shape.InputLength >= (shape.Product == TarantulaCe ? 29 : 37) · L21 public const int Vendor = 0x3537; · L22 public const int TarantulaCe = 0x103D;。 [GameSir.cs L24](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/GameSir.cs#L24)

- 二次来源 GamesirBatteryTray（MIT）, gamesir-battery-tray（MIT）
- 实机和捕获线索 they answer on rather than by model; both pages are as measured on Windows (HidP_GetCaps), the byte-swapped order is taken [GameSir.cs L4](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/GameSir.cs#L4) · its cable (3537:100F) the Nova 2 Lite answers FF at 19: charging, no percent (YesCharge, MIT, on hardware). A percent of 0 [GameSir.cs L10](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/GameSir.cs#L10)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 3537:103D
- 不移植含状态切换的序列。Heartbeat 路径写后监听，对报文作用与唤醒影响需要额外证据。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "gamesir.status" | Output | StatusRequest() · StatusRequest L28 BrandFamilies.Frame(4, 0x00, 0x01, 0x01, 0x00) | Status · Status L30 r[0]，!= 0x00，null，r[1]，== 0x01，r[2]，r[19]，== 0xFF，Charging | 800 ms，2 次 | [GameSir.cs L52](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/GameSir.cs#L52) |
| "gamesir.heartbeat" | OutputListen | Heartbeat() · Heartbeat L39 [0x0F, 0xF2, 0x00] | r => Report(r, tarantulaCe) · Report L41 r[0]，!= 0x12，r[level]，null，r[charge] | 600 ms，2 次 · 有唤醒/触发发送 | [GameSir.cs L54](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/GameSir.cs#L54) |

与模型选择相关的分支可从 [GameSir.cs L48](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/GameSir.cs#L48) 复核。

### Glorious

识别与报告形状涉及 Takes L25 shape.Vendor == Vendor && Products.ContainsKey(shape.Product) && shape.FeatureLength == 65 && (MouseShapes.Collection(shape, 0xFFFF, 0) \|\| MouseShapes.Collection(shape, 0xFFFF, 1) \|\| MouseShap… · L18 public const int Vendor = 0x258A; · L19 public static readonly IReadOnlyDictionary<int, string> Products = new Dictionary<int, string>。 [Glorious.cs L26](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Glorious.cs#L26)

- 二次来源 OpenMouse（AGPL-3.0）, HaloBattery（MIT）
- 实机和捕获线索 link, so its receiver has no reading for it). The 4K/8K cable answers the exchange itself (OpenMouse's capture of Glorious CORE on a [Glorious.cs L10](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Glorious.cs#L10) · From HaloBattery's glorious.py (MIT; measured on a 2022 receiver, HaloBattery #281, after GloriousBatteryMonitor-Go, MIT, [Glorious.cs L12](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Glorious.cs#L12)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 按配置函数检查，缺明确可独立统计的 VID:PID。
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "glorious.f83" | Feature | MouseShapes.Request83() | MouseShapes.Battery83 | 600 ms，2 次 · GapMs 100 · FeatureLength 65 | [Glorious.cs L31](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Glorious.cs#L31) |

与模型选择相关的分支可从 [Glorious.cs L29](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Glorious.cs#L29) 复核。

### HyperX mouse

识别与报告形状涉及 Takes L51 (Find(Heartbeat, shape) is not null \|\| Find(Battery2, shape) is not null) && MouseShapes.Collection(shape, 0xFF00, 0x01) && shape.InputLength >= 6 · L22 public const int Hp = 0x03F0, Kingston = 0x0951;。 [HyperX.cs L46](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/HyperX.cs#L46)

- 二次来源 OpenMouse（AGPL-3.0）
- 实机和捕获线索 santeri3700's Pulsefire Dart reverse engineering (CC0; captures of NGenuity on a Dart: 46 00 00 01 01 then [HyperX.cs L11](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/HyperX.cs#L11)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 按配置函数检查，缺明确可独立统计的 VID:PID。
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "hyperx.battery" | Output | Battery2Request() · Battery2Request L39 BrandFamilies.Frame(2, 0x50, 0x02) | Battery2Battery · Battery2Battery L41 r[0]，== 0x51，r[1]，== 0x02，r[2]，r[3]，null | 500 ms，2 次 · Gate shape.OutputIds.Contains(0x52) ? LinkRequest() : null | [HyperX.cs L55](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/HyperX.cs#L55) |
| "hyperx.heartbeat" | Output | HeartbeatRequest() · HeartbeatRequest L30 BrandFamilies.Frame(65, 0x00, 0x51) | HeartbeatBattery · HeartbeatBattery L32 r[0]，== 0x51，r[4]，r[5]，null | 600 ms，2 次 · Gate ConnectionRequest() · 去前导零 | [HyperX.cs L57](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/HyperX.cs#L57) |

与模型选择相关的分支可从 [HyperX.cs L60](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/HyperX.cs#L60) 复核。

### Incott

识别与报告形状涉及 Takes L26 shape.Vendor == Vendor && Products.Contains(shape.Product) && MouseShapes.Collection(shape, 0xFF05, 0x01) && shape.InputLength >= 3 · L15 public const int Vendor = 0x093A; · L16 public static readonly int[] Products = [0x522C, 0x622C];。 [Incott.cs L27](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Incott.cs#L27)

- 二次来源 MouseDock（MIT）
- 实机和捕获线索 product string names the mouse. From OpenMouse's Incott module and notes (AGPL; facts only: measured over a charge [Incott.cs L10](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Incott.cs#L10)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 093A:522C, 093A:622C
- 可直接移植。对确切集合执行被动输入，保留 04/FF 等模式判别与原通知长度。MouseDock MIT 来源可核对。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "incott.listen" | Listen | 空帧表（只读输入） | Battery · Battery L17 r[0]，!= 0x09，null，r[i]，r[1] | 600 ms，1 次 | [Incott.cs L32](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Incott.cs#L32) |

与模型选择相关的分支可从 [Incott.cs L30](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Incott.cs#L30) 复核。

### Jabra

识别与报告形状涉及 Takes L33 s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF00) && s.Usage is 0x0001 or 0x0005 && s.OutputLength >= 64 && s.InputLength >= 64 · L17 public const int Vendor = 0x0B0E;。 [Jabra.cs L34](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Jabra.cs#L34)

- 二次来源 HeadsetControl（GPL-3.0）
- 实机和捕获线索 jabra_gnp_protocol.hpp (GPL; facts only: the 0x08 address, the refusal reasons and the Link 390's usage 5, verified on Linux [Jabra.cs L12](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Jabra.cs#L12)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 按配置函数检查，缺明确可独立统计的 VID:PID。
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "jabra.dongle" | Output | Request(Dongle) · Request L22 BrandFamilies.Frame(7, 0x05, to, 0x00, Sequence, 0x46, 0x12, 0x02) | Battery(Dongle) · Battery L24 r[0]，!= 0x05，r[1]，!= 0x00，r[2]，r[3]，r[4]，!= 0xC0，null | 800 ms，2 次 | [Jabra.cs L39](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Jabra.cs#L39) |
| "jabra.usb" | Output | Request(Usb) · Request L22 BrandFamilies.Frame(7, 0x05, to, 0x00, Sequence, 0x46, 0x12, 0x02) | Battery(Usb) · Battery L24 r[0]，!= 0x05，r[1]，!= 0x00，r[2]，r[3]，r[4]，!= 0xC0，null | 800 ms，2 次 | [Jabra.cs L40](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Jabra.cs#L40) |

与模型选择相关的分支可从 [Jabra.cs L44](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Jabra.cs#L44) 复核。

### Keychron mouse / VGN mouse / Partners 表

识别与报告形状涉及 Takes L29 shape.Vendor == Vendor && !NordicProducts.Contains(shape.Product) && MouseShapes.Collection(shape, 0xFFC1, 0x01) && shape.InputLength >= 21 · L19 public const int Vendor = 0x3434;。 [Keychron.cs L30](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L30)

- 二次来源 OpenMouse（AGPL-3.0）
- 实机和捕获线索 Launcher and not confirmed on hardware) and the "1k" protocol on page 0x8C (Launcher uses it only where a device has no [Keychron.cs L12](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L12) · LANMIN-X/Jingzao-JZM5-Battery's JZM5BatteryTray.swift (MIT; measured on the receiver). [Keychron.cs L36](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L36) · Motospeed module (AGPL; facts only, not verified on hardware). On its cable (FFF1) the mouse answers a random level, [Keychron.cs L39](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L39)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 按配置函数检查，缺明确可独立统计的 VID:PID。
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "keychron.status" | Output | Request() · Request L21 BrandFamilies.Frame(2, 0xB3, 0x06) | Battery · Battery L23 r[0]，!= 0xB4，r[1]，!= 0x06，null，r[20] | 500 ms，2 次 | [Keychron.cs L56](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L56) |
| "keychron.status-feature" | FeatureListen | Request() · Request L21 BrandFamilies.Frame(2, 0xB3, 0x06) | Battery · Battery L23 r[0]，!= 0xB4，r[1]，!= 0x06，null，r[20] | 500 ms，2 次 | [Keychron.cs L57](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L57) |
| partner.Prefix + ".status" | Output | Request() · Request L21 BrandFamilies.Frame(2, 0xB3, 0x06) | Battery · Battery L23 r[0]，!= 0xB4，r[1]，!= 0x06，null，r[20] | 500 ms，2 次 | [Keychron.cs L62](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L62) |
| "vgn.b3" | Output | Request() · Request L21 BrandFamilies.Frame(2, 0xB3, 0x06) | Battery · Battery L23 r[0]，!= 0xB4，r[1]，!= 0x06，null，r[20] | 500 ms，2 次 | [Keychron.cs L65](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L65) |

与模型选择相关的分支可从 [Keychron.cs L52](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L52) 复核。

### Lenovo Headset

识别与报告形状涉及 Takes L20 s.Vendor == Vendor && s.Product == VoipReceiver && HeadsetPages.Is(s, 0xFF07) && s.Usage == 0x0222 && s.Interface is 3 or -1 && s.InputLength >= 8 · L11 public const int Vendor = 0x17EF, VoipReceiver = 0xA07D;。 [LenovoHeadsets.cs L21](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/LenovoHeadsets.cs#L21)

- 二次来源 HeadsetControl（GPL-3.0）
- 实机和捕获线索 文件内未找到明确测量记录。
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 17EF:A07D
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "lenovo-headset.status" | FeatureListen | Request() · Request L12 BrandFamilies.Frame(61, 0x24, 0x01) | Battery · Battery L14 r[0]，!= 0x27，r[1]，!= 0x01，null，r[2]，!= 0x00，r[7] | 1000 ms，2 次 | [LenovoHeadsets.cs L26](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/LenovoHeadsets.cs#L26) |

与模型选择相关的分支可从 [LenovoHeadsets.cs L24](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/LenovoHeadsets.cs#L24) 复核。

### Lofree

识别与报告形状涉及 Takes L40 shape.Vendor == Vendor && shape.Product == Receiver && Page(shape.UsagePage, 0xFF02) && shape.Usage == 0x0002 && shape.Interface == 1 && shape.OutputLength == 17 && shape.InputLength >= 17 && … · L16 public const int Vendor = 0x05AC, Receiver = 0x024F;。 [Lofree.cs L40](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Lofree.cs#L40)

- 二次来源 HaloBattery（MIT）, vigil-watt（MIT）
- 实机和捕获线索 文件内未找到明确测量记录。
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 05AC:024F
- 需核对。共享 05AC:024F 需要完全相符的产品串、父设备及报告证明。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "lofree.flow" | Output | Request(0x04) · Request L18 BrandFamilies.Frame(17, 0x08, command, 0x00, 0x00, 0x00, 0x80)，frame[16] = (byte)(0x55 - sum) | Battery · Battery L37 r[6]，r[7]，null | 800 ms，2 次 · Gate Request(0x03) | [Lofree.cs L47](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Lofree.cs#L47) |

与模型选择相关的分支可从 [Lofree.cs L44](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Lofree.cs#L44) 复核。

### Logitech Centurion

识别与报告形状涉及 new("Logitech Centurion", shape => shape.Vendor == Vendor && shape.Product == Product && shape.UsagePage == 0xFFA0 && shape.Usage == 0x0001 L25 · L14 public const int Vendor = 0x046D, Product = 0x0AF7;。 [LogitechCenturion.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/LogitechCenturion.cs#L1)

- 二次来源 HeadsetControl（GPL-3.0）, device-battery-viewer（MIT）
- 实机和捕获线索 at 6 says the headset is off. From vigil-watt's CenturionCodec (MIT; measured on the receiver on Windows, so the page as [LogitechCenturion.cs L7](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/LogitechCenturion.cs#L7)
- 本项目已知 ID 读取器 logitech_centurion。
- 核对到的精确身份 046D:0AF7
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "logitech.centurion" | Output | Request() · Request L15 BrandFamilies.Frame(64, 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A) | Battery · Battery L17 r[0]，== 0x51，r[1]，== 0x05，r[6]，== 0x00，null，== 0x0B，r[8]，== 0x04，r[10]，r[12]，== 0x02 | 800 ms，2 次 | [LogitechCenturion.cs L27](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/LogitechCenturion.cs#L27) |

与模型选择相关的分支可从 [LogitechCenturion.cs L23](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/LogitechCenturion.cs#L23) 复核。

### MCHOSE G7 MAX

识别与报告形状涉及 Takes L14 s.Vendor == Vendor && Products.Contains(s.Product) && EweadnMice.BeiyinShape(s) · L12 public const int Vendor = 0x41E4; · L13 public static readonly IReadOnlySet<int> Products = new HashSet<int> { 0x1204, 0x1001, 0x1003 };。 [MchoseG7Max.cs L15](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseG7Max.cs#L15)

- 二次来源 公开驱动线索
- 实机和捕获线索 times 300 ms apart; asked here three times, 60 ms apart. Not measured by us. [MchoseG7Max.cs L8](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseG7Max.cs#L8)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 41E4:1001, 41E4:1003, 41E4:1204
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "mchose.g7max" | Feature | EweadnMice.BeiyinRequest() | EweadnMice.Beiyin | 600 ms，3 次 · GapMs 60 · FeatureLength 64 | [MchoseG7Max.cs L19](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseG7Max.cs#L19) |

与模型选择相关的分支可从 [MchoseG7Max.cs L17](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseG7Max.cs#L17) 复核。

### MCHOSE V9 Pro

识别与报告形状涉及 Takes L39 s.Vendor == Vendor && s.Product == Product && s.InputLength >= 4 && s.OutputLength >= 64 && (HeadsetPages.Is(s, 0xFF90) && s.Usage == 0x0001 && s.Name.Contains("V9 PRO", StringComparison.Ordin… · L20 public const int Vendor = 0x291D, Product = 0x385D;。 [MchoseV9Pro.cs L40](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseV9Pro.cs#L40)

- 二次来源 HaloBattery（MIT）
- 实机和捕获线索 mchose-v9-pro.md measured it so on Windows, aipno/mchose-linux's HEADSET.md has it in the descriptor with report 0x55). [MchoseV9Pro.cs L6](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseV9Pro.cs#L6) · - 0x02 on battery (HaloBattery's mchose_v9.py, rafagfran and vigil-watt measured it); [MchoseV9Pro.cs L8](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseV9Pro.cs#L8) · - 0x00 on its cable, read as charging (HaloBattery measured it at 60 %, the level following M HUB while it charged; [MchoseV9Pro.cs L9](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseV9Pro.cs#L9)
- 本项目已知 ID 读取器 mchose_headset。
- 核对到的精确身份 291D:385D
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "mchose.v9pro" | Output | Request() · Request L21 BrandFamilies.Frame(64, 0x55, 0x65, 0x01) | shape.Name.Contains("G9 PRO", StringComparison.OrdinalIgnoreCase) ? G9Battery : Battery · G9Battery L33 r[3]，== 0x00，null | 500 ms，2 次 | [MchoseV9Pro.cs L46](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseV9Pro.cs#L46) |

与模型选择相关的分支可从 [MchoseV9Pro.cs L44](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseV9Pro.cs#L44) 复核。

### metapanda.status / Metapanda / NearLink receiver

识别与报告形状涉及 Collection L58 shape.Vendor == ThunderobotMice.Vendor && MouseShapes.Collection(shape, 0xFFA0, 0x02) && shape.InputLength >= 9 · Takes L60 shape.Product == P1 && Collection(shape) · Others L61 shape.Product is not (P1 or ThunderobotMice.Ml903) && Collection(shape) && shape.InputLength >= 25 && shape.OutputIds.Contains(0xA0) · L23 public const int P1 = 0x00B3;。 [Metapanda.cs L59](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Metapanda.cs#L59)

- 二次来源 vigil-watt（MIT）
- 实机和捕获线索 of reach. Measured in #305 on a P1 (31 % and 32 % against the vendor's driver, four requests out of four answered within [Metapanda.cs L11](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Metapanda.cs#L11) · (DeviceReadBook). Neither is measured on such a product, and what it is comes from its product string alone. There the [Metapanda.cs L17](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Metapanda.cs#L17)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 369B:00B3
- 需核对。泛 NearLink 分支按已有集合与输入形状，要求原厂型号与报告门禁。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "metapanda.status" | Output | Request() · Request L25 BrandFamilies.Frame(2, 0xA0, 0x50) | parse | 600 ms，2 次 | [Metapanda.cs L66](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Metapanda.cs#L66) |

与模型选择相关的分支可从 [Metapanda.cs L68](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Metapanda.cs#L68) 复核。

### NuPhy

识别与报告形状涉及 IsExcluded L62 WiredOnly.Contains(product) \|\| product is (>= 0x0700 and <= 0x07FF) or 0x1020 or (>= 0x6F00 and <= 0x6FFF) or 0x8A04 or 0xAF0F or 0xFE00 \|\| (!string.IsNullOrEmpty(name) && (name.Contains("Upgr… · Takes L92 shape.Vendor == Vendor && !IsExcluded(shape.Product, shape.Name) && shape.UsagePage == 0x0001 && shape.Usage == 0x0000 && shape.InputLength == 65 && shape.OutputLength == 65 · L12 public const int Vendor = 0x19F5, U1Dongle = 0x2620;。 [NuPhy.cs L93](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NuPhy.cs#L93)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 文件内未找到明确测量记录。
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 按配置函数检查，缺明确可独立统计的 VID:PID。
- 需核对。有私有 Power Memory 读取，接口为 0001:0000、65 B。须按目标产品限制，和 B2-5 的 FF60 范围分开。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "nuphy.func1" | Output | Request(Profile1BatteryOffset) · Request L68 BrandFamilies.Frame(65, 0x00, 0x55, 0x05, 0x00, (byte)(0x01 + lo + hi), 0x01, lo, hi, 0x00) | r => Battery(r, Profile1BatteryOffset) · Battery L74 r[0]，== 0x00，r[1]，== 0xAA，r[o]，r[o + 1]，== 0x05，r[o + 2]，r[o + 4]，== 0x01，r[o + 5]，r[o + 6]，r[o + 7]，r[o + i]，r[o + 3]，null，r[o + 8]，== 0xFA，r[p]，r[p + 1]，== 0xFB，r[p + 2]，== 0x07，r[p + 4]，r[p + 5] | 400 ms，1 次 · 去前导零 | [NuPhy.cs L100](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NuPhy.cs#L100) |
| "nuphy.func0" | Output | Request(Profile0BatteryOffset) · Request L68 BrandFamilies.Frame(65, 0x00, 0x55, 0x05, 0x00, (byte)(0x01 + lo + hi), 0x01, lo, hi, 0x00) | r => Battery(r, Profile0BatteryOffset) · Battery L74 r[0]，== 0x00，r[1]，== 0xAA，r[o]，r[o + 1]，== 0x05，r[o + 2]，r[o + 4]，== 0x01，r[o + 5]，r[o + 6]，r[o + 7]，r[o + i]，r[o + 3]，null，r[o + 8]，== 0xFA，r[p]，r[p + 1]，== 0xFB，r[p + 2]，== 0x07，r[p + 4]，r[p + 5] | 400 ms，1 次 · 去前导零 | [NuPhy.cs L104](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NuPhy.cs#L104) |

与模型选择相关的分支可从 [NuPhy.cs L96](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NuPhy.cs#L96) 复核。

### NVIDIA

识别与报告形状涉及 Collection L16 shape.Vendor == Vendor && shape.Product == Product && !(shape.UsagePage == 0x0001 && shape.Usage is 0x0002 or 0x0006) && shape.UsagePage != 0x000C && shape.InputLength >= 15 · L15 public const int Vendor = 0x0955, Product = 0x7214;。 [NvidiaShield.cs L18](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NvidiaShield.cs#L18)

- 二次来源 SDL（zlib）
- 实机和捕获线索 checked on hardware (the 2015 controller, 0955:7210, which SDL sends the same commands to, is not asked), and only on the [NvidiaShield.cs L4](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NvidiaShield.cs#L4) · (reserved); the percent at byte 14 was read on hardware and is followed. Charging is another command (0x3A) and is not [NvidiaShield.cs L10](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NvidiaShield.cs#L10)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 0955:7214
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "shield.battery" | Output | Request() · Request L20 BrandFamilies.Frame(33, 0x04, 0x07, 0x00) | Battery · Battery L22 r[0]，!= 0x03，r[1]，!= 0x07，r[2]，!= 0x00，r[14]，null | 800 ms，2 次 | [NvidiaShield.cs L33](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NvidiaShield.cs#L33) |

与模型选择相关的分支可从 [NvidiaShield.cs L31](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NvidiaShield.cs#L31) 复核。

### Pulsar

识别与报告形状涉及 Takes L31 shape.Vendor == Vendor && Products.Contains(shape.Product) && shape.Interface == 3 && MouseShapes.Collection(shape, 0xFFFF, 0x01) && shape.FeatureLength == 65 · L14 public const int Vendor = 0x3710, Receiver = 0x5403, Cable = 0x3402; · L15 public static readonly int[] Products = [Receiver, Cable];。 [Pulsar.cs L32](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Pulsar.cs#L32)

- 二次来源 pulsar-x3-python（MIT）
- 实机和捕获线索 cable the mouse charges: HaloBattery reports it charging. From HaloBattery's pulsar.py (MIT; measured on a receiver on [Pulsar.cs L7](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Pulsar.cs#L7) · where its reply was not captured), after jonkristian/pulsar-x3-python (MIT), whose PROTOCOL_ANALYSIS lists the query [Pulsar.cs L9](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Pulsar.cs#L9)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 3710:3402, 3710:5403
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "pulsar.sonix" | Feature | Request() · Request L16 BrandFamilies.Frame(65, 0x00, 0x00, 0x08, 0x81, 0x01)，frame[63] = (byte)sum，frame[64] = (byte)(sum >> 8) | shape.Product == Cable ? CableBattery : Battery · CableBattery L29 Charging，null | 600 ms，2 次 · GapMs 50 · FeatureLength 65 | [Pulsar.cs L37](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Pulsar.cs#L37) |

与模型选择相关的分支可从 [Pulsar.cs L35](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Pulsar.cs#L35) 复核。

### Razer BlackShark V3

识别与报告形状涉及 Takes L40 s.Vendor == Vendor && s.Product is Cable or Dongle or ProCable or ProDongle && s.UsagePage == 0xFF14 && s.Usage == 0x0001 && s.Interface is 5 or -1 && s.OutputLength >= 64 && s.InputLength >= … · L20 public const int Vendor = 0x1532, Cable = 0x0579, Dongle = 0x057A, ProCable = 0x0576, ProDongle = 0x0577;。 [RazerBlackSharkV3.cs L41](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerBlackSharkV3.cs#L41)

- 二次来源 HaloBattery（MIT）
- 实机和捕获线索 From HaloBattery's blackshark_v3.py (MIT; after a Synapse capture of this dongle on Windows showing 97 %, so the page as [RazerBlackSharkV3.cs L12](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerBlackSharkV3.cs#L12)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 1532:0576, 1532:0577, 1532:0579, 1532:057A
- 不移植含状态切换的序列。初始化与唤醒帧随读取发送，保留协议事实，需另找单独读法。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "blackshark-v3.battery" | Output | Frame(Battery) · Frame L23 BrandFamilies.Frame(64, 0x02, 0x00, 0x60, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, direction, command)，frame[62] = xor | Level · Level L32 r[0]，!= 0x02，r[1]，r[2]，!= 0x60，r[10]，r[11]，r[12]，null，r[i]，r[62]，r[13] | 1000 ms，1 次 · GapMs 50 · MinIntervalMs 300_000 · Before [Frame(Init, Wake), Frame(Charging, Wake), Frame(Charging)] | [RazerBlackSharkV3.cs L47](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerBlackSharkV3.cs#L47) |

与模型选择相关的分支可从 [RazerBlackSharkV3.cs L44](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerBlackSharkV3.cs#L44) 复核。

### Razer Headset

识别与报告形状涉及 Takes L141 if (s.Vendor != Vendor) return false; if (Status2020.Contains(s.Product)) return (s.Product == 0x0528 ? s.UsagePage == 0xFF00 : HeadsetPages.Is(s, 0xFF00)) && s.Usage == 0x0001 && s.FeatureLen… · L41 public const int Vendor = 0x1532; · L45 public const int BarracudaPro = 0x053A, KrakenV4 = 0x056C;。 [RazerHeadsets.cs L142](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L142)

- 二次来源 HaloBattery（MIT）
- 实机和捕获线索 byte is not the battery's, so only the charging state is given. From HaloBattery's blackshark2020.py (MIT; measured on a [RazerHeadsets.cs L11](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L11) · 0528 on Windows, so its page is taken only as measured); the Nari's capture in Modzeleczek/RazerNariBatteryLevel (no [RazerHeadsets.cs L12](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L12) · HaloBattery's blackshark.py (MIT) and vigil-watt's razer-headset.md (MIT, measured on a 0555: 02 0E 50 49 08 CE 92 DE 05 [RazerHeadsets.cs L20](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L20)
- 本项目已知 ID 读取器 barracuda, razer。
- 核对到的精确身份 1532:0528, 1532:053A, 1532:056C
- 不移植含状态切换的序列。PA / Barracuda 包含会话开关，仅保留已知独立只读路径的线索。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "razer-headset.status" | Feature | StatusRequest() · StatusRequest L60 BrandFamilies.Frame(64, 0xFF, 0x0A, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x02, 0x05) | StatusReads() · StatusReads L75 BatteryRead，null | 600 ms，2 次 · GapMs 150 | [RazerHeadsets.cs L159](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L159) |
| "razer-headset.pa" | Output | PaQuery(Battery) · PaQuery L84 BrandFamilies.Frame(64, 0x02, 0x80, 0x08, 0x00, 0x00, 0x50, 0x41, 0x08, 0x00, 0x03, command) | PaBattery · PaBattery L85 r[0]，== 0x02，r[2]，== 0x50，r[3]，== 0x49，r[12]，r[13]，== 0x01，r[14]，r[15]，null | 700 ms，2 次 · GapMs 35 · Before [PaRemote(true), PaRemote(true)] · After [PaRemote(false)] · Then PaQuery(Charge) | [RazerHeadsets.cs L161](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L161) |
| "razer-headset.barracuda" | Output | BarracudaQuery(Battery) · BarracudaQuery L96 BrandFamilies.Frame(64, 0x01, 0x80, 0x08, 0x50, 0x41, 0x08, 0x08, 0x03, command) | BarracudaBattery · BarracudaBattery L97 r[0]，== 0x01，r[3]，== 0x50，r[4]，== 0x49，r[13]，r[14]，r[15]，r[16]，null | 900 ms，2 次 · GapMs 50 · Before [BarracudaRemote(true)] · After [BarracudaRemote(false)] · Then BarracudaQuery(Charge) | [RazerHeadsets.cs L164](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L164) |
| "razer-headset.kraken" | Output | MtkQuery(Battery, mtk.Domain) · MtkQuery L111 BrandFamilies.Frame(64, 0x02, 0x00, 0x60, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, domain, command)，frame[62] = Xor(frame, 0, 61) | r => MtkBattery(r, mtk.Domain) · MtkBattery L126 r[13]，null | 1000 ms，2 次 · Then mtk.AskCharging ? MtkQuery(Charge | [RazerHeadsets.cs L167](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L167) |
| "razer-headset.v3x" | Feature | V3xRequest() · V3xRequest L130 BrandFamilies.Frame(64, 0x07, 0x00, 0x03, 0x00, 0x00, 0x00, 0x02, 0x07, 0x80, 0x00)，frame[62] = Xor(frame, 3, 60) | V3xBattery · V3xBattery L137 r[0]，== 0x07，r[6]，== 0x02，r[7]，r[8]，== 0x80，r[10]，r[62]，null | 600 ms，2 次 · GapMs 50 · FeatureLength 64 | [RazerHeadsets.cs L174](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L174) |

与模型选择相关的分支可从 [RazerHeadsets.cs L178](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L178) 复核。

### Redragon mouse

识别与报告形状涉及 Takes L34 shape.Vendor == Vendor && shape.Product is Cable or Receiver && MouseShapes.Collection(shape, 0xFF00, 0x01) && shape.FeatureLength >= 8 · L18 public const int Vendor = 0x258A, Cable = 0x002E, Receiver = 0x002F;。 [Redragon.cs L35](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Redragon.cs#L35)

- 二次来源 OpenMouse（AGPL-3.0）
- 实机和捕获线索 verified on firmware 2.95 and 2.97 through OpenMouse Bridge on Windows: the link read 00 while the mouse slept, and [Redragon.cs L13](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Redragon.cs#L13)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 258A:002E, 258A:002F
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "redragon.m690" | Feature | Request() · Request L19 BrandFamilies.Frame(8, 0x05, 0x90) | Battery · Battery L21 r[0]，!= 0x05，r[1]，!= 0x90，null，r[2]，r[3]，== 0x01，== 0x02 | 600 ms，2 次 · GapMs 60 · FeatureLength 8 · Gate shape.Product == Receiver ? LinkRequest() : null | [Redragon.cs L40](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Redragon.cs#L40) |

与模型选择相关的分支可从 [Redragon.cs L38](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Redragon.cs#L38) 复核。

### RK

识别与报告形状涉及 Takes L38 s.Vendor == Vendor && Receivers.Contains(s.Product) && s.UsagePage == 0xFF02 && s.Usage == 0x0002 && s.InputLength == 20 && s.OutputLength == 20 · L15 public const int Vendor = 0x258A; · L16 public static readonly int[] Receivers = [0x01BB, 0x0233]; · L39 private static bool Takes(HidShape s) => s.Vendor == Vendor && Receivers.Contains(s.Product) && s.UsagePage == 0xFF02 && s.Usage …。 [RkKeyboard.cs L39](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RkKeyboard.cs#L39)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 文件内未找到明确测量记录。
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 258A:01BB, 258A:0233
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "rk.r98pro" | Output | Request(Battery) · Request L19 BrandFamilies.Frame(20, 0x13, command, 0x01)，frame[19] = (byte)(0x13 + command + 1) | Level · Level L37 r[5]，null | 1000 ms，2 次 · Gate Request(Identity) | [RkKeyboard.cs L45](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RkKeyboard.cs#L45) |

与模型选择相关的分支可从 [RkKeyboard.cs L42](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RkKeyboard.cs#L42) 复核。

### AJAZZ / CAROTMAS / MonsGeek / VGN / Rongyuan receiver

识别与报告形状涉及 Collection L51 shape.UsagePage == 0xFFFF && shape.Usage == 0x0002 && shape.FeatureLength == 65。 [RongyuanStatus.cs L52](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L52)

- 二次来源 vigil-watt（MIT）, YesCharge（MIT）
- 实机和捕获线索 VGN-NEON75-AIR.md), so a 100 there may be the cable's and not a measured level. [RongyuanStatus.cs L17](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L17) · Facts from: HaloBattery's monsgeek.py (MIT; MonsGeek's driver, 3151:5026 measured at 54 % and 52 %); mousedock (MIT; [RongyuanStatus.cs L18](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L18) · VGN V HUB's RyServe.setStatus for 391D:2A05, measured at 82 %; MonsGeek's V4 driver for 3151:5038, user-tested); [RongyuanStatus.cs L19](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L19)
- 本项目已知 ID 读取器 veekos_shine60, vgn_ry_ms。
- 核对到的精确身份 0DB0:197E, 0DB0:8E9A, 145F:0376, 145F:038D, 25AA:2008, 25AA:4008, 25AA:4018, 3151:4011, 3151:5026, 3151:502B, 3151:5038, 3151:5055, 347A:A302, 347A:A305, 379A:1804, 379A:1807, 38A9:0002, 38EE:0000, 38EE:0002, 38EE:0004, 391D:1A04, 391D:1A08, 391D:1A0E, 391D:1A13, 391D:1A25, 391D:1A2B, 391D:1A41, 391D:2A05, 391D:2A07
- 需核对。部分精确型号已有采集，按接收器产品与下游身份分别验证校验和及回复。优先 3151:4011、3151:5038、391D:2A07、3151:5055。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| prefix + ".status" | Feature | Request(checksum) · Request L32 BrandFamilies.Frame(65, 0x00, Status, 0, 0, 0, 0, 0, 0, 0xFF - Status) : BrandFamilies.Frame(65, 0x… | MouseReceivers.Contains((shape.Vendor, shape.Product)) ? MouseBattery : Battery · MouseBattery L40 Battery(r, true) | 600 ms，2 次 · GapMs 100 · FeatureLength 65 | [RongyuanStatus.cs L64](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L64) |

与模型选择相关的分支可从 [RongyuanStatus.cs L87](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L87) 复核。

### Ryunix

识别与报告形状涉及 Takes L27 shape.Vendor == Vendor && Products.Contains(shape.Product) && (MouseShapes.Collection(shape, 0x000A, 0xC7) \|\| MouseShapes.Collection(shape, 0x000A, 0x00)) && shape.InputLength >= 7 · L17 public const int Vendor = 0x04F3; · L18 public static readonly int[] Products = [0x026E, 0x026F];。 [Ryunix.cs L28](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Ryunix.cs#L28)

- 二次来源 OpenMouse（AGPL-3.0）
- 实机和捕获线索 reading). From OpenMouse's Ryunix and Amkette modules (AGPL; facts only: a capture on a wired XS Flow 2 Speed, [Ryunix.cs L12](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Ryunix.cs#L12)
- 本项目已知 ID 读取器 eweadn_x23。
- 核对到的精确身份 04F3:026E, 04F3:026F
- 可直接移植。只收已有 Input 报告。固定 04F3:026E/026F 与集合保留，由 OpenMouse 采集对照。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "ryunix.listen" | Listen | 空帧表（只读输入） | Battery · Battery L20 r[0]，!= 0x04，r[1]，r[3]，r[5]，r[4]，null | 600 ms，1 次 | [Ryunix.cs L33](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Ryunix.cs#L33) |

与模型选择相关的分支可从 [Ryunix.cs L31](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Ryunix.cs#L31) 复核。

### Scyrox

识别与报告形状涉及 Takes L47 shape.Vendor == Vendor && (StatusProducts.Contains(shape.Product) \|\| shape.Product == V12m) && MouseShapes.Collection(shape, 0xFF00, 0x02) && shape.InputLength == 64 && shape.OutputLength == 64 · L20 public const int Vendor = 0x395A, V12m = 0x0006;。 [Scyrox.cs L48](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Scyrox.cs#L48)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 zzy6-a/ScyroxTray's README and ScyroxV2.cs (no license; facts only, from Scyrox's S-Center 2.0 web driver, measured: [Scyrox.cs L11](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Scyrox.cs#L11)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 395A:0006, 395A:000B, 395A:000C, 395A:000E
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "scyrox.profile" | Output | ProfileRequest() · ProfileRequest L33 Frame(0x01) | Profile · Profile L44 r[0]，== 0x08，r[1]，== 0x01，r[3]，r[4]，r[5]，r[7]，r[62]，r[20]，null | 600 ms，2 次 | [Scyrox.cs L54](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Scyrox.cs#L54) |
| "scyrox.status" | Output | StatusRequest() · StatusRequest L32 Frame(0x2E, 0x00, 0x03) | Status · Status L34 r[0]，!= 0x08，r[1]，!= 0x2E，null，r[i]，r[63]，r[5]，r[6]，r[4] | 600 ms，2 次 | [Scyrox.cs L55](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Scyrox.cs#L55) |

与模型选择相关的分支可从 [Scyrox.cs L51](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Scyrox.cs#L51) 复核。

### AULA / GANSS / EWEADN / Huafenda keyboard

识别与报告形状涉及 HuafendaCollection L73 shape.Usage == 0x0061 && shape.InputLength is 33 or 65 && shape.OutputLength is 33 or 65 && (shape.Vendor == SonixVendor && HuafendaReceivers.Contains(shape.Product) ? Page(shape.UsagePage, 0x… · TakesHuafenda L77 HuafendaProducts.Contains((shape.Vendor, shape.Product)) && HuafendaCollection(shape) · Collection L81 Page(shape.UsagePage, 0xFF60) && shape.Usage == 0x0061 && shape.InputLength == 33 && shape.OutputLength == 33 · TakesBorrowed L88 shape.Vendor == AppleVendor && shape.Product == BorrowedReceiver && Collection(shape) && (shape.Interface == 3 \|\| Named(shape.Name) is not null) · TakesEweadn L91 shape.Vendor == SonixVendor && shape.Product == EweadnReceiver && Collection(shape) && shape.Interface == 3 · L32 public const int AppleVendor = 0x05AC, BorrowedReceiver = 0x024F, SonixVendor = 0x0C45, EweadnReceiver = 0xFEFE;。 [SonixQuery.cs L73](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L73)

- 二次来源 vigil-watt（MIT）, aula-keyboard-battery-tray（MIT）, Aula-F75-Max-Driver（MIT）
- 实机和捕获线索 interface 3, or where the product string names the keyboard ("F108Pro Dongle" measured; GANSS, GS3104) when the [SonixQuery.cs L12](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L12) · interface number moved. vigil-watt (MIT; GANSS capture 97 %, F108 Pro checked on the keyboard), aula-keyboard-battery-tray [SonixQuery.cs L13](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L13) · reports and must not be written). vigil-watt (MIT; from the driver, measured at 100 %). The AULA F87 Pro V2D's receiver [SonixQuery.cs L16](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L16)
- 本项目已知 ID 读取器 aula_keyboard, eweadn_x87。
- 核对到的精确身份 05AC:024F, 0C45:FEFE
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "huafenda.info" | Output | HuafendaRequest(shape.OutputLength) · HuafendaRequest L49 BrandFamilies.Frame(65, 0x00, 0xAA, 0x10, 0x38, 0x00, 0x00, 0x00, 0x01, 0x00)，BrandFamilies.Frame(33, 0x00, 0xAA, 0x10, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00) | Huafenda · Huafenda L54 r[0]，!= 0x55，r[1]，!= 0x10，r[2]，r[3]，!= 0x00，r[4]，null，r[25]，r[26] | 600 ms，2 次 · 去前导零 | [SonixQuery.cs L78](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L78) |
| prefix + ".query" | Output | Request() · Request L33 BrandFamilies.Frame(33, 0x00, 0x20, 0x01)，frame[32] = (byte)sum | Battery · Battery L41 r[0]，!= 0x20，r[1]，!= 0x01，r[2]，!= 0x00，r[3]，null，r[i]，r[31] | 800 ms，2 次 · 去前导零 | [SonixQuery.cs L94](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L94) |

与模型选择相关的分支可从 [SonixQuery.cs L96](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L96) 复核。

### Sony INZONE

识别与报告形状涉及 Takes L41 s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF04) && s.InputLength >= 64 && s.OutputLength >= 64 && s.Product switch { H5 => s.Usage == 0x0002, H9II => s.Usage == 0x0001 && s.Interface is 5 or … · L17 public const int Vendor = 0x054C, H5 = 0x0EBF, H9II = 0x0FA8;。 [SonyInzone.cs L42](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyInzone.cs#L42)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 文件内未找到明确测量记录。
- 本项目已知 ID 读取器 sony_inzone。
- 核对到的精确身份 054C:0EBF, 054C:0FA8
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "inzone.battery" | Output | Request() · Request L19 BrandFamilies.Frame(14, 0x02, 0x0C, 0x01, 0x00, 0xFC, 0x08, 0x96, 0xC3, 0x41, 0x04, 0x01, Transacti…，frame[13] = (byte)sum | Battery · Battery L28 r[0]，!= 0x02，r[2]，!= 0x04，r[3]，!= 0xFF，r[5]，!= 0x00，r[6]，!= 0x96，r[7]，!= 0xC3，r[8]，!= 0x1，null，r[1]，r[i]，r[length + 1]，r[9]，r[10]，r[11]，r[12]，r[14]，== 0xFF，r[13] | 1000 ms，2 次 | [SonyInzone.cs L51](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyInzone.cs#L51) |

与模型选择相关的分支可从 [SonyInzone.cs L49](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyInzone.cs#L49) 复核。

### Sony PULSE

识别与报告形状涉及 Takes L44 s.Vendor == Vendor && s.FeatureLength == Length && s.InputLength == Length && s.Product switch { Receiver => s.UsagePage == 0xFF01 && s.Usage == 0x0020 && s.OutputLength == 0, Case => s.UsageP… · L19 public const int Vendor = 0x054C, Receiver = 0x0ECC, Case = 0x0ED0;。 [SonyPulse.cs L45](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyPulse.cs#L45)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 shared vendor id 0x054C, so only these products and collections, as FerrisPulse measured them on Windows: [SonyPulse.cs L4](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyPulse.cs#L4) · From FerrisPulse's SonyDeviceHelper.PulseExplore.cs and docs/PULSE_EXPLORE.md (GPL; facts only; measured on a receiver and [SonyPulse.cs L14](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyPulse.cs#L14)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 054C:0ECC, 054C:0ED0
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "pulse.receiver" | FeatureGet | [Report] | Wireless · Wireless L26 null，r[39]，r[56]，r[55]，Coarse | 600 ms，2 次 · FeatureLength Length | [SonyPulse.cs L53](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyPulse.cs#L53) |
| "pulse.case" | FeatureGet | [Report] | Usb · Usb L35 null，r[56]，r[55]，Charging，Coarse | 600 ms，2 次 · FeatureLength Length | [SonyPulse.cs L54](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyPulse.cs#L54) |

与模型选择相关的分支可从 [SonyPulse.cs L56](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyPulse.cs#L56) 复核。

### SteelSeries Headset

识别与报告形状涉及 Takes L73 if (s.Vendor != Vendor \|\| Find(s.Product) is not { } exchange \|\| s.Interface is not (3 or -1) \|\| s.OutputLength < 2) return false; return exchange == Exchange.Arctis1 ? HeadsetPages.Is(s, 0xFF… · L26 public const int Vendor = 0x1038;。 [SteelSeriesHeadsets.cs L74](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SteelSeriesHeadsets.cs#L74)

- 二次来源 HeadsetControl（GPL-3.0）, HaloBattery（MIT）
- 实机和捕获线索 _nova_5, _gamebuds, and pull request 584 for the Omni, verified on one: 93 %), with HaloBattery's steelseries.py and [SteelSeriesHeadsets.cs L19](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SteelSeriesHeadsets.cs#L19) · steelseries_elite.py (MIT) for the B0 echo every answer starts with and the Elite (measured: 01 b0 00 00 01 00 1f 64 ..., [SteelSeriesHeadsets.cs L20](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SteelSeriesHeadsets.cs#L20) · receiver; not measured by us. [SteelSeriesHeadsets.cs L35](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SteelSeriesHeadsets.cs#L35)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 按配置函数检查，缺明确可独立统计的 VID:PID。
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| name | Output | Request(exchange) · Request L41 [0x06, 0x12]，BrandFamilies.Frame(64, 0x01, 0xB0),，[0x00, 0xB0] | Battery(exchange) · Battery L48 r[0]，!= 0x06，r[1]，!= 0x12，null，r[2]，== 0x01，r[3]，!= 0xB0，Coarse，== 0x02，r[4]，!= 0x01，r[14]，r[6]，r[15] | 1000 ms，2 次 | [SteelSeriesHeadsets.cs L88](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SteelSeriesHeadsets.cs#L88) |

与模型选择相关的分支可从 [SteelSeriesHeadsets.cs L91](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SteelSeriesHeadsets.cs#L91) 复核。

### Thunderobot

识别与报告形状涉及 Takes L39 shape.Vendor == Vendor && shape.Product == Ml903 && MouseShapes.Collection(shape, 0xFFA0, 0x02) && shape.InputLength >= 25 · L13 public const int Vendor = 0x369B, Ml903 = 0x0234;。 [Thunderobot.cs L40](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Thunderobot.cs#L40)

- 二次来源 vigil-watt（MIT）
- 实机和捕获线索 ThunderobotCodec and its notes (MIT; tags from Thunderobot's own DLL, the layout from a measured answer: [Thunderobot.cs L8](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Thunderobot.cs#L8)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 369B:0234
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "thunderobot.info" | Output | Request() · Request L14 BrandFamilies.Frame(2, 0xA0, 0x02) | Battery · Battery L16 r[0]，!= 0xA1，r[1]，!= 0x02，null，r[at]，r[i]，r[at + 1]，== 0x02，== 0x25，r[at + 2] | 800 ms，2 次 | [Thunderobot.cs L44](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Thunderobot.cs#L44) |

与模型选择相关的分支可从 [Thunderobot.cs L46](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Thunderobot.cs#L46) 复核。

### URX

识别与报告形状涉及 Takes L21 shape.Vendor == Vendor && shape.Product == Receiver && shape.Name.Contains("URX", StringComparison.OrdinalIgnoreCase) && Page(shape.UsagePage, 0xFF60) && shape.Usage == 0x0061 && shape.Interfa… · L14 public const int Vendor = 0x36B0, Receiver = 0x3002;。 [Urx.cs L21](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Urx.cs#L21)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 The URX Core75 on its 2.4 GHz receiver, 36B0:3002 ("URX Core75 Wireless", measured in HaloBattery #245). That receiver id is [Urx.cs L3](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Urx.cs#L3)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 36B0:3002
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "urx.via" | Output | Request() · Request L15 BrandFamilies.Frame(33, 0x00, 0x08, 0x00, 0x07) | Battery · Battery L17 r[0]，== 0x08，r[1]，== 0x00，r[2]，== 0x07，r[3]，null | 800 ms，2 次 · 去前导零 | [Urx.cs L27](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Urx.cs#L27) |

与模型选择相关的分支可从 [Urx.cs L25](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Urx.cs#L25) 复核。

### Vancer

识别与报告形状涉及 Takes L12 shape.Vendor == Vendor && Products.Contains(shape.Product) && shape.FeatureLength == 65 && MouseShapes.Collection(shape, 0xFFFF, 0) · L10 public const int Vendor = 0x373E; · L11 public static readonly int[] Products = [0x0066, 0x0067];。 [Vancer.cs L13](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Vancer.cs#L13)

- 二次来源 OpenMouse（AGPL-3.0）
- 实机和捕获线索 the 0x83 feature exchange (MouseShapes.Request83), page 0 command 0x83 of Arbiter's Web HUB. OpenMouse's captures [Vancer.cs L5](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Vancer.cs#L5)
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 373E:0066, 373E:0067
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "vancer.f83" | Feature | MouseShapes.Request83() | MouseShapes.Battery83 | 600 ms，2 次 · GapMs 100 · FeatureLength 65 | [Vancer.cs L18](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Vancer.cs#L18) |

与模型选择相关的分支可从 [Vancer.cs L16](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Vancer.cs#L16) 复核。

### VGN V108 / VGN 13 receiver

识别与报告形状涉及 Standard L31 shape.UsagePage == 0x0001 && shape.Usage != 0 \|\| shape.UsagePage == 0x000C · L18 public const int Vendor = 0x391D, Receiver = 0x2A02; · L35 public static readonly (int Vendor, int Product, string Name)[] Receivers = · L44 new("VGN 13 receiver", shape => Receivers.Any(r => r.Vendor == shape.Vendor && r.Product == shape.Product) && MouseShapes.Collect…。 [VgnV108.cs L33](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/VgnV108.cs#L33)

- 二次来源 用户贡献 PR #278
- 实机和捕获线索 off the cable, as in the captured 100 % answer. From pull request #278 (nekox41, measured on a V108 receiver: 100 %). Only [VgnV108.cs L7](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/VgnV108.cs#L7) · report 0x13 with 20-byte reports. Not measured by us. [VgnV108.cs L14](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/VgnV108.cs#L14)
- 本项目已知 ID 读取器 vgn_beiying。
- 核对到的精确身份 258A:024F, 258A:027B, 258A:027F, 391D:024F, 391D:027B, 391D:027F, 391D:258A, 391D:2A01, 391D:2A02, 391D:391D
- 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "vgn-v108.battery" | Output | Request() · Request L19 BrandFamilies.Frame(20, 0x13, 0x4A, 0x01)，frame[19] = Sum(frame) | Battery · Battery L27 r[0]，== 0x13，r[1]，== 0x4A，r[2]，== 0x01，r[3]，== 0x00，r[4]，== 0x02，r[5]，r[19]，r[6]，null | 600 ms，2 次 | [VgnV108.cs L42](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/VgnV108.cs#L42) |
| "vgn.13" | Output | Request() · Request L19 BrandFamilies.Frame(20, 0x13, 0x4A, 0x01)，frame[19] = Sum(frame) | Battery · Battery L27 r[0]，== 0x13，r[1]，== 0x4A，r[2]，== 0x01，r[3]，== 0x00，r[4]，== 0x02，r[5]，r[19]，r[6]，null | 600 ms，2 次 | [VgnV108.cs L46](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/VgnV108.cs#L46) |

与模型选择相关的分支可从 [VgnV108.cs L38](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/VgnV108.cs#L38) 复核。

### WOBKEY / PMO / WOBKEY

识别与报告形状涉及 Takes L72 shape.Vendor == Vendor && BeModels.ContainsKey(shape.Product) && UsagePages.Is(shape.UsagePage, 0xFF60) && shape.Usage == 0x0061 && shape.InputLength == 257 && shape.OutputLength == 257 · IsRdrReceiverName L77 !string.IsNullOrEmpty(name) && !name.Contains("URX", StringComparison.OrdinalIgnoreCase) && RdrReceiverNames.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase)) · TakesRdr L80 shape.Vendor == Vendor && UsagePages.Is(shape.UsagePage, 0xFF60) && shape.Usage == 0x0061 && ((shape.Product == RdrReceiver && IsRdrReceiverName(shape.Name) && shape.InputLength is 33 or 65 &&… · L13 public const int Vendor = 0x36B0, Receiver = 0x3071, Cable = 0x3075, RdrReceiver = 0x3002;。 [WobRainy.cs L73](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/WobRainy.cs#L73)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 文件内未找到明确测量记录。
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 36B0:3002, 36B0:3071, 36B0:3075
- 不移植含状态切换的序列。RDR 前后帧打开与关闭模式，且共享接收器需身份分流。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "wob.be" | Output | Request() · Request L31 BrandFamilies.Frame(257, 0x00, 0xBE, 0xBB, 0x04, 0xED) | Battery · Battery L39 r[0]，== 0x00，r[1]，== 0xBE，r[o]，!= 0xBE，r[o + 1]，!= 0xBB，null，r[o + 2]，r[o + 5]，== 0xED，r[o + 9]，r[o + 13]，r[o + 3]，r[o + 4] | 1000 ms，2 次 · Gate BeModels[shape.Product].Receiver ? OnlineRequest() : null · 去前导零 | [WobRainy.cs L91](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/WobRainy.cs#L91) |
| "wob.rdr" | Output | RdrFrame(shape.OutputLength, 0x30, 0x18) · RdrFrame L53 BrandFamilies.Frame(outputLength, 0x00, 0xAA, command, 0x00, 0x00, size) | RdrBattery · RdrBattery L65 r[0]，== 0x00，r[1]，== 0xAA，r[o]，!= 0xAA，r[o + 1]，!= 0x30，r[o + 2]，!= 0x00，r[o + 3]，r[o + 4]，!= 0x18，r[o + 7]，!= 0x55，null，r[o + 8]，r[o + 9] | 800 ms，2 次 · GapMs 30 · Before [RdrFrame(shape.OutputLength, 0x10, chunk)] · After [RdrFrame(shape.OutputLength, 0x11, chunk)] · Gate shape.Product == RdrReceiver ? RdrOnlineRequest(shape.OutputLength) : null · 去前导零 | [WobRainy.cs L109](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/WobRainy.cs#L109) |

与模型选择相关的分支可从 [WobRainy.cs L87](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/WobRainy.cs#L87) 复核。

### XIBERIA

识别与报告形状涉及 Takes L16 s.Vendor == Vendor && HeadsetPages.Vendor(s) && s.InputLength >= 64 && s.OutputLength >= 64 · L12 public const int Vendor = 0x36F9;。 [Xiberia.cs L17](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Xiberia.cs#L17)

- 二次来源 未单列二次来源。按 BrimDeck Apache-2.0 和第三方说明核对。
- 实机和捕获线索 文件内未找到明确测量记录。
- 本项目已知 ID 读取器 无（按列出的具体 ID）。
- 核对到的精确身份 按配置函数检查，缺明确可独立统计的 VID:PID。
- 需核对。原规则按 36F9 整个 VID，FF82/FF90 共享，需按可证型号、长度及安全查询收窄。

| Way | 传输 | 请求构造 | 解析与字段位置 | 等待、尝试和前后帧 | 源码位置 |
| --- | --- | --- | --- | --- | --- |
| "xiberia.battery" | Output | Request() · Request L13 BrandFamilies.Frame(4, 0x55, 0x65, 0x00, 0x00) | Battery · Battery L15 r[0]，== 0x55，r[1]，== 0x65，r[2]，== 0x00，r[3]，null | 800 ms，2 次 | [Xiberia.cs L21](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Xiberia.cs#L21) |

与模型选择相关的分支可从 [Xiberia.cs L19](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Xiberia.cs#L19) 复核。

### 共用形状代码

[HeadsetPages.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/HeadsetPages.cs#L1) 提供厂商页与字节序匹配。[MouseShapes.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MouseShapes.cs#L1) 提供鼠标集合、Feature 请求及规范化。该两文件是复用 helper。

## 共用实现的独立家族

| 家族 | 独立的识别范围 | Way 与限制 |
| --- | --- | --- |
| Audeze | 3329 的 Maxwell / Maxwell 2，不同 PID 走相应集合与长度 | Airoha RACE，仅有线型号走 CableBattery，NoHeadset 命中时停止。 [AirohaRace.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L63) |
| AKG | 0ECB:20D9，FF13，接口 3 或未知 | 精确 AkgN9 选择 akg.race。 [AirohaRace.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L69) |
| Skullcandy | 34F0 的 Plyr / Crusher Plyr 两款，选择相应集合 | skullcandy.race，保留不同回包边界。 [AirohaRace.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L70) |
| ATK HFD | 373B 的 HfdProducts，FF60:0061 | atk.hfd，只按 HfdBattery。 [AtkOfficial.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L91) |
| Bytech | BytechReceivers 中明确的配对字，与 FF60:0061 输出至少 64 B | 最靠前的非零 Output ID 选定读法。 [AtkOfficial.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L93) |
| ATK Nearlink | 373B 的 NearlinkProducts，FFB2:0001 | 选择具有相应报告的近链电量询问。 [AtkOfficial.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L96) |
| ATK Evision V2 | 单列 YogoProducts，FF60 路径 | Before 进入会话，该分支只保留线索。 [AtkOfficial.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L102) |
| ATK HECHI | HechiProducts，FFA0:0002 | 1147 使用 HechiCableBattery，各接收器使用 HechiBattery。 [AtkOfficial.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L108) |
| AULA / GANSS | 仅借用 05AC:024F 的已知产品串与相符集合 | Sonix 33 B 查询，需按共享 ID 限定产品。 [SonixQuery.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L89) |
| EWEADN | 0C45:FEFE，Sonix 集合与接口 3 | 相同帧族在独立端点匹配。 [SonixQuery.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L91) |
| 华奋达 | HuafendaProducts，FF60 与 33/65 B，要求身份 | 复用 HuafendaWay，对目标产品做专用状态读取。 [SonixQuery.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L64) |
| Keychron 鼠标 | 3434，FFC1:0001，排除 NordicProducts | b3 有 Output 与 FeatureListen 两路。 [Keychron.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L52) |
| 合作品牌鼠标 | Partners 表中每条明确的 VID:PID 和页条件 | 该品牌本身的包装名，统一使用 B3 输出。 [Keychron.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L42) |
| VGN 鼠标 | 391D 的 VgnReceivers，FFC1:0001 | 在该命名分支发送 B3 并读 B4。 [Keychron.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L64) |
| AJAZZ 容源 | 3151:4011，FFFF:0002，Feature 65 B | 选择带校验的 F7。 [RongyuanStatus.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L89) |
| CAROTMAS 容源 | 3151:502B，同一集合和长度 | 选择带校验的 F7。 [RongyuanStatus.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L90) |
| MonsGeek 容源 | 3151:5026 / 5038，同集合和长度 | F7 直接发送，已证产品和不同接收器独立。 [RongyuanStatus.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L91) |
| VGN 容源 | 391D:2A05 / 2A07、3151:5055 与型号表的实证目标 | F7 带校验，记录内有 2A05 采集读数。 [RongyuanStatus.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L92) |
| 共享容源接收器 | Shared 表中的多款接收器 | 按 MouseReceivers 区分回复字段，保留身份分流。 [RongyuanStatus.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L70) |
| XIBERIA | 源码将 36F9 整 VID 限在可读写厂商集合，输入输出至少 64 B | 输出之后确认 55 65 00 与第 3 字节 1–100，暂不扩展到本项目共享 ID。与 B1 中 #528 的 87% 属于两条证据。 [Xiberia.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Xiberia.cs#L12) |

后续筛选时，先查容源的具名型号和 XIBERIA 的准确接收器身份。NuPhy 此处是私有 Power Memory 的 0001:0000 报告，仍不能作为通用 FF60:0061 读法。 [NuPhy.cs](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NuPhy.cs#L93)

## DeviceProtocols.cs 的共用和既有直连路径

| 代码类 | 设备匹配和解码路径 | 取舍 |
| --- | --- | --- |
| Hidpp | 046D，接收器配对槽与直连分开，HID++ 10/11 报告。Feature 1000、1001、1004 及 ADC 等读取状态或电压。 [DeviceProtocols.cs L61](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L61) | 需核对。寄存器写与通知开关和电量读拆开，按 logitech 既有门禁。 |
| Razer | 1532 的具体 Model 表，90 B Feature、事务号、07:80 电量、07:84 充电。 [DeviceProtocols.cs L210](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L210) | 需核对。项目 razer 已有路径，新增 PID 必须精确验证。 |
| Dongles | 包含 ATK、VGN、AULA、CompX、VAXEE、NearLink、迈从、英菲克、AJAZZ 的既有构造和解码。CompX 17 B 链、分离报告 08/09、A5 与容源等各有自己的表。 [DeviceProtocols.cs L297](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L297) | 需核对。电量方法与写设置方法分开，比较 pulsar、mchose、vgn_ry_ms、aula_keyboard。 |
| Asus | 0B05 的 Model/OmniModels 区分键盘、鼠标、耳机。OMNI 读配对身份表后分派 01/07 与专用充电请求。 [DeviceProtocols.cs L1366](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L1366) | 需核对。已有 asus_azoth、asus_omni、asus_headset。 |
| SteelSeriesMice | 1038 的 Model/Catalog，按具体鼠标电量方法及回复帧。 [DeviceProtocols.cs L1510](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L1510) | 需核对。原实现来源 rivalcfg（WTFPL-2.0），保留型号依赖。 |
| Pushes | 按不同厂商的主动报文格式验证 HID report 和电量字段。 [DeviceProtocols.cs L1589](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L1589) | 需核对。已有 rapoo 和 fa6x_passive，逐个对照报告。 |
| Corsair | 1B1C 的 Bragi 具体型号、路由与报文长度，状态与充电各走相应属性。 [DeviceProtocols.cs L1701](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L1701) | 需核对。维持已证设备和会话范围。 |
| Headsets | 多种 HyperX 与 Kingston 型号按 Kind 分配请求、连接/充电字段和输入。 [DeviceProtocols.cs L1778](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L1778) | 需核对。已有 hyperx 相关读取器。 |
| BatteryService | 系统电量服务路径。通信由平台处理。 [DeviceProtocols.cs L1922](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L1922) | 需核对。保留系统与设备字段区分。 |
| PlayStation | 按 054C 具体 PlayStation 类型与连接读取 Input 电量。 [DeviceProtocols.cs L1936](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L1936) | 需核对。B2-7 同时受未注册索引阻塞。 |
| HidBattery | 标准 Power/Battery HID 字段及范围换算。 [DeviceProtocols.cs L1977](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L1977) | 需核对。已有 standard_hid。 |
| AppleAdvert | Apple 周边广告字段与左右部件。 [DeviceProtocols.cs L1993](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/DeviceProtocols.cs#L1993) | 需核对。按系统暴露数据和版本复核。 |

## 移植优先级

按固定 T00 的开放 Issue 去重数降序。全表只计源证据中可确认的具体 ID，VID-wide 及未拆开共享目录的行列在末尾。同一条反馈可能同时出现在两行。HTTP 目录和协议报告需要按产品名及连接再复核。

| 家族 | Issue 数 | 对应 ID 范围 | MyFinder 已列读取器 | 建议 |
| --- | ---: | --- | --- | --- |
| AULA / GANSS / EWEADN / Huafenda keyboard [SonixQuery.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonixQuery.cs#L1) | 71 · [#77](https://github.com/wushuai0802/myfinder-device-battery/issues/77), [#114](https://github.com/wushuai0802/myfinder-device-battery/issues/114), [#126](https://github.com/wushuai0802/myfinder-device-battery/issues/126), [#163](https://github.com/wushuai0802/myfinder-device-battery/issues/163), [#207](https://github.com/wushuai0802/myfinder-device-battery/issues/207), [#211](https://github.com/wushuai0802/myfinder-device-battery/issues/211), [#226](https://github.com/wushuai0802/myfinder-device-battery/issues/226) | 05AC:024F, 0C45:FEFE | aula_keyboard、eweadn_x87 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Lofree [Lofree.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Lofree.cs#L1) | 35 · [#77](https://github.com/wushuai0802/myfinder-device-battery/issues/77), [#114](https://github.com/wushuai0802/myfinder-device-battery/issues/114), [#126](https://github.com/wushuai0802/myfinder-device-battery/issues/126), [#163](https://github.com/wushuai0802/myfinder-device-battery/issues/163), [#207](https://github.com/wushuai0802/myfinder-device-battery/issues/207), [#211](https://github.com/wushuai0802/myfinder-device-battery/issues/211), [#226](https://github.com/wushuai0802/myfinder-device-battery/issues/226) | 05AC:024F | 无 | 需核对。共享 05AC:024F 需要完全相符的产品串、父设备及报告证明。 |
| EWEADN mouse [Eweadn.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Eweadn.cs#L1) | 25 · [#110](https://github.com/wushuai0802/myfinder-device-battery/issues/110), [#136](https://github.com/wushuai0802/myfinder-device-battery/issues/136), [#149](https://github.com/wushuai0802/myfinder-device-battery/issues/149), [#174](https://github.com/wushuai0802/myfinder-device-battery/issues/174), [#200](https://github.com/wushuai0802/myfinder-device-battery/issues/200), [#203](https://github.com/wushuai0802/myfinder-device-battery/issues/203), [#266](https://github.com/wushuai0802/myfinder-device-battery/issues/266) | 088D:062E, 089D:062F, 372E:1026 | eweadn、eweadn_s9pro | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| AJAZZ / CAROTMAS / MonsGeek / VGN / Rongyuan receiver [RongyuanStatus.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RongyuanStatus.cs#L1) | 21 · [#126](https://github.com/wushuai0802/myfinder-device-battery/issues/126), [#219](https://github.com/wushuai0802/myfinder-device-battery/issues/219), [#226](https://github.com/wushuai0802/myfinder-device-battery/issues/226), [#234](https://github.com/wushuai0802/myfinder-device-battery/issues/234), [#297](https://github.com/wushuai0802/myfinder-device-battery/issues/297), [#403](https://github.com/wushuai0802/myfinder-device-battery/issues/403), [#450](https://github.com/wushuai0802/myfinder-device-battery/issues/450) | 0DB0:197E, 0DB0:8E9A, 145F:0376, 145F:038D, 25AA:2008, 25AA:4008, 25AA:4018, 3151:4011, 3151:5026, 3151:502B, 3151:5038, 3151:5055, 347A:A302, 347A:A305, 379A:1804, 379A:1807, 38A9:0002, 38EE:0000, 38EE:0002, 38EE:0004, 391D:1A04, 391D:1A08, 391D:1A0E, 391D:1A13, 391D:1A25, 391D:1A2B, 391D:1A41, 391D:2A05, 391D:2A07 | veekos_shine60、vgn_ry_ms | 需核对。部分精确型号已有采集，按接收器产品与下游身份分别验证校验和及回复。优先 3151:4011、3151:5038、391D:2A07、3151:5055。 |
| MCHOSE V9 Pro [MchoseV9Pro.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseV9Pro.cs#L1) | 16 · [#140](https://github.com/wushuai0802/myfinder-device-battery/issues/140), [#169](https://github.com/wushuai0802/myfinder-device-battery/issues/169), [#189](https://github.com/wushuai0802/myfinder-device-battery/issues/189), [#197](https://github.com/wushuai0802/myfinder-device-battery/issues/197), [#204](https://github.com/wushuai0802/myfinder-device-battery/issues/204), [#304](https://github.com/wushuai0802/myfinder-device-battery/issues/304), [#305](https://github.com/wushuai0802/myfinder-device-battery/issues/305) | 291D:385D | mchose_headset | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Ryunix [Ryunix.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Ryunix.cs#L1) | 7 · [#80](https://github.com/wushuai0802/myfinder-device-battery/issues/80), [#104](https://github.com/wushuai0802/myfinder-device-battery/issues/104), [#240](https://github.com/wushuai0802/myfinder-device-battery/issues/240), [#359](https://github.com/wushuai0802/myfinder-device-battery/issues/359), [#397](https://github.com/wushuai0802/myfinder-device-battery/issues/397), [#760](https://github.com/wushuai0802/myfinder-device-battery/issues/760), [#843](https://github.com/wushuai0802/myfinder-device-battery/issues/843) | 04F3:026E, 04F3:026F | eweadn_x23 | 可直接移植。只收已有 Input 报告。固定 04F3:026E/026F 与集合保留，由 OpenMouse 采集对照。 |
| URX [Urx.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Urx.cs#L1) | 7 · [#78](https://github.com/wushuai0802/myfinder-device-battery/issues/78), [#121](https://github.com/wushuai0802/myfinder-device-battery/issues/121), [#178](https://github.com/wushuai0802/myfinder-device-battery/issues/178), [#412](https://github.com/wushuai0802/myfinder-device-battery/issues/412), [#517](https://github.com/wushuai0802/myfinder-device-battery/issues/517), [#844](https://github.com/wushuai0802/myfinder-device-battery/issues/844), [#884](https://github.com/wushuai0802/myfinder-device-battery/issues/884) | 36B0:3002 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| WOBKEY / PMO / WOBKEY [WobRainy.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/WobRainy.cs#L1) | 7 · [#78](https://github.com/wushuai0802/myfinder-device-battery/issues/78), [#121](https://github.com/wushuai0802/myfinder-device-battery/issues/121), [#178](https://github.com/wushuai0802/myfinder-device-battery/issues/178), [#412](https://github.com/wushuai0802/myfinder-device-battery/issues/412), [#517](https://github.com/wushuai0802/myfinder-device-battery/issues/517), [#844](https://github.com/wushuai0802/myfinder-device-battery/issues/844), [#884](https://github.com/wushuai0802/myfinder-device-battery/issues/884) | 36B0:3002, 36B0:3071, 36B0:3075 | 无 | 不移植含状态切换的序列。RDR 前后帧打开与关闭模式，且共享接收器需身份分流。 |
| ATK Headset [AtkHeadsets.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkHeadsets.cs#L1) | 5 · [#173](https://github.com/wushuai0802/myfinder-device-battery/issues/173), [#175](https://github.com/wushuai0802/myfinder-device-battery/issues/175), [#233](https://github.com/wushuai0802/myfinder-device-battery/issues/233), [#422](https://github.com/wushuai0802/myfinder-device-battery/issues/422), [#589](https://github.com/wushuai0802/myfinder-device-battery/issues/589) | 373B:1262, 373B:12D4, 373B:12D6 | atk_headset | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Darmoshark [Darmoshark.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darmoshark.cs#L1) | 5 · [#119](https://github.com/wushuai0802/myfinder-device-battery/issues/119), [#438](https://github.com/wushuai0802/myfinder-device-battery/issues/438), [#689](https://github.com/wushuai0802/myfinder-device-battery/issues/689), [#819](https://github.com/wushuai0802/myfinder-device-battery/issues/819), [#978](https://github.com/wushuai0802/myfinder-device-battery/issues/978) | 1915:0723, 1915:073C, 248A:FF10, 248A:FF12, 248A:FF30 | 无 | 不移植含状态切换的序列。有绑定/收发前置路径，需先单独证明其影响。 |
| RK [RkKeyboard.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RkKeyboard.cs#L1) | 5 · [#322](https://github.com/wushuai0802/myfinder-device-battery/issues/322), [#357](https://github.com/wushuai0802/myfinder-device-battery/issues/357), [#605](https://github.com/wushuai0802/myfinder-device-battery/issues/605), [#651](https://github.com/wushuai0802/myfinder-device-battery/issues/651), [#720](https://github.com/wushuai0802/myfinder-device-battery/issues/720) | 258A:01BB, 258A:0233 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| ATK HFD keyboard / Bytech keyboard / ATK Nearlink mouse / ATK Evision V2 keyboard / ATK HECHI mouse [AtkOfficial.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AtkOfficial.cs#L1) | 4 · [#147](https://github.com/wushuai0802/myfinder-device-battery/issues/147), [#301](https://github.com/wushuai0802/myfinder-device-battery/issues/301), [#556](https://github.com/wushuai0802/myfinder-device-battery/issues/556), [#589](https://github.com/wushuai0802/myfinder-device-battery/issues/589) | 373B:108B, 373B:108C, 373B:111E, 373B:111F, 373B:1147, 373B:1148, 373B:114D, 373B:114E, 373B:1151, 373B:1152, 373B:1156, 373B:1179, 373B:1196, 373B:119B, 373B:119C, 373B:11B5, 373B:11FF, 373B:1200, 373B:1231 | 无 | 不移植含状态切换的序列。Yogo 读取前发送启动报告，需确认回退和单独读取。 |
| VGN V108 / VGN 13 receiver [VgnV108.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/VgnV108.cs#L1) | 4 · [#97](https://github.com/wushuai0802/myfinder-device-battery/issues/97), [#251](https://github.com/wushuai0802/myfinder-device-battery/issues/251), [#493](https://github.com/wushuai0802/myfinder-device-battery/issues/493), [#530](https://github.com/wushuai0802/myfinder-device-battery/issues/530) | 258A:024F, 258A:027B, 258A:027F, 391D:024F, 391D:027B, 391D:027F, 391D:258A, 391D:2A01, 391D:2A02, 391D:391D | vgn_beiying | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| AULA mouse / AULA headset [Aula.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Aula.cs#L1) | 3 · [#657](https://github.com/wushuai0802/myfinder-device-battery/issues/657), [#691](https://github.com/wushuai0802/myfinder-device-battery/issues/691), [#963](https://github.com/wushuai0802/myfinder-device-battery/issues/963) | 258A:0138, 38A6:001D, 38A6:28FD | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Incott [Incott.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Incott.cs#L1) | 3 · [#724](https://github.com/wushuai0802/myfinder-device-battery/issues/724), [#838](https://github.com/wushuai0802/myfinder-device-battery/issues/838), [#907](https://github.com/wushuai0802/myfinder-device-battery/issues/907) | 093A:522C, 093A:622C | 无 | 可直接移植。对确切集合执行被动输入，保留 04/FF 等模式判别与原通知长度。MouseDock MIT 来源可核对。 |
| Razer BlackShark V3 [RazerBlackSharkV3.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerBlackSharkV3.cs#L1) | 3 · [#198](https://github.com/wushuai0802/myfinder-device-battery/issues/198), [#302](https://github.com/wushuai0802/myfinder-device-battery/issues/302), [#334](https://github.com/wushuai0802/myfinder-device-battery/issues/334) | 1532:0576, 1532:0577, 1532:0579, 1532:057A | 无 | 不移植含状态切换的序列。初始化与唤醒帧随读取发送，保留协议事实，需另找单独读法。 |
| Sony INZONE [SonyInzone.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyInzone.cs#L1) | 3 · [#330](https://github.com/wushuai0802/myfinder-device-battery/issues/330), [#813](https://github.com/wushuai0802/myfinder-device-battery/issues/813), [#893](https://github.com/wushuai0802/myfinder-device-battery/issues/893) | 054C:0EBF, 054C:0FA8 | sony_inzone | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| ASTRO [AstroA50.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AstroA50.cs#L1) | 1 · [#343](https://github.com/wushuai0802/myfinder-device-battery/issues/343) | 046D:0B1C | astro | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| ASTRO A50 Gen 4 [AstroA50Gen4.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AstroA50Gen4.cs#L1) | 1 · [#343](https://github.com/wushuai0802/myfinder-device-battery/issues/343) | 9886:002C | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Logitech Centurion [LogitechCenturion.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/LogitechCenturion.cs#L1) | 1 · [#370](https://github.com/wushuai0802/myfinder-device-battery/issues/370) | 046D:0AF7 | logitech_centurion | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Razer Headset [RazerHeadsets.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/RazerHeadsets.cs#L1) | 1 · [#575](https://github.com/wushuai0802/myfinder-device-battery/issues/575) | 1532:0528, 1532:053A, 1532:056C | barracuda、razer | 不移植含状态切换的序列。PA / Barracuda 包含会话开关，仅保留已知独立只读路径的线索。 |
| Redragon mouse [Redragon.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Redragon.cs#L1) | 1 · [#311](https://github.com/wushuai0802/myfinder-device-battery/issues/311) | 258A:002E, 258A:002F | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| 8BitDo [EightBitDo.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/EightBitDo.cs#L1) | 0 | 2DC8:202F, 2DC8:6000, 2DC8:6001, 2DC8:6003, 2DC8:6006, 2DC8:6009, 2DC8:6012, 2DC8:6100, 2DC8:6101 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| ASUS Headset [AsusStrixGo.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AsusStrixGo.cs#L1) | 0 | 0B05:18D6, 0B05:18D7 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Alienware [Alienware.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Alienware.cs#L1) | 0 | 413C:211D | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Audeze / AKG / Skullcandy [AirohaRace.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/AirohaRace.cs#L1) | 0 | 0ECB:20D9, 3329:4B18, 3329:4B19, 3329:4B1A, 3329:4B28, 3329:4B29, 34F0:3210, 34F0:5310 | audeze | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Corsair Void [CorsairVoid.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/CorsairVoid.cs#L1) | 0 | 1B1C:0A0C, 1B1C:0A14, 1B1C:0A16, 1B1C:0A1A, 1B1C:0A2B, 1B1C:0A38, 1B1C:0A4F, 1B1C:0A51, 1B1C:0A55, 1B1C:0A75, 1B1C:1B23, 1B1C:1B25, 1B1C:1B27 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| DARKRIM [Darkrim.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Darkrim.cs#L1) | 0 | 3654:6901 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| EDIFIER [Edifier.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Edifier.cs#L1) | 0 | 2D99:E049 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| FIFINE [Fifine.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Fifine.cs#L1) | 0 | 3142:00C3 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Finalmouse [Finalmouse.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Finalmouse.cs#L1) | 0 | 361D:0100 | 无 | 需核对。VID 与 FF00 集合筛选覆盖大量运输，必须限制固件和实际报告。 |
| Flydigi [Flydigi.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Flydigi.cs#L1) | 0 | 37D7:2502 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| GameSir [GameSir.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/GameSir.cs#L1) | 0 | 3537:103D | 无 | 不移植含状态切换的序列。Heartbeat 路径写后监听，对报文作用与唤醒影响需要额外证据。 |
| Lenovo Headset [LenovoHeadsets.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/LenovoHeadsets.cs#L1) | 0 | 17EF:A07D | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| MCHOSE G7 MAX [MchoseG7Max.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/MchoseG7Max.cs#L1) | 0 | 41E4:1001, 41E4:1003, 41E4:1204 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| NVIDIA [NvidiaShield.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NvidiaShield.cs#L1) | 0 | 0955:7214 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Pulsar [Pulsar.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Pulsar.cs#L1) | 0 | 3710:3402, 3710:5403 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Scyrox [Scyrox.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Scyrox.cs#L1) | 0 | 395A:0006, 395A:000B, 395A:000C, 395A:000E | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Sony PULSE [SonyPulse.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SonyPulse.cs#L1) | 0 | 054C:0ECC, 054C:0ED0 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Vancer [Vancer.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Vancer.cs#L1) | 0 | 373E:0066, 373E:0067 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| metapanda.status / Metapanda / NearLink receiver [Metapanda.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Metapanda.cs#L1) | 0 | 369B:00B3 | 无 | 需核对。泛 NearLink 分支按已有集合与输入形状，要求原厂型号与报告门禁。 |
| Thunderobot [Thunderobot.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Thunderobot.cs#L1) | 0 | 369B:0234 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| BETOP [Betop.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Betop.cs#L1) | 待定 | 按运行时形状 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Glorious [Glorious.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Glorious.cs#L1) | 待定 | 按运行时形状 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| HyperX mouse [HyperX.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/HyperX.cs#L1) | 待定 | 按运行时形状 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| Keychron mouse / VGN mouse / Partners 表 [Keychron.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Keychron.cs#L1) | 待定 | 按运行时形状 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| NuPhy [NuPhy.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/NuPhy.cs#L1) | 待定 | 按运行时形状 | 无 | 需核对。有私有 Power Memory 读取，接口为 0001:0000、65 B。须按目标产品限制，和 B2-5 的 FF60 范围分开。 |
| SteelSeries Headset [SteelSeriesHeadsets.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/SteelSeriesHeadsets.cs#L1) | 待定 | 按运行时形状 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |
| XIBERIA [Xiberia.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Xiberia.cs#L1) | 待定 | 按运行时形状 | 无 | 需核对。原规则按 36F9 整个 VID，FF82/FF90 共享，需按可证型号、长度及安全查询收窄。 |
| Jabra [Jabra.cs L1](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck.Core/Brands/Jabra.cs#L1) | 待定 | 按运行时形状 | 无 | 需核对。核对实际固件与逐报告长度，保持最小读取路径和已知报告字段。 |

后续由 Claude 按任务书的冻结范围和实机证据安排移植。
