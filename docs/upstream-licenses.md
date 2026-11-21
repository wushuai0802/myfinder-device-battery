# 上游许可证台账

核对日期 2026-11-21。本台账服务于 MyFinder Device Battery 的 `AGPL-3.0-only` 源码。按固定提交读取原始许可证、版权及必要的源码头。列出 42 个计划条目，以及可核验的 DS4Windows 派生项目。BrimDeck 致谢 29 项，SDL 和 rivalcfg 已在核心清单中。

## 判定

- **可以改编** 表示相应已核文件的许可证允许改编，并需满足署名、许可通知、源代码和再分发义务。协议身份、读写边界和回放仍须逐项验证。
- **只能参考事实** 适用于只有公开设备事实或编译产物能核对的范围，也适用于许可证不可组合的实现。以独立实现可核实的事实为限。
- **不能用** 表示没有取得可复核的项目许可原文，或无法定位具体源码和许可。仓库可公开浏览也需要明确授权。

MIT、zlib、BSD 类按各自通知保留条件。Apache-2.0 与 GPLv3 兼容，保留改动标识、第三方署名、专利条款和已有 NOTICE。AGPLv3 §13 允许将 GPLv3 和 AGPLv3 部分链接或组合，各部分分别维持其许可。对明确 GPL-2.0-or-later 的文件，选择版本 3 后才有这条组合路径。仅 GPL-2.0-only 的文件只能参考事实。[GNU 许可证兼容性](https://www.gnu.org/licenses/license-list.html) · [AGPLv3 第 13 条](https://www.gnu.org/licenses/agpl-3.0.html#section13) · [Apache-2.0 原文](https://www.apache.org/licenses/LICENSE-2.0)

## 核心项目

| 项目 | 固定提交 | 许可及结论 | 有用范围 | 例外和原文 |
| --- | --- | --- | --- | --- |
| OpenMouse-Project/mouse-protocol | `93c4cc601d448ea481eb59d7b785eec75ae16ec2` | AGPL-3.0，**可以改编** | `src/drivers/`、`src/asus/` | 未见另列许可证文件的子目录。保留 AGPL 原有声明。 [LICENSE](https://github.com/OpenMouse-Project/mouse-protocol/blob/93c4cc601d448ea481eb59d7b785eec75ae16ec2/LICENSE) |
| libratbag/libratbag | `8235b5bb6032dea15901ab58a8218956f4494d08` | MIT，**可以改编** | `src/driver-hidpp20.c`、`src/hidpp20.c`、`data/devices/` | `src/hidpp20.c` 有同类 MIT 源码声明。[HID++ 源码](https://github.com/libratbag/libratbag/blob/8235b5bb6032dea15901ab58a8218956f4494d08/src/hidpp20.c#L1) [COPYING](https://github.com/libratbag/libratbag/blob/8235b5bb6032dea15901ab58a8218956f4494d08/COPYING) |
| flozz/rivalcfg | `189ff05f4022d58b9bac5cf388ad4a4b4892fc7f` | WTFPL-2.0，**可以改编** | `rivalcfg/devices/` | 未见另列许可证文件。GNU 许可证列表列为与 GPL 兼容的宽松许可证。B2-7 的许可门槛仍按该卡执行。 [LICENSE](https://github.com/flozz/rivalcfg/blob/189ff05f4022d58b9bac5cf388ad4a4b4892fc7f/LICENSE) |
| Sapd/HeadsetControl | `163c6edf744c28804d95e3653e5ea7bc9486b46c` | GPL-3.0，**可以改编** | `lib/devices/`、`lib/device_registry.cpp` | `debian/copyright` 同为 GPL-3。组合时保留该部分的 GPLv3 权利与义务。 [license](https://github.com/Sapd/HeadsetControl/blob/163c6edf744c28804d95e3653e5ea7bc9486b46c/license) |
| libsdl-org/SDL | `853ccac4823af1b331a4503dd753c103a6813482` | zlib，**可以改编** | 仅 `src/joystick/hidapi/` | 该范围未见另列 LICENSE。`src/hidapi/`、`src/video/yuv2rgb/` 和测试另有许可证文件，均在本卡 SDL 源码范围外。 [LICENSE.txt](https://github.com/libsdl-org/SDL/blob/853ccac4823af1b331a4503dd753c103a6813482/LICENSE.txt) |
| gabomdq/SDL_GameControllerDB | `4e87e3c6c2574f49e1cdf8526b6f829b61e5ccb3` | zlib，**可以改编** | `gamecontrollerdb.txt` | 仓库入口转到 `mdqinc/SDL_GameControllerDB`。该数据用于识别与映射。 [LICENSE](https://github.com/mdqinc/SDL_GameControllerDB/blob/4e87e3c6c2574f49e1cdf8526b6f829b61e5ccb3/LICENSE) |
| Ryochan7/DS4Windows | 未取得（404） | 不确定，**不能用** | 原入口未取得可用提交 | 原仓库的提交查询返回 HTTP 404。可核对的派生项目另外列出。  |
| qmk/qmk_firmware | `80973aa0b5413e9a5bbbf9293507f39e870f9aa3` | GPLv2 原文，`quantum/via.c` GPL-2.0-or-later，**可以改编** | `quantum/via.c`、`drivers/battery/`（前者已证） | 逐文件检查版本选择权。`lib/lib8tion` 为 MIT，`quantum/midi/bytequeue` 为 GPLv3，部分 Flehrad 键盘为 Apache-2.0，OLED 有独立素材许可。[VIA 源码声明](https://github.com/qmk/qmk_firmware/blob/80973aa0b5413e9a5bbbf9293507f39e870f9aa3/quantum/via.c#L1) [LICENSE](https://github.com/qmk/qmk_firmware/blob/80973aa0b5413e9a5bbbf9293507f39e870f9aa3/LICENSE) |
| the-via/firmware | `bc32102518533741af10b05ebbad38c4eef34997` | GPL-3.0，**只能参考事实** | 根目录 `.hex`、`.uf2` 固件发布物 | 本次记录版本与型号线索。若需改编具体按键或电量逻辑，需取得真实源码。 [LICENSE](https://github.com/the-via/firmware/blob/bc32102518533741af10b05ebbad38c4eef34997/LICENSE) |
| the-via/app | `75a7275fd4e13b20f6d646583c5b6cbac8a9e92d` | GPL-3.0，**可以改编** | `src/`，含 `src/store/devicesThunks.ts` | 未见另列许可证文件的子目录。采用 GPLv3 与 AGPLv3 的组合条款。 [LICENSE](https://github.com/the-via/app/blob/75a7275fd4e13b20f6d646583c5b6cbac8a9e92d/LICENSE) |
| vial-kb/vial-qmk | `dd43959ae5c08d8a28d38a1acf7b04e86b14a344` | GPLv2 原文，`quantum/vial.c` GPL-2.0-or-later，**可以改编** | `quantum/vial.c`、`drivers/battery/`（前者已证） | 同样存在独立 MIT、GPLv3、Apache-2.0、素材许可。每条复用路径查文件头。[Vial 源码声明](https://github.com/vial-kb/vial-qmk/blob/dd43959ae5c08d8a28d38a1acf7b04e86b14a344/quantum/vial.c#L1) [LICENSE](https://github.com/vial-kb/vial-qmk/blob/dd43959ae5c08d8a28d38a1acf7b04e86b14a344/LICENSE) |
| zmkfirmware/zmk | `4de313367288aad5aeaa1243890e40dc813f7008` | MIT，**可以改编** | `app/src/`、`app/module/drivers/sensor/battery/` | 未见另列许可证文件的子目录。Zephyr 等通过外部依赖引入，各自适用许可。 [LICENSE](https://github.com/zmkfirmware/zmk/blob/4de313367288aad5aeaa1243890e40dc813f7008/LICENSE) |
| pwr-Solaar/Solaar | `e7304c4c451cc9bb4f206a914844525e67856a28` | GPLv2 原文，已核 `hidpp20.py` 为 GPL-2.0-or-later，**可以改编** | `lib/logitech_receiver/hidpp20.py` | 具体文件明确可选择新版 GPL。仅该已核范围可按 v3 组合，其余逐文件。`docs/LICENSE.txt` 另单列。 [HID++ 源码声明](https://github.com/pwr-Solaar/Solaar/blob/e7304c4c451cc9bb4f206a914844525e67856a28/lib/logitech_receiver/hidpp20.py#L1) [LICENSE.txt](https://github.com/pwr-Solaar/Solaar/blob/e7304c4c451cc9bb4f206a914844525e67856a28/LICENSE.txt) |
| openrazer/openrazer | `ab2452847d12f4c789af040b17599012a0dd80b9` | GPL-2.0-or-later，**可以改编** | 已核 `driver/razercommon.c` 及 `pylib/openrazer/client/devices/__init__.py` | 两文件 SPDX 为 GPL-2.0-or-later，Debian 权利文件列 `GPL-2+`。`logo/` 另为 CC BY-SA 4.0。代码按已核文件选择 GPLv3 组合，Linux 驱动行为需独立核实。 [LICENSES/GPL-2.0-or-later.txt](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/LICENSES/GPL-2.0-or-later.txt) · [LICENSES/CC-BY-SA-4.0.txt](https://github.com/openrazer/openrazer/blob/ab2452847d12f4c789af040b17599012a0dd80b9/LICENSES/CC-BY-SA-4.0.txt) |
| MoFeng2223/BrimDeck | `f1510d09677492a13a31d26bc37c3509da1b818f` | Apache-2.0，**可以改编** | `src/BrimDeck.Core/Brands/`、`src/BrimDeck.Core/DeviceProtocols.cs`（仅可证 Apache 及许可兼容部分） | 根目录未见 `NOTICE`。读取 `src/BrimDeck/ThirdPartyNotices.txt`，涉及的衍生协议保留各原项目的署名与许可。按本台账的项目和实际文件交叉核对。 [LICENSE](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/LICENSE) |
| CircumSpector/DS4Windows（DS4Windows 派生） | `62e4e59097156d75dda7a0def9120454809021b9` | MIT，**可以改编** | `DS4Windows/DS4Library/DS4Device.cs`、`InputDevices/DualSenseDevice.cs` | Crc32 为 Apache-2.0，HidLibrary 有自己的 MIT 声明，已有第三方 NOTICE。 [LICENSE](https://github.com/CircumSpector/DS4Windows/blob/62e4e59097156d75dda7a0def9120454809021b9/LICENSE) · [NOTICE.txt](https://github.com/CircumSpector/DS4Windows/blob/62e4e59097156d75dda7a0def9120454809021b9/NOTICE.txt) |

另查 `Jays2Kings/DS4Windows` 固定提交 `14b831d7a0b74986e68fadea0330ddfb2955f3b4`。只查到 `DS4Windows/HidLibrary/LICENSE.htm`，不能给应用源码推定授权。上述派生项目与原入口的结论各自独立。

### OpenMouse 基线比较

现行 main 固定为 `93c4cc601d448ea481eb59d7b785eec75ae16ec2`，本项目此前引用 `30d6ad37d0ecc9fff53f86d5058d67a88e07150d`。两者的 LICENSE 原文 SHA-256 均为 `429cc8696668f427bbce064e7527bc1ed0f74ad0e99f44419af22836993d73b3`。源码树的具体 PID 和能力差异在 B2-2 另盘点。当前树未见 `src/drivers/` 另列许可证文件。[LICENSE](https://github.com/OpenMouse-Project/mouse-protocol/blob/93c4cc601d448ea481eb59d7b785eec75ae16ec2/LICENSE) · [LICENSE](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/LICENSE)

### BrimDeck 许可与署名

BrimDeck 固定 `f1510d09677492a13a31d26bc37c3509da1b818f` 的项目原文是 Apache-2.0，根树未发现 `NOTICE`。其 `src/BrimDeck/ThirdPartyNotices.txt` 原文已读取，SHA-256 为 `35207ba7757d526f4e108e405b4ba35e18c498139cc3fc416535873b7cfd0026`。来源包括 rivalcfg 的鼠标接口/电量行为和 SDL 的 HIDAPI 数据。逐协议移植要保留相应原来源声明。[LICENSE](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/LICENSE) · [src/BrimDeck/ThirdPartyNotices.txt](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/src/BrimDeck/ThirdPartyNotices.txt) · [docs/zh-CN/devices.md](https://github.com/MoFeng2223/BrimDeck/blob/f1510d09677492a13a31d26bc37c3509da1b818f/docs/zh-CN/devices.md)

## BrimDeck 致谢中的其余上游

| 项目 | 固定提交 | 许可及结论 | 有用范围 | 子目录及原文 |
| --- | --- | --- | --- | --- |
| CharlesGool/device-battery-viewer | `88b9cd6fd970a93aa01272a43161cf976ea7c351` | MIT，**可以改编**（项目自有源文件） | `plugins/`，含 `vxe_mouse.cs` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/CharlesGool/device-battery-viewer/blob/88b9cd6fd970a93aa01272a43161cf976ea7c351/LICENSE) |
| xzeldon/razer-battery-report | `c3055e80808533298ab58f3f817278a51af947af` | MIT，**可以改编**（项目自有源文件） | `crates/razer-battery-report/src/` | `assets/icons/lucide/LICENSE` 含 ISC 和 MIT，图标单独适用。 [LICENSE](https://github.com/xzeldon/razer-battery-report/blob/c3055e80808533298ab58f3f817278a51af947af/LICENSE) |
| Lkig/ATK-A9-Battery | `3d740fe927d2e2be7f5a943958fd9edb0cf56693` | MIT，**可以改编**（项目自有源文件） | `src/` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/Lkig/ATK-A9-Battery/blob/3d740fe927d2e2be7f5a943958fd9edb0cf56693/LICENSE) |
| cobanov/atkctl | `1755d85106630cacc2f6c77a843b5819c4e07c37` | MIT，**可以改编**（项目自有源文件） | `Sources/`、`Tests/` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/cobanov/atkctl/blob/1755d85106630cacc2f6c77a843b5819c4e07c37/LICENSE) |
| Tan-TanZi/InphicBatteryTray | `86c8cd08eaae74966799a840e9a8d45ca1a6cd37` | MIT，**可以改编**（项目自有源文件） | `src/InphicBatteryTray.cs` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/Tan-TanZi/InphicBatteryTray/blob/86c8cd08eaae74966799a840e9a8d45ca1a6cd37/LICENSE) |
| Rockeyxx/aj179-linux | `084b23349ed69993824bba30c26598d704fa5b8b` | MIT，**可以改编**（项目自有源文件） | `gui/battery.py` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/Rockeyxx/aj179-linux/blob/084b23349ed69993824bba30c26598d704fa5b8b/LICENSE) |
| HeyOkay/HaloBattery | `73e229cbee2e97fad659fcd96c78afdfa78488fb` | MIT，**可以改编**（项目自有源文件） | `halo/` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/HeyOkay/HaloBattery/blob/73e229cbee2e97fad659fcd96c78afdfa78488fb/LICENSE) |
| evren-oct/vigil-watt | `4df830043296760f5d5c273f4cdc0ea9456c4bc7` | MIT，**可以改编**（项目自有源文件） | `src/`、`captures/` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/evren-oct/vigil-watt/blob/4df830043296760f5d5c273f4cdc0ea9456c4bc7/LICENSE) |
| late-autumn-gif/mousedock | `3055abf3fd3d2ffbad2395f81cd102b1a8902e23` | MIT，**可以改编**（项目自有源文件） | `src/`、`docs/` | `THIRD-PARTY-NOTICES.md` 记录另一个 MouseDock、Halo Battery、NAudio 等来源。 [LICENSE](https://github.com/late-autumn-gif/mousedock/blob/3055abf3fd3d2ffbad2395f81cd102b1a8902e23/LICENSE) |
| sdparklab/Voltide | `c1872c4c93124d62c05bf5887fe89c32c6be8dd6` | MIT，**可以改编**（项目自有源文件） | `src/Core/`、`src/Interop/` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/sdparklab/Voltide/blob/c1872c4c93124d62c05bf5887fe89c32c6be8dd6/LICENSE) |
| incconutwo/mouse-battery-tray | `c1c6059472dab366c700a7f7516e0dbffde68fd7` | MIT，**可以改编**（项目自有源文件） | `devices.py`、`dump_devices.py` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/incconutwo/mouse-battery-tray/blob/c1c6059472dab366c700a7f7516e0dbffde68fd7/LICENSE) |
| misaka310/mouse-battery-tray | `6a7ef93d0fdedcad507b4cd85ca7e6c18423238c` | MIT，**可以改编**（项目自有源文件） | `src/`、`docs/` | `THIRD_PARTY_NOTICES.md` 保留 incconutwo 版 MIT 署名。 [LICENSE](https://github.com/misaka310/mouse-battery-tray/blob/6a7ef93d0fdedcad507b4cd85ca7e6c18423238c/LICENSE) |
| Iris-0109/rapoo-tray | `20ff56a96b94e7acfcb43f53851c5daa75aa73e0` | MIT，**可以改编**（项目自有源文件） | `src/rapoo_protocol.cpp` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/Iris-0109/rapoo-tray/blob/20ff56a96b94e7acfcb43f53851c5daa75aa73e0/LICENSE) |
| D3m0nZOnFire/mousectl | `8a8ddd7721d46405ed17e84849b92054a2eb4191` | MIT，**可以改编**（项目自有源文件） | `mousectl/` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/D3m0nZOnFire/mousectl/blob/8a8ddd7721d46405ed17e84849b92054a2eb4191/LICENSE) |
| oitsukiii/betop-battery | `353eae0a9c22304297f9de7260edb17f778c8954` | MIT，**可以改编**（项目自有源文件） | `src/betop_battery/` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/oitsukiii/betop-battery/blob/353eae0a9c22304297f9de7260edb17f778c8954/LICENSE) |
| WWWyyy452/GamesirBatteryTray | `bab189d29a7b60305ce57552067f0eeb5c713aa7` | MIT，**可以改编**（项目自有源文件） | `GamesirBatteryTray/GameSirDevice.cs`、`DeviceRegistry.cs` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/WWWyyy452/GamesirBatteryTray/blob/bab189d29a7b60305ce57552067f0eeb5c713aa7/LICENSE) |
| agnostos-t/gamesir-battery-tray | `a01b71d0fb8363b95353962522235d266a49401a` | MIT，**可以改编**（项目自有源文件） | `gamesir_battery.py` | `licenses/` 有 hidapi 多选许可、Pillow、PyInstaller GPL 加例外、pystray GPL/LGPL 等。 [LICENSE](https://github.com/agnostos-t/gamesir-battery-tray/blob/a01b71d0fb8363b95353962522235d266a49401a/LICENSE) |
| Barba2k2/darmoshark-m3-configurator | `9f098163a9ef8c6bc68b99bc902c305330fee17e` | MIT，**可以改编**（项目自有源文件） | `app/src-tauri/src/commands/read_commands.rs` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/Barba2k2/darmoshark-m3-configurator/blob/9f098163a9ef8c6bc68b99bc902c305330fee17e/LICENSE) |
| LANMIN-X/Jingzao-JZM5-Battery | `790cc19742fdfa63b1106d5e6a834e97796b2c55` | MIT，**可以改编**（项目自有源文件） | `JZM5BatteryTray.swift` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/LANMIN-X/Jingzao-JZM5-Battery/blob/790cc19742fdfa63b1106d5e6a834e97796b2c55/LICENSE) |
| jonkristian/pulsar-x3-python | `04c08b391bdedf659bc2a74b810a1ce880794e96` | MIT，**可以改编**（项目自有源文件） | `pulsar_x3.py` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/jonkristian/pulsar-x3-python/blob/04c08b391bdedf659bc2a74b810a1ce880794e96/LICENSE) |
| Yesid-Robayo/YesCharge | `ccfa80807859a52163ac494757a29eedf5681e84` | MIT，**可以改编**（项目自有源文件） | `core/`、`app/src/api.rs` | `THIRD_PARTY_NOTICES.txt` 列出编入应用的 Rust 依赖与字体多种许可。 [LICENSE](https://github.com/Yesid-Robayo/YesCharge/blob/ccfa80807859a52163ac494757a29eedf5681e84/LICENSE) |
| KhromotozzDevOut/aula-keyboard-battery-tray | `172df7b839e6871ea9dcecacd2e06c47efd0d91b` | MIT，**可以改编**（项目自有源文件） | `AulaKeyboardBatteryTray.cs` | 固定树未发现另列许可证的代码子目录。 [LICENSE.txt](https://github.com/KhromotozzDevOut/aula-keyboard-battery-tray/blob/172df7b839e6871ea9dcecacd2e06c47efd0d91b/LICENSE.txt) |
| VitalyArt/Aula-F75-Max-Driver | `4ad0cbcb073284f200e7ff022a12394a4fb69375` | MIT，**可以改编**（项目自有源文件） | `Sources/`、`docs/` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/VitalyArt/Aula-F75-Max-Driver/blob/4ad0cbcb073284f200e7ff022a12394a4fb69375/LICENSE) |
| Klegus/mchose-macos | `7fd17a6664c59053de75facee3ee2fce7d631eea` | MIT，**可以改编**（项目自有源文件） | `mchose_config.py` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/Klegus/mchose-macos/blob/7fd17a6664c59053de75facee3ee2fce7d631eea/LICENSE) |
| NaoCoding/ShowMeTheConnection | `a4447b28c2852cd16f13604a5a475d6cdd0056f4` | MIT，**可以改编**（项目自有源文件） | `src/`、`scripts/detect_atk_hid.py` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/NaoCoding/ShowMeTheConnection/blob/a4447b28c2852cd16f13604a5a475d6cdd0056f4/LICENSE) |
| patalbansishashank/Headroom | `dc7976994abf9dc163c41ecb4d783a825ad5dc1c` | MIT，**可以改编**（项目自有源文件） | `bin/` | 固定树未发现另列许可证的代码子目录。 [LICENSE](https://github.com/patalbansishashank/Headroom/blob/dc7976994abf9dc163c41ecb4d783a825ad5dc1c/LICENSE) |
| tdryer/eh-fifty | `d6cecc274b9aeaa5dd69d8bfe32970cb33a4af4a` | MIT，**可以改编**（项目自有源文件） | `eh_fifty.py` | 固定树未发现另列许可证的代码子目录。 [LICENSE.txt](https://github.com/tdryer/eh-fifty/blob/d6cecc274b9aeaa5dd69d8bfe32970cb33a4af4a/LICENSE.txt) |

闭源厂商驱动在后续研究中按公开设备事实使用。任务书的真实设备、冻结文件和协议证据界限继续适用。
