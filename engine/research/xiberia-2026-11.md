# 西伯利亚耳机调查（2026-11）

基线 `b70b621921c11ac65172e3d63fa50d64b2153b9d`。查阅公开报告、发布源码及驱动入口。现有证据没有达到“可实现”的标准，候选需补到具体产品串、接口、报告和可对照电量。

## 结论总表

| VID:PID | 型号或产品串 | 结论 | 依据 | 若可实现，复用读取器 |
| --- | --- | --- | --- | --- |
| `10D6:4801` | XIBERIA K03S（部分用户写 K30S） | 需用户资料 | 多次诊断同集合，缺准确驱动读数和回复。[#954](https://github.com/wushuai0802/myfinder-device-battery/issues/954)、[#215](https://github.com/wushuai0802/myfinder-device-battery/issues/215)、[#427](https://github.com/wushuai0802/myfinder-device-battery/issues/427)、[#742](https://github.com/wushuai0802/myfinder-device-battery/issues/742) | 待确定 |
| `10D6:4801` | K02BS | 需用户资料 | 已有精确型号和接口长度，两厂商集合各被动等候 6 秒均 0 帧。[#736](https://github.com/wushuai0802/myfinder-device-battery/issues/736)、[#789](https://github.com/wushuai0802/myfinder-device-battery/issues/789)、[#908](https://github.com/wushuai0802/myfinder-device-battery/issues/908) | 待确定 |
| `36F9:C001` | K03S / XIBERIA K03S Wireless 7.1 | 需用户资料 | #528 有用户环境中的 87% 当前读数，缺实现、报文和独立对照。#212 的产品串与集合见下文。[#528](https://github.com/wushuai0802/myfinder-device-battery/issues/528)、[#212](https://github.com/wushuai0802/myfinder-device-battery/issues/212) | 待确定 |
| `36F9:C001` | K05S / XIBERIA S21 Wireless 7.1 | 需用户资料 | 同 PID，厂商集合有差异，缺各自电量报文。[#569](https://github.com/wushuai0802/myfinder-device-battery/issues/569)、[#500](https://github.com/wushuai0802/myfinder-device-battery/issues/500) | 待确定 |
| `36F9:F016` | XIBERIA K03S（含反馈称 K30S 电竞版的接收器） | 需用户资料 | #56 记录左耳壳显示、受测路径暂无回应。具体电量接口和报文未证。[#56](https://github.com/wushuai0802/myfinder-device-battery/issues/56)、[#259](https://github.com/wushuai0802/myfinder-device-battery/issues/259) | 待确定 |
| `36F9:F002` | XIBERIA K06S Wireless | 需用户资料 | #846 给出 2.4G 身份和 Windows 音频端点，#852 的耳机读取器为 unavailable。[#846](https://github.com/wushuai0802/myfinder-device-battery/issues/846)、[#852](https://github.com/wushuai0802/myfinder-device-battery/issues/852) | 待确定 |
| `36F9:F001` | XIBERIA T10 Wireless（#271 的关联型号） | 需用户资料 | 官方页面有 T10 系列驱动线索，具体版本电量路径未证。[#225](https://github.com/wushuai0802/myfinder-device-battery/issues/225)、[#271](https://github.com/wushuai0802/myfinder-device-battery/issues/271) | 待确定 |

## A 组 10D6:4801

- [#954](https://github.com/wushuai0802/myfinder-device-battery/issues/954) 汇集 #215、#231、#427、#742、#774、#788、#817 的 `10D6:4801 "XIBERIA K03S" 000B:0001 000C:0001 FF82:0001 FF90:0001`。这些诊断给出接收器身份线索，暂无对应官方软件版本、电脑端读数或可解析报文。
- [#789](https://github.com/wushuai0802/myfinder-device-battery/issues/789)、[#908](https://github.com/wushuai0802/myfinder-device-battery/issues/908) 的原始诊断行是 `10D6:4801 "K02BS" 000B:0001 000C:0001 FF82:0001 FF90:0001`。#789 记录 MI_00 有四个 HID 集合，MI_01 为 USB Audio。当时使用系统音频驱动。#908 给出 Actions、`REV_1505`。[#736](https://github.com/wushuai0802/myfinder-device-battery/issues/736) 记录两厂商集合均为输入/输出 64 字节，分别被动监听 6 秒均 0 帧。K02BS 官方软件版本、实际读数、耳机回包均无。该帖的键盘屏显和其他集合数据属于该帖的另两台设备。
- [#108](https://github.com/wushuai0802/myfinder-device-battery/issues/108) 的 `10D6:4801 "Baseus AeQur G10" 000B:0001 000C:0001 FF82:0001 FF90:0001` 为倍思耳机。82% 和 86% 对照属于同帖的 SCC100 键盘。共享 ID 与集合不能用于配对这些型号。

## B 组 36F9:C001、F016

- [#212](https://github.com/wushuai0802/myfinder-device-battery/issues/212) 的诊断为 `36F9:C001 "XIBERIA K03S Wireless 7.1" 000B:0001 000C:0001 FF21:0001 FF22:0001 FF82:0001 FF90:0001`。[#500](https://github.com/wushuai0802/myfinder-device-battery/issues/500) 的 S21 有同一组集合，[#569](https://github.com/wushuai0802/myfinder-device-battery/issues/569) 的 `36F9:C001 "K05S" 000B:0001 000C:0001 000C:0001 FF22:0001` 少了多项厂商集合。各款缺少官方软件版本、读数与回复。
- [#528](https://github.com/wushuai0802/myfinder-device-battery/issues/528) 写明用户自行处理过，并显示 `XIBERIA K03S | headset | mchose_headset | 87% | 当前读数：是 | 36F9:C001`。这是值得跟进的单次读数。核对官方 v1.3.9 提交 `bbcb779be4633ecf9183f53409de118dc9cb1fd6` 与当前基线，均没有该 PID 的迈从耳机适配。因此无法用官方版本字符串、读取器名或 87% 反推出请求和字段。缺少用户改动的源码或固定提交、逐报告长度和与耳机显示的读数对照。[当前型号表](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/mchose_headset.py#L40-L50)
- [#56](https://github.com/wushuai0802/myfinder-device-battery/issues/56) 和 [#259](https://github.com/wushuai0802/myfinder-device-battery/issues/259) 均列 `36F9:F016 "XIBERIA K03S" 000B:0001 000C:0001 FF82:0001 FF90:0001`。#56 用户报告耳机小屏和曾经 20 秒被动监听零上报，也表示未能取得回复。官方软件版本、可对照的电脑端百分比与原始回包没有附出。#56 的后续正式版模板涉及同帖其他设备，当前代码没有这一 PID。仓库已归档的 Linux `36F9:C009` 修复面向 USB 音频，适用 ID 和功能均有区别。[既有研究](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/research/batch4-protocols.md#L186-L201)

## C 组 36F9:F002、F001

- [#846](https://github.com/wushuai0802/myfinder-device-battery/issues/846) 明确 K06S 无线接收器 ID `36F9:F002`，Windows 端点为 XIBERIA K06S Wireless。反馈中 XIBERIA HUB 读数未确认。[#852](https://github.com/wushuai0802/myfinder-device-battery/issues/852) 给出 `36F9:F002 "XIBERIA K06S Wireless" 000B:0001 000C:0001 FF82:0001 FF90:0001` 及 `xiberia_k06s | unavailable |`。同帖 72% 是机械师 L8 Max 鼠标。官方 v1.3.10 提交 `297b7991194171c130928849a3ba0b6e249c385d` 未注册这个读取器。两帖未附 K06S 电量回复或实现。
- [#225](https://github.com/wushuai0802/myfinder-device-battery/issues/225) 的 T10 诊断为 `36F9:F001 "XIBERIA T10 Wireless" 000B:0001 000C:0001 FF82:0001 FF90:0001`。[#271](https://github.com/wushuai0802/myfinder-device-battery/issues/271) 给出官方链接及 T10 系列、K06S、K03S 超竞版等支持列表。未给出下载包版本、对应 PID 与可对照电量。T10 的型号与 K06S 分开保留，支持列表也没有证明这些型号共用电量协议。

## 来源与许可证

| 来源 | 本次获取的证据 | 版本、许可 |
| --- | --- | --- |
| [西伯利亚官网](https://www.xiberia.net/) | 首页 HTTP 200，导航明确链接电竞耳机驱动 h-col-181、服务支持 h-col-105，以及单列的电竞耳塞网页驱动。耳机下载页及服务页 HTTP 204 且正文空。英文路径及域名变体是 204 或 TLS 失败。未获取官方程序或协议文本。 | 首页响应文本按 UTF-8 重编码 SHA-256 `0a8bbe19e8d34a56cc558a3cd3c0a27c447c3a499ed6c0ecae32649c7679996c`。驱动版本与许可证不确定。 |
| [官方耳机驱动入口](https://www.xiberia.net/h-col-181.html) 与 [#271](https://github.com/wushuai0802/myfinder-device-battery/issues/271)、[#736](https://github.com/wushuai0802/myfinder-device-battery/issues/736) | 确认入口和用户报告的型号线索。K02BS 下载项由 #736 查阅时未发现。 | 仅记录事实和链接。 |
| [HeadsetControl](https://github.com/Sapd/HeadsetControl/tree/163c6edf744c28804d95e3653e5ea7bc9486b46c) | 核对固定提交文件树和 XIBERIA 代码搜索。未取得这些精确 ID 的电量实现。对 XIBERIA、36F9/C001/F016/F002、10D6/4801 的独立代码搜索也未取到协议。 | `163c6edf744c28804d95e3653e5ea7bc9486b46c`，GPL-3.0（仓库许可证）。 |
| [Linux USB 声音修复](https://git.zx2c4.com/linux-rng/commit/sound/usb?id=15c0f794acc900cc71b5a54b35c864c88e4ec8bc) | 仓库既有调查所援引的 C009 音频差异线索。 | `15c0f794acc900cc71b5a54b35c864c88e4ec8bc`，GPL-2.0-or-later（固定提交 sound/usb/quirks.c 的 SPDX），仅引用。 |
| [#954](https://github.com/wushuai0802/myfinder-device-battery/issues/954)、[#528](https://github.com/wushuai0802/myfinder-device-battery/issues/528)、[#852](https://github.com/wushuai0802/myfinder-device-battery/issues/852) 等公开反馈 | 只摘身份、集合与用户报告值。 | 反馈按公开链接引用，用户改动源码与许可不确定。 |

## 未确认事项

最高优先线索是 #528。可复核的最小补件是对应改动的源码或提交、精确产品串和设备的两个不同上报电量与屏显对照，附涉及集合及逐报告长度。次优先是 #852 的 K06S 读取器来源和一次真实耳机回包。若官方驱动文件后来可取得，先静态核对设备表、读取链和许可。10D6:4801 按 K03S、K02BS 及共享 VID/PID 的其他厂商分别核实。

**⛔ 检查点**。交 Claude 核对证据分级，收到相应协议及维护者确认后，再决定是否有下一阶段。
