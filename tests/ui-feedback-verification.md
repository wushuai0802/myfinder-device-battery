# UI feedback 验证记录

所有新增测试使用假设备、注入的鼠标活动/音频快照及写入器；不访问真实 HID、不修改 Windows 默认音频设备。音量偏好测试只写测试输出目录。

修改前确认：`UiFeedbackTests` 的 configuration、active tray、volume layouts、collapse during drag/queue、audio detection、audio cards 六组均失败；新增 G Pro Wireless 接收器、插线及身份/回读用例在原实现上有 17 个失败子用例（未提供 DPI 能力）。实现期间还用失败回归检查了无效目标类型的拒绝，以及 PRO X 部分身份读取后的恢复。

最终离线验证：

- `engine/runtime/python.exe -I -B -m unittest discover -s ./engine/tests -q`：440 项通过。
- `engine/runtime/python.exe -I -B -m unittest discover -s ./tests -p "test_*.py" -q`：181 项通过。
- `tests/Run.ps1 -OutputDirectory ./o`：C# 功能/托盘/运行隔离检查与 PowerShell 5.1、7 安装器检查通过；随后运行时采样在 `RuntimeSamplerTests.ps1:78` 因 `Get-CimInstance Win32_Process` 拒绝访问而退出 1。托盘测试中另有两项真实前台激活断言在当前桌面不可验证。未修改这些验收规则，完整验收由流水线重跑。
- 最终单独编译运行 `UiFeedbackTests`：141 项检查通过；覆盖两种布局、活动切换与回退、收起命中/持久化/拖动和队列、键盘和无障碍、分页、耳机插拔/绑定/歧义/隐藏/去重，以及音频快照序列化。
- 深浅主题、收起/展开、经典/通用布局的 8 张假设备预览保留在 [previews/ui-feedback](previews/ui-feedback)。临时 C# 输出 `o` 在交付前删除。

已有测试只修改 `AudioSettingsTests.cs` 中“无配置文件的新安装”默认值断言，从 `auto` 改为任务要求的 `active`；旧文件缺省 `mouse`、显式旧值及其他已有断言保留。`test_logitech_dpi.py` 的假总线增加槽位参数，原 PRO X 用例继续使用默认槽位 1，断言未放宽。

改动文件：`README.md`、`docs/compatibility.md`；`engine/providers/logitech.py`、`engine/tests/test_logitech_dpi.py`；`scripts/AudioControl.cs`、`CompactRenderer.cs`、`DeviceSettings.cs`、`PopupPages.cs`、`SettingsForm.cs`、`TrayApp.cs`、`UiState.cs`、`VisualDesign.cs`、`WirelessLayout.cs`；`tests/AudioSettingsTests.cs`、`Run.ps1`、`UiFeedbackTests.cs`；本验证记录及测试预览。

仍待实测：用户 G Pro 的具体接收器 PID、无线/插线单元 ID 和真实 DPI 读写；真实桌面的前台激活及音频插拔；3.5mm 插孔的驱动检测能力。插线控制只在精确型号、槽位 `0xFF`、现有 HID++ 长集合路径和有效非零单元 ID 都满足时开放。Windows 耳机枚举值已对照 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/ne-mmdeviceapi-endpointformfactor) 核实为 Headphones=3、Headset=5。

旧版会拒绝含 `active` 的设置并在内存使用整份默认设置，音频绑定等也暂不生效；原文件暂留，旧版再次保存时现有机制先尝试写入 `.unread.bak`。降级前将托盘来源改为“自动”并保存。
