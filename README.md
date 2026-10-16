# MyFinder Device Battery

Windows 上的鼠标与耳机电量卡片，支持 MyDockFinder / MyFinder，也可独立作为系统托盘应用使用。

当前版本 **1.1.0**，适用于 Windows 10/11 x64，使用系统自带的 .NET Framework。没有 macOS 安装包。

支持的硬件：

- ATK HUB 识别为 **A9 Air+** 的2.4 GHz鼠标，已验证接收器 VID/PID `373B:1278`、FF02:0002、17字节报告。其他接收器或同名变体不保证兼容。
- **HyperX Cloud Alpha Wireless**，已验证 `03F0:098D`、FF43:0202、31字节报告。
- 音源切换和 Windows 麦克风静音支持用户主动绑定其他活动音频端点；这不会让其他耳机自动获得电量支持。

![双设备浅色卡片](docs/images/cards-light.png)

![深色操作反馈](docs/images/cards-dark.png)

图片由实际绘制代码配合模拟状态生成，用于展示布局和操作反馈，不是实时硬件截图。

## 使用和安装

从 [Releases](https://github.com/wushuai0802/myfinder-device-battery/releases) 下载 Windows x64 ZIP并解压。

1. 临时使用：运行 `启动.bat`，或 `outputs/AtkMouseBattery.exe --show`。
2. 稳定安装及自启：运行 `一键安装自启.bat`。程序复制到 `%LOCALAPPDATA%\Programs\MyFinderDeviceBattery`，再创建当前用户的启动文件夹和开始菜单快捷方式，无需管理员权限。
3. 以后更新会复用已记录的安装目录。稳定安装后可以移动或删除原解压包；直接运行的绿色模式需保留原目录。
4. 更新前先从托盘右键退出程序。安装器不会强行结束正在运行且需要替换的已安装程序；复制和注册失败会尝试回滚本次修改。

自定义安装目录：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-Startup.ps1 -InstallDirectory D:\Tools\MyFinderDeviceBattery
```

运行 `一键卸载自启.bat` 只关闭自启，保留程序、开始菜单入口和设置。若要同时移除开始菜单入口：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Uninstall-Startup.ps1 -RemoveStartMenu
```

安装器兼容 Windows PowerShell 5.1 和 PowerShell 7，并识别本程序旧版的乱码快捷方式。Windows“启动应用”中已禁用的项目需先手动启用。自启直接运行 GUI exe，没有 PowerShell窗口、计划任务、服务或 watchdog。

## 面板和操作

- 左键点击托盘图标展开，点击其他窗口或按 `Esc` 动画收起；再次点击图标可切换展开状态。
- 开始菜单、`启动.bat` 或 `scripts/Start.ps1` 会唤出已运行实例的面板，保留单实例。启动脚本不是开机自启入口；开机仍安静运行。
- 鼠标卡片显示真实 DPI 档位；点击只选择现有档位，不改写档位数值。切换中显示目标，核验后再改变选中状态。刚确认的当前档位重复点击会跳过写入。
- 耳机卡片在无线连接确认后显示，使用独立卡片和8个逻辑像素的透明间距。音频接收器存在不代表耳机无线已连接。
- 音源和静音按钮显示处理中状态，失败时显示短提示，悬停可查看错误。输出切换保留通讯默认输出和默认录音设备。
- `Tab` / `Shift+Tab` 或左右方向键移动焦点，`Enter` / 空格执行聚焦操作。
- 面板按所在显示器 DPI 缩放，绘图与命中坐标共用同一比例。四种比例有离线渲染与坐标检查；实际跨屏表现仍需具体设备验证。

如果 MyFinder 把图标放进溢出区，在 MyFinder 的逐应用托盘设置中允许显示本程序；本工具不改写 MyFinder 设置。

## 设置

点击鼠标卡片右上角齿轮，或托盘右键的“设置…”：

- 分别选择扬声器、耳机、麦克风按钮绑定的 Windows 设备。
- 绑定默认自动识别 Realtek 扬声器和 HyperX 音频端点。自定义绑定按设备ID精确选择；设备断开后按钮不可用，不静默切换到其他设备。
- 设置窗口显示当前实际输出，便于区分“按钮绑定”与“正在输出”。保存配置本身不切换音源或改变静音。
- 托盘默认显示鼠标，可选耳机或两者中较低的电量；最低模式优先使用新鲜读数，只有缓存时标注上次确认。悬停明确显示来源和缓存状态，不轮流闪动数字。
- 低电量提醒默认关闭。启用后，在20%及以下每台设备每轮放电提醒一次；充电或回升至30%后重新计数。缓存读数不触发提醒；全屏、演示或系统不允许通知时延后。
- 接收器插拔、系统恢复会合并触发一次检查，仍保留原有定时兜底。耳机开机未必产生 Windows 设备事件。

托盘右键的“音源设置”还提供快速绑定、刷新设备列表和恢复默认音源识别。

## 刷新、缓存和安全

打开面板立即读取；面板内鼠标 DPI 每2秒调度，成功读取电量后至少30秒再读。耳机正常约6秒检查，失败后退避。面板关闭后每分钟检查，没有新增常驻高频轮询。

鼠标移动主要是唤醒硬件；真正读取由定时器、打开面板或手动刷新触发。未知值显示 `--`。读数失败保留上次成功数据并标明时间，超过新鲜度阈值会改变显示和提示；跨日缓存包含日期。电量和充电标记分别检查时效。

鼠标写入只选择已有 DPI 档位：核验设备身份、系统行、全部 DPI 行及校验值，写入后回读确认，失败时尝试恢复。鼠标操作串行处理，连续操作合并等待目标。耳机只使用已验证的查询命令，不写固件、配对或设备设置。

音源和麦克风操作独立排队，写入后核验并在失败时尝试恢复。麦克风按钮控制 Windows 端点静音，不能读取耳机物理静音开关。程序不联网，不读取账号、浏览器或密码管理器数据。

## 本地数据

目录：`%LOCALAPPDATA%\MyFinderDeviceBattery`。

| 文件 | 用途 |
| --- | --- |
| `audio-settings.json` | 音源绑定、托盘模式和提醒偏好；首次保存时创建 |
| `install.json` | 稳定安装目录及快捷方式目标 |
| `status.json` | 运行诊断，不参与自动刷新控制 |
| `low-battery-state.txt` | 启用提醒后的去重记录 |
| `active-ui-render.png`、`visual-status.json` | 满足条件时生成的本机绘图诊断 |

运行状态文件不再由 Git 跟踪；卸载自启保留用户设置。更换驱动后 Windows 可能分配新的设备ID，需要重新绑定。

## 构建和验证

从源码仓库运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -OutputDirectory .\tmp\build
```

使用系统自带的 x64 .NET Framework 编译器，不下载依赖。构建会运行协议、设置、端点匹配、菜单、控制队列、缓存、低电量策略、DPI坐标和安装回滚检查。测试用模拟硬件和隔离文件夹，安装器检查分别覆盖 PowerShell 5.1、可用时的 PowerShell 7，不改真实自启项或音源。测试截图保存在指定输出目录下。

`scripts/Package.ps1 -OutputDirectory <目录>` 生成安装包及 `SHA256SUMS.txt`。GitHub Actions 在 main 推送及 PR 上构建并运行相同检查。离线检查不代替每种硬件、游戏和显示器组合的实测。

`AtkBatteryProbe.exe --help`、`--self-test` 不访问 HID。`--enumerate` 读描述符；`--power`、`--diagnostic` 查询设备；`--dpi-read` 读取 PAW3955 档位表；`--select-stage N` 会实际选择一个已有的一基档位。

## 参考

- [ATK 官方 HUB](https://bpcdn.atkgear.com/hub-v3/production/3.2.27/static/index-F-MYsmWR.js)
- [mouse_tray Windows Nordic 驱动参考](https://github.com/Fan4Metal/mouse_tray/blob/master/mouse_tray/drivers/chipset/nordic52.py)
- [mouse_tray 原生尺寸图标参考](https://github.com/Fan4Metal/mouse_tray/blob/master/mouse_tray/ui/icons.py)
- [MyDockFinder 托盘显示配置](https://github.com/mydockfinder/mydockfinder-for-Win10-Win11/blob/release/language/English.ini)
- [Apple 材质](https://developer.apple.com/design/human-interface-guidelines/materials)与[动效](https://developer.apple.com/design/human-interface-guidelines/motion)设计参考
