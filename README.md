# MyFinder Device Battery (外设电量与控制卡片)

专为 Windows 与 MyDockFinder 打造的 macOS 质感外设电量监控与控制中心。支持 **ATK A9 Air+ 无线鼠标**（实时电量、PAW3955 真实 DPI 档位读取与切换）与 **HyperX Cloud Alpha Wireless 无线耳机**（真实连接与电量检测、音频输出设备一键切换、麦克风静音控制）。

> **常见问题：没有安装 MyDockFinder / MyFinder 可以使用吗？**  
> **完全可以！** 本工具是纯正的 Windows 原生系统托盘应用程序（基于 Win32 NotifyIcon 与 Per-Pixel Alpha 分层半透明窗口构建）。  
> - **搭配 MyDockFinder**：电池图标会自动优雅融入 MyFinder 顶部仿 Mac 菜单栏。  
> - **单独运行**：图标常驻于 Windows 任务栏右下角系统托盘区，鼠标悬浮显示电量，点击即可弹出完整毛玻璃控制面板。

---

## 快速上手 (Quick Start)

1. **即开即用**：直接双击运行根目录下的 `启动.bat` 或 `outputs/AtkMouseBattery.exe`。
2. **一键开机自启**：双击运行 `一键安装自启.bat`，即可自动向当前用户的 Windows 启动文件夹与开始菜单添加纯净启动项（无需管理员权限，启动时静默常驻托盘，不弹黑框）。
3. **一键移除自启**：双击运行 `一键卸载自启.bat` 即可快速撤销自启快捷方式。

---

## Appearance

## Appearance

- A colored circular badge with a large integer readout in one tray icon. Glyphs use
  measured ink bounds and 4x supersampling, then render at the native tray size.
- Battery ring colors: 50-100 green, 20-49 amber, 0-19 red. These are the
  component's display bands, not claims about ATK firmware LED thresholds.
- A solid level-colored circular badge with near-black digits makes the level
  color visible on MyFinder wallpaper backgrounds. Cached readings use a
  neutral badge; charging adds a compact separate corner marker.
- Gemini's macOS-style layout uses light/dark material gradients, subtle edge
  highlights, separate battery and DPI sections, and hover feedback.
- The popup uses per-pixel alpha for smooth corners; open/close transitions use
  elapsed-time cubic easing (190 ms / 135 ms) and can reverse without jumping.
- Automatic sync is visually quiet after the first reading. Identical visible
  state does not republish the popup, and identical icon/tooltip state does not
  update the notification icon. The upper-right refresh control uses a standard
  clockwise arrow on a rounded button and a "刷新状态" hover tooltip.
- No live reading is shown as `--`, never as an invented battery percentage.
- If a reading fails, a previous successful percentage is labelled "last confirmed",
  not presented as current. The previous value is only kept in memory.
- The panel is only opened by the user. No notifications or foreground pop-ups.

## Data and safety

The receiver is VID 373B / PID 1278, with Windows I/O collection FF02:0002
and 17-byte input/output reports. The ATK HUB identifies the paired device as
A9 Air+. The HID reply includes a length byte before the paired mouse CID/MID;
the latter are bytes 6/7 including report ID, and writes require `1,54`.

The panel displays the mouse's actual DPI stages, rather than hardcoded presets.
A click selects that existing stage by writing only the two-byte active-stage
pair at EEPROM `0x0004`. The stage values are not rewritten. Identity, the
system row, and every DPI row are checked before writing; the new active pair
is read back before the selection changes in the UI. Failed switches attempt
to restore and verify the original configuration.

Opening the panel requests an immediate battery/DPI sync. While it is open,
DPI sync is scheduled every two seconds and battery reads at most every thirty
seconds after a successful read. Switching a stage updates the verified state
immediately and queues a fresh battery/DPI sync. Closing the panel stops the
fast timer; background sync is once per minute. Mouse operations share one queue
and one per-device mutex. A HID handle is shared within one snapshot, then
closed; it is not kept open while the panel is idle.

Only the following documented query opcodes are used for battery/identity:

- 03: wireless mouse online state.
- 04: battery percentage, charge flag and voltage.
- 10 hexadecimal: model CID/MID.

One instance is allowed. No credentials,
device serial numbers, browser data or network access are used.

An online byte other than 1 does not distinguish sleeping from disconnected.
The program therefore waits for a verified live reply without diagnosing a
hardware fault or fabricating a reading.

## Run

Double-click `outputs/AtkMouseBattery.exe`, or run `scripts/Start.ps1`.
Left-click the battery icon for details; right-click and choose Exit to stop.
MyFinder already has `trayicoallshow_myfinder=1` in the user's current settings.
If its tray policy puts this app in overflow, use MyFinder's per-app tray setting
to display this executable directly in MyFinder. No existing settings are edited.

2026-10-08: after live battery and menu-bar verification, per-user login startup
was enabled with `scripts/Install-Startup.ps1`. The Startup-folder shortcut
`MyFinder 设备电量.lnk` launches `outputs/AtkMouseBattery.exe` directly, without
a PowerShell window, scheduled task, service, watcher or recovery watchdog.
It starts quietly as a tray icon, without automatically opening the panel.
A matching Start-menu shortcut provides a manual launcher. If the application
is already running, click its battery icon to open the panel; the existing
single-instance mutex prevents duplicate copies.
To disable login startup, delete only that shortcut from the user's Startup
folder, or disable this item in Windows Startup Apps. The application itself
and the manual Start-menu launcher can remain installed.

## Build and offline tests

Run `scripts/Build.ps1`. Uses Windows' existing .NET Framework compiler, no
additional runtime download. The build runs offline packet/parser tests.
`work/queue-test/StageSyncQueueTests.cs` exercises the actual UI controller with
a slow fake receiver to check serialization, click/refresh coalescing, immediate
open sync, and stopping the fast timer when the panel closes. It opens no HID
device and injects no desktop input.
`AtkBatteryProbe.exe --help` and `--self-test` perform no HID access.
`--enumerate` reads device descriptors; `--power` and `--diagnostic` send queries;
`--dpi-read` reads the PAW3955 table at `0x1B00` and validates each six-byte row.
`--select-stage N` explicitly selects an existing one-based stage and verifies
its active-stage readback. `AtkMouseBattery.exe --show` opens the panel on launch.

`outputs/appearance-preview.png` is a drawn design preview with no real value.
It is not a desktop screenshot or evidence of MyFinder integration.

## HyperX headset and audio controls

The popup uses independent mouse and headset cards with an 8px transparent
gap. Four mouse stages use a 280x210 mouse card; five to eight use 280x230.
The headset card is 280x130. The old bottom DPI hint has been removed.

The output buttons select the exact Realtek speaker and HyperX Cloud Alpha
Wireless Windows endpoints. Console and Multimedia defaults are changed;
the Communications default and recording defaults are preserved. The mic
button controls Windows mute on the HyperX recording endpoint, rather than
the headset's physical mute button. Both operations verify the result and
attempt restoration on failure. Audio writes are queued independently of
mouse HID reads, and user actions take priority over background audio reads.

The HyperX provider uses only `21 BB 03/0B/0C` queries on the verified
03F0:098D, FF43:0202, 31-byte collection. Receiver presence and wireless
connection are different states. On 2026-10-08, moving the receiver from its
USB 2.0 hub to a direct motherboard USB port restored control responses.
After restarting the headset, both the independent probe and running widget
read connected, 76% battery and not charging. These are sampled values, not
fixed display defaults. The combined port change and USB reset do not prove
that the hub alone caused the original failure.

The temporary audio-endpoint presence fallback has been removed. The headset
card appears after a verified connected reply, or briefly uses a labelled
previous connection while the receiver remains present. A radio-off reply
hides the card; Windows endpoint presence cannot override it. Unknown battery
remains `--`, and later timeouts cannot turn a known-off state into connected.

Focused real tests passed for Realtek/HyperX switching and Windows mic mute;
the original HyperX output, unmuted mic and separate communications endpoint
were restored afterward. NGENUITY installation and startup settings were
not changed. Headset reads run separately from the mouse queue and back off
after timeouts; no scheduled startup task or recovery watchdog was added.

## Sources

- ATK official HUB build:
  https://bpcdn.atkgear.com/hub-v3/production/3.2.27/static/index-F-MYsmWR.js
- Windows FF02 output-report implementation:
  https://github.com/Fan4Metal/mouse_tray/blob/master/mouse_tray/drivers/chipset/nordic52.py
- MyDockFinder official tray-display labels:
  https://github.com/mydockfinder/mydockfinder-for-Win10-Win11/blob/release/language/English.ini
- Native-size numeric tray rendering reference:
  https://github.com/Fan4Metal/mouse_tray/blob/master/mouse_tray/ui/icons.py
- Apple material and motion guidance:
  https://developer.apple.com/design/human-interface-guidelines/materials
  https://developer.apple.com/design/human-interface-guidelines/motion

## Status

2026-10-08: ATK HUB visibly identified the mouse as A9 Air+ and displayed 75%.
The local receiver probe reads the PAW3955 table successfully: four stages at
400 / 800 / 1200 / 1600 DPI, active stage 2 (800 DPI), with all six-byte CRCs
valid. The tray panel now displays real stage values and automatically syncs
when opened, during use, and after a stage switch. The scalar stage indices
and their checksums are validated without silently clamping invalid values.
The stage-switch implementation was verified on the receiver with an
800 -> 1200 -> 800 cycle. The open panel detected the temporary change without
a manual refresh, and all four original stage rows were unchanged afterward.
The visual redesign retains the byte-identical HID code and refresh/switch
controller. Native 16/24/32 pixel icon states and light/dark panel samples were
checked offline, and Windows accepted the per-pixel layered popup surface.
`outputs/active-ui-render.png` is an application render from verified data, not
a desktop screenshot. `visual-status.json` records the compositor result.
