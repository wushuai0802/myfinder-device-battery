# T13-1 休眠中卡住与提示条调查

基线 `b70b621921c11ac65172e3d63fa50d64b2153b9d`。只读核对源码和公开反馈，交付调查后停在 ⛔。

## “休眠中”怎样出现和清除

底部的“1 台设备休眠中”来自 `TrayApp.ApplyWireless` 的 `GenericIdleSummary`。每个无组件错误的快照会重新计算两个计数。`health.state == "unavailable"` 且 `reason == "no_measurement"` 计为 idle。错误、超时、其余 unavailable 或 backoff 计为失败。还有 idle 且无失败才显示该提示。后续快照若有有效读数、候选消失或健康状态变化，旧的 idle 会被重新计算。组件异常时立即清除 idle 汇总，保留历史读数的非当前标记。后台、用户同步和活动刷新各有调度门禁。[TrayApp.cs](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/TrayApp.cs#L1314-L1356)

引擎 `_accept` 会在已匹配到实体候选、该轮没有可接受读数时写入 `unavailable / no_measurement`。被动读取器只拿到历史值时也可以落在这一分支。这一原因描述测量结果，涵盖接收器静默、只保存历史等情形。它没有确认物理休眠。单卡“休眠”由 `ConnectionState` 或 `MeasurementState` 的显式 `sleeping` 触发，和底部提示是两条链。公开反馈健康行只带 provider、state 和 errorCode，缺少 reason、请求代际与相邻快照。[引擎健康状态](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/server.py#L857-L892) · [单卡显示](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/CompactRenderer.cs#L363) · [反馈字段](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/GenericBattery.cs#L29-L42)

## 两条反馈的归类

| Issue | 已验证的诊断 | 可得结论 |
| --- | --- | --- |
| [#935 竹林鸟 ZG3 朱雀](https://github.com/wushuai0802/myfinder-device-battery/issues/935) | 用户报告 beta.19 下 2.4G 耳机先正常，随后一直显示休眠，重连、重装和重启后电量未更新。所附诊断只列出当前 `pulsar` 鼠标读数和 MINI 60 HE 键盘接收器的线索，健康行是 `eweadn_x87`、`aula_keyboard` 的 unavailable。没有 ZG3 对应的耳机卡片或读取器健康记录。 | 用户看到状态不推进。无法将该状态归到 ZG3 的读取器，缺少证明读取器持续无读数或界面丢失最新快照的记录。 |
| [#231 迈从 G87](https://github.com/wushuai0802/myfinder-device-battery/issues/231) | 1.3.9-beta.2 的诊断列出 `41E4:2001`、`FF02:0002` 与 `FF04:0002`，`mchose_by_keyboard \| unavailable \|`，没有错误原因。当前 96% 属于 M7 Pro。随后维护者提供 beta.3 的拒绝诊断和 1.3.10-beta.1 的协议改进。该帖没有新版唤醒前后的 G87 诊断。 | 这次能确认在读取器侧未得到可用读数。快照只有一次，无法确认是否持续无读数，也无法区分接口门禁、静默或状态更新问题。 |

两例均缺能对应设备并比较唤醒前后的连续快照。#935 先补目标耳机的设备 ID、卡片与读取健康。#231 先补最新版本的 G87 同一接收器前后两次状态，包括当前读数、reason 和已安全公开的错误原因。已有证据优先排查当前无读数的成因。

## “部分读取暂不可用（N 项）”为什么点了没反应

[#587](https://github.com/wushuai0802/myfinder-device-battery/issues/587) 的补充报告说明点击后无变化，附带一次 `bluetooth | error | read_failed` 和 BS-T17 100% 历史读数。主页页脚把健康汇总交给 `DrawSync`，它在 `FooterTextRect` 中绘制文本，只给旁边刷新图标绘制动作 0。主页面的条形、环形和网格控件树没有给这段文字注册命中或关闭动作，点文字没有任何动作。刷新图标会触发全部设备同步。更多菜单的诊断动作 202 才会打开含读取器健康的诊断页。失败仍由新快照计数，渲染文本本身无法关闭。[页脚绘制](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/CompactRenderer.cs#L603-L632) · [条形和环形控件](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/PopupPages.cs#L405-L408) · [网格控件](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/PopupPages.cs#L461) · [动作分发](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/TrayApp.cs#L299-L308)

## 拟修复和首批回归测试

| 方案 | 预计变更 | 先写的测试 |
| --- | --- | --- |
| A 查看原因 | `PopupPages.cs`、`CompactRenderer.cs`、`TrayApp.cs` 合计约 20–35 行。把读取器故障文本变成有名称和命中范围的诊断入口，接已有诊断页。保留刷新按钮与失败诊断。 | 在 `tests/CardsFeedbackTests.cs` 添加测试，以模拟失败快照检查三布局中点击文字或键盘激活能打开诊断。确认刷新仍走动作 0。现有实现缺该控件，入口断言应先失败。 |
| B 查看原因并允许收起当次提示 | 在 A 的三个文件上另加约 20–35 行。关闭只影响当前打开面板的展示。不同故障或恢复后再次失败时重新显示，诊断持续保留。 | 在 `tests/CardsFeedbackTests.cs` 新增模拟生命周期测试。关闭后同一故障保持收起，换故障重新显示，诊断计数照常更新。现有实现缺关闭动作，第一条断言应先失败。 |
| 状态提示校正 | 优先核对能复现的快照。若只确认 `no_measurement` 被翻成物理休眠，预计改 `TrayApp.cs`、`WirelessLayout.cs` 约 5–12 行，并让面板明确显示暂未取得读数。若已收到有效快照仍显示旧状态，从 `ApplyWirelessRead` 和 UI 应用路径定位，收到复现才估算。 | 先新增一个只含候选但无测量的 fake，用面板可见提示断言语义正确。唤醒后同代完成快照有当前读数时，应清 idle 且恢复卡片。第二条已有近似绿证据，复现失败再补精确红测。现有用例固定了旧文案，若选择改文案，须先确认该组既有断言的处理范围。 |

现有测试证明了错误优先、idle 清除和页脚配色。新测试针对交互和特定状态转换。方案只涉及前端状态呈现与 fake 回归，读取器的发送逻辑保持原边界。[现有健康相关测试](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/tests/CardsFeedbackTests.cs#L38-L71)

**⛔ 检查点**。待审查后选择 A 或 B，并决定是否调整休眠提示文案。收到匹配的复现证据后，才决定是否需要修改状态推进逻辑。
