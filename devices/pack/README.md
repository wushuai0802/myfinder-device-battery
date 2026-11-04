# 型号数据源

只编辑 `source.json` 的 `entries` 和 `disabled`；这是完整快照，保留仍需分发的所有条目。版本、时间、格式和 keyId 由发布工具生成。

新增条目从 `engine/providers/profiles.py` 的 `profiles()` 选择已注册的 provider、profile 和 profileRev。目前仅支持 Razer 新型号；不能添加 `native_atk`，也不能自行发明协议参数。示例（FE01 仅为示意，发布前必须核对真实诊断信息）：

```json
{
  "provider": "razer", "profile": "hyperspeed-mouse-1f", "profileRev": 1,
  "match": {"vendorId": "1532", "productId": "FE01", "usagePage": "FF00"},
  "name": "Example Mouse", "kind": "mouse", "level": "C", "source": "issue-23"
}
```

按诊断信息填写接口/usage 约束，不猜测 ID；不得与内置/保留 ID 或本包其他新增条目重复。默认验证等级 C，新增型号只读电量。

禁用项示例：`{"provider":"razer","vendorId":"1532","productId":"FE01","reason":"issue-23 读数错误"}`。禁用优先于新增和内置条目，可以禁用已有引擎 HID 品牌，不能禁用原生 ATK 或蓝牙读取器。

本地校验、发布和回滚见 [维护说明](../../docs/device-pack.md)。
