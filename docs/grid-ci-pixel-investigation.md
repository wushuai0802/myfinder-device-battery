# Grid CI 像素调查

本次交付是**诊断候选，尚未修复**。根因仍未知，没有可宣称的修复前失败／修复后通过路径。工作树基线为 `50e4897a4782e7e6b19faba2dac8ad375bb165d8`。本地未复现不能解释掉 CI 失败；候选需要主管用一次准确提交的 CI 收集原始图像和绘制输入，再继续因果定位。

当前基线没有跟踪本报告；以下从任务提供的 `audit.md`、`previous-review.md` 和两份失败日志保留此前结论。

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
