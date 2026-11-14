# Grid CI 像素调查

当前交付是 **1114v 进程顺序对照，尚无产品修复**，基线为 `695d36972ebcd5826b0118aff421fa89e11b382a`。仅改独立 PowerShell 诊断脚本、实验 job 和本文；C# 绘制、PanelDesignTests、原矩阵／零容差断言、engine 与版本均未改。下文 1114t／1114p 保留为历史记录，其分支和命令不代表本轮 CI 配置。

## 1114v：真实变体与顺序对照

主管已核对准确 CI `43577351933`（head 为本轮基线）：三 job 均成功，四进程内部 124 帧／174 比较无差异；但真实 `cold-off/first.png` 与 `cold-on/first.png` 全 390×780 图有 **12 点差异**，原 390×150 设备区恰 **6 点**，其余六点在同名 DPI 标题平移 `(+2,+81)`。坐标／颜色见 `.ai/evidence/grid-ci-43577351933/cross-process-differences.json`，主管评审见 `.ai/evidence/grid-glyph-order-1114v/coordinator-review.md`。两变体的原始行 SHA256 分别以 `4C0EA413`、`C5656991` 开头；进程最后八帧切换 capture 后仍各自保持。旧失败发生在同一进程两帧，这次跨进程证据不能冒充完全相同的复现。

相同 CI VM／EXE／字体／GDI+ 的两个进程已有差异，单靠本机与 CI 的字体版本差异不足以解释。初始 capture、进程顺序、首次字形实例化或其他状态仍未分清；会话／kernel 缓存只是未证实猜想，不据此预热或修改产品。

```powershell
./tests/Run-GridPixelDiagnostic.ps1 -OutputDirectory ./tmp/ggo-1114v-run -TimeoutSeconds 180 -CasePlan order-control
```

`CasePlan` 默认 `reference` 保留原四角色及顺序；`order-control` 为 `cold-on → cold-off → cold-off → cold-on`，唯一 ID／目录依次 `01-cold-on`、`02-cold-off`、`03-cold-off`、`04-cold-on`。传给现有 C# 的角色仍为合法 `cold-on/off`。每进程 32 帧，总计 **128 帧／156 次进程内比较**；保留全部首帧及原比较逻辑，没有额外预热、丢帧或改字体／缓存。总预算 180 秒（含编译及跨进程比较），单进程最多 35 秒，原截止与精确子进程清理规则不变。

四 case 全部有效完成后，先核对首帧的 Parameters、scale／dark／offset、尺寸／DPI／像素格式、RegionObserver、完整前后业务状态及核心 Graphics；只排除 capture 事件及其 Graphics 记录序号前缀、标签／耗时和每进程即时生成的绝对 UTC，fresh／status／电量／文字等仍逐项一致，原始时间保留在各 `result.json`。order-control 任一业务输入不一致即报错，不硬比；reference 的不同 scale 首帧对明确记 `skipped_incompatible_inputs`，只比较同输入对。

`first-frame-comparison.json` 保存四条源记录（唯一 ID／role／order／PID、完整 PNG 路径及文件 hash、原始行 hash、核对后的输入）和至多六对全图零容差结果（点／左右 ARGB／bounds、原设备区数量）。reference 的 1.25× 图宽 325，固定 390×150 矩形统计为与图像的交集，`originalDeviceAreaFullyCovered=false`；order-control 四图均完整覆盖原设备区。原图保留，不以裁图决定通过。任意真实差异使整体 `cross_process_difference / failed / exit 1`，即使四 case 各自 `not_reproduced`；所有图相同也只记 `not_reproduced`。case 错误／超时保留非零状态，跨进程为 `not_run`，不补跑 case；比较自身失败／超时另记 error／timeout。

新顺序让首个进程从 off 改为 on，并重复两种角色，可观察 A/B 变体是否跟随最早启动位置、capture 角色或两者都不稳定。单次新 CI 仍不能证明首次字形或缓存的因果关系，也不能把“同图”当修复。实验 job 仅在 push 到 `refs/heads/codex/grid-glyph-order-1114v` 时以 order-control 运行，保留 fixed SHA、`always()`、3 天 artifact 和 8 分钟上限；普通 Windows／protocols、触发器及权限均不变。下一步由主管提交并 push 准确版本、核对 run head SHA 后比较四首帧的 A/B 顺序；本机 PS 语法／diff 检查不代表远端图形验收。

### 1114v 本机与离线验证

最终脚本在 Windows PowerShell **5.1.26100.9711** 仅运行一次 order-control 绘制批次：`tmp/ggo-1114v-run/summary.json`，**5273.227 ms**，`not_reproduced / passed / exit 0`。四唯一目录／PID 顺序为 `01-cold-on/63576`、`02-cold-off/34292`、`03-cold-off/42904`、`04-cold-on/33656`，各 **32 帧／39 比较**，共 **128／156**。四张首帧保留，所有 JSONL 与 result 计数、首帧引用、capture 最后八帧切换、35 秒进程预算及最终源码 hash 均核对一致；没有额外绘制批次。

四首帧输入全部一致，六对全图及设备区均 **0 差异**，本批次未出现进程间变体。四个原始行 hash 均为 `4C0EA413498DDFC163F6B62F8257980059AEF3E7E727329A79BC9D166BC33333`，与旧 CI 较深版本相同；此前主管本机批次记录为 `C5656991…`，这项跨批次变化如实保留，不能据此定因或声称本轮进程间复现。

从最终脚本 AST 提取纯读图／输入核对／分类函数，在同一 Windows PowerShell 5.1 中离线读取未修改的 CI `43577351933` 原 PNG：精确 **12／6** 点，全部坐标／左右 ARGB／bounds／原始 hash 与主管 JSON 一致；同图为 0。实际旧 CI 四首帧以 reference 核对后得到 `cross_process_difference / exit 1`，不同 scale 的四对明确跳过；order-control 业务输入不一致、未完成四 case、未完成 pairs 和已到期读图预算均被拒绝。该验证只读旧 PNG，不是新的 native 渲染复现，没有注入像素或运行下载的 EXE。

结果与临时校验脚本在 `tmp/ggo-1114v-checks/{offline-verification.json,native-verification.json,real-ci-copy-final/first-frame-comparison.json,Verify-Offline.ps1,Verify-Run.ps1}`；早期离线尺寸守卫失败记录保留在 `real-ci-copy/`，没有因此重复 native 批次。PS 语法与 `git diff --check` 通过；精确比对 CI 仅两行变化，跟踪变更仅三个白名单文件。新实验分支 CI、其真实失败／超时 artifact 和强制终止路径仍待主管验证，尚无产品修复。

## 1114t：运行与读图

简报记录基线的准确 push run `43572909664` 两平台成功，只有完整包 artifact，没有触发失败取证。这不能消除此前两次六像素失败。新 runner 摘录中，失败 run `43480911417` 的 `run-43480911417-runner.txt:3-6`、失败 run `43569094626` 的 `run-43569094626-runner.txt:7-10`，以及通过 run `43572909664` 的 `run-43572909664-runner.txt:7-10` 都记录 Windows Server 2025 / `windows-2025-vs2026`，同镜像版本 `20261103.298`。这些只读摘录位于 `.ai/evidence/grid-pixel-repro-1114t/`，不能仅凭 OS 名称定因。

```powershell
# 新建、明确且短的目录；拒绝覆盖已有证据。无需 Build.ps1 或真实设备。
./tests/Run-GridPixelDiagnostic.ps1 -OutputDirectory ./tmp/gpr-1114t -TimeoutSeconds 180
# 脚本编译后的单 case 入口（主管调查用；本机批次不额外重复执行）：
# ./tmp/gpr-1114t/PanelDesignTests.exe --grid-repaint-probe --case cold-off --output ./tmp/another-case --budget-ms 34000
```

| 冷进程 case | 有界对照 | 完整绘制帧数上限 |
| --- | --- | --- |
| `cold-off` / `cold-on` | 1.5×浅色首帧；同 layout 连画；重新 Build offset=0/31；最后切换 capture 与原首帧比较 | 各 32 |
| `matrix-prefix-off` / `matrix-prefix-on` | 原矩阵连续的 1.25×浅色 → 1.25×深色 → 1.5×浅色；每段保留 baseline → offset=0/31/max → 普通／真实 offscreen 控件反馈的调用顺序 | 各 30 |

这是原序列的有界连续前缀对照，不声称重放完整 Panel 历史；更早的 1×及其他 fixture 组合不在 32 帧预算内。没有自行预热、清缓存或丢弃首帧。所有 baseline 都用于后续比较。offset=0 与同 layout 使用完整图像；offset=31/max 保留原完整设备区和固定 volume/footer 区。全图反馈对照只使用 `VisibleRect.IsEmpty` 的真实隐藏控件，沿用原断言的状态。

`capture=false` 不挂绘制／测量观察器，只在真实帧边界读取核心 Graphics 参数；这项额外只读取证仅新入口启用，旧调用默认不变。矩阵前缀在原本的位置保留 `InteractionLayout.Observer=regions.Add`。`capture=true` 还记录实际文字、字体、测量缓存与绘制事件。两个冷 case 最后八帧切换 capture，其余阶段保持初始开关。

脚本总预算最多 **180 秒**（含编译），编译最多 30 秒；至多 4 个冷测试进程，各至多 35 秒且按剩余预算分配，内部另留 1 秒给退出；总预算留 2 秒写汇总。原生超时只终止 `launched-processes.json` 中本脚本创建的精确 Process 对象，不终止树或其他进程。任意差异立即停止，不运行剩余 case；超时或取证不完整均失败。退出码：未复现 `0`、像素差异 `1`、编译／诊断错误 `2`、超时 `124`。`not_run` 表示提前停止后的未执行 case。

下载新 job 的独立 `grid-repaint-diagnostic` artifact 后：

1. 先看 `summary.json` 的 `status/outcome/exitCode` 和各 `case-summary.json`。`passed + not_reproduced` 仅表示本次无差异，不表示 fixed。
2. 每 case 的 `result.json` 保存全部帧序号、首帧／baseline 路径、原始 LockBits 行 SHA256、最终帧 hash、逐次比较区域／左右帧号／时间、capture、状态及核心 Graphics 参数。成功也保存 `first.png`，前缀另外保存两段 baseline；不存每张相同 PNG。`frames.jsonl` 与 `comparisons.jsonl` 是逐条落盘记录，强制超时导致最终 JSON 不完整时仍可读取；`execution.json` 是父进程的最终执行／超时判定。
3. 差异看该 case 的 `differences/<pid-guid>/{left.png,right.png,difference.json,difference-8x.png}`；仍是完整原图和原区域零容差比较，8×图只辅助定位。取证写入错误单列 `ArtifactError`，不会吞掉失败。
4. `environment.json` 保存实际 Windows build、白名单 runner 变量、全部编译源／脚本／字体／编译器 hash，`executable.json` 保存 EXE hash；各 case 的 `result.json.Environment` 保存实际加载的 GDI+ 路径、版本和 hash。缺失字体标 `unavailable`，不改系统字体或配置。编译和每 case 分别保存 `stdout.txt/stderr.txt`，不 dump 环境。

CI 只追加 `grid-repaint-diagnostic`，条件严格为 push 到 `refs/heads/codex/grid-pixel-repro-1114t`，`windows-latest`、8 分钟 job 上限。沿用既有 checkout/upload 固定 SHA，`always()` 上传独立输出、保留 3 天；差异／超时直接使 job 失败，无 `continue-on-error`。原 windows/protocols、gate、触发器和权限内容未改。main、PR 和 workflow_dispatch 不运行此长诊断。

本机实际运行结果记录在本节下方；原完整 Panel 的历史通过记录见后文，本轮不重复全套、性能或产品构建。后续 owner 为主管：审阅四文件 diff 后，仅在实验分支提交并 push 本次准确提交；核对远端 run 的 head SHA，再读取新 job 的图像／case 摘要。若出现真实差异，依据首个差异安排 renderer 或 fixture 的有范围根因任务；若未复现，保留该事实，不能改称已修复。执行者不提交、推送、触发远端或修改产品。

### 1114t 本机实际验证

唯一绘制批次：`tmp/gpr-1114t-run/summary.json`，编译 exit `0`，总体 exit `0`，`passed / not_reproduced`，**4542.85 ms / 180000 ms**。四个冷进程均保存并比较首帧，共 **124 帧 / 174 次严格比较 / 8 张 baseline PNG**；逐帧及逐比较 JSONL 数量与最终汇总一致，没有丢弃首次绘制或保存每张相同 PNG。已查看 `cold-off/first.png`，为包含 `ATK A9 Air+` 的完整 390×780 图像。成功不等于根因修复。

| case | PID | 帧数 | 比较次数 | 父进程记录耗时 |
| --- | --- | --- | --- | --- |
| cold-off | 25568 | 32 | 39 | 985.04 ms |
| cold-on | 70560 | 32 | 39 | 793.38 ms |
| matrix-prefix-off | 21880 | 30 | 48 | 690.18 ms |
| matrix-prefix-on | 54672 | 30 | 48 | 780.83 ms |

每 case 的 `result.json/stdout.txt/stderr.txt` 与根目录 `environment.json/executable.json/launched-processes.json` 均保留。实际 build `26300.9711`，GDI+ `10.0.26100.9711`，五个字体文件均可读取 hash。最终 C# 与该批次记录的源码 hash 一致，EXE SHA256 为 `7EFACB9439F937B2B3B3442012F81BC82020D0F1810492860760255D91659A05`。

启动前曾因 Windows PowerShell `Start-Process` 返回空 ExitCode 而把生成 EXE 的编译误报为失败，记录在 `tmp/gpr-1114t/summary.json`；当时未启动任何诊断冷进程。改为持有 `Process.Start` 的句柄并异步读取输出后才执行上述绘制批次，初次记录没有覆盖。批次后仅完善父汇总的 `running → not_reproduced` 状态，防止未执行 case 时预报成功；从最终脚本 AST 实际执行完整／缺一 case 的汇总分支，分别通过／拒绝，不再绘图。最终脚本 SHA256 为 `374F5821D4720ECA1C93068FB9B4170B9740F7451C9FE1FC5C80C03C0D67CDDB`，批次内的脚本 hash 则如实保留当时版本。

用该批次 EXE 反射检查：入口拒绝 `35001 ms`、进程预算到期抛 `TimeoutException`、第 33 帧在绘制前抛 `InvalidOperationException`；不启动额外冷进程、fixture 或图像，守卫记录为 `tmp/gpr-guard-1114t/result.json`。最终 PowerShell 语法和 `git diff --check` 通过；逐段比对原 `GridSameArea/GridRawDifference/GridRepaintIdentity/GridOverflowViewportMatrix`、原 Main 其余内容以及原 CI 前缀，均未变。变更范围只有四个白名单文件。

本轮未触发真实像素差异或父进程强制终止，失败图路径与远端 `always()` 上传尚待实验分支 CI 实测；没有新增真实六像素图、根因或硬超时执行证据。原完整 Panel／全 Python／性能／产品 Build 不重跑。主管应将这四文件提交并 push 到 **`codex/grid-pixel-repro-1114t`**，核对准确 head SHA 后单独检查新 job 与同名 artifact；无需手动 dispatch 或原样重跑旧提交。

## 1114p 历史记录（以下不是本轮运行）

下文保留此前基线 `50e4897a4782e7e6b19faba2dac8ad375bb165d8` 上诊断候选的调查和验证；历史文件路径属于当时工作树，不能视作本轮新证据。

## 已有 CI 证据

| 来源 | 组合／offset | 实际差异 |
| --- | --- | --- |
| main run `43569094626`，commit `50e4897a4782e7e6b19faba2dac8ad375bb165d8` | normal / native=True / audio=False / collapsed=True / scale=1.5 / light，offset=0 | 设备区 390×150，6 pixels，首点 `(22,97)`，bounds `(21,97)-(76,100)`，`FFDFE1E5 → FFE7E9EB` |
| PR597 run `43480911417` / job `132076818706` | 同组合，offset=31 | 同样的区域、数量、首点、bounds 和颜色 |

两份日志均显示左右帧的布局、可见对象、电量、DPI、fixture 时间戳和 owner 状态一致，PR 的滚动值为 `0/73 → 31/73`；main 连 offset=0 也失败。`LockBits` 与 `GetPixel` 一致，不是行 padding 假阳性。main 相对已通过 beta.8 的绘制源没有变化，不能把问题限定为 PR597 的改动。

此前 PR581 的 push run `43363234957` 也在完整像素断言失败，而 PR run `43363280309` 通过。当时的诊断增强保留了零容差和原断言，没有证明根因已修复。PR597 后来同 head 的 push run `43480898035` 通过，同样不能消除 PR 失败。此前 docs-only 审计仅本地运行一次 `--grid-overflow`，约 15.8 秒通过；它是未复现记录。上述历史均来自任务提供的证据，本任务未查询远端状态。

原始资料：`.ai/evidence/grid-pixel-fix-1114p/{main-ci-failure.txt,pr-windows-failed.log,audit.md,previous-review.md}`。原 CI 未保存两帧 PNG 或全部六点坐标，当前没有真实六像素差异图。

## 绘制链与可验证假设

真实调用链为 `CompactRenderer.PaintWireless → PaintGridContent → GridTile → InteractionLayout.Paint`。互动区域先在空 clip 下测量，恢复 Graphics 后计算区域，再平移并实际绘制。卡片名称最终经过 `DrawLabel → Graphics.DrawString`；normal 名称逻辑 rect 为 `[11,55,104,18]`，字体为 Segoe UI、11 pixel、Regular，颜色为 `Muted(dark)`。差异几何上落在该名称的抗锯齿区域；旧日志没有逐条绘制记录，因此这仍不是对每个差异像素的绘制者证明。

`VisualDesign.SnapTextRect/SnapFontSize` 会先取整。`DrawLabel` 在实际绘制时把 PixelOffset 设为 Default，并在 SystemDefault hint 时使用 AntiAliasGridFit。`InteractionLayout.LabelBounds` 独立创建测量 Graphics，缓存 key 包含字体、文字、尺寸、缩放、分数原点和 hint。测量 Graphics 的其他默认值并不自动继承绘制上下文；是否与 CI 差异有关尚未证实。

本次逐项对照的假设：

1. 互动区域测量或重新 Build 改变第二次绘制的平移、clip 或名称位置。
2. 测量缓存命中、失效或 measure/paint 次序改变字形栅格。
3. 两次实际绘制的 Graphics 参数、字体或 StringFormat 有未记录的变化。

本地对照没有支持其中任何一项为根因。名称绘制记录均为 transform `[1.5,0,0,1.5,0,0]`、无限 clip、96 DPI、AntiAliasGridFit、TextContrast=4、实际 PixelOffset=Default、SourceOver/Default；互动区域没有造成名称平移。不能据此断言 CI 也具有相同输入。

## 有界实验

实验窗口共 **73.99 秒**，小于六分钟；不操作设备、音频、系统字体设置或用户配置。原版严格入口编译并运行成功。另启动三个独立进程（PID `42100`、`68712`、`10588`），各保留首帧，分别进行 16 次零容差对照，共 48 次，全部无差异：

- 同一 layout 重绘 8 次；重新 Build 后 offset=0/31 各两次。
- 清空托管测量缓存后重画，以及明确的 measure-first 对照。
- 独立名称的 paint-first / measure-first 对照。

三个新进程的首帧与同进程重复分开记录；“清空托管缓存”不等于新的进程，也不等于清除 Windows 字体缓存。本任务没有清除系统字体缓存或更换字体。

本机系统 build 为 `26300`，加载的 GDI+ 为 `10.0.26100.9711`。源码、Segoe UI 字体文件及 GDI+ SHA256 见 `tmp/grid-pixel-1114p/environment-original.json`，各帧输入见 `fresh-{1,2,3}/*.txt`，首帧见各目录 `first.png`。原版运行输出为 `original.log`。初次临时探针因 C# 编译器不接受混用的 slash 路径而编译失败，保留于 `compile.log`；纠正路径后的编译为 `compile-probe.log`。这次编译失败不是像素 red。

## 诊断候选的最小改动

改动文件为 `tests/PanelDesignTests.cs`、`scripts/CompactRenderer.cs`、`scripts/InteractionLayout.cs`、`.github/workflows/ci.yml` 和本报告。

两个源文件只增加默认 null 的观察回调：在真实 `DrawString` 前读取现有 Font、颜色、StringFormat 和有效 Graphics；记录测量栅格及缓存命中/未命中后的原始结果。没有新增字体对象来预热，没有改变原绘制参数、缓存 key、布局、文字或电量逻辑。

`RenderGridObserved` 在内存记录左右帧各自的前后状态、绘制顺序、互动区域、矩阵、clip bounds 与原始 region data、可见 clip、DPI、PageUnit/Scale、RenderingOrigin、TextContrast、hint、PixelOffset、smoothing、compositing、interpolation、Font 和 StringFormat。回调在 finally 中恢复，捕获错误写入记录，不替代像素断言。

仅原严格比较失败时写出：完整 `left.png` / `right.png`、`difference.json`（比较区域内全部差异坐标和左右 ARGB、原始 LockBits 首差异、左右捕获、源码/EXE/字体/GDI+ hash、真实 Windows build）和 `difference-8x.png`。放大图从左至右为差异 bounds 的左图、右图、洋红差异图；只用于诊断，完整比较区域和原图保留。通过时不写这些文件。.NET Framework 无 manifest 时的 `Environment.OSVersion` 可能报告 6.2，故另以只读方式记录注册表 build，且把该 API 值标为 `reportedOS`。

本地文件位置为 `tmp/grid-pixel-diagnostics/<pid-guid>/`；CI 为 `$RUNNER_TEMP/myfinder-grid-pixel-diagnostics/<pid-guid>/`。CI 仅追加 `if: failure()` 的 `grid-pixel-failure-diagnostics` 上传，沿用固定 SHA `cf430e030ddbb5b0abf93d22962f4752f3646cd9`，保留 3 天；原步骤、平台、条件、权限和失败结果保持原样。

新回归 `GridRepaintIdentity` 保留 150% 浅色 native 名称的首帧，完整图像零容差比较同 layout 重绘、诊断回调关闭/开启、重新 Build 的 offset=0；offset=31 继续比较完整设备区和固定音量/footer 区。它在原矩阵之后加入原 Panel 入口，不提前预热原矩阵。独立 `--grid-repaint` 入口用于新进程首帧验证，第一帧同样参与断言。

## 验证结果与证据

```powershell
& ./.ai/orchestration/Verify-GridPixel1114p.ps1 -Root . -OutputDirectory ./tmp/grid-pixel-1114p/final-focused
& ./tmp/grid-pixel-1114p/final-focused/PanelDesignTests.exe --grid-repaint
& ./.ai/orchestration/Verify-GridPixel1114p.ps1 -Root . -OutputDirectory ./tmp/grid-pixel-1114p/full-panel -FullPanel
```

三者均 exit 0：聚焦严格矩阵约 17.82 秒，独立冷进程入口约 0.54 秒，全 Panel 约 41.41 秒、**139820 checks / 0 failed groups**。全 Panel 包含 ring/bar/grid、normal/large、dark/light 的实际渲染矩阵，未更新 golden。日志为 `final-focused.log`、`cold-entry.log`、`full-panel.log`，汇总为 `final-verification.json`。这些通过运行没有新建诊断文件。

全 Panel 后仅补充失败 JSON 的 Windows build 元数据；最终源码另用外部脚本聚焦运行于 `final-source/`，**exit 0，18.56 秒**，结果见 `final-source-verification.json` / `final-source.log`，最终交付源码 hash 见 `final-source-hashes.json`。共享绘制源未再改变，故没有重复完整 Panel。

临时 `Verify-Diagnostic.ps1` 验证取证链路：原图严格相等后，明确注入一个像素差异 `(22,97) FFDFE1E5 → FFDFE1E4`，调用原 `Run/Check` 机制，退出码 **1**；再用文件阻断取证目录创建，仍保留原断言失败并退出 **1**。PNG 解码为两张完整 390×780，放大图为 40×8；JSON 包含一个精确坐标、154/140 条左右事件、实际名称 DrawString，以及测量 miss/hit。它是合成负向链路验证，**不是 CI 六像素复现，也不是根因修复的 red/green**。探针最初的名称匹配规则误把 DPI 组标题也计入，收紧匹配后验证通过；原记录与图像均保留。

最新合成图与 JSON：`tmp/grid-pixel-diagnostics/8804-95ea63357e4c401ba270521635332a2a/`。负向日志与结果为 `tmp/grid-pixel-1114p/{synthetic-failure.log,synthetic-blocked-artifact.log,diagnostic-verification.json}`；此前探针产物没有删除。生成图片和 EXE 均位于忽略的 tmp，不进入交付 diff。

## 尚未验证与接续

原 CI 的真实六像素 red 尚未在本地复现；产品缺陷、fixture 输入差异、字体/GDI+ 状态或其他原因仍未判定。观察回调会增加执行时间，可能影响间歇问题出现的时机；本地通过不能当作修复。CI 上传步骤尚未在远端执行，真实设备和原生 UI 验收未做。

主管审查后，用一次准确提交的 CI 获取失败 artifact；若再次出现六像素差异，先比对真实左右图及每条测量/paint 输入，再选择产品或 fixture 的最小修复。若一次 CI 通过且没有取到失败图，同样不得宣称已查明根因。本任务未运行 Build.ps1、整套 Python、性能测试或发布流程，未提交、推送、合并或切换分支。
