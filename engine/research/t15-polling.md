# T15 读取器轮询频率与耗电调查

基线 `b70b621921c11ac65172e3d63fa50d64b2153b9d`。覆盖注册的 61 个通用读取器、另装配的 `standard_hid` 和两条原生读取路径。计数来自该基线的静态代码。

## 调度与计数口径

表中 G 是通用读取的合格轮次。面板打开时最短间隔 8 秒，闭合时是配置的 60、120 或 300 秒，默认 60 秒。锁定时后台定时器至少 300 秒。轮次完成后才开始计算下次间隔。手动同步、拓扑和定向活动触发可提前安排。工作忙、没有合格设备、预算用完、子进程退避均可减少实际次数。[托盘调度](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/TrayApp.cs#L920) · [后台和面板定时器](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/TrayApp.cs#L961-L969) · [通用读取节流](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/TrayApp.cs#L1315-L1329)

分发对每个合格物理设备串行化。普通通用读器没有独立秒级定时器。单轮预算上限 36 秒。已确认的引擎失败会按 5、10、20 秒递增至最多 300 秒，未测到电量的成功空返回可走不同路径。显式强制刷新不走普通调度退避。蓝牙与标准 HID 有独立最短间隔。[失败退避](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/server.py#L1022-L1032) · [分发和预算](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/server.py#L1269-L1310)

以下数字以一条合格路径或物理候选为单位，描述符、身份或读写失败可提前退出。候选路径循环的总量取决于枚举结果。写包括 Output 和 Feature Set。Feature Get 是主动读取，单独计数。Input 等待是收包操作。B 指 `BatteryHidProvider` 的默认一次请求，最多读 32 次且通常共享 1000 ms 窗口，默认没有重发。下列差异以各行注明为准。[通用读器注册表](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/server.py#L23-L69) · [通用 HID 基类](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/battery_hid.py#L362-L386)

## 通用读取器逐项清单

| 读取器 | 最短调度 | 每轮查询与重试 | 休眠、静默或无回复 |
| --- | --- | --- | --- |
| `asus_azoth` | G | 普通 1 写，OMNI 1 次身份 + 1 电量，单请求最多 4 次 Input，重发 0 | 型号未确认时不查电量，缺有效回显无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/asus_azoth.py#L65) |
| `mchose_g3v2` | G | 1 次在线，确认后 1 次电量，重发 0，两段共 0.55 s | 未在线或静默则止于在线阶段。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/mchose_g3v2.py#L91) |
| `eweadn_x23` | G | 0 写，1 次 1200 ms Input 等待 | 静默或长度不符无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/eweadn_x23.py#L52) |
| `eweadn_x87` | G | 1 次身份/电量页查询，重发 0 | 无有效回复则不生成读数。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/eweadn_x87.py#L37) |
| `weikav_nut75` | G | B 的 1 写，重发 0 | 无合法输入无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/weikav_nut75.py#L19) |
| `veekos_shine60` | G | 0 写，1 次 Feature Get，重试 0 | 格式或电量失败则无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/veekos_shine60.py#L32) |
| `aula_mouse` | G | B 的 1 写，重发 0 | 没有合格集合或回复便无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/aula_mouse.py#L58) |
| `aula_keyboard` | G | F87 为 1 写，限静默重发 1 次。F99 MAX 为身份与状态 2 写，重发 0 | F87 错帧不重发，F99 MAX 身份失败不查下一段。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/aula_keyboard.py#L91) |
| `razer` | G | 一般电量 1 请求，成功才查充电 1 次。每请求只对连续外来回复重发最多 2 次 | 缓存句柄收到 04 当轮结束，下一次仍可查询。无缓存分支按路径/TID 探测。变体另见下文。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/razer.py#L407) |
| `audeze` | G | 单电量 1 写，必要时 14 初始化 + 6 状态写。失败后可以改走仅全序列。每序列尾额外读 2 次 | 未收到任何帧的序列前 3 次连续静默即终止，已知短查询无效则跳过它。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/audeze.py#L291) |
| `wlmouse` | G，另有静默门禁 | 每个候选厂商集合 1 次 Feature Set，15×最多两种长度 Get，随后零发送监听 | 通常走候选直到成功。历史超过 300 s 且连续静默后，稳定集合最多每第 5 轮扫描一次。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/wlmouse.py#L82) |
| `mchose` | G | 旧 Feature 最多 4 写/4 Get，G7 1 写，A5 1 写加最多 1 重发，A5 V2 最多 20 次尝试且共 2 s，V3 1 写 | 按已知型号走各自路径。失败保留有限历史或无新值，静默仍可能在后续合格轮复查。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/mchose.py#L412) |
| `mchose_headset` | G | 1 电量写，特定 PRO 接收器初次额外 1 Feature Set 和 1 Get，电量窗口最多 32 次 Input，无重发 | 固件条件失败时跳过，静默无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/mchose_headset.py#L152) |
| `atk_headset` | G | B 的 1 写，重发 0 | 无有效回复不生成新测量。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/atk_headset.py#L1) |
| `moondrop` | G | 1 写，最多 32 次 Input，重发 0 | 静默和无效帧退出，无有效百分比。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/moondrop.py#L111) |
| `sony_inzone` | G | 1 写、受限 Input 循环，重发 0 | 无有效帧不产生新读数。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/sony_inzone.py#L77) |
| `zhuaudio` | G | 1 在线查询，在线才 1 电量查询，两段共 32 次 Input 或 2 s，重发 0 | 在线/序号/帧失败停止，仍可在后续合格轮尝试。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/zhuaudio.py#L254) |
| `g3m` | G | B 的 1 写，重发 0 | 静默无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/g3m.py#L1) |
| `eweadn` | G | B 的 1 写，重发 0 | 静默无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/eweadn.py#L1) |
| `eweadn_s9pro` | G | 1 Feature Set + 1 Feature Get，重试 0 | 检查完整回复后才生成电量。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/eweadn_s9pro.py#L85) |
| `vgn_keyboard` | G | B 的 1 写，重发 0，最长 2500 ms | 识别休眠/无回复，不生成新读数。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/vgn_keyboard.py#L35) |
| `cherry` | G | B 的 1 写，重发 0 | 描述符或回复失败没有新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/cherry.py#L42) |
| `lingbao` | G | 0 写，1 Feature Get，重试 0 | 无效/无读数报文没有新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/lingbao.py#L70) |
| `vgn_beiying` | G | B 的 1 写，重发 0，最长 2500 ms | 异常回复或静默留诊断，没有新读数。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/vgn_beiying.py#L66) |
| `vgn_dragonfly3` | G | B 的 1 写，重发 0 | 静默无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/vgn_dragonfly3.py#L1) |
| `vgn_ry_ms` | G | 1 Feature Set + 1 Feature Get，重试 0 | 查询与回复不符或没有电量时不产出。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/vgn_ry_ms.py#L117) |
| `ninjutso_legacy` | G | 1 次配置编号、1 次读电量设置，每次 1 Set + 1 Get。合规的零编号额外重试 1 次 | 首次静默或错帧即退出，未验证配置编号时不发后段。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/ninjutso_legacy.py#L68) |
| `flydigi` | G | B 的 1 写，重发 0 | 身份/状态/回复未证时无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/flydigi.py#L1) |
| `mchose_qhw_keyboard` | G | 1 次 `_exchange`，重发 0 | 序号不符、静默或错误无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/mchose_qhw_keyboard.py#L38) |
| `mchose_glw_keyboard` | G | 1 活动配置查询，成功后分 2 段读配置，累计最多 3 写，重发 0 | 任一前段失败便停止。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/mchose_glw_keyboard.py#L50) |
| `mchose_k99v3` | G | B 的 1 写，重发 0 | 无有效实测格式回复就无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/mchose_k99v3.py#L33) |
| `mchose_v9pro2` | G | B 的 1 写，重发 0 | 按报告证明和回复门禁，识别无测量不呈新读数。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/mchose_v9pro2.py#L68) |
| `mchose_by_keyboard` | G，身份退避 | 初次 1 识别 + 1 电量，有身份时 1 电量，重发 0 | 识别静默有 60 s 起、最多 1800 s 的退避。已确认未知的 41E4 型号在重插前零发送。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/mchose_by_keyboard.py#L208) |
| `hyperx_alpha2` | G | 1 写，重发 0。先有限排空，随后最多 1.5 s 读回复 | 无匹配回复无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/hyperx_alpha2.py#L98) |
| `hyperx_cloud3` | G | 1 电量，成功后 1 充电，每次只在指定错误退化为 1 次 Feature Set，最多 6 次 Input | 电量失败跳过充电。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/hyperx_cloud3.py#L147) |
| `hyperx` | G | 1 电量，成功后 1 充电，每次 1 写和 1 读，重发 0 | 无电量跳过充电。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/hyperx.py#L90) |
| `keychron` | G | 同一 Feature 查询最多 3 次 Set，每次等待 Input，重发最多 2 次 | 无有效值后等待后续调度。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/keychron.py#L83) |
| `pulsar` | G | 电量 1 写，最多 4 次 Input，重发 0。符合条件且电量成功才读取 DPI，缓存校验 1 写，完整读取额外 3+档位数 | 无电量不进入 DPI 读，接收器存在时可显示非当前历史。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/pulsar.py#L709) |
| `jbl` | G，Input 监听跨轮 | 0 写，后台线程以约 250 ms 读超时收通知，轮询消费已到数据 | 无通知只依现有历史/状态，移除接口时释放监听。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/jbl.py#L99) |
| `dareu` | G | 1 Feature Set，有限 Input，重发 0。分离接口先有限排空 | 排空失败时零发送，静默/错误无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/dareu.py#L91) |
| `delux` | G | 1 次预读，之后 B 的 1 写，重发 0 | 门禁失败零发送，无合法回复则不刷新。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/delux.py#L125) |
| `rapoo` | G | 0 写，排空后最多 32 次 Input 或 1.5 s 等通知 | 静默可显示历史并标非当前，路径消失释放。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/rapoo.py#L218) |
| `fa6x_passive` | G | 0 写，排空与被动 Input 最多 1.5 s，普通等待部分最多 32 读 | 无通知只保留有效历史，路径消失释放。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/fa6x_passive.py#L164) |
| `logitech` | G | 每接收器最多 6 槽，单直连 1 槽。每槽先 1 ping，命中后按需身份、最多 4 个电量 Feature 各 1 探测 + 1 读取，旧协议最多 2 寄存器读取，特定型号另读 DPI | 睡眠/不可达槽下轮仍 ping，超时由 2.0 s 缩到 0.6 s。身份分页和额外 DPI 请求随设备变化。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/logitech.py#L549) |
| `steelseries` | G | 普通耳机 1 写。鼠标最多 3 写/每次最多 6 读。Classic 每候选 1～2 次型号专用查询，逐集合探测 | 按型号取无连接/无电量，缓存成功集合，退出当轮等待。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/steelseries.py#L266) |
| `barracuda` | G | 每集合最多 3 次会话开启，电量和充电各最多 4 写，成功开启后有结束命令。各查询最多 3 次重发 | 开启全部失败不发电量，缺电量时只结束会话。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/barracuda.py#L155) |
| `asus` | G | 最多 3 次写，每次最多 8 Input，重发最多 2 次 | 无法确认电量时无新读数。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/asus.py#L141) |
| `asus_omni` | G | 每已证接收器 1 写，最多 4 Input，重发 0 | 无有效回显/电量无新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/asus_omni.py#L111) |
| `asus_headset` | G | 1 电量，成功后 1 充电，每次 1 写，重发 0，共用截止时间 | 静默或无效回包不生成新值。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/asus_headset.py#L150) |
| `gwolves` | G | 每候选集合 1 Feature Set、最多 15 Get，Set 不重发 | 按候选尝试，静默时可保留 300 s 的非当前历史。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/gwolves.py#L208) |
| `lofree` | G | 在线与电量各 1 事务，每事务最多 3 写（起始、请求、结束），事务重试 0 | 已离线/无在线回复时不发电量事务。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/lofree.py#L88) |
| `astro` | G | 1 写，最多 8 Input，重发 0 | 只有有效电量才更新。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/astro.py#L102) |
| `corsair` | G | 旧读法 3 预备写 + 电量最多 3 写。Bragi 特定型号预备最多 3，电量按最多 2 路找合法样本，成功追加第 2 次采样，特定型号另 1 充电查询 | 旧读法无耳机应答不查电量。Bragi 各集合共享每接收器 7 s 预算。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/corsair.py#L333) |
| `lamzu` | G | 每合格路径 1 Feature Set、最多 10 Get，Set 不重发 | 有线优先和接收器回退，静默可存 300 s 历史。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/lamzu.py#L64) |
| `am_infinity` | G | 每合格集合 1 Feature Set + 1 Get，重试 0 | 保留短期历史，静默不会更新电量时间。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/am_infinity.py#L94) |
| `steelseries_elite` | G | 每 PID 优先缓存集合，写失败可换集合，最多 1 个接受的请求，随后有界 Input 等待 | 离线通知/无电量时无新读数。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/steelseries_elite.py#L162) |
| `logitech_centurion` | G | 常规 1 个桥接请求或 1 个旧请求。初见做两级发现，4 次根/计数查询加两级特性枚举数量。每个交换写 1 次，重发 0 | 静默或桥接超时无新读数，路径和成功发现可缓存。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/logitech_centurion.py#L139) |
| `hyperx_cloud3s` | G | 电量与成功后的充电各 1 个成功写。写失败可挑另一集合，每次仅写一次 | 无电量不查充电，读取窗口耗尽结束。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/hyperx_cloud3s.py#L147) |
| `nzxt` | G | 1 写，最多 10 Input，重发 0 | 接收器仍在且静默时可保留 300 s 历史。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/nzxt.py#L152) |
| `bluetooth` | G 且 60 s，连续 3 次无电量后 300 s | 0 私有 HID 写，请求 Windows 属性，由独立工作槽读系统缓存 | 读取失败/缺失使用限次保留，系统设备是否联网唤醒由系统决定。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/bluetooth.py#L330) |
| `xinput` | G | 0 私有 HID 写，最多 4 槽各 1 次 XInputGetState，已接入每槽再 1 次 GetBatteryInformation | 缺失槽位无读数。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/xinput.py#L62) |
| `standard_hid` | G 且至少 30 s | 0 Set/Output，按合格标准 Feature 字段，每字段 1 Get，首个有效结果即停止 | 只查设备声明的标准电量字段，缺字段的路径缓存拒绝。[源码](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/standardhid.py#L43) |

## 原生读取路径

| 读取器 | 调度 | 每轮查询与重试 | 休眠或无回复 |
| --- | --- | --- | --- |
| 原生 ATK A9 Air+ | 面板打开的 liveSync 每 2 s 通知一次，已有电量时约 30 s 再查电量，其余轮仍可读 DPI。后台走配置间隔 | 一个身份/初始请求，包含电量时再一次电量查询。DPI 读是一个系统块和 1～8 个档位。各单次读取按原路径等待，未发现当轮重发循环 | 收不到响应时保留适用的历史状态，已知设备缺席跳过。这是电量之外也可能重复查询的路径。[托盘入口](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/TrayApp.cs#L961-L969) · [原生查询](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/HidBattery.cs#L276-L295) |
| 原生 HyperX Cloud Alpha | 面板界面正常至少 6 s，失败后 10、20、40、60 s。后台跟随托盘且至少 60 s | 连接、电量、充电最多各 1 写，2400 ms 总预算。确认断线后结束余下查询 | 接收器缺席不发送，状态未确认时不把历史值当新读数。[调度](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/TrayApp.cs#L1409-L1435) · [原生读取](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/HeadsetBattery.cs#L229-L273) |

## 雷蛇毒蝰终极版和耗电

[#588](https://github.com/wushuai0802/myfinder-device-battery/issues/588) 报告 1.3.11-beta.4 的毒蝰终极版耗电异常。反馈中是 `1532:007B` 和 57% 当前读数。此表是当前基线的静态结论，现场当时的轮询和因果关系尚缺测量。[已知型号映射](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/razer_profiles.py#L48-L55)

这个 PID 用普通 Feature 路径。有可用的缓存集合时，清醒轮通常发一次电量查询，再发一次充电状态查询。每个逻辑查询发一次 Feature Set，然后读取回包。约 1 秒的窗口内，忙只继续读取，无关成功回复每累计四次才可能重发，Set 上限三次。缓存路径若返回 04（接收器在、设备关机或休眠均可能）立刻结束该次电量请求，跳过充电。这个状态没有让设备停止参加后续合格轮，所以下一轮仍可发一次电量查询。鼠标历史显示可以保留 300 s，这个窗口也不是发送退避。冷发现会遍历合格接口及最多五种 TID，拒绝通信的接口冷却 300 s，外来回复达到阈值才进入每轮最多两次的重发现。[发送与读回](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/razer.py#L157-L242) · [历史读数](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/razer.py#L356-L395) · [接口选择](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/razer.py#L407-L529)

雷蛇同模块变体包含 Viper V4 Pro 每候选 1 Set、最多 3 Get，BlackShark 2020 每候选 1 Set 和约 0.5 秒 Get 窗口，及 PA 路径最多两次打开句柄、每次最多 4 个唤醒尝试，后接电量与充电各至多 3 次查询。独立的 `barracuda` 见上表。[V4](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/razer_v4.py#L61-L74) · [2020](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/blackshark2020.py#L72-L116) · [PA](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/providers/blackshark.py#L130-L209)

代码已经确认数条路径会在没有新读数或鼠标空闲时继续发查询，例如雷蛇已缓存路径和罗技 ping。某些雷蛇耳机分支还有显式接收器唤醒尝试。USB 请求有无唤醒实际鼠标无线链路，以及是否会改变电量消耗，目前待实测。耗电归因以对照实验为准。

## 退避方案草案

先只研究毒蝰终极版普通 Feature 路径。对同一物理接收器连续收到明确的 04 后，按 60、120、300 秒逐步延后自动查询。成功得到同一目标的可验证回复时清除退避。重插、已验证的同设备活动及用户手动同步可各触发一次定向检查。独立保留忙、外来回复和传输错误的判别，历史读数继续使用原测量时间。先用 fake 时钟和报告计数测试重复休眠、手动同步、恢复、同型号两只互不影响，再根据用户对照决定是否实现。

## #588 回访草稿

> 请在相近的使用条件下，对毒蝰终极版做两段相同时长的对照，一段让 MyFinder 正常在托盘运行，另一段从托盘菜单完全退出。两段尽量使用同一起始电量、同样的鼠标用量和灯光设置，期间保持充电和 Synapse 状态一致。请贴出两段的起止电量、时长和 MyFinder 版本，方便核对。
