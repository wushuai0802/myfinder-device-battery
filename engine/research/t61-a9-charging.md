# [T61] ATK A9 Air+ 充电双卡调查

**⛔ 检查点 1，等待 Claude 审查及维护者选择方案。** 源码基线为 `2271cd76b922fa665b5656f0ad6ab307374b6db1`。现场运行版本为 `1.3.11-beta.20`。从该版本到源码基线，相关读取和面板代码中只有 `TrayApp.cs` 的反馈窗口改动。

### 现场记录

北京时间 2026 年 11 月 21 日 01:42 只读查看了 `status.json`、`wireless-cache.json`、`readings-cache.json` 和 `diagnostics.log`。截图记录的是有线 60% 对原生历史 35%。文件采样稍晚，同一组合显示有线 95% 对原生历史 35%。

| 路径 | 样本 |
|---|---|
| 通用路径 | `373B:1293`，产品串 `ATK A9 Air`，读取器 `pulsar`，公开 Physical ID `device-18cf7bcddb7790c56f7d8854`。`01:42:21.729` 取得设备回复，电量 95%，充电中。`pulsar` 的这条分支用 HID 产品串作为名称。 |
| 原生路径 | 任务卡的 `ATK A9 Air+` 原生路径使用 `373B:1278`。`readings-cache.json` 中的 35% 最后确认在 `01:03:15.022`。`01:42:51.446` 的状态没有当前电量，原生读取错误为 `HID read timed out; mouse state is unknown`。 |

原生超时发生在选中并打开接收器后等待回复。它说明本次缺少可用的鼠标回复。持久化通用缓存的 `nativeMousePresent` 值为 `null`，无法从该文件确定实时接收器枚举状态。诊断日志没有补足这两个路径的配对标识。通用记录的连接标签是 `USB / 2.4G`，插线场景和充电状态由现场描述及现有回复共同支持。[原生读取路径](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/scripts/HidBattery.cs#L276) · [超时位置](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/scripts/HidBattery.cs#L172) · [通用产品串来源](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/engine/providers/pulsar.py#L829)

### (a) 双卡的原因

- 通用引擎从已发现接口生成 `WirelessDevices`，其快照在合并历史后执行 `suppress_old_connections()`。它要求同一读取器、相同设备类型、不同的非空 `physicalId`、电量差在 5 个百分点内，且至少一个读数为历史。接收器 `373B:1278` 负责原生路径，原生卡在 C# 生成，所以这张卡根本没有作为引擎抑制候选进入比较。若把两张截图中的卡放到该比较，读取器不同和 25 个百分点的差距也会分别拒绝匹配。[引擎原生排除](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/engine/server.py#L105) · [引擎抑制条件](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/engine/server.py#L294) · [快照调用](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/engine/server.py#L819)
- C# 在 `WirelessLayout.Build` 先加入 `native:mouse`，再逐一加入引擎卡。列表只按卡片 ID 去重，并应用隐藏设置。`SuppressedDevice` 仅记录引擎已抑制项的名字和类型，供诊断或活动恢复判断使用。原生与通用卡在展示层没有经过跨路径配对。[面板构建](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/scripts/WirelessLayout.cs#L31) · [SuppressedDevice](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/scripts/GenericBattery.cs#L207) · [活动恢复用法](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/scripts/TrayApp.cs#L1230)

### (b) 如何可靠确认是同一只鼠标

现场只能把 `373B:1293` 的有线接口与 `373B:1278` 的原生接收器列为这一台鼠标的候选组合。VID/PID 和产品串说明接口型号，无法单独标识某一只鼠标。有线的 `physicalId` 是 USB 父设备证据，接收器具有自己的 USB 父设备。当前状态文件没有覆盖两条连接的共同机身序列号、配对单元 ID 或等效标识。

落地时应使用已验证的共同机身标识，或现有证据能验证的接收器配对单元与有线单元映射。先在同一只鼠标的受控换线过程比对两种记录，可确认本次样本的设备归属。运行时仍须能复核这一映射，多只同型号或无法绑定时保持各自的显示记录。引擎的 `unitId` 带物理父设备作用域，不能直接跨两个 USB 父设备相等比较。[公开物理及单元 ID 生成](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/engine/server.py#L512) · [物理设备分组说明](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/engine/topology.py#L1)

### 拔线后的现有行为

下一次健康的完整刷新会更新通用列表。有线消失时，通用引擎可在最多 300 秒的历史窗口内保留最近读数。刷新失败时 C# 也可能继续显示旧卡并标成非当前，双卡不保证在拔线瞬间消失。接收器存在时原生卡可保留或重现。它要收到有效的原生新回复后才由历史恢复为当前。代码路径已核对，拔线时序尚缺实机记录。[历史保留](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/engine/server.py#L793) · [历史状态](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/engine/server.py#L541) · [C# 快照及接收器状态](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/scripts/TrayApp.cs#L1333) · [原生读数更新](https://github.com/wushuai0802/myfinder-device-battery/blob/2271cd76b922fa665b5656f0ad6ab307374b6db1/scripts/TrayApp.cs#L1598)

### (c) 可选方案

| 方案 | 插线后的显示 | 拔线后的恢复 | 取舍 |
|---|---|---|---|
| **A 暂时隐藏原生卡** | 在可靠绑定同一只鼠标、有线读数为当前且原生读数过期时，展示通用有线卡。原生 DPI 控件随原生卡隐藏。 | 有线路径失去当前状态时解除隐藏，恢复原生卡。已绑定且确认断开的有线历史项留在诊断中，面板回到原生卡。读数是否为当前由原生回复决定。 | 展示层改动较集中，充电期间少了原生名称和 DPI 入口。 |
| **B 合为一张卡** | 在同样的身份门禁下沿用原生名称，电量和充电状态来自当前有线读数。DPI 沿用原生的目标及有效性检查。原生控制失效时以历史档位展示。 | 清除有线电量覆盖，把同一卡片恢复为原生读数。后续刷新仍由原生路径决定。 | 名称和卡片位置稳定，需同时验证来源切换、DPI 禁用和缓存清理。 |

本阶段已交付双卡成因与方案对照。进入第 2 步需 Claude 审查、可靠的身份绑定依据和维护者选定方案。
