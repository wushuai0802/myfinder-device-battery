# Lingbao M1 Pro 电量候选边界

本项目依据本仓库 [#171](https://github.com/wushuai0802/myfinder-device-battery/issues/171) 的用户贡献独立实现，仅限 `088D:052F` 2.4G 接收器；相关请求为本仓库 [#95](https://github.com/wushuai0802/myfinder-device-battery/issues/95)。证据保持 C 级：用户提供了原始帧和场景记录，维护者尚未做实机验收。其他共享品牌、ID 或名称不由此获得支持；未复制厂商程序实现。

产品全名必须为 `Wireless-Receiver`、MI_01、`FF00:0001`，路径和真实 USB physical parent 必须非空，不能用 `collection:` 回退。先统计同一 parent 内所有该身份的厂商集合，再检查各字段；重复集合即拒绝，不能过滤掉另一条不符的证明来凑出唯一集合。多个独立 parent 保留独立卡片。

原始 HID Report Descriptor 尚未取得。Windows Feature 最大长度 33 只能证明集合容量，不能证明 report ID `0F` 的字节数。运行时只对上述精确身份/集合以 access 0 读取元数据，使用现有完整 caps 校验，允许该目标的固定 Usage 字节数组；要求 Feature 最大值恰为 33、逐 ID 映射恰为 `{"15":33}`（十进制字符串 ID）。未知、部分返回、API 失败及不符的报告证明不缓存，下次发现可恢复。没有证明就不开查询句柄，也不新增 raw descriptor 回退。

合法查询句柄每轮仅调用一次 `get_feature_report(0x0F, 33)`，随后在 finally 中关闭。没有任何 SET_FEATURE、Output、初始化、配置、配对或固件操作，也没有 DPI 功能。异常、短回复和关闭失败只提供固定无路径诊断，不在当轮重试，不进入错误退避；关闭失败仍可保留此前已经有效解析的读数。

以下两条是 #171 的完整原始 33 字节回复；第二条仅零基 offset 4 不同，两条 offset18/19 都是 `41/00`，即 65%、flag0。它们没有证明其他电量或充电场景的完整帧。

```text
0F 00 30 02 EC 01 10 4D 35 33 36 01 06 4C 58 11 11 01 41 00 FF 00 FF D0 00 0C 31 BE 00 5B 81 2C C8
0F 00 30 02 EB 01 10 4D 35 33 36 01 06 4C 58 11 11 01 41 00 FF 00 FF D0 00 0C 31 BE 00 5B 81 2C C8
```

用户另以表格报告无线电量 `65 → 68 → 66`，插线 flag1 时数值恒为 100。68%、66% 和充电 100/flag1 的自动化测试帧是基于表格的合成变体，不能称为完整抓包。原 Device Descriptor 的零基 offset14/15/16 为 `01/02/00`，与无序列号一致；逐字段说明曾混写偏移，不能据此改判原字节。配置值 `A0` 的 bit5 为 1，用户对远程唤醒的文字解读也有误；这些文案问题不改变 Feature 原始帧。

解析严格要求 33 个整数 byte，拒绝布尔、非整数和越界值；ID 必须为 `0F`，offset7–10 为 `M536`、13–14 为 `LX`。不增加未经证实的头字段常量检查。第一版仅接收 flag（byte19）为 0 且百分比（byte18）为 1–100 的读数；flag1 一律按 `charging placeholder; no new reading` 舍弃，绝不显示为新鲜 100%。其他 flag 和 0% 均不产生新读数。现有历史保留可继续展示旧值，但确认、测量和查询时间保持原值，首次就遇到占位时没有卡片。

充电状态与鼠标无线连接状态始终未知：`charging_known=False`、`connection_known=False`，不猜测 byte21。现有 schema 中有效回复的 `fresh`、`measuredAtUtc`、`confirmedAtUtc` 表示主机本轮取得并接受回复的时间，不能作为设备采样时钟或鼠标 RF 在线证明。重复有效回复是否来自接收器缓存、睡眠/关机行为、冷启动/重连读取、官方软件共存、固件差异及真实充电/低电量仍未确认。

离线验证均使用 fake HID/DLL，覆盖逐 ID 证明、失败恢复、零写入、单次读取、占位舍弃、历史时间、禁用、独立 parent 和固定诊断。禁用 device-pack 条目在 manager、child 和离线索引中生效；不新增可下载私有协议 profile。实机验收仍需现有原始 Feature caps、完整充电帧，以及正常冷启与睡眠/RF 新鲜度证据；本次没有访问真实 HID。
