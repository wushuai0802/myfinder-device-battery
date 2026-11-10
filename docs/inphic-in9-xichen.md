# 英菲克 IN9 / XICHEN protocol 5 电量协议静态分析

结论：现有网页静态证据能说明 XICHEN 的身份查询与电量解析，但**不能据此启用 `248A:5B2F` 的精确读取器**。该 PID 没有 IN9 精确条目，任务提供的集合又不满足网页的通讯集合过滤；尚缺目标接收器自己的描述符、逐报告 ID 长度和官方驱动通讯证据。不能将 `248A:*`、`FF01:0010` 或网页的 33 字节发送数组当成该接收器的兼容性证明。

## 证据范围与偏移约定

分析日期：2026-11-10。只读取当前工作树 `.ai/evidence/inphic/web/` 的 HTML、脚本和清单；未运行网页或厂商 JavaScript，未联网获取其他版本，未打开 HID、发送设备命令或验证实机。

下文用 **P** 指代本地文件 [`page-fdff76e9871af3c5.js`](../.ai/evidence/inphic/web/js/page-fdff76e9871af3c5.js)。它是单行压缩代码，故引用给出文件别名和可原样搜索的片段，而不依赖压缩后的行号。P 的 SHA-256：

```text
4737cf6d5487da0521a5221389323a67e6cb8841a4b6fa58114563d4dfabd482
```

[`index.html`](../.ai/evidence/inphic/web/index.html) 可搜索 `./_next/static/chunks/app/page-fdff76e9871af3c5.js`，[`chunks.txt`](../.ai/evidence/inphic/web/chunks.txt) 也列出此入口。这里只证明所提供网页包的静态实现，**没有证明它与官方 Windows 驱动 V1.1.5 使用相同协议或支持同一批 IN9 硬件**。

`248A:5B2F`、名称 `Wireless-Receiver`、集合 `0001:0000 / 0001:0002 / 0001:0006 / 0001:0080 / 000C:0001 / FF00:0001 / FFEF:0000` 以及 V1.1.5 可显示电量，均是任务说明中的用户现场信息，本轮未重新采集。

所有偏移从 0 开始，十六进制值带 `0x`。发送数组记为 B，包含供底层 API 使用的报告 ID 槽 B[0]；回复数组记为 R，是 `new Uint8Array(e.data.buffer)` 的 WebHID 数据，报告 ID 由事件另行携带。**不能假定发送与接收具有相同的 ID 前缀，也不能直接将 R 的偏移套到含 ID 前缀的 Windows HID 缓冲。**

## 1. PID 通配、身份识别与 `5B2F` 的匹配结果

### 1.1 配置的实际内容

出处：P，检索 `"productDesc":"XICHEN M003"`、`"productDesc":"XICHEN M030"`、`"productDesc":"IN9 Gaming Mouse"`。静态提取 `k=JSON.parse('...')` 中的 JSON 数据，可得：

| 条目 / deviceId | VID:PID | deviceMode | hardwareId | icTypes | comm / listener |
| --- | --- | --- | --- | --- | --- |
| XICHEN M003 / 201 | `248A:*` | 0 | M003 | 17、149，即 `0x11 / 0x95` | 均 `FF01:0010` |
| XICHEN M003 / 201 | `249A:*` | 1 | M003 | `0x11 / 0x95` | 均 `FF01:0010` |
| XICHEN M030 / 202 | `248A:*` | 0 | M030 | `0x11 / 0x95` | 均 `FF01:0010` |
| XICHEN M030 / 202 | `249A:*` | 1 | M030 | `0x11 / 0x95` | 均 `FF01:0010` |
| IN9 Gaming Mouse / 203 | `248A:5C2E / 5D2E / 5E2E` | 0 | M910 | 49，即 `0x31` | 均 `FF01:0010` |
| IN9 Gaming Mouse / 203 | `248A:5C2F`、`249A:5C2F` | 1 | M910 | `0x31` | 均 `FF01:0010` |

以上均为 `protocol:5`、`identityStrategy:"xichen-device-info"`、`commMode:1`、`rateProtocol:1`。`249A` 确实存在于原数据；是否笔误没有证据，不能擅自按 `248A` 解释。网页将 deviceMode 0 映射为 USB、1 映射为 2.4G，出处：P，检索 `N=0===P?"USB":1===P?"2.4G":"BLE"`。

### 1.2 匹配分为候选协议选择和身份严格匹配

出处：P，以下分别为原样可搜索片段：

```javascript
function M(e){if(null==e)return!1;if("number"==typeof e)return 65535===e;let t=e.trim().toUpperCase();return"*"===t||"0XFFFF"===t}
function E(e,t,n,r){let o=e=>"number"==typeof e?e:parseInt(e,16);return o(e)===n&&(!!M(t)||o(t)===r)}
```

`M` 接受 `"*"`、字符串 `"0xFFFF"` 或数字 65535；`E` 始终要求 VID 相等，然后检查 PID 通配或相等。通配不取消 VID、后续 hardwareId 和 icType 的要求。

通讯工厂先遍历 `k.products[].devices[]`，收集 VID/PID 匹配项的 protocol；只在集合大小为 1 时选定协议。对 `248A:5B2F`，M003 和 M030 的通配项使候选协议同为 5，于是可创建 `ti`，**这一步没有认定具体型号**。出处：P，检索：

```javascript
E(o.vendorId,o.productId,e,t)&&null!=o.protocol&&n.add(o.protocol)
if(1===n.size)return Array.from(n)[0]
5===i?new ti(e):new eE(e)
```

实际扫描先要求能配到 listener，然后执行 `s.isXichen()&&!await this.identifyXichenDevice(p)`；失败就 `releaseUnidentifiedXichenDevice(p)` 并跳过。`identifyXichenDevice` 强制调用 `getXichenDeviceInfo(!0)`，再重新遍历型号表，要求同时满足下列条件且最终恰好一个匹配：

```javascript
if(5!==o.protocol||"xichen-device-info"!==o.identityStrategy)continue;
o.hardwareId!==i.hardwareId
!o.icTypes.includes(i.icType)
if(1!==c.length)return
```

VID/PID 仍由 `E(o.vendorId,o.productId,e.vendorId,e.productId)` 检查。通配成功还有警告，出处：P，检索 `[XICHEN] 当前使用未固化 PID 通配匹配，请采集后写入精确 PID`。

因此，在**另有合格通讯/监听集合且能取得身份回复的前提下**：

| `248A:5B2F` 的身份回复 | 严格匹配结果 |
| --- | --- |
| M003 且 icType 为 `0x11` 或 `0x95` | 只能匹配 `248A:*` 的 M003、deviceMode 0 项 |
| M030 且 icType 为 `0x11` 或 `0x95` | 只能匹配 `248A:*` 的 M030、deviceMode 0 项 |
| M910 且 icType 为 `0x31` | **没有匹配**；IN9 的精确 PID 仍不包含 `5B2F` |
| 其他或不唯一的匹配 | 拒绝识别 |

这也说明通配项可能把一个实际无线接收器归入配置的 USB 模式，不能把这种候选匹配当成支持证明。结合任务所列全部集合，当前网页还会更早在 comm 枚举处排除它，详见第 3 节。

### 1.3 身份查询及 hardwareId 的来源

查询配置要求 **Usage Page `0xFF01`、Usage `0x0010`**，comm 和 listener 相同。出处：P，检索 `"hardwareId":"M910","icTypes":[49],"commUsagePage":"0xFF01","commUsage":"0x0010"`；M003/M030 条目使用同一组集合。

身份查询为 `getXichenDeviceInfo` → `this.setData(e4(16))`，即命令 `0x10`。具体输出 ID、数组长度和发送方式见第 2 节。回复至少 15 字节，R[4..7] 拼为四字符 hardwareId，并要求大写字母/数字各四位；不是用产品字符串推断。出处：P：

```javascript
if(e.length<15)throw Error("XICHEN 设备信息响应长度不足");
for(let n=4;n<8;n++){let r=null!==(t=e[n])&&void 0!==t?t:0;0!==r&&(d+=String.fromCharCode(r))}
if(!/^[A-Z0-9]{4}$/.test(d))throw Error("XICHEN hardwareId 无效: ".concat(JSON.stringify(d)));
```

ASCII 值 M910=`4D 39 31 30`、M003=`4D 30 30 33`、M030=`4D 30 33 30` 是上述字符规则的编码展开，**不是 `5B2F` 的实机回复**。R[10] 的 icType 也参与型号校验。

## 2. protocol 5 的电量读取与帧格式

### 2.1 查询是 Output，不是 Feature

出处：P，检索 `class ti extends eE`、`async getDeviceData()`、`async flushQueue()`、`async webhid_write_command()`、`async hid_write(e)`。静态调用链为：

```text
getXichenDeviceInfo → setData(e4(16)) → e3 → api.getDeviceData
  → 队列 ["get", e] → webhid_write_command → hid_write → HIDDevice.sendReport
  → readP → inputreport 的 data.buffer
```

关键原样片段：

```javascript
let t=e3(e);for(let e=0;e<3;e++){let n=await this.api.getDeviceData(t);
args:["get",e]
let[t,...r]=n,o=await this.webhid_write_command(...r);
await t.hid_write(r);let a=Array.from(await t.readP(o));
let n=e[0],r=new Uint8Array(e.slice(1));
t._device.sendReport(n,r)
```

`webhid_write_command` 组装 `Array(33).fill(0)`，`hid_write` 将首槽作为报告 ID，其余 32 字节作为载荷，所以这里是 **Output report ID `0x00`，32 字节载荷，主机数组共 33 字节**。它没有按 `getReportId()` 自动改用描述符中第一份 Output 报告的 ID。

同文件另有 `hid_send_feature_report` / `sendFeatureReport` 和 `hid_get_feature_report` / `receiveFeatureReport`，但上面的 XICHEN 查询链没有走它们。不能因为基类的 `setData` 使用 Feature 就错判：`ti` 已覆盖该方法。Feature 的描述符补齐逻辑 `getFeatureReportByteLength` 也不为此 Output 查询提供长度证明。

### 2.2 `0x10` 身份/初始电量请求的完整字节

出处：P：

```javascript
function e3(e){let t=Array(33).fill(0);for(let r=0;r<Math.min(e.length,33);r++){var n;t[r]=e5(null!==(n=e[r])&&void 0!==n?n:0)}let r=0;for(let e=5;e<32;e++)r+=t[e];return t[32]=255&r,t}
function e4(e){return e3([0,255&e])}
function e6(e,t){return e3([0,255&e,0,1,255&t.length,...t])}
```

将常量 16 代入 `e4` 得以下 33 字节；这是**静态展开，不是抓包**：

```text
00 10 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

| 发送数组位置 | 值 | 能由代码确认的含义 |
| --- | --- | --- |
| B[0] | `00` | `sendReport` 的报告 ID 参数，不属于传入的 32 字节 data |
| B[1] | `10` | 身份查询命令，含初始电量；是 data[0] |
| B[2] | `00` | 本构造器固定零，具体协议语义未命名 |
| B[3] | `00` | 本查询构造器固定零；写构造器 `e6` 在此放 1，不能据此补充未说明的位语义 |
| B[4] | `00` | 本查询无附加数据；`e6` 对应位置放写入数据长度 |
| B[5..31] | 全零 | 27 个零填充字节，本查询无参数 |
| B[32] | `00` | `sum(B[5..31]) & 0xFF`，不含 B[1] 命令字 |

发送数组长度是软件常量，不是 `5B2F` 报告描述符的实测结果。不要尝试为其他报告长度、报告 ID 或集合“补零适配”。

### 2.3 身份回复中的初始电量、充电和连接字段

出处：P，`getXichenDeviceInfo` 返回对象中的原样片段：

```javascript
icType:null!==(a=e[10])&&void 0!==a?a:0,reportMax:null!==(i=e[11])&&void 0!==i?i:0,chargeFlag:null!==(c=e[12])&&void 0!==c?c:0,batteryValue:null!==(l=e[13])&&void 0!==l?l:0,connectStatus:null!==(s=e[14])&&void 0!==s?s:0
```

| WebHID 回复数据位置 | 解释 / 证据限度 |
| --- | --- |
| R[0..3] | 身份解码器没有验证这些头字段；不能自行断言其中有 `0x10` 回显或固定状态码 |
| R[4..7] | 四字符 hardwareId |
| R[8..9] | firmwareRaw 的低、高字节；被拼为版本字符串，不是电量 |
| R[10] | icType，型号匹配要求 `0x11 / 0x95` 或 `0x31` |
| R[11] | 源码命名 reportMax，未见用它证明报告 ID、方向或字节长度 |
| R[12] | 原始 chargeFlag |
| R[13] | batteryValue，直接用整数百分比，没有电压、多字节拼接或除数换算 |
| R[14] | connectStatus |
| R[15..22] | 可选 password 字符串，遇零停止；读取电量不需要，应排除日志输出 |
| 其余 | 此解码器未说明 |

仅要求 `R.length >= 15`，**未证明身份回复一定 32 或 33 字节**；也没有在这个解码器里校验输入报告 ID、命令回显或回复校验和。密码范围出处：P，检索 `for(let t=15;t<Math.min(e.length,23);t++)`；版本范围检索 `e[9]` 所在的 `getXichenDeviceInfo`。

初次识别的充电布尔值是“原始 flag 非零即真”：出处 P 的 `function to(e){return 2===e||3===e?e:e?3:0}` 和 `e.chargeFlag=(a=i.chargeFlag,0===to(a)?0:1)`。这里只观察到网页如何解释，不代表已证实每个 flag 的真实充电语义。

在线判断为 `e.isOnline=0===d.deviceMode||1===i.connectStatus`：USB 配置直接在线，2.4G 配置要求 R[14]=1。因此不能仅凭 `connect_status=1` 或成功打开接收器就断言鼠标 RF 在线，也不能用通配项误归为 USB 后的结果验证在线字段。

### 2.4 后续电量是 `0xC0` Input 状态帧

出处：P，`setupReportListener(e)` 的 XICHEN 分支：

```javascript
S=192===(f=t)[0]&&f.length>=2&&(null!==(m=f[1])&&void 0!==m?m:0)!==1;
192===b[0]&&b.length>=4?Uint8Array.from([255&y,64,to(null!==(w=b[3])&&void 0!==w?w:0),null!==(j=b[2])&&void 0!==j?j:0,null!==(k=b[1])&&void 0!==k?k:0])
```

这是对 Input **数据字节**的判定；**`0xC0` 不是在此处得到证明的 HID report ID**。

| R 位置 | 值 / 解释 |
| --- | --- |
| R[0] | `C0`，状态帧类型 |
| R[1] | 1 表示在线；其他值标记 `receiver-offline` |
| R[2] | 电量整数百分比，直接使用 |
| R[3] | 原始充电 flag，经 `to` 归一化 |
| R[4..] | 分支未解析，固定总长度未知 |

可解析的最短 `C0` 数据长度为 4；2～3 字节的短帧不会完成转换和状态更新。程序将它转换为 `[deviceId, 0x40, normalizedCharge, battery, connectStatus]` 的内部格式；这里的 `0x40` 是内部状态码，不是另一个需要发送的电量命令。后续共用解析器以 `3===n||2===n?1:0` 得充电布尔值，XICHEN 非零 flag 经 `to` 后均满足此条件。

界面共用解析器接受 `0 <= battery <= 100`，再显示 `%`；出处：P，检索 `o>=0&&o<=100?(b(o)` 和 `tY.debug("初始电量: ".concat(e.batteryValue,"%"))`。管理器的原始赋值没有同等范围验证；界面还存在 `battery_value:e.battery_value||100` 的默认值，以及 `K="USB"===N&&O?1:` 的 USB 充电推断。它们均不能当成设备真实 100% 或真实充电的证据。

同分支还接受 `194===b[0]&&b.length>=2`，即 `C2` 的 DPI 档位通知，R[1] 高半字节加 1；它不是电量。有效通知可令管理器更新 `lastReportTime` 并在线；2.4G 超时检查使用 `OFFLINE_TIMEOUT=8e3`、`ONLINE_CHECK_INTERVAL=1e3`。这些是网页的活跃/超时策略，不是设备采样时钟或缓存新鲜度证明。出处：P，检索 `e.lastReportTime=E,e.isOnline=!0`、`this.OFFLINE_TIMEOUT=8e3`。

### 2.5 主动查询和主动上报的边界

两种途径并存：识别时主动发送 `0x10`，取得初始电量；之后 listener 和 comm 的 input observer 消费 `C0` 主动通知。出处 P：`t.getXichenDeviceInfo(!0)`、`e.listener.on("device-manager-report"`、`addInputObserver("device-manager-comm-report"`。

`getXichenDeviceInfo` 可返回缓存，出处 P：`if(!t&&this.identity)return this.identity`；本文件只有其定义、`getFirmwareVersion` 中的调用和 `identifyXichenDevice` 中的强制调用，没有独立周期电量查询调用。`startOnlineCheck` 定时执行 `checkDevicesOnlineStatus`，只检查时间/状态，不发电量请求。未见订阅 `C0` 前发送使能命令，但静态监听代码**不能证明设备无需初始化就必然上报、按何频率上报或上报是否来自缓存**。

接收底层把 `event.reportId` 与 `data.buffer` 分开，但队列和 `eM` listener 向上述解析器只传数据。出处 P：`t(n,e.reportId)`、`new Uint8Array(e.data.buffer);this.emit(t)`。因此这批代码不能给出身份回复和 `C0` 的精确 HID 输入报告 ID。它只证明最小可解析长度，不能替代逐 ID 的描述符长度。

查询容错也不能照搬为单次读取：`ti.setData` 遇 `192===n[0]||194===n[0]` 会重发，最多前三次加最后一次共 4 次；`getXichenDeviceInfo` 外层还有 3 次重试，最坏可发 12 次相同查询。出处 P：`for(let e=0;e<3;e++)` 后的 `return this.api.getDeviceData(t)`，及 `for(let t=0;t<3;t++)try`。通用 `readP` 默认等待 500 ms，回复队列没有在此按 `0x10` 回显和输入 ID 关联事务；这些弱校验都不是新读取器应自动继承的保证。

## 3. `FF01` 与实际 `FF00 / FFEF` 的不一致

**没有找到针对 XICHEN 的 `FF01:0010 → FF00:0001` 或 `FFEF:0000` 查询回退。** 此结论限定于已提供网页包的 HID 枚举、通讯构造和管理器路径，不扩大为官方 V1.1.5 不支持。

出处：P，`G` 从设备表生成集合配置，`J` / `$` 分别汇总 comm / listener 的 usage pair；`q` / `Q` 用完整 pair 过滤：

```javascript
q=e=>e.filter(e=>{var t;return null===(t=e.collections)||void 0===t?void 0:t.some(e=>J.some(t=>e.usagePage===t.usagePage&&e.usage===t.usage))})
Q=e=>e.filter(e=>{var t;return null===(t=e.collections)||void 0===t?void 0:t.some(e=>$.some(t=>e.usagePage===t.usagePage&&e.usage===t.usage))})
```

对配置 JSON 的全集去重结果为：

| 方向 | 所有允许的顶层集合 pair | 任务所列集合能否满足 |
| --- | --- | --- |
| comm (`J`) | `000B:0000`、`FF01:0002`、`FF01:0010` | 全部不满足 |
| listener (`$`) | `000A:0000`、`FF00:0001`、`FF01:0010` | 只有 `FF00:0001` 满足全局 listener 过滤 |

`FF00:0001` 出现在其他 `1D57:FA65` / Inphic KP 8K 配置的 listener 中，出处 P：检索 `"productId":"0xFA65"` 连同 `"listenerUsagePage":"0xFF00","listenerUsage":"0x0001"`。全局 listener 命中不意味着它是 XICHEN 的可用通讯接口。**若用户列出的集合完整，`5B2F` 的 comm 列表为空，因而到不了身份查询。**

代码中确有以下容易误认为回退的规则，但都不能补齐这项缺口：

| 规则 | 源码出处（P 的可搜索片段） | 实际边界 |
| --- | --- | --- |
| 首个 VID/PID（含通配）配置 | `G.find(n=>n.vendorId===e&&(null==n.productId||n.productId===t))` | 按表顺序选首项，没有精确 PID 优先重排；248A 通配先于 IN9，但这些条目的集合恰好相同 |
| 报告元数据提取 | `let s=t?"".concat(l,":").concat(t):l,d=0,p=0,u=0,x=ee(e.vendorId,e.productId)`；`o.usagePage===i&&o.usage===c` | `en` 在目标集合中取第一份 Output 的 reportId/首 item 的 size/count；匹配失败保留零值，没有改试其他 page |
| 目标 usage 未定义时泛选 | `void 0===i||void 0===c||o.usagePage===i&&o.usage===c` | 只在缺少目标配置/角色时放宽，XICHEN 已有明确 FF01/0010，且仍先经过 `q/Q` |
| 单设备同时作 comm/listener | `if(t.length>0&&0===n.length)`；`return o&&a` | 已有 comm、没有 listener，且 comm 对象包含配置允许的两类 page 时才构造 listener；不能从仅 listener 反造 comm |
| 通讯与监听配对 | `t_(e.address)===a`；`1===t.length?t[0]:void 0` | 优先相同基础地址；否则只有一个同 VID/PID listener 才用，多候选跳过。是接口配对，不是鼠标无线配对命令 |
| 授权时省略通配 PID | `r=M(n.productId)?void 0:tI(n.productId)`；`usagePage:o,usage:a` | 仍带集合过滤；不是任意 248A 集合都能进通讯链 |

`en` 的 `interface:2` 是固定包装字段，`_baseAddress` 由网页对象上的 UUID/随机值生成；它们不是从 Windows 描述符取得的 MI_02 或 USB physical parent 证明，出处 P：`interface:2`、`e._baseAddress=l`、`self.crypto.randomUUID()`。

还需区分：`en` 未找到 Output 元数据并不自行拒绝，它仍缓存 `_device`，`hid_write` 仍对该对象调用 `sendReport`。因此网页的元数据选择不能视为“精确集合 + 精确报告长度”的强制安全门。这里不能借此绕过 `q/Q` 去试目标接收器的其他集合。

## 4. 必须从电量读取器排除的命令/流程

最小身份/电量路径 `getXichenDeviceInfo → ti.setData → getDeviceData → hid_write` 只构造 `0x10` 查询，`C0` 处理只解析输入；未见其调用恢复出厂、配置写入、无线配对或刷写方法。**读取用途仍需要一次主机到设备的 Output 查询，不等于零写入。** 若要求绝对不发任何报告，这批证据只提供被动消费 `C0` 的候选逻辑，尚不能保证它会出现。

完整网页同时具有以下流程，不能为读电量而复用整套启动、Profile 同步、恢复或升级功能。以下列出可证实的命令，不把同文件共存误写成 `0x10` 的必经步骤。

| 方法 / 流程 | 静态命令及出处（均在 P） | 与电量的关系 / 必须排除项 |
| --- | --- | --- |
| `setReportRate` | `e6(2,[Math.max(1,Math.min(8,0|e))])` | `0x02` 写回报率，排除 |
| `setDpiData` | `let l=e6(3,c);return l[4]=37,l`；`return e6(4,t)` | `0x03` 写 DPI、`0x04` 写 DPI 色，排除 |
| `setLightConfig` | `e6(5,[a,i` | `0x05` 写灯光，排除 |
| `setPerformanceConfig` | `this.setData(e6(6,` | `0x06` 写性能，排除 |
| `setSleepConfig` | `this.setData(e6(7,` | `0x07` 写睡眠/唤醒，排除 |
| `setMacroData` | `a[0]=0,a[1]=8,a[2]=0,a[3]=t|r<<4` | `0x08` 写宏分片，排除 |
| `setMouseKeys` / `resetMouseKeys` | `t[1]=9,t[3]=1,t[4]=15`；`await this.setMouseKeys(t)` | `0x09` 写按键，包含重置按键，排除 |
| `restoreFactorySettings` | `await this.setData(e6(15,[255]))` | `0x0F`、数据 `FF`，之后又写按键/DPI/性能/回报率/灯光/睡眠，全部排除 |
| Profile 同步 `tc` | `async function tc(e,t,n)`、`await e.setDpiData(r)` | 串行调用上述配置/按键/宏写入；不是读电量初始化，排除 |
| `getFirmwareVersion` | `getXichenDeviceInfo()`、`"2.4g"===this.mode`、`this.setData(e4(32))` | 读鼠标身份中的版本；2.4G 时另发 `0x20` 读接收器版本。不是固件写入，但电量不需要，排除附加查询 |
| 网页完整连接 | `await D.getFirmwareVersion()`；`await E(D,!1,C)` | 还会读取按键、DPI、灯光、回报率（`e4(17)`、`e4(19/20/22/23)`、`e4(21)`、`e4(18)` 对应 `0x11/13/14/16/17/15/12`），不能把整套连接当作单条电量查询 |
| 独立 OTA 功能 | `let cE={Start:177,Data:178,Reset:179}`；`e.sendFeatureReport(t.reportId,t.data)` | Feature ID `B1` 开始、`B2` 数据、`B3` 重启，全部排除。存在于网页另一套升级路径，不能据此声称支持 XICHEN 或必须先升级 |

这里相同数值的意义依位置、方向和协议而变：XICHEN 的 **B[1]=0x0F** 是恢复出厂，**B[1]=0x15** 是灯光读取；OTA 的 `B1/B2/B3` 是另一路 Feature report ID。不能只按数字相同就认定为同一种命令。

在 `ti` 的方法全集、身份/状态调用链中没有找到无线配对命令；P 中“配对”相关日志说的是 comm/listener 物理地址关联。这个有限搜索结果不证明所有英菲克固件都没有配对协议，未知命令也不能纳入读取流程。

## 5. 能否设计 `248A:5B2F` 的精确只读读取器

**目前不能给出可启用的实现方案。** 可以保留候选格式：配置标示的 `FF01:0010` 上，以 Output ID 0、32 字节 data 发送 `0x10`，从身份数据 R[12/13/14] 读充电/电量/连接，并监听 `C0`。但该格式尚未和 `248A:5B2F` 的任何一个实际集合建立证据关联。给接收器任意厂商集合发这条命令、改 PID 表、把集合改成 FF00/FFEF、降级为通配读取，都会超出现有证据。

需要补齐的证据如下；这是后续采集条件，本轮没有执行采集或设备操作。

| 缺少什么 | 为什么必要 / 当前可核对的源码边界 |
| --- | --- |
| 目标接收器完整 USB/HID 描述符及设备路径/MI、真实 physical parent、固件或 bcdDevice 信息 | 确定七个集合是否完整以及所属接口。P 只要求 FF01/0010，`interface:2` 不是 MI 证据 |
| 同一目标、同一集合的 Input/Output/Feature 逐 ID bit/byte 长度及完整性证明，最好原始 HID Report Descriptor 加解析结果 | 证明真正可用的方向、ID 和长度；`Array(33)` 只是发送常量，`reportMax` 与 HID 最大 caps 长度均不足以证明特定 ID |
| V1.1.5 针对 `248A:5B2F` 正常读取身份/电量的完整通讯记录，注明接口/集合、API 类型、report ID、方向、是否含 ID 前缀和实际长度 | 解决 FF01 与 FF00/FFEF 的冲突，并确认这批网页协议是否适用；不能仅凭相近 PID、鼠标商品名或驱动能显示电量外推 |
| 同一目标的完整身份回复及 `C0` 原始帧（如果有），保留头、尾、长度、输入 ID，敏感字段脱敏 | 确认硬件是 M910/M003/M030 还是另一协议，建立命令应答关联及严格解析边界，避免将并发通知当作查询回复 |
| 电量变化、插拔充电、鼠标开关/休眠/唤醒、接收器单独在线等对应场景与时间记录 | 验证百分比、flag 语义、RF 在线、接收器缓存和数据新鲜度；网页的 USB 推断、8 秒超时和默认 100% 不能证明这些性质 |

补证后才有条件形成“精确 VID/PID + 精确集合/同一 parent + 精确方向/报告 ID/长度”的拒绝式边界：任何证明缺失、不完整或不一致时不发送查询；不探测其他集合，不执行配置、配对、复位或固件操作。该原则是本报告的后续设计约束，不是当前已有或已验证的读取器。

## 静态核对结果

已将五项问题分别追到配置、通配谓词、工厂、身份解码、队列/Output 发送、集合过滤、Input 转换和相邻写入/OTA 代码，并保留上文可搜索片段；对内嵌设备 JSON 只作数据解析，核对了全部 9 个 protocol 5 配置和 3 组 comm/listener pair。未执行任何厂商代码；本报告不构成描述符证明、V1.1.5 通讯复现或 `248A:5B2F` 实机验收。
