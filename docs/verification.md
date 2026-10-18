# 验证范围与复验方法

设备兼容性以 [兼容目录](compatibility.md) 为准。自动回归、隐藏窗口绘制与真实设备验证分别记录，不把测试数量等同于实机型号数量。

以下开发验证脚本位于源码仓库的 `tests` 目录，完整运行包不携带测试工具，也不需要这些脚本才能正常使用。

## 自动回归

`scripts/Build.ps1` 编译协议工具与主程序，并运行协议解析、音频设置、端点匹配、控制器、托盘事件、拖动取消、设备管理、音频作用域、布局/可访问性、进程隔离和安装回滚检查。设备和写入端点由模拟实现代替。

CI另运行Python引擎/协议fixture，将同一份已验证构建打包为完整ZIP，再隔离解压、安装并启动真实应用和便携读取器，验证输入结束和父进程退出时的回收。发行包的SHA256清单用于核对本机安装与CI产物。

## 交互与性能

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-Performance.ps1 -OutputDirectory .\work\performance -Animate
```

这会用模拟设备执行100次隐藏弹窗生命周期和20次拓扑变化，记录实际绘制的P50/P95及超过50ms的次数，验证窗口句柄释放和对象可回收。结果写到 `interaction-performance.json` 与CSV。CI不以机器相关的绝对耗时或进程内存值作为脆弱门槛。

隐藏表面绘制没有验证显示器合成节奏，也不能证明可见桌面60fps；“点击逻辑耗时”不包含真实硬件I/O，“首帧”不等于屏幕出光延迟。真实游戏、多显示器、读屏和窗口管理器行为需要另记实测。

## 八小时资源采样

`tests/Measure-Runtime.ps1` 只读取已经运行的目标进程及其后代，不启动/重启目标，不发设备或音频命令，不改自启。默认28800秒、每30秒一个样本；需要传入主进程PID，避免与同路径音频broker混淆。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Measure-Runtime.ps1 -ExecutablePath "D:\Tools\MyFinderDeviceBattery\outputs\AtkMouseBattery.exe" -TargetProcessId 12345 -OutputDirectory .\work\runtime -StatusPath "$env:LOCALAPPDATA\MyFinderDeviceBattery\status.json"
```

把示例路径和PID替换为真实主进程。采样绑定EXE路径、PID、启动时间、版本与SHA256；退出或换版会明确提前结束，不把短跑写成八小时通过。样本有数量上限，summary.json 区分warmup和steady，记录首末值、变化率、主进程/进程树、私有内存/工作集、句柄/线程及按逻辑核归一的CPU。

时长完成以有效样本的采集时间为准，后续写日志耗时不补足观察时长。已观测后代按PID和实际启动时间持续跟踪，父进程先退出后仍计入；确认退出或PID被重用时移除。暂时不可读的已知身份保留并标记子树不完整，仍计入进程数量上限。

30秒采样可能漏掉短生命周期子进程或瞬时峰值；结果中的峰值仅为观测样本峰值，CPU也不是完整系统会计。报告必须注明实际设备场景，不能把无通用设备时的占用当作典型多设备占用。

`EightHoursCompleted`仅表示目标时长完成；`ContinuousEightHoursObserved`还要求主进程采样无超过`max(4 × 采样间隔, 2秒)`的空档或时钟倒退。`CompleteTreeEightHoursObserved`进一步要求每次子树快照完整；短命子进程在读取时退出会计入不完整快照，但不否定主进程连续观察。不完整快照不纳入Tree统计；任一标记均不代表捕获了采样间的所有子进程。

采样器本身的身份拒绝、完成、提前退出、二进制替换和日志上界由 `tests/RuntimeSamplerTests.ps1` 使用有限、惰性的模拟进程验证，覆盖PowerShell5.1与7。

## 原优化方案的持续项

- 数据真实性、隔离读取、交互、安装和包验证由自动回归覆盖；每次发行仍以实际CI结果为准。
- 设备隐藏/排序、固定托盘设备、通用耳机显式音频绑定和主音量均提供软件回归。保存映射本身不操作系统音源，未绑定角色不自动继承其他耳机。
- INFI75已补有线与2.4G证据，尚无准确电量协议，因此仍不支持电量。蓝牙或其他型号只有取得实际协议/设备证据后才提高等级；不刷固件、不猜命令。
- 原生Windows蓝牙实现取代PowerShell是性能研究候选，须先证明连接与测量语义等价，当前继续使用已验证的系统属性路径。
- 8小时、真实鼠标档位切换、已连接耳机、游戏/多屏、实际读屏和Windows重启应逐项列出实际完成情况。受硬件或桌面环境限制的项目保留待验证，不能用模拟结果替代。

本次发行的具体实测、资源条件和剩余边界见对应Release记录。
