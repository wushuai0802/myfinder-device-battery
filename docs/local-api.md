# 本机只读开放接口（JSON-RPC 2.0 / SSE）

MyFinder Device Battery 宿主进程提供可选的**本机只读开放接口**，供 MyDockFinder、Rainmeter、OBS、Stream Deck 或自定义桌面工具读取实时外设电量与连接状态。

## 启用与连接信息

1. 接口**默认关闭**。在「MyFinder 控制中心 → 设置」中开启 **本机只读开放接口（JSON-RPC 2.0）**。
2. 启动后仅绑定 `127.0.0.1`（不对外网或局域网开放），随机生成 256-bit Bearer Token，并以**仅当前 Windows 用户可读写的私有 ACL** 写入会话文件：
   `%LocalAppData%\MyFinderDeviceBattery\api-session.json`
3. 关闭接口或退出程序时自动删除 `api-session.json` 并吊销旧令牌。

`api-session.json` 示例：

```json
{
  "schema": 1,
  "host": "127.0.0.1",
  "port": 51234,
  "rpcUrl": "http://127.0.0.1:51234/rpc",
  "eventsUrl": "http://127.0.0.1:51234/events",
  "token": "<64-hex-chars>",
  "pid": 12345,
  "startedAtUtc": "2026-11-25T01:55:00.0000000Z"
}
```

## 鉴权与安全限制

- 每次请求必须携带请求头：`Authorization: Bearer <token>`（常量时间比对）。
- 禁止通过 URL 查询参数传递令牌（返回 HTTP `400`）。
- 拒绝所有带 `Origin` 头的跨站浏览器请求（返回 HTTP `403`）。
- 单次请求体上限 `16 KiB`，最大并发连接 `8`，最大并发 SSE 订阅 `4`。
- **严格只读**：不暴露任何 HID 写入、DPI 切换、路径、设备序列号或内部异常堆栈。

## JSON-RPC 2.0 方法（`POST /rpc`）

| 方法 | 说明 |
|---|---|
| `ping` | 健康检查，返回 `{"ok": true, "schema": 1, "appVersion": "..."}` |
| `getSnapshot` | 返回脱敏完整设备快照 `{schema, generatedAtUtc, appVersion, deviceCount, devices}` |
| `listDevices` | 返回 `{devices: [...]}` |

其他未知方法一律返回标准 JSON-RPC 2.0 错误 `-32601 Method not found`。

## 实时事件流（`GET /events`）

携带 `Authorization: Bearer <token>` 发起 `GET /events`，服务端返回 `text/event-stream`：
- 建立连接立即推送首帧 `event: snapshot`；
- 当设备电量或状态变更时推送最新快照；
- 空闲期间每 15 秒发送 `: keepalive` 心跳。
