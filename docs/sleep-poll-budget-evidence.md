# 休眠相关读取调用预算证据（2026-11-14）

原调查源码基线：`c9d843693c86daede86739649b8e3ac9bbea2f2d`；本次执行基线：`08534632c9ca232f5cd40ec26b5a0343fd3fd148`（beta.7 源码加原调查报告）。原策略/JSON 探针 **37/37** 是历史结果；本次修复测试隔离后，聚焦 `ActivityRefreshTests` **187/187**，其中原有 65 项断言保留并通过，新增 122 项（含有界屏障完成检查）。下文分别记录历史探针和本次真实控制器/fake 子进程计数。未证实读取预算缺陷；请求次数不能证明设备被唤醒或耗电增加；#128、#588 仍待用户同条件 A/B，beta.7 不是设备睡眠修复。

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

表中行号为原调查基线锚点。本次聚焦运行保留上述原有断言及 fake `DpiBusy`，没有放宽条件；未运行完整测试套件或真实 DPI 操作。本次新增组合覆盖见“控制器动态交错”。

## 历史运行：策略与真实 JSON transport、纯 fake 子进程（37 项）

直接使用未改动的 `ActivityRefreshPolicy` 和 `GenericBatteryWorker`；自制进程只读 stdin、写假帧与任务 tmp 日志，无引擎/HID。没有构造 TrayContext。驱动显式传入策略时间和 fresh/canStart 回调，并以文件屏障阻塞进程；只跳动时间数据，不等待 8/60/300 秒。这里 fresh 回调是探针提供，**未验证控制器 FreshActivitySource 的接线**。

计数 T=发往 fake stdin 的 generic poll JSON 总数，P=physical-target，G=全量，F=force=true，A=全量完成后的策略派发（含 retry），Q=控制器 genericQueued 补发，N=最终策略 pending（含在途）。不等同于函数入口次数或底层硬件事务。新探针未调用 native、DPI、headset/audio 委托，不能据此量化整个应用的读取总量。

| 场景与触发/完成顺序 | 预期依据 | 实际 T/P/G/F/A/Q/N | 结果/定位 |
| --- | --- | --- | --- |
| 全量阻塞→100 次 stale 活动→全量返回 fresh→Take | Take 先查 fresh，清除待办 | 1/0/1/1/0/未测/0 | PASS；ActivityRefresh.cs:35 |
| 同上但全量 stale→targeted 阻塞期间再 100 次活动→targeted fresh→1 秒后 100 次 stale 活动 | Record 在途合并；Complete 成功冷却 5 秒 | 2/1/1/2/1/未测/0 | PASS；ActivityRefresh.cs:25、47 |
| 同上但 targeted stale→+1 秒不重试→+1.5 秒 retry 仍 stale→再 100 次活动 | 只一次 retry；失败后冷却 15 秒 | 3/2/1/3/2/未测/0 | PASS；ActivityRefresh.cs:47 |

三者全量阻塞时均 T=1、N=1、Coalesced=99；后两者 targeted 阻塞时 T=2、N=1、Coalesced=199。第三者 targeted 完成时 N=1，retry 后 N=0、Retries=1。所有请求日志均为 force=true；**没有实际运行 timer/非强制请求或控制器队列**。A 是探针驱动真实策略所得，不能冒充 Q。

## 本次控制器动态交错：真实 Context、纯 fake 子进程

`ControllerInterleavings` 使用既有内部 `Context`，native 委托返回假数据，`GenericBatteryWorker` 启动测试 EXE 的自身副本，不启动 Python/engine。每条 fake 请求写 `requests.log` 和编号 `entered-N`，只在测试写入 `release-N` 后返回；屏障超时会失败，不作为成功完成。每个场景先完成构造器自己的第 1 次强制刷新，并只从场景计数中扣除这一次。活动时间通过原 `activityClock` 注入；时间门禁只数据化设置 `genericChecked`，没有等待 60/300 秒。

本节 T/P/G/F/A/Q/N 中，A 是真实控制器 `activityRefresh.Reads`（含 retry）；Q 是在屏障处观察到 `genericQueued` 后的额外全量 JSON 数，并断言没有其他 targeted/全量派发。它们不再是探针手工模拟的队列。数字仍限于单通道、无额外拓扑/清历史/用户触发的有限忙窗，不是全应用 I/O 总数。

| 场景、触发/完成顺序 | 实际 T/P/G/F/A/Q/N | 动态断言/结论 |
| --- | --- | --- |
| 全量在途→100 次 stale 活动→全量 fresh | 1/0/1/1/0/0/0 | PASS `full-fresh`：忙时 pending=1、Coalesced=99；真实 FreshActivitySource 清待办，零 targeted |
| 同上，全量 stale→targeted 在途再 100 次活动→targeted fresh | 2/1/1/2/1/0/0 | PASS `target-fresh`：Coalesced=199；fresh 完成清 pending，无 retry |
| 同上，targeted stale→+1 秒不派发→+1.6 秒 retry stale→100 次活动 | 3/2/1/3/2/0/0 | PASS `target-stale`：首次 stale targeted 后 pending=1；只一次 retry，之后 100 次均命中 cooldown |
| 全量在途→5 force + 5 nonforce→100 次活动→全量 fresh→补发 fresh | 2/0/2/2/0/1/0 | PASS `full-merge-first-fresh`：强制位只保留一个，补发优先于活动 |
| 同上，首轮全量 stale→补发 fresh | 2/0/2/2/0/1/0 | PASS `full-merge-first-stale`：首轮 stale 也不绕过已有强制队列派发活动 |
| targeted 在途→PanelOpened→targeted fresh→补发全量 fresh | 2/1/1/2/1/1/0 | PASS `panel-target-fresh`：targeted 完成后 pending=0，面板独立强制读仍补发一次 |
| 同上，targeted stale→补发全量 fresh | 2/1/1/2/1/1/0 | PASS `panel-target-stale`：补发在途时 pending=1；补发 fresh 清除 retry 待办，Retries=0 |

`ControllerTimeGates` 分别设置背景间隔 60、300 秒，调用真实 `PanelOpened/PanelClosed`；不创建可见 Details。每组场景总数（扣掉构造器）T/P/G/F=3/0/3/1：面板打开的强制全量一次、面板非强制到期一次、关闭后的背景非强制到期一次。

| 门禁/入口 | 设置与实际计数 | 结果 |
| --- | --- | --- |
| 打开面板后的真实 RequestWirelessRead(false) | genericChecked 距今 7 秒：0；9 秒：1，force=false、parkEmptyReaders=false | PASS，两个背景配置均相同；测门槛两侧，未测恰好 8 秒边界 |
| 面板开时真实 refresh.OnTick | genericChecked=MinValue 仍 0 次 | PASS；panelOpen 拦截背景入口 |
| PanelClosed 后真实 refresh.OnTick | 背景间隔减 2 秒：0；加 2 秒：1，force=false、parkEmptyReaders=true | PASS，60/300 秒均覆盖；完成后 genericChecked 回到当前 UTC，liveSync 已停 |

仍未动态验证：可见 Details 的真实 2 秒 liveSync 与 refresh 同时投递、native 30 秒读取预算、120 秒配置及精确时间边界、多通道/拓扑/清历史组合、长期任意强制触发流。已有 fake DpiBusy 在同一聚焦运行仍为一次假 set_dpi、一次 physical poll，native 不读；没有实机写入。

完成会在 `TrayApp.cs:1316` 更新时间门禁，无新电量也如此；未在途的 stale 活动依然可强制 targeted。活动自身没有结果时，只有 `ActivityRefresh.cs:53` 的一次延迟重试。现有逻辑没有规定“所有独立强制触发共用一次读”的全局预算，不能凭两次调用宣布越界。

## 测试隔离修正与安全 red/green

原基线的 `ApplyWireless` 在 complete 时无条件排队调用 `SafeDiagnostic.Transition`，成功/错误帧均可能写用户 diagnostics.log。本次只在该派发增加 `complete && persistStatus`，并加每个 Context 独立的可选 `wirelessDiagnosticDispatcher` 构造参数（readonly 字段，在初始刷新前安装）。未改 `Diagnostics.cs`，没有全局禁日志、清 last 字典或新增异常吞噬。

生产默认行为证明来自 diff：公开构造器仍传 diagnostics=true，且不传新参数；默认派发仍是原来的 ThreadPool.QueueUserWorkItem→SafeDiagnostic.Transition，category=`device_readers`，state 仍 `GenericError ?? GenericHealthSummary ?? "ready"`。原 SafeDiagnostic 清理/去重/旋转/异常处理/目标均未改，partial 仍不派发。没有修改 ActivityRefresh 策略、请求/续队列、DPI 校验、读取时间戳或 provider。这是**测试隔离修复**，不是设备耗电根因或休眠修复。

`DiagnosticIsolation` 只调用真实 `ApplyWireless`，每次 fake sink 都是内存计数：diagnostics=false 的 success/error × complete/partial 四组合均 0；在已隔离实例上用反射暂时翻转 persistStatus，true 的 complete success/error 各一次、category/state 完整匹配，partial 两种均 0。此处不执行 diagnostics=true 构造器，避免 HID 枚举；短暂启用 gate 期间 fileWriter 替换成内存 SnapshotWriter，临时配置关闭 RememberReadings 和 LowBatteryNotifications，并在 finally 恢复原 writer 和 false gate，退出不会调用真实 AudioBroker shutdown。其余 Context 从构造起使用遇诊断即抛错的本地 fake，且均 diagnostics=false；没有访问用户诊断文件或真实配置。

安全 red：先只加入相同 fake 派发接缝，保留旧 `if(complete)`，编译成功退出 0；仅运行隔离入口，运行退出 1，断言 `FAIL diagnostics=false complete success dispatches zero diagnostic writes`。sink 已在构造前注入，没有为证明旧行为写真实日志。green：加 persistStatus 门禁后聚焦编译/运行均退出 0，187 checks。没有把编译错误当 red。

## 本次复核材料

本工作树忽略目录 `tmp/activity-diagnostics-isolation-1114f/` 保留 `Run-Focused.ps1`、`red/`、`green/`。每个阶段包含完整逐参数 `compile-command.txt`、`run-command.txt`、全部参与编译源码的 `source-sha256.txt`、`compile.log`、`run.log`（逐项断言名及动态计数）、`exit-codes.txt` 和 `evidence-sha256.txt`；green 子目录还包含所有假请求、屏障文件和临时 server.py/EXE。编译使用 Framework64 v4 csc、scripts/*.cs（排除 Probe/Tests/AssemblyInfo）、ActivityRefreshTests.cs、DevicePackSchema 资源与原测试所需 Framework 引用；21 条 CS0649 警告，零错误。没有运行完整 Build/Run.ps1、Python、性能、网络或硬件检查。

`Finalize-Evidence.ps1` 从通过日志分离原有 65 项和新增 122 项断言名，保存为 `original-65-assertions.txt`、`added-122-assertions.txt`；另存 `dynamic-counts.txt`、最终源码/资源 SHA 与包含全部假请求日志的 `all-artifacts-sha256.csv`。

```powershell
# 已运行，脚本拒绝覆盖原证据目录：
& '.\tmp\activity-diagnostics-isolation-1114f\Run-Focused.ps1' -Phase red
& '.\tmp\activity-diagnostics-isolation-1114f\Run-Focused.ps1' -Phase green
```

red 是当时仅加入接缝、尚无门禁的源码，编译源码 SHA 已独立保存；不要用最终源码运行 red 并期待失败。主管可先审核上述局部 fake 与内存 writer，再复制 runner 到另一忽略临时任务目录（保持 tmp 下两级目录结构）独立复跑 green。

| 本次材料 | SHA256 |
| --- | --- |
| red/run.log（编译退出 0、断言退出 1） | `FAA96E3453C00398000D5F5966D7BBE8DAE10DFFD610D035EFF7C4D651390071` |
| scripts/TrayApp.cs | `99B36049BDACED3962098A5820292CE764271EA9D8B0C7B9C1B6C130724A5609` |
| tests/ActivityRefreshTests.cs | `91BF33D1B4E37588784ED62E955CD1F15F8391FC049827D9813F61F9EB27BEE8` |
| green/compile.log | `AC2B079A44810FF411152EA1BA728F753856F5E43F0E8ADA830D1BD615D812CD` |
| green/run.log | `675D869ABF88E38EF8B05C24E25826149F8FD67CE62D2892817317777749EDAB` |
| green/source-sha256.txt | `38205A70FB4BB1F8A87FE962B7B237CD606EB76B6B63B2CE85B89DC9A5E52103` |

## 历史探针复核材料（原任务记录，未在本次重跑）

原任务记录位于当时工作树忽略目录 `tmp/sleep-call-budget-1114e/`，不会随文档自动进入本专用工作树。以下 SHA 与命令保留为历史证据，本次未重新取得或重跑这些材料。原编译命令及完整参数保存在 `Run-Probe.ps1`：使用 Framework64 v4 csc、原有 scripts/*.cs（排除 Probe/Tests/AssemblyInfo，与现有测试相同）、`/main:SleepPolicyProbe` 及所需 Framework 引用/DevicePackSchema 资源。原入口：

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

最终核对：`git diff --check`；相对本次执行基线仅改 `scripts/TrayApp.cs`、`tests/ActivityRefreshTests.cs`、本文。版本、README、CHANGELOG、CI、engine/provider、全局日志策略均未改；未提交/推送/合并/切换分支，不自动发布测试版。
