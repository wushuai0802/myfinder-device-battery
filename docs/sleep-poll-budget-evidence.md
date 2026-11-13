# 休眠相关读取调用预算证据（2026-11-14）

基线：`c9d843693c86daede86739649b8e3ac9bbea2f2d`。未证实读取预算缺陷；新运行的策略/JSON 探针为 **37/37**。控制器交错仅作静态推导，原因见隔离缺口。请求次数不能证明设备被唤醒或耗电增加；#128、#588 仍待用户同条件 A/B，beta.7 不是设备睡眠修复。

## 已有覆盖与复用边界

先检查 `tests/ActivityRefreshTests.cs`；本基线对应路由方法名为 `Controller`，没有 `ActivityRouting`。

| 入口/定位 | 已有断言覆盖 |
| --- | --- |
| `Policy:25` | 新鲜跳过、100 包合并、忙时保留、1.5 秒重试一次、失败退避、独立源、unsupported、拓扑和 Dispose；新鲜已有读可清 pending |
| `Transport:57` | 互斥/无效目标不启动进程，physicalId JSON 与 health 解析 |
| `Controller:74` | 新鲜 native/generic 零读；native 忙时延后；双接收器各一次 targeted；锁屏、后台独立 fallback、退出 |
| `BoundedFailures:110` | unsupported 拓扑失效；unavailable/forget 各至多两次 targeted，继续活动不绕退避 |
| `DpiBusy:130` | fake DPI 阻塞→活动 pending=1→释放；断言一次 fake set_dpi、一次 physical poll，native 不读 |
| `MakeWorker:54`、`Requests:55`、`PhysicalRequests:56`、`Context:68` | 自身副本充当 python.exe；JSON 日志计数；内部构造器、假 native、注入活动时钟；Main:163 使用临时配置 |

任务简报说明上述已有断言在 beta.7 通过；本次仅复用其覆盖信息，**未重跑，也未取得对应原始通过日志**。不把源码断言当成本次通过结果。新探针只补“全量与活动重叠、在途活动与完成结果交错”的组合计数；不重复 DPI 和完整测试套件。

## 新运行：策略与真实 JSON transport、纯 fake 子进程

直接使用未改动的 `ActivityRefreshPolicy` 和 `GenericBatteryWorker`；自制进程只读 stdin、写假帧与任务 tmp 日志，无引擎/HID。没有构造 TrayContext。驱动显式传入策略时间和 fresh/canStart 回调，并以文件屏障阻塞进程；只跳动时间数据，不等待 8/60/300 秒。这里 fresh 回调是探针提供，**未验证控制器 FreshActivitySource 的接线**。

计数 T=发往 fake stdin 的 generic poll JSON 总数，P=physical-target，G=全量，F=force=true，A=全量完成后的策略派发（含 retry），Q=控制器 genericQueued 补发，N=最终策略 pending（含在途）。不等同于函数入口次数或底层硬件事务。新探针未调用 native、DPI、headset/audio 委托，不能据此量化整个应用的读取总量。

| 场景与触发/完成顺序 | 预期依据 | 实际 T/P/G/F/A/Q/N | 结果/定位 |
| --- | --- | --- | --- |
| 全量阻塞→100 次 stale 活动→全量返回 fresh→Take | Take 先查 fresh，清除待办 | 1/0/1/1/0/未测/0 | PASS；ActivityRefresh.cs:35 |
| 同上但全量 stale→targeted 阻塞期间再 100 次活动→targeted fresh→1 秒后 100 次 stale 活动 | Record 在途合并；Complete 成功冷却 5 秒 | 2/1/1/2/1/未测/0 | PASS；ActivityRefresh.cs:25、47 |
| 同上但 targeted stale→+1 秒不重试→+1.5 秒 retry 仍 stale→再 100 次活动 | 只一次 retry；失败后冷却 15 秒 | 3/2/1/3/2/未测/0 | PASS；ActivityRefresh.cs:47 |

三者全量阻塞时均 T=1、N=1、Coalesced=99；后两者 targeted 阻塞时 T=2、N=1、Coalesced=199。第三者 targeted 完成时 N=1，retry 后 N=0、Retries=1。所有请求日志均为 force=true；**没有实际运行 timer/非强制请求或控制器队列**。A 是探针驱动真实策略所得，不能冒充 Q。

## 控制器交错：静态路径与尚缺的运行证据

以下假设 worker 可用、无清历史/拓扑变化、同一 generic 通道、无额外事件；数字均为源码推导，实际计数未测。初始构造器自己的强制刷新须先完成，才能将后续请求按场景计数。

| 场景、触发/完成顺序 | 静态预期与定位 | 实际/结论 |
| --- | --- | --- |
| PanelOpened 先发全量→stale 活动→全量 fresh | `PanelOpened:1055`→`RefreshBattery:1169` 强制读；`ActivityCanStart:1208` 阻塞 activity；完成→Continue→Take 清新鲜待办。预计 T/P/G/F/Q/N=1/0/1/1/0/0 | 未测；新探针仅支持策略部分 |
| 活动 targeted 已在途→PanelOpened→targeted fresh→补发全量 fresh | `RequestWirelessRead:1310` 将强制读合并为 genericQueued；`ContinueWirelessQueue:1273` 优先强制补发，再 Drain。预计 2/1/1/2/1/0 | 未测；第二次有独立强制请求依据，不能直接判重复 bug |
| 全量在途→5 次强制全量+5 次非强制定时触发→完成→补发完成 | busy 分支只让 force 置一个队列位，非 force 不排队；补发绕过时间门禁。预计 2/0/2/2/1/0；无论首轮 fresh 与否都补一次 | 未测；只对这一有限忙窗成立，继续发生新 force 可继续排队 |
| 面板打开时 refresh 与 liveSync 同到 | `refresh:955` 被 panelOpen 拦截；`liveSync:958` 需 details 可见，每 2 秒触发，generic 受 `1311` 的 8 秒门禁；native 电量只在无值/≥30 秒时读（仍可读 DPI） | 未测；不是两个 Timer 都发 generic 读 |
| PanelClosed→非强制后台 tick | `1058` 只停 liveSync，refresh 保留；`1311` 按完成时 genericChecked 计间隔，默认 60 秒（配置可 120/300），小于门槛不读，到期且空闲才读；`1312` parkEmptyReaders=true | 未测；activityTime 注入不控制这里的 DateTime.UtcNow |
| fake DPI 在途→活动→DPI 完成 | `1208` 暂缓；`1292` 更新 genericChecked，但 `1300`→Continue→Drain 的活动读是 force，不受 8/60 秒门禁；DPI 状态新鲜不等于电量新鲜 | 本次未测；已有 DpiBusy 断言一次写、一次 targeted，禁止真实写入 |

完成会在 `TrayApp.cs:1316` 更新时间门禁，无新电量也如此；未在途的 stale 活动依然可强制 targeted。活动自身没有结果时，只有 `ActivityRefresh.cs:53` 的一次延迟重试。现有逻辑没有规定“所有独立强制触发共用一次读”的全局预算，不能凭两次调用宣布越界。

## 隔离缺口与最小建议（未实施）

`ActivityRefreshTests.Context:68` 使用内部构造器、diagnostics=false，绕开公开构造器的 HID/输入/音频/更新注册；`UseTransientConfiguration(RememberReadings=false)` 阻止配置读取及缓存写入，`SaveStatus:1120` 也有 persistStatus 门禁。但 `ApplyWireless:1346` 在 **complete 时无条件** 排队调用 `SafeDiagnostic.Transition`，该函数在 `Diagnostics.cs:107`、`DeviceSettings.cs:117` 指向用户 LocalApplicationData 下的 diagnostics.log；成功和错误帧都有此路径。当前没有临时目录或 sink 注入入口。因而现有 Context 不能保证本任务要求的全隔离；按简报第 4 条采用静态缺口，不运行该入口，也未访问用户诊断文件。

这是可定位的**测试隔离缺口**，不是设备睡眠/耗电因果。若主管后续需要控制器动态复核，最小候选是在 `TrayApp.ApplyWireless` 的诊断派发增加 persistStatus 门禁；回归覆盖 diagnostics=false 的成功/错误完成均不落用户诊断，再补本表控制器交错。此处仅建议，不修改生产逻辑，也不因未证实的耗电报告另建优化工作。

## 复核材料

均位于本工作树忽略目录 `tmp/sleep-call-budget-1114e/`，不会随文档自动进入提交。编译命令及完整参数保存在 `Run-Probe.ps1`：使用 Framework64 v4 csc、原有 scripts/*.cs（排除 Probe/Tests/AssemblyInfo，与现有测试相同）、`/main:SleepPolicyProbe` 及所需 Framework 引用/DevicePackSchema 资源。实际入口：

```powershell
& '.\tmp\sleep-call-budget-1114e\Run-Probe.ps1'
```

脚本拒绝覆盖已存在的 runs 目录；复跑请保留原证据并使用另一份临时任务目录。编译退出 0，有 21 条 CS0649 警告；运行退出 0，37 checks。未运行全套 Build、Run.ps1、Python 或性能/硬件检查。

| 相对上述 tmp 目录的文件 | SHA256 |
| --- | --- |
| SleepPolicyProbe.cs | `FEFF9B0898E0D24E03B3624F44BDF52578D9C3D406C025775EB8EB74855847E2` |
| Run-Probe.ps1 | `C0056C11680546B9DE351789167E723749E8C95870BB8E31597B377737FD9710` |
| SleepPolicyProbe.exe | `E264592732DACCA4D2E81D606B2AB65923739888895B8C9CD0ABF573DFB03C1B` |
| compile.log | `D394EA500E8855FC4E7101ABE41F979E9D78C2740ABEC19142D93C6536723B14` |
| run.log | `75295045D90F7D40E82F9679AA6BF308FB1BC56B58B7D5E1C6F560D17E3C8212` |
| runs/full-fresh/requests.log | `78F7C223560136AB8C980B663A3CED6A3FFAD858EBE1A9AD2B00A1813312D2DD` |
| runs/full-stale-target-fresh/requests.log | `05D346453FADAED0553A300EEBB3BE32BCA021D388FF2809716670B7EA65A2E3` |
| runs/full-stale-target-stale/requests.log | `447D9A090EA02B5832F8D690B0F7403DB9EFA0C57A1E51CCC5B84B5CFD7527C8` |

最终核对：`git diff --check`；相对基线唯一新增的非忽略文件为本文，生产源码、已有测试、版本和分支未改变；未提交/推送/合并。
