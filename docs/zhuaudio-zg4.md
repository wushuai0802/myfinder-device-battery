# 竹林鸟 ZhuAudio ZG4 只读电量候选

证据等级 C：本实现结合官方 Z HUB 的静态协议和 #459 用户提供的原始 HID 描述符。尚无目标硬件的真实查询/回复验收；合成帧、fake 设备和描述符解析通过均不代表实机通过。官网截图的 100% 也不证明每轮获得了新采样。

## 身份和集合

仅接受 `3654:5055`、产品全名 `ZhuAudio ZG4`、MI04 / `FF05:0001`。同一个非空、已证明的物理 parent 内，该精确身份的 FF05 必须只有一个，再核对产品、接口和报告；即使第二个 FF05 产品名错误或报告证明缺失，也拒绝整组。路径必须非空，`collection:` 未证明 parent 和蓝牙路径均拒绝。不同 parent 独立显示和刷新，不按名称或相近百分比合并卡片。

| 方向 | 唯一 report ID | Windows/hidapi 总长度 | data 长度 |
| --- | --- | --- | --- |
| Input | `CE`，映射键 `206` | 64 | 63 |
| Output | `CD`，映射键 `205` | 64 | 63 |

运行时须由 caps 分别证明这两个单一字节数组及其最大长度，映射恰为 `{"206":64}` / `{"205":64}`。仅在该精确 VID/PID/FF05 内复用现有固定 Usage Data,Array 证明，不放宽共享解析器。只有最大长度、未知 proof、其他 ID、对调 ID、分裂或部分 caps 均不授权协议 I/O。发现只用 access=0 的元数据句柄，不读输入/Feature、不发输出，也不新增 raw descriptor 回退；API 失败、partial count 或不符报告不缓存，可在后续发现恢复。

#459 的另外两个 FF02 描述符分别为无编号 64-byte data 和 C0/63-byte data，均不进入本协议。不尝试、不回退、不复制官网的多接口/多报告主动探测。旧 FF02/CD 方案已被新 FF05 描述符取代。水月雨 `80/27`、ATK 及非官方 DARKRIM 命令不是本候选的证据。

## 仅有的两个请求

先读取零参数 dongle `02` 状态，只有 CRC 有效的 `02` 回复且 payload 首字节为 `01`，才读取设备 `FE` 的单一属性 `07`。其他状态、无状态或 CRC 不符均不继续电量查询。

```text
CD 07 AA 5A CC 80 51 01 02 + 55 个 00                 （64 字节）
CD EE 80 00 06 00 FF 01 02 FE 07 AA + 52 个 00        （64 字节，示例 seq=01）
```

第一条按官方 `cxF/bXq` 编码，CRC16 初值 0、右移多项式 A001；输入 `[01,02]` 的 CRC 是 `5180`，线上低字节在前。dongle 回复的 `AA CC U L CRClo CRChi cmd payload` 由 `bP2/cvR` 校验，`U` 被官方代码跳过，含义未知，不能当作 seq 或成功位。实现只接受源码明确的两种 CRC 长度，并要求完整帧、零补齐以及存在时的总长前缀一致；不推测其他校验格式。

电量内帧是 `FF seq LEN cmd payload AA`，LEN 包含 cmd，整帧严格等于 LEN+4。回复必须匹配当前 seq；cmd 仅 `07` 或 `FE` 且属性号 `07`，value 至少三字节。value[0] 的低 7 位为 1–100 候选百分比，bit7 为充电；剩余两字节语义未知，不解释成电压。短值、0、101–127、请求回显、旧 seq、无关 opcode 及单独 ACK 均不产生新读数，不默认 0、不把充电强制为 100。

输入只能从完整 CE/64 报告开始，支持直接 FF 或完整 EE/EA 分片链。分片索引必须连续，首片总长、后续零总长字段、保留字节、结束位、实际长度和补零须一致；总长最多 1024。丢片、重复、乱序、超时或矛盾长度不能借新首片重新拼接。两阶段合计最长 2 秒、最多 32 次 read，不为每阶段重置预算。设备 ACK 为接收信息，未发现需要发送额外 ACK，不新增 host ACK。

短写和 I/O 异常结束本次 query，不在同一次调用内重试或改开其他路径，始终尝试 finally 关闭。后续 poll 可按下述序号门禁重试；诊断中的 `no retry` 仅指本次调用。open/write 的底层阻塞仍由原有默认 7 秒隔离查询、36 秒整轮预算和两个 reader 槽位约束；本改动未调整全局调度或蓝牙时序。

## 序号、历史和未验证边界

每个物理 parent 在当前 provider 内独立保存序号游标和未决/退休集合；“本次调用 pending”单独保存，并由 finally 清除，poll 重入锁保持不变。状态 `02` 没有 FF 事务，超时、短写、异常、离线或耗尽读取预算均不分配/退休 FE 序号，也不永久隔离接收器；下一次 poll 可重新查询状态。

只有在线状态有效且仍有读取预算、准备尝试发送 `FE 07` 时，才从 1..255、0 的 8-bit 循环中选择未退休序号，并在 write 前登记为未决。短写、发送或读取异常、超时、未收到完整有效电量、仅 ACK、分片不完整或发送后断开均保留该序号为退休；下一次 poll 可以选择其他可用 seq，迟到的退休 seq 电量或 ACK 不能完成当前 query，也不能解除退休。同一实例内不因静默、缺席枚举、drain 或重开而清除此集合，不猜测超时取消。

只有当前 query 收到当前 seq 的完整有效电量才能释放该序号，后续允许按官方 8-bit 规律回绕，255→0→1 后继续正常读取。只有全部 256 个序号均未决/退休，才在 open 前拒绝，并给出 `ZG4 all 256 FE sequences unresolved`。成功读取次数本身没有 256 次上限。

这些约束能拒绝常规旧 seq 以及本实例的未决/退休 seq，不能证明设备采样时间，也不能证明无限新鲜度：成功序号回绕后，同一 seq 的旧重复帧仍无法仅凭 8-bit 序号区别。worker 重建、实例/缓存淘汰、pack 快照变化和进程冷启可能丢失本地序号/退休状态；不同 worker、重连、官网共存的状态与固件 seq 回显均未由目标硬件验证。不引入随机 seq 掩盖关联，不宣称绝对取消或全生命周期回复归属已解决，也不以这些未知为由在 256 次正常读取后永久停用。状态 `02` 本身没有已证明的事务序号。

无有效电量时沿用既有历史保留时效：百分比可保留，确认、测量、成功查询时间不刷新，充电恢复未知。只给出固定、不含路径或原始异常、48 字符内诊断；不增加真实 errors 或失败退避。manager 和 child 都遵守该 provider 的 device-pack 禁用。没有新增可下载私有协议 profile 或 DPI 控制。

待实机确认：精确元数据能否通过当前 caps 门禁、真实 `02` 和 `07` / `FE 07` 报文、单属性读取与 seq 回显、至少一个非 100 值、充/放电、耳机断开、休眠唤醒、冷启重连、多个接收器及官网共存。空闲 8 秒没有帧不表示永不主动上报；官方 UI 可能保留旧值。上述未知不由本轮纯测试替代。

## 合成回归与旧契约修正

先仅新增以下三个独立 fake 回归，再修改 provider。在基线 `e1bb26a478c35d685e26251f189da62a6747b8a7` 上执行：

```powershell
.\engine\runtime\python.exe -I -B engine/tests/test_zhuaudio.py ZhuAudioProviderTests.test_status_failures_allow_next_poll_without_consuming_a_sequence ZhuAudioProviderTests.test_300_successes_wrap_255_zero_one_without_exhaustion ZhuAudioProviderTests.test_retired_sequence_late_battery_and_ack_cannot_complete_next_poll -v
```

三个测试均红，共 11 个失败断言：状态阶段七种场景的下一轮无法正常读取；连续成功在第 257 轮停止；FE seq1 超时后，直接 FF / EE / EA 三种旧回复场景均无法用 seq2 恢复。修复后相同三个测试通过。另覆盖 300 次状态超时不占用 FE 序号、退休序号跨成功回绕仍被跳过、256 个真正未决序号才耗尽、FE 短写/异常/ACK-only/不完整回复、I/O 与 close 后释放 pending、错误分片在同一 query 不重启、两 parent 独立，以及 child 保持实例跨多条 query 和 300 次成功回绕。

原 `test_timeout_keeps_pending_even_if_a_later_queue_contains_old_matching_reply` 和 child 的 `test_child_keeps_timeout_pending_and_does_not_consume_later_old_reply` 把一次超时后的永久拒绝当成正确行为，现替换为允许不同 seq 重试且拒绝退休 seq 的断言。原 `test_8bit_sequence_wrap_is_unique_then_further_io_is_refused` 的“256 次成功后停用”替换为 300 次成功正常回绕，以及仅 256 个未决 FE 才停用。短写、I/O 异常和两 parent 测试中相同的永久拒绝断言同步改为下一 poll 恢复；本次 query 不重试、finally 关闭、隐私诊断和旧 seq 不生成读数的断言保留。其余身份、报告证明、CRC、帧/分片、范围和预算门禁未放宽。

## 证据归属

官方参考：<https://drive.zhuaudio.cn/web/> 的 `main.dart.js`，6,076,412 字节，SHA256 `04bee7a730669e758dd3e01df12e1823c18f262b2be4df43879595b6413bab3d`。只作为惰性文本阅读，未 import/eval 或运行。关键原始位置：`cxF/cxE/cxD` L49249–49285（字节偏移 1023186 起），`bP2/cvR/cvQ` L15340–15400（357321 起），`aaX.bmb/bnv/a7I` L95873–96008（2563762 起），`afq.bGW` L96324–96374（2574977 起），`pw` L95105–95107（2546173 起），`lU.JQ/b29/b3O` L74758–74908，`c_m` L15630–15633（363967 起）。这些锚点只对上述哈希有效。

用户描述符：[贡献者 #459 的补充](https://github.com/wushuai0802/myfinder-device-battery/issues/459#issuecomment-6745518125)。原始附件 SHA256 `e93ae8de2651690a71f4091a0bfbb70d38d5c0eccf9d1be75f5ecd068bc5cec2`；本项目仅保留必要的脱敏协议事实，不引用用户序列号、猜测命令或附件探针脚本。新 provider 为本项目独立实现，未复制、分发厂商 JS 或声称其许可证已核验。
