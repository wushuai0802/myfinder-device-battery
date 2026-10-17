# 通用版兼容范围

目标是让兼容清单内的设备接上就用：自动枚举、匹配内置读取器并生成卡片，不让最终用户填写协议参数、安装Python或录制USB数据。

目前为1.3.0-beta.2。接入26个通用读取器，加上原有ATK A9 Air+与HyperX Cloud Alpha Wireless专用读取。没有统一协议能覆盖所有2.4G外设；以下是协议/型号族范围，不是市场占有率统计，也不等于每个变体均已实测。

机器可读验证来源为[兼容目录](../engine/compatibility.json)，详情页使用同一来源。本项目已经取得历史实机证据的确切原生配置标A；通用读取器统一保守标C，表示协议已接入、仍需本项目按型号实测，上游记录不会自动升级为本项目实测。INFI75标D，当前不宣称兼容。

| 范围 | 设备类型 | 自动读取条件 |
| --- | --- | --- |
| Logitech HID++ 1.0/2.0 | 鼠标、键盘、耳机 | 支持相应电量寄存器/特性的Lightspeed、Unifying、Bolt等接收器；按接收器槽位发现设备 |
| Razer | 鼠标、耳机、2.4G键盘 | 内置OpenRazer型号表；键盘包括BlackWidow V3/V4、DeathStalker V2 Pro部分无线/有线变体 |
| Lofree Hyzen | 2.4G键盘 | 内置型号接收器和对应厂商集合；协议已接入，当前无本项目实机验证 |
| HyperX | 耳机 | Cloud Alpha Wireless专用；另有Alpha 2、Cloud II/Core、Cloud III/III S等各自匹配表 |
| SteelSeries | 鼠标、耳机 | Aerox/Rival及Arctis/Nova/GameBuds已列明型号，按各自报文匹配 |
| Corsair | 鼠标、耳机 | Dark Core RGB Pro SE、Virtuoso部分型号、Void v2/HS80 Max等对应接收器 |
| Audeze、JBL、Astro | 耳机 | Maxwell、Quantum 910、A50 Gen 5等已列明设备 |
| ASUS ROG/TUF | 鼠标 | 内置型号表；不扩称支持所有ROG键盘 |
| Pulsar / ATK / VXE | 鼠标 | Pulsar X2部分型号、VXE R1部分接收器等，按确切ID与集合匹配 |
| WLmouse、MCHOSE、G-Wolves、LAMZU | 鼠标 | 已内置的型号及接收器变体 |
| Keychron、NZXT、Angry Miao | 鼠标 | M5/Ultra-Link已列明设备、Lift Elite、AM Infinity 8K等；不扩称支持全品牌键盘 |
| Windows蓝牙电量 | 鼠标、耳机、键盘及其他设备 | 已在Windows连接，系统能提供电量属性；不会自动配对设备 |
| 标准HID电量字段 | 鼠标、键盘及其他HID设备 | HID描述符明确声明电量字段；只用GetFeature读取，不写入或抢占键鼠输入 |

更细的上游型号与“上游实机验证/未经实机验证”区分见[上游清单](https://github.com/HeyOkay/HaloBattery/blob/2251f0534a8e886c9692ead42056059b4f69d8c0/docs/devices.md)。本项目未启用其手柄模式切换及手柄专用读取器，因此不要直接把上游整张表视为本项目承诺。

本机INFI75没有在通用标准读取中取得电量，目前不能宣称兼容。即使某些HFD键盘共享VID/PID，也不向INFI75发送未经该型号证实的命令；添加其专用协议属于后续型号适配。用户无需刷固件、改键位或修改接收器设置。

## 数据和设备控制

- 设备报告百分比时显示原值；只有档位或电压估算时标为“约值”，不捏造1%精度。
- 未返回电量时显示未知或带原时间的缓存；缓存不声明设备在线，也不触发低电量提醒。
- Windows蓝牙属性显示“系统提供 · 最近查询”；本次缺少属性时沿用原读数时间，不伪造设备测量或充电证据。
- 同型号接收器按物理实例区分，Logitech槽位保持独立；跨读取器只在具有可靠设备单元证据时合并，不按名字、品牌或整个接收器容器强行合并。极少数多来源重复显示仍需真实样本补充。
- 通用设备以电量监控为主。DPI、麦克风与音源等控制按已验证能力显示，不能只凭同品牌名称开启控制项。
- 存在厂商驱动独占设备、休眠、固件变体等情形时，接收器存在不代表电量当前可读。新增型号由维护端补充读取器，最终用户不用调报文。

界面、桥接、安装事务及协议解析均有自动化验证；其他人的实际硬件仍需按型号核验。兼容表会随验证结果更新。

<!-- BEGIN GENERATED COMPATIBILITY CATALOG -->
来源：`engine/compatibility.json`；由 `python engine/catalog.py --markdown` 离线生成。

等级是证据范围，不是当前在线状态，也不是市场覆盖率。A为历史实机记录；本轮读数另行复验。

| 项目 | 类型 | 等级 | 边界 |
| --- | --- | --- | --- |
| ATK A9 Air+ / 用户已验证接收器变体 | mouse | A | `373B:1278` / `FF02:0002` / 17字节；固件版本未记录 |
| HyperX Cloud Alpha Wireless | headset | A | `03F0:098D` / `FF43:0202` / 31字节；固件版本未记录 |
| Razer 已列明鼠标、耳机与键盘 | mouse, headset, keyboard | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Audeze Maxwell 已列明变体 | headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| WLmouse 已列明型号 | mouse | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| MCHOSE 已列明协议与型号 | mouse | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| HyperX Cloud Alpha 2 | headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| HyperX Cloud III 与已列明Core变体 | headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| HyperX Cloud II 已列明变体 | headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Keychron M5 / Ultra-Link 8K | mouse | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Pulsar / ATK / VXE / Hitscan 已列明型号 | mouse | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| JBL Quantum 910 已列明变体 | headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Logitech HID++ 与已列明G系列耳机 | mouse, keyboard, headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| SteelSeries 已列明鼠标与耳机 | mouse, headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Razer Barracuda Pro | headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| ASUS ROG / TUF 已列明鼠标 | mouse | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| G-Wolves 已列明型号 | mouse | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Lofree Hyzen 2.4G | keyboard | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Astro A50 Gen 5 | headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Corsair 已列明鼠标与耳机 | mouse, headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| LAMZU Maya X | mouse | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Angry Miao AM Infinity 8K | mouse | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| SteelSeries Arctis Nova Elite | headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Logitech G PRO X 2 LIGHTSPEED | headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| HyperX Cloud III S | headset | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| NZXT Lift Elite Wireless | mouse | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| Windows 系统蓝牙电量属性 | mouse, keyboard, headset, device | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| 描述符声明的标准HID电量Feature字段 | mouse, keyboard, device | C | 协议已接入，待本项目实机验证；限代码内既有匹配表 |
| INFI75-1.1 / RKGK890 | keyboard | D | 已发现但未取得电量，不列为兼容 |

通用读取器来源固定为 HaloBattery `2251f0534a8e886c9692ead42056059b4f69d8c0`；标准HID字段读取为本项目实现。
<!-- END GENERATED COMPATIBILITY CATALOG -->
