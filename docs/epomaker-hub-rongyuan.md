# EPOMAKER HUB：容园电量协议与 INFI75 候选路径的静态核对

## 1. 结论摘要

- **背景混合了两套协议：`05AC:024F / FF68:0061` 注册为华奋达，不是容园。**
- 容园键盘的 `GET_BATTERY=0x82` 是 64 字节 Feature 查询，校验在数据偏移 7；完整请求见 2.2。
- 华奋达用 `GET_DEVICE_INFO=0x10` 的 Output/Input 交换同时读取型号、链路和电量，不用容园 `0x82 / 0x8F`。
- `NEED_FULL_DEVICE_IDS` 属于华奋达 SDK 的共享 ID 消歧；与容园 `F6/F7/FC` 接收器状态机无关。
- 所给脚本未见 INFI75 / Infiverse 型号项；同 VID/PID、集合或官网链接均不能证明该型号会应答。
- 有线 INFI75 的 `MI_02 / FF68:0061` 仅是华奋达候选，缺逐报告描述符、真实回复、型号与固件能力证明。
- 已记录的 `2.4G Wireless Keyboard / FFB5:0001 / Input、Output 各 8 / Feature 0` 没有对应 HUB 路径，不发送。
- 本次只作本地静态阅读，未执行厂商脚本、联网或访问 HID；不改变兼容状态，不承诺支持。

## 2. 七个问题的逐条回答

### 阅读口径

本节 `pretty/…` 均相对 `.ai/evidence/epomakerhub/20261112/`。行号属于机械换行副本，不是 JavaScript 语句编号；短引文是定位片段，不大段转载脚本。涉及请求构造的关键文本已回到 `assets/index-lp9XA374.js` 核对。

所有字节索引从零开始：`D` 是传给 WebHID 发送方法的数据，不包含独立的 `reportId` 参数；`B` 是容园封装返回的数组；`H` 是华奋达 `inputreport.data`；`C` 是各华奋达回复去掉 8 字节头以后拼接的设备信息内容。不要把这些偏移与 Windows 缓冲区的 Report-ID 槽混用。

“静态确认”仅说明所给文件这样写；“推断”不等于设备端执行；“未确认”不以其他品牌、同 ID 或通用 HID 常识补齐。历史硬件枚举也不是本轮现场测量。

### 2.1 设备匹配、型号区分与 productNum 来源

#### WebHID 筛选不是只看 VID/PID，也不是产品名白名单

| 阶段 | 静态行为 | 依据与短引文 |
| --- | --- | --- |
| 授权选择器 | 仅传注册表中的 VID/PID；这里没有 usage、产品名或 report ID 条件 | `pretty/index-lp9XA374.js.txt:745–747`：`map(e=>({vendorId:e.vendorId,productId:e.productId}))` |
| 注册查找 | 先取该 VID/PID 的**第一条**注册项，选择协议类和初始配置 | `pretty/index-lp9XA374.js.txt:703`：`find(n=>n.vendorId===e&&n.productId===t)` |
| 集合过滤 | 在设备的 `collections` 中找该初始项的 usagePage/usage；匹配位于第一个集合的候选优先。没有逐报告 ID/长度拒绝逻辑，日志打印报告 ID 不等于校验了它 | `pretty/index-lp9XA374.js.txt:761–767`：`findIndex(e=>(e.usagePage??0)===i&&(e.usage??0)===a)` |
| 打开与协议检查 | 先连接，再 `setReportId(model.reportId)`，随后 `checkDevice()`；检查成功才采用返回的精确型号 | `pretty/index-lp9XA374.js.txt:728–737`：`s.setReportId(o.model.reportId)`、`h.model&&(g=h.model)` |
| 华奋达重找授权设备 | 优先同一对象，否则退到同 VID/PID **且同 productName**；这不是 INFI75 产品名注册表，也没有证明物理父设备唯一 | `pretty/index-lp9XA374.js.txt:9216–9217`：`t===e`、`t.productName===e.productName` |

华奋达 SDK 的另一个过滤器只要求某个集合的 usagePage 落在允许表，不检查 usage；它比顶层的 usagePage/usage 匹配宽。依据 `pretty/index-lp9XA374.js.txt:9086–9100` 的 `VALID_USAGE_PAGES.includes(t.usagePage||0)`；允许表在 `pretty/index-lp9XA374.js.txt:9164`，值为 `FF68 / FF80 / FF60 / FF00 / FF01 / FF1B`。这些并非可以轮流试发的集合清单。

#### `05AC:024F` 与 `0C45:*` 的实际注册项

注册常量为 `nN=3141`、`rN=1452`；十六进制分别是 `0C45`、`05AC`。下表全部来自 `pretty/index-lp9XA374.js.txt:9263`，并由 `pretty/index-lp9XA374.js.txt:9264–9268` 的 `registerDevice(t.vendorId,t.productId,Oj,t,tN)` 注册为**华奋达 `Oj / tN`**。原文示例：`` vendorId:rN,productId:591,reportId:0,usagePage:65384,usage:97,name:`Epomaker TH108 PRO` ``、`productNum:7`。表中名称是 HUB 显示名称，**不是必须等于 USB productName 的字符串**。

| VID:PID | 型号名称与十进制 productNum | 集合；配置 reportId |
| --- | --- | --- |
| `05AC:024F`（1452:591） | TH108 PRO `7`；TH108 `2`；TH108 KR `5`；TH87 `12` | `FF68:0061`；`0` |
| `0C45:8092`（3141:32914） | HE80 `5120 / 4100 / 4864 / 4364` | `FF68:0061`；`0` |
| `0C45:8006`（3141:32774） | TH108 ISO `18`；TH87 JIS `31`；TH108 V2 PRO `11` | `FF68:0061`；`0` |
| `0C45:FEFE`（3141:65278） | TH108 JP `16` | `FF68:0061`；`0` |
| `0C45:800A`（3141:32778） | TH99 PRO `27` | `FF68:0061`；`0` |
| `0C45:8009`（3141:32777） | G84 PRO `21`；TH108 PRO DE `45`；TH108 PRO JIS `52` | `FF68:0061`；`0` |
| `0C45:8010`（3141:32784） | G84 Pro KR `47` | `FF68:0061`；`0` |
| `0C45:8100`（3141:33024） | G84 PRO JIS `48` | `FF68:0061`；`0` |
| `0C45:8007`（3141:32775） | TH87 ISO `87` | `FF68:0061`；`0` |
| `0C45:800B`（3141:32779） | TH80 V2 PRO `35` | `FF68:0061`；`0` |
| `0C45:8011`（3141:32785） | TH65 `40` | `FF68:0061`；`0` |
| `0C45:8012`（3141:32786） | TH65 `76` | `FF68:0061`；`0` |
| `0C45:FEFA`（3141:65274） | TH65 `40`（由 wirelessForm 派生）；TH65 `76`（显式添加） | `FF60:0061`；`0`；`connectionMode=receiver` |

同表另有相邻 ID `05AC:0250`（1452:592）：TH87 DE、productNum `9`、`FF68:0061`、reportId `0`；不是 `024F` 的别名。

派生项机制也有直接证据：`pretty/index-lp9XA374.js.txt:5760–5767` 的 `` connectionMode:t.connectionMode??`receiver` `` 与 `r.push(iT(i,e))` 保留原型号 productNum/config，替换 wirelessForm 指定的 ID/集合；因此不能漏掉 `FEFA / 40`。但同 VID/PID 先取首项的机制并不按不同集合分别查注册项，不能据此宣称所有同 ID 集合都能选中。

#### productNum 来自回复，不是产品名猜测

- **华奋达**：`GET_DEVICE_INFO=0x10` 的内容 `C[14] | C[15]<<8` 是 `product`；`C[12..13]` 是 `manufacturer`。`checkDevice` 取 `n.product||0`，按实际 VID/PID 和这个 product 精确找 `productNum`；无匹配就失败。依据 `pretty/index-lp9XA374.js.txt:8741`：`product:n[14]|n[15]<<8`；`pretty/index-lp9XA374.js.txt:8663–8670`：`selectModelByProductNum(a.vendorId,a.productId,i)`；`pretty/index-lp9XA374.js.txt:9268`：`r.productNum===n`。`manufacturer` 参与 SDK 的 full ID，但这里的最终型号查找没有比较它。
- **容园**：另一个 `GET_DEVICE_INFO=0x8F` 取 `B[1..4]` 小端 32 位 `deviceID`，再在该 VID/PID 的型号表中精确匹配 `productNum`，且检查构建品牌。依据 `pretty/index-lp9XA374.js.txt:3610–3611`：`r=e[3]<<24|e[2]<<16|e[1]<<8|e[0]`；`pretty/index-lp9XA374.js.txt:5654–5670`：`s=a.deviceID||0`、`r.find(e=>e.productNum===n)`。这是不同字段、不同命令，不能混用。

所给 `assets/*.js` 和 `pretty/*.txt` 中不区分大小写检索 `INFI75|Infiverse` **无匹配**；上述完整华奋达注册表和容园表 `pretty/index-lp9XA374.js.txt:5770` 都未见该型号。该负面结论仅覆盖这批文件，不涵盖未提供或未来更新的模块。官网 INFI75 页面指向 HUB 是来源关系，不能代替型号表或应答证据。

### 2.2 传输、封包与完整请求

#### A. 容园：Feature，不是 Output/Input 电量交换

`pretty/index-lp9XA374.js.txt:473` 初始化 `reportId=0`，容园型号表 `pretty/index-lp9XA374.js.txt:5770` 也配置 `reportId:0,usagePage:65535,usage:2`。发送实际调用 `pretty/index-lp9XA374.js.txt:520` 的 `device.sendFeatureReport(this.reportId,new Uint8Array(e))`；查询在 `pretty/index-lp9XA374.js.txt:529–542` 调 `receiveFeatureReport(this.reportId)`。`inputreport` 监听另用于档位、忙闲、擦除通知等，不是这里的电量回复（`pretty/index-lp9XA374.js.txt:5729–5745`：`if(e.reportId!==5...)return`）。

包构造的静态依据是 `pretty/index-lp9XA374.js.txt:3164–3172`，关键原文为 ``algorithm:`sum_complement`,startIndex:0,endIndex:7,checksumIndex:7``、`return~...reduce((e,t)=>e+t,0)&255`、`Array(t-r.length).fill(0)`。其含义：

1. 默认总数据长 64 字节；字符串先按十进制 `parseInt` 转换。
2. 输入末尾补零到 64；默认校验范围是半开区间 `D[0..6]`，**不含偏移 7**。
3. `D[7] = (~sum(D[0..6])) & FF`，即一字节求和后按位取反；不是二补数 `-sum`，也不是 CRC。
4. `D[8..63]` 不参加默认校验。LED 等另传校验配置，不得套用到电量查询；`crc16` 分支这里只赋 0，并没有实现 CRC。
5. 电量与设备信息都只传一个 opcode 给 `Y.buildPacket`，没有其他参数，分别见 `pretty/index-lp9XA374.js.txt:3606–3616` 的 `GET_DEVICE_INFO=143`、`GET_BATTERY=130`、`Y.buildPacket(t)`。

以下每帧四行，每行 16 字节，**都是发送数据 `D[0..63]`，WebHID 的 report ID 参数另为 `0`**。

`GET_BATTERY (0x82)`：`sum=82+00+00+00+00+00+00=82`，`(~82)&FF=7D`，`82+7D=FF`。

```text
82 00 00 00 00 00 00 7D 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

容园 `GET_DEVICE_INFO (0x8F)`：`sum=8F`，`(~8F)&FF=70`，`8F+70=FF`。

```text
8F 00 00 00 00 00 00 70 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

这里的发送数据长是静态确认的 64；脚本没有验证回复必须恰为 64，也没有验证回复校验和。容园设备信息的最低保护写的是 `t.length>7`，却访问到 `B[8]` 的固件高字节（`pretty/index-lp9XA374.js.txt:3610–3613`：`i=t.slice(5,9)`、`c.toString(16)`）；安全解析至少需要 9 个有效数据字节，不能复制这个不足的长度检查。真实目标的 Feature ID/长度仍须描述符证明。

#### B. 华奋达：Output/Input，另一个 GET_DEVICE_INFO

`pretty/index-lp9XA374.js.txt:8707–8738` 的 `jj=0`、`await e.sendReport(jj,x)`、`e.addEventListener(`inputreport`,p)` 确认 Output report ID `0`，回复由 Input 事件交付；不调用 Feature Get/Set。`setReportId(0)` 的成员变量不是这条 SDK 发送链的动态参数。

这条链**不使用 `Y.buildPacket`，没有添加校验**。`Pj` 原文在 `pretty/index-lp9XA374.js.txt:8725–8730`：`new Uint8Array(i).fill(0)`、`c[0]=170,c[1]=e,c[2]=t,c[3]=n&255,c[4]=n>>8&255`、`c[6]=s?1:0`。头格式为：

| 偏移 | 含义 |
| --- | --- |
| `D[0]` | `AA` 请求头 |
| `D[1]` | opcode；设备信息为 `10` |
| `D[2]` | 本包内容长度 |
| `D[3..4]` | 内容地址，小端 |
| `D[5]` | 本次调用为 0 |
| `D[6]` | 最后一包为 1，其余为 0 |
| `D[7]` | 本次调用为 0 |
| `D[8..N-1]` | 本次是读请求，没有传 data，全部为 0；不是校验位 |

`Uj` 明确调用 `Nj(e,{cmd:Vj.GET_DEVICE_INFO,contentSize:56})`（`pretty/index-lp9XA374.js.txt:8740`）；`N` 却取 `collections[0].outputReports[0].items[0].reportCount || 32`，每包内容容量 `N-8`（`pretty/index-lp9XA374.js.txt:8707`）。**INFI75 的 N 未确认，不能从默认 32 倒推出它有 32 字节 Output，也不能从另一个集合的 Feature 65 推出这里 N=64。**

为使请求可逐字节复核，以下给出代码可导出的两种**条件化示例**，不是已测 INFI75 的帧长，亦不限定 SDK 只支持这两种长度。

若运行时证明本集合 Output ID `0` 的数据长为 **64**，56 字节内容恰好一包：

```text
AA 10 38 00 00 00 01 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

若运行时证明数据长为 **32**，则依次发三包、每包等自己的回复（24+24+8=56）；不是只发第一包，也不自行缩短 `contentSize`：

```text
AA 10 18 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

```text
AA 10 18 18 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

```text
AA 10 08 30 00 00 01 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

Input 的命令关联条件是 `H[0]==55`、`H[1]==10`：`pretty/index-lp9XA374.js.txt:8730–8736` 的 `e[0]===85`、`c.cmd!==r`。设备信息调用没有传 `checkAddr:true`，监听器也**未检查 `event.reportId`**；因此 Input 报告 ID、精确返回长度和是否可靠回显地址均为未确认，不能写成源码已证明 Input ID `0`。它按收到顺序把各包 `slice(8)` 拼接再取前 56 字节，未严格验证回复长度/`lenOrType`；未来验证应补上关联和完整性检查，而不是补零凑足。

### 2.3 电量回复、充电标志、无读数、超时与重试

#### 容园 `getBatteryInfo`

解析直接见 `pretty/index-lp9XA374.js.txt:3615–3618`：`r=n.length>1?n[1]:void 0`、`r>0&&r<=100`、`a>=0&&a<=2?a:null`、`keyboardChargePercentTrusted=!1`。

| 项目 | 静态可确认的规则 |
| --- | --- |
| 回显 | 解析函数本身不查 `B[0]`；初始化后连接层通常按 `B[0]==82` 护栏检查，见下文。没有已提供的真实回复样本 |
| 电量 | `B[1]`，仅数字 `1..100` 产生 `batteryLevel`；`0`、`101..255`、缺字节为 `undefined`，不是 0% 或 100% |
| 充电字段 | `B[2]`，仅 `0 / 1 / 2` 接受；缺字节或其他值为 `null`，不是“未充电” |
| `1` 的含义 | 进入界面的 charging 分支；`pretty/index-lp9XA374.js.txt:11229` 的 `keyboardChargeStatus===1`、`Te.value&&!W.keyboardChargePercentTrusted` 关联到 `device.charging`。这建立了软件显示含义，不是实测固件状态 |
| `2` 的含义 | `pretty/index-lp9XA374.js.txt:11229` 的 `keyboardChargeStatus===2` 使 `De` 为真；`:11249` 的 `De.value&&!Ne.value` 绘制闪电后再画电池/百分比。**没有明确的 full/充满映射，不认定2=充满**；与1的设备端区别未确认 |
| `0` 的含义 | 被接受但不触发上述1/2状态分支；未见进一步区分“放电/未插线/其他”的映射，不能补写成设备端已证实语义 |
| Trusted 标志 | 容园每次解析都设 `false`，不是按某个固件能力位计算；界面用 `Te.value&&!W.keyboardChargePercentTrusted` 决定充电时不要按可信百分比显示。不能据它判断有无电量计 |
| 成功不等于读数 | 即使空/短数组，回调仍可能返回 `success:true`，但百分比为空；不得把 success 当作有效电量证明 |

显示策略另有型号配置 `hidePercent` / `hideFullPercentInReceiverMode`；`pretty/index-lp9XA374.js.txt:5531` 的 `batteryConfig={isSupportBattery:!0,hidePercent:!0,...}` 与 `pretty/index-lp9XA374.js.txt:11202–11203` 的 `n?.hidePercent===!0||xU(...)` 说明“界面没数字”不等于没有字段。反之全局缓存默认/复位值 100 也不是测量：`pretty/index-lp9XA374.js.txt:1381` 的 `W.keyboardBattery=100`。保留旧值时不得标记本次新鲜读数。

回显护栏是连接层行为，不能漏掉也不能夸大：

- `pretty/index-lp9XA374.js.txt:5660–5663` 的 `echoExemptOpcodes:new Set([...130,...143...])` 在型号识别后把 `82 / 8F` 归入“允许同形回复但检查命令头”的集合；`pretty/index-lp9XA374.js.txt:5711–5715` 使用 `headerRetries:s?2:0,expectHeader:s?a:void 0`。这不是“完全不校验回显”。
- 具体循环在 `pretty/index-lp9XA374.js.txt:5689–5700`：`a=await t(0)`，后续遇到错误头先判断 `e===r` 就失败。因此这里配置数值 **2 实际最多两次完整 Set/Get 尝试（初次 + 一次重发）**，不是初次加两次。空数组因 `o.length>0` 条件不进入错误头判定；解析仍须独立拒绝空/短回复。
- 连接初期只有 `8F` 在默认豁免表，基础延迟为 15 ms；型号选定后改成 `featureReadDelayMs??0`。重发的读前延迟递增 15 ms，护栏还等待 `i||5`。依据 `pretty/index-lp9XA374.js.txt:5701–5715`：`Yw=15`、`Qw=15`、`i+e*Yw`。不能为 INFI75 指定某个容园型号的延迟。

`timeout:3e3` 同时写在 `82 / 8F` 队列选项里（`pretty/index-lp9XA374.js.txt:3609`、`:3616`），但**只是本段队列保存的参数，不是可证实的 3 秒 I/O 截止**：`pretty/index-lp9XA374.js.txt:3118–3135` 保存 `timeout`、`maxRetries:t.maxRetries??0`，执行处只 `await this.hidBase.sendCommand(...)`，没有依此启动超时计时器。两条命令未设置队列重试，故队列级重试为 0。

另外还有底层异常重试，不能与上述护栏混成一个数字：`pretty/index-lp9XA374.js.txt:518–525` 的 `n<=5` 仅对 `NotAllowedError` 最多 **5 次额外发送重试**，等待 `60*(n+1)` ms；`pretty/index-lp9XA374.js.txt:527–541` 的 `e<=3` 最多 **3 次额外 Get 重试**，等待 `40*(e+1)` ms。忙闲等待见 `pretty/index-lp9XA374.js.txt:505–509` 的 `waitForIdle(e=3e3)`，不是 API 超时；这些循环均不能保证挂起的系统 I/O 在固定墙钟时间退出。

#### 华奋达：电量在设备信息内容中

`pretty/index-lp9XA374.js.txt:8740–8741` 的 `slice(0,56)`、`workMode:n[16],batteryLevel:n[17],chargeStatus:n[18]` 给出：

| 内容偏移 | 含义与限制 |
| --- | --- |
| `C[4..7]` | 内容 VID/PID，小端；可作为未来验证的交叉核对，不是源码已经实施的拒绝条件 |
| `C[12..13] / C[14..15]` | manufacturer / product，小端；后者与注册表 productNum 比较 |
| `C[16]` | workMode：`0=usb / 1=bt / 2=2.4g`，其他为 null；依据 `pretty/index-lp9XA374.js.txt:8605–8608`：`` case 0:return`usb` `` 等 |
| `C[17]` | 原始电量字节；若第一包内容至少覆盖到这里，对应首个 Input 的 `H[25]`。不是容园的 `B[1]` |
| `C[18]` | 原始 chargeStatus，首包覆盖时对应 `H[26]`；**其取值含义未确认**，不能复用容园 0/1/2 |

`pretty/index-lp9XA374.js.txt:8613–8619` 的 `e=await t.getDeviceInfo()` 说明每次电量请求会读 `0x10`；失败后 `e||=...getDeviceInfo()??null` 回退缓存，因此可返回旧数据而不是本轮实时电量。它设 `W.keyboardChargeStatus=null`、`keyboardChargePercentTrusted=!0`，虽把原始 chargeStatus 放在返回对象里，却没有据它驱动充电显示。该 trusted=true 是这条实现的策略，不是电量计校准证明。

华奋达的原始解析没有限制电量 `<=100`；更新界面只检查数字且 `>0`（`pretty/index-lp9XA374.js.txt:8619`、`:11230–11236`：``typeof t==`number`&&t>0``）。所以源码会忽略 0/缺值，却没有把 `101..255` 明确标为无读数，也没有无电量计固件哨兵定义；`:11249` 虽将图形填充宽度夹到0..100，百分比文本仍取原值，不能当成解析器已校验范围。建议验证阶段把越界、截断、缓存值作为“无新读数”，此为保守验收规则，不冒充厂商已经实现或承认 0 的设备端语义。

华奋达 `Nj` 默认**每包**超时 500 ms、额外重发最多 3 次，总计最多 4 次尝试；`Uj` 没覆盖这两个默认值。依据 `pretty/index-lp9XA374.js.txt:8707` 的 `timeout:o=500,maxRetries:s=3`、`:8718–8725` 的 `ie<=s&&!S`，及 `pretty/index-lp9XA374.js.txt:8733` 的 `setTimeout(...,i)`。64 字节示例为一包、32 字节示例为三包，回复等待预算分别最多约 2 秒/6 秒，**不含可能挂起的 sendReport 及调度时间，不能当成硬墙钟保证**。顶层还有 60 秒电量轮询（`pretty/index-lp9XA374.js.txt:11239–11243`：`setInterval(()=>void Ge(),6e4)`），不是本次查询的重试次数。

### 2.4 是否纯查询；哪些命令绝不发送

就**所给主机代码调用链**而言，容园 `82 / 8F` 是 `F.QUERY`，华奋达 `10` 只构造读取请求，没有先调用配置写入。依据 `pretty/index-lp9XA374.js.txt:3608–3618` 的 `type:F.QUERY,waitForResponse:!0`，以及 `pretty/index-lp9XA374.js.txt:8740` 的 `Uj=async e=>...GET_DEVICE_INFO,contentSize:56`。Feature 查询用 `sendFeatureReport` 提交请求，不等于写键盘配置；反之名为 QUERY 也不是所有固件无副作用的形式证明。

- **容园直连**：`getBatteryInfo()` 自身不先调用 `8F`、握手或切模式；但是 HUB 的连接工作流会先 `checkDevice()` 读 `8F`、匹配型号，再初始化。依据 `pretty/index-lp9XA374.js.txt:5641–5658`：`this.deviceInfoProtocol.getHidDeviceInfo()`；屏幕型号还可读 `AD`（`pretty/index-lp9XA374.js.txt:5671`：`isSupportOLED===!0&&await ...getOLEDInfo()`）。这不是已证明设备要求的握手，不能把完整 HUB 初始化照搬成电量前置步骤。
- **华奋达**：连接时和电量刷新时都读 `10`；`initDeviceData()` 为空，见 `pretty/index-lp9XA374.js.txt:8658–8668` 的 `n=await t.getDeviceInfo()`、`async initDeviceData(){}`。虽然 `COMMUNICATION_START:1 / COMMUNICATION_END:2` 出现在 `pretty/index-lp9XA374.js.txt:8740` 枚举中，所跟踪 `Uj -> Nj -> Pj` 没有调用它们，不能凭名称加发握手。某些接收器名触发的 full ID 读取还是同一 `10`，不是切模式。
- **容园接收器**：需要设置路由目标和状态机操作，见 2.5；不能声称完整接收器流程只有两个纯读 opcode。路由锁定是否持久化及设备端副作用未确认。

以下是明确的**不发送清单**；没有列到的命令也不自动获准。未来最小验证只能白名单采用已经确认路径所需的查询，不能以 `GET` 名称、最高位为 1、`F.QUERY` 或“会恢复原值”为理由放行其他操作。

| 协议/类别 | 已定位的状态改变入口；全部排除 | 依据与短引文 |
| --- | --- | --- |
| 容园 v1 设置/恢复 | `01 FEA_CMD_SET_RESERT`（原文拼写）；`11 SET_SLEEPTIME`；`09 SET_KBOPTION`；`06 SET_DEBOUNCE` | `pretty/index-lp9XA374.js.txt:4095–4098`：`FEA_CMD_SET_RESERT=1`；恢复入口为 `type:F.SET` |
| 容园共用灯光/档位 | `07 SET_LEDPARM`、`08 SET_SLEDPARM`、`0C SAVE_CUSTOM_LAYER`；`04 SET_PROFILE`；`03 SET_REPORT_RATE` | `pretty/index-lp9XA374.js.txt:3173`、`:3276`、`:3634`：`SET_PROFILE=4` 等 |
| 容园键映射/宏/Fn | `0A SET_KEYMATRIX`、`8B SET_KEYMAPPING`、`10 SET_FN`；`0B SET_MACRO`、`0C DELETE_MACRO`、`0D PLAY_MACRO`；宏、轴体、RT 和高级键的所有写入/重置入口 | `pretty/index-lp9XA374.js.txt:1539`：`SET_KEYMAPPING=139`、`PLAY_MACRO=13`；说明高位为 1 不必然只读，重叠 opcode 也不能跨子协议解释 |
| 容园屏幕/Flash | `25 SET_TFTLCDDATA`、`27 SET_OLEDLANGUAGE`、`28 SET_OLEDCLOCK`、`2A SET_OLEDWEATHER`、`2B SET_OLEDEFFECT`、`2C SET_FLASHCHIPERASE`；所有屏幕上传、擦除和时间同步 | `pretty/index-lp9XA374.js.txt:1539`：`SET_FLASHCHIPERASE=44`；`:4035`：`清屏命令已被设备收下，等擦 flash 完成通知` |
| 容园 V2 的差异 | 恢复 `02`，选项 `06`，防抖 `11`，睡眠 `12`，键/Fn 写命令另有分派；不得拿 v1 opcode 解释 v2 | `pretty/index-lp9XA374.js.txt:4147–4158`：`Yy=6,Xy=2,Zy=17,...$y=18`；`:4160–4170` 的设置实现 |
| 容园测试与代理 | 行程测试开/关 `1B`；接收器选目标 `F6`、取/丢缓存回复 `FC` 等均不属于 INFI75 最小查询 | `pretty/index-lp9XA374.js.txt:5606`：`Y.buildPacket([27,e?1:0])`；`:4390–4393`：`discardPendingResponse()`、`rawSend([246,this.opts.target])` |
| 华奋达配置写入 | `21 SET_GAME_MODE`、`22 SET_KEY`、`23 SET_LED_EFFECT`、`24 SET_CUSTOM_LED_DATA`、`25 SET_MACRO`、`26 SET_FN_KEY`、`27 SET_MAGNETIC_AXIS_RT`、`28 SET_MAGNETIC_AXIS_DKS_DATA`、`2A SET_DOT_MATRIX_MODE`、`2B SET_LIGHT_BOX`、`2D SET_SIDE_LIGHT` | `pretty/index-lp9XA374.js.txt:8740`：`SET_GAME_MODE:33,SET_KEY:34,...`；不能作为读取电量的初始化 |
| 华奋达其他状态写入 | `30 / 31 SET_KEYBOARD_CUSTOM_FUNCTION_ON/OFF`、`34 SET_TEMPORARY_COMMAND_DATA`、`35 SET_MUSIC_DATA`、`36 CLEAR_LED_DATA`、`3C SET_MUSIC_DATA_24G_64_BYTE`、`40 SET_LED_BOOT_ANIMATION`、`41 SET_LED_USER_ANIMATION`、`42 SET_LED_DATA`、`4F SET_FLASH_DOWNLOAD`、`50 SET_TFT_USER_ANIMATION`、`51 SET_TFT_BUILT_IN_INDEX` | 同 `pretty/index-lp9XA374.js.txt:8740`：`CLEAR_LED_DATA:54`、`SET_FLASH_DOWNLOAD:79` 等 |
| 华奋达校准/恢复/会话 | `64 / 65 SET_CALIBRATION_ON/OFF`、`66 / 67 SET_SIMULATION_TEST_ON/OFF`、`69 / 6A SET_CALIBRATION_ON/OFF_V2`；`0F SET_FACTORY_RESET`，含子值 `05 CLEAR_CALIBRATION / FF RESET_ALL`；`01 / 02 COMMUNICATION_START/END` 不自行添加 | `pretty/index-lp9XA374.js.txt:8740`：`Hj={CLEAR_CALIBRATION:5,RESET_ALL:255}`；`:8790–8793`：`Pj(Vj.SET_FACTORY_RESET,n,...)` |
| 其他族 Bootloader / 恢复 | QMK `0B BOOTLOADER_JUMP`、`0A EEPROM_RESET`、所有 SET/RESET/SAVE；RLW `enterBootloader`、`factoryReset`、`resetKeyboard` 和所有设置；鼠标的 `7F ENTER_BOOTLOADER` 也绝不移植到键盘 | `pretty/index-lp9XA374.js.txt:10344`：`BOOTLOADER_JUMP=11`；`pretty/hid-manager.min-DgAd_HFq.js.txt:242–247`：`e[0]=u`，其中 `u=11` 见该文件 `:156`；鼠标入口见 `pretty/index-lp9XA374.js.txt:5892` 的 `[tE.ENTER_BOOTLOADER,85,170,85,170]` |

**清本地缓存不是设备命令**：`pretty/index-lp9XA374.js.txt:5664` 的 `clearAllCaches(){...clearCache?.()...clearProfileCache?.()}` 只清协议对象缓存；`pretty/index-lp9XA374.js.txt:3163` 的 `clearQueue()` 清内存队列。不能凭方法名发明一个“清设备缓存” opcode。本次也不调用这些页面/缓存操作。`FC` 消费接收器残留回复则是实际线上操作，须与本地清缓存分开。

### 2.5 接收器：full ID 与容园代理是两回事

#### NEED_FULL_DEVICE_IDS 的真正作用

`pretty/index-lp9XA374.js.txt:9079–9085` 的 `generateDeviceId` 先拼 `VID:PID:productName`，命中 `needsFullDeviceId` 后打开设备、调用 `Uj(e)`，再拼 `VID:PID:productName:manufacturer:product`；原先未打开者读完会关闭。失败回退基础 ID。它是**华奋达 SDK 型号身份补充**，不是容园电量的接收器套包，也不是描述符证明。

名单是完整字符串匹配，见 `pretty/index-lp9XA374.js.txt:9080`：`.includes(t)`。`pretty/index-lp9XA374.js.txt:9164` 确有 `1452:591:2.4G Dongle`、`1452:591:2.4G Wireless Receiver`、`3141:65278:2.4G Dongle` 等；还含 `05AC:0250` 与 `0C45:FEFC / FEFA / FEF9` 的若干名字。**出现在这个名单不等于注册了该型号/传输**；FEFC、FEF9 没有出现在 2.1 的本批华奋达型号表里。

华奋达已注册的 TH65 接收器路径是 `0C45:FEFA / FF60:0061 / Output ID 0 + Input`，仍走 `Uj -> Nj` 的 `AA 10 ... -> 55 10 ...`，没有在此链上加 `F6/F7/FC`。依据为 `pretty/index-lp9XA374.js.txt:9263` 的 `` connectionMode:`receiver` ``、`usagePage:65376,usage:97`，及 `pretty/index-lp9XA374.js.txt:9111` 的 `getDeviceInfo(){...Uj(this.device)}`。其 Input ID/精确长度仍未由这些配置证实。

#### 容园代理的已见格式

“协议外套接收器代理”的日志描述出现在**鼠标**注册表：`pretty/index-lp9XA374.js.txt:6391` 的 `Click Lite ... 容园 v1 协议外套接收器代理`。容园键盘也使用共用 `Hb`，但目标不同：`pretty/index-lp9XA374.js.txt:5745` 的 `target:10,offlineByteIndex:3` 是键盘目标 `0A`；鼠标策略在 `pretty/index-lp9XA374.js.txt:5801` 为 `target:5,offlineByteIndex:4`。不能把那句鼠标日志当作 INFI75 接收器证据。

已见键盘模式是一个**前后包状态机**，不是在 `82` 前添一个固定外层头：

1. Feature ID `0`、64 数据字节的 `F6 0A [00×62]` 锁目标；`F7 [00×63]` 查询通道状态。
2. 状态就绪且键盘在线才原样发送完整业务包，例如 2.2 的容园 `82`。
3. 继续发 `F7`，等 `status[0]==1 && status[5]==1`，再发 `FC [00×63]`，随后 Get Feature 取业务回复；旧残留也可能先经 `FC` 丢弃。

依据：`pretty/index-lp9XA374.js.txt:4393` 的 `rawSend([246,this.opts.target])`；`:4368–4380` 的 `rawSend(e)`、`rawSend([252])`；`:4396` 的 `transport.send(Fb([247]))`。代理控制帧由 `pretty/index-lp9XA374.js.txt:4325–4330` 的 `Fb` 仅补零到 64，**不加 Y 的反码校验**；底层 Feature 发送/接收见 `pretty/index-lp9XA374.js.txt:5745` 的 `super.sendFeatureReport` / `receiveFeatureReport`。电量业务包仍保留自己的 `7D`。

`F7` 状态被解释为 byte1 键盘电量、byte2 鼠标电量、byte3 键盘离线、byte4 鼠标离线、byte5 sendDone，见 `pretty/index-lp9XA374.js.txt:4356` 的 `byte1_kbBattery:e[1]` 等；至少 6 字节、非全零、byte3/4/5 为 0/1、两个电量不大于100 的形状判定在 `pretty/index-lp9XA374.js.txt:4333–4335`。`pretty/index-lp9XA374.js.txt:1377–1380` 的 `t>0&&(W.keyboardBattery=t)` 表明零值不更新键盘电量。这个状态电量不是 `82` 的业务回显，不能混作其回复；状态机甚至允许连续非法帧后的降级处理（`:4397`），不是严密的协议认证。

已见代理等待参数：通道 ready 5000 ms × 最多 2 轮；data ready 1200 ms，业务最多额外重发 1 次；首次等100 ms、轮询间隔通常100 ms；发 FC 前等10 ms、后等30 ms；状态回声最多重取2次。依据 `pretty/index-lp9XA374.js.txt:4322–4324` 的 `bb=2`、`wb=1200,...Db=1,Ob=5e3,...Mb=2,Nb=10,Pb=30` 及 `:4363–4385` 的对应循环。底层 Feature 仍可能挂起，不提供硬截止。本次不建议实施这套状态机。

容园键盘表中派生接收器是 `3151:5038 / FFFF:0002 / reportId 0`，对应 HE75 V2 `3518`、G84 HE JIS `3703`（`pretty/index-lp9XA374.js.txt:5770` 的两个 `wirelessForm:{vendorId:12625,productId:20536,...}`）。接收器辅助枚举 `Ub` 又会对同 VID/PID 接口排序并发 F7 探针，不严格锁定一个集合：`pretty/index-lp9XA374.js.txt:4416–4430` 的 `filter(t=>t.vendorId===e.vendorId&&t.productId===e.productId)`、`e.sendFeatureReport(0,r)`。这是官方现有选择风险，不是本项目可照搬的许可。

#### `2.4G Wireless Keyboard / FFB5:0001` 的结论

所给全部脚本/pretty 中检索 `FFB5|65461` 无匹配；`2.4G Wireless Keyboard` 仅出现在 `pretty/index-lp9XA374.js.txt:5770` 的 **`3151:5038` 型号备注**，不是 `05AC:024F` 产品名路径。华奋达过滤的 expected 集合/允许页也不含 FFB5（2.1 所引 `:761`、`:9164`）。

历史枚举 `.ai/evidence/infi75-driver-1.2/local-enumeration-20261111.txt:7–10` 记录的是 `05AC:024F / MI_02 col01 / FFB5:0001`、Input/Output 各8、Feature0，且归属 INFI75 本就标为 unconfirmed。它既不是华奋达已登记集合，也不满足容园64字节 Feature；**没有可确认的 2.4G 电量交换，全部请求次数为0**。不能因名称相似、同 VID/PID 或无线模式而切换到 F6/F7/FC，也不向8字节 Output 硬塞/截短 `AA 10`。

### 2.6 协议族：四族，容园分支后是五条键盘路径

初始化注册分组在 `pretty/index-lp9XA374.js.txt:11038`：`cT.registerAll()`（Vendor A键盘）、`cN.registerAll()`（华奋达）、`QP.registerAll()`（RLW）、`rB.registerAll()`（QMK）。Vendor A 再由 `` keyMappingConfig.keyboardProtocol===`v2` `` 选择 YC500 命令层，但共用设备信息/电量对象：`pretty/index-lp9XA374.js.txt:5618–5622` 的 `this.deviceInfoProtocol=new xy(e)`。

| 键盘路径 | 本批已登记 VID:PID 与集合 | 读取电量的实际入口与依据 |
| --- | --- | --- |
| 容园 v1 / Vendor A（含 FEA_CMD_* 设置） | `3151:5002 / 5030 / 5054 / 502F / 5029 / 502D / 5056`；`39AB:9016`；派生接收器 `3151:5038`。均 `FFFF:0002`，配置 reportId0 | 共用 `xy.getBatteryInfo`：`82`，64字节 Feature；见 `pretty/index-lp9XA374.js.txt:3606–3617` 的 `GET_BATTERY=130`。ID依据 `pretty/index-lp9XA374.js.txt:5770`，如 `vendorId:12625,productId:20482` |
| 容园 YC500 V2 | `3151:4015` 的 HE75 Mag / Phantom 系列 / Breeze75；以及 `3151:5009` Cybrix29。均 `FFFF:0002`，配置 reportId0 | 仍共用同一个 `82 / xy`，不是另一种电量 opcode；`pretty/index-lp9XA374.js.txt:5534` 的 `_w / vw / Ew` 设置 `` keyboardProtocol=`v2` ``，`:5619–5622` 分派确认。Cybrix29 的表面 `protocolVersion=1.0.0` 不足以判为v1 |
| 华奋达 | 2.1 全表（含相邻 `05AC:0250`）；通常 `FF68:0061`，TH65接收器 `0C45:FEFA / FF60:0061` | `10 GET_DEVICE_INFO`，Output0/Input；`C[17]` 电量；`pretty/index-lp9XA374.js.txt:8613–8619` 的 `t.getDeviceInfo()` 与 `:8741` 的 `batteryLevel:n[17]` |
| 睿联威 RLW SDK 1.1.6 | `2E3C:C365 / C366 / C367 / C368`，`FF1B:0091`，配置 reportId1 | HUB `getBatteryInfo` 未实现：`pretty/index-lp9XA374.js.txt:10254` 的 `deviceErrors.notImplemented`；注册见 `:10337` 的 `vendorId:11836,productId:50021` 等。SDK另有 Input1、byte0=01 的 deviceStatus 推送，电量在byte9、状态在byte8，但没有证实独立电量查询或HUB采用它：`pretty/hid-manager.min-DgAd_HFq.js.txt:6–9`、`:74–75` 的 `batteryStatus:e[8],batteryPercent:e[9]` |
| QMK/VIA | `FF60:0061`，配置 reportId0、数据32；VID/PID完整分组如下 | 未提供电量命令；`pretty/index-lp9XA374.js.txt:10384` 明说 `QMK 无电量读取命令`；不能把 QMK 与同集合的华奋达接收器合并 |

QMK 的 ID 依据为 `pretty/index-lp9XA374.js.txt:10918`，原文例 `` vendorId:14e3,productId:12605,name:`EPOMAKER Bamboo75` ``；`14e3` 是 JavaScript 十进制科学计数法 **14000=36B0**，不是十六进制 `14E3`。`pretty/index-lp9XA374.js.txt:10345` 的 `USAGE_PAGE:65376,USAGE:97,REPORT_SIZE:32,...REPORT_ID:0` 给出集合/报告配置。按VID分组，PID均为十六进制：

- `36B0`：`313D,31A8,3066,3055,30DF,30D4,3120,304A,3075,30C4,3128,30AA,30AF,30A3,30B9,30B2,3048,31AC,306A,3101,3081`。
- `342D`：`E489,E491,E4D2,E506,E4C6,E4FE,E473,E472`。
- `5343:0080`、`28E9:3021`。

这里不把鼠标 Nex、YXT、容园鼠标 v1/v2 或耳机/音频 OuYu/GX2 计入键盘族。特别是 `pretty/index-lp9XA374.js.txt:6267` 的“容园 v1 协议（FEA_CMD_* / Click Lite / Click Plus）”位于**鼠标**构造分支；不是 `05AC:024F` 的归类证据。OuYu 导入见 `pretty/index-lp9XA374.js.txt:11039–11041`，其实现 `pretty/OuYuDevice-lh0GwVzR.js.txt:100–117` 是 `readEq / writeEq / writeLighting`，不是遗漏的一条键盘电量路径。

因此 `05AC:024F + FF68:0061` 在这批 HUB 中走**华奋达**；“容园 v1 / YC500”的字样不能改变 `:9265` 的实际注册协议类。

### 2.7 对 INFI75 的适用性

**不能认定有线 INFI75 会应答容园 `GET_BATTERY=82`；也尚不能认定它会正确应答华奋达 `10`。**

| 支持进一步研究的依据 | 不支持直接认定的依据 |
| --- | --- |
| 历史枚举确认有线 `05AC:024F / INFI75-1.1 / MI_02 / FF68:0061`，与华奋达顶层筛选相符；见 local-enumeration `:2–5` | HUB该 ID 注册的是 TH108/TH87，未见 INFI75；`pretty/index-lp9XA374.js.txt:9263` 的 `productNum:7 / 2 / 5 / 12` 不能当作 INFI75 的 product |
| 给定 SOURCE.txt 记录官网 INFI75 页面引向 HUB | 页面链接不证明任意固件、任意有线集合或2.4G接收器都能使用它 |
| 华奋达 `10` 内容确含型号与电量；`pretty/index-lp9XA374.js.txt:8741` 的 `product:n[14]\|n[15]<<8`、`batteryLevel:n[17]` 建立了候选链 | 有线 FF68 的逐报告 ID、长度、字段布局及 `10` 真回复均未提供；FF13的Feature65属于另一个集合，不能移用 |
| 电量方法不要求主机先配置写入；`pretty/index-lp9XA374.js.txt:8613–8619` 的调用只是读设备信息 | 没有固件实现/抓包证明只读语义，也不知道 INFI75 的 product/版本/电量计能力；型号未列出时，HUB自有 `:8670` 也拒绝连接 |

维护者自己的单次只读验证前至少还缺：目标物理身份/唯一集合的重新确认；该集合完整 Input/Output 逐报告描述符；据此确定实际 N 与 Input ID/长度；明确的一次性验证授权、可中止的超时方案和无其他驱动竞争条件。验证应采集原始回复、product/manufacturer、版本与原始电量/状态，不先设置模式、刷固件或使用容园命令兜底。

历史静听25秒没有Input（local-enumeration `:11`）不等于设备不会应答主动 `10`；带/无电量计升级程序仅有文件名记录（`:14–15`），不能判定当前固件，也不能指定无电量计回复值。`engine/compatibility.json:102–115` 的 `infi75_observed` 仍为D级、`compatible=false`，本次未改。

## 3. 建议的最小只读交换（条件规格，不是本轮执行授权）

### 3.1 目前默认：零协议打开、零发送

现有材料尚不能给 INFI75 一份所有长度/报告ID均已确认的可执行规格。下面把**候选交换**与必须先补的证据分开；缺条件就拒绝，不枚举试发，不让失败触发另一协议、另一集合或无线代理。

| 项目 | 维护者有线 INFI75 候选 | 已记录2.4G接收器 |
| --- | --- | --- |
| 精确身份 | `05AC:024F / SONiX / INFI75-1.1`，绑定维护者选定物理父设备 | `05AC:024F / 2.4G Wireless Keyboard`，归属本就未确认 |
| 接口/集合 | 仅 `MI_02 / FF68:0061`；不是FF13或FFFF | `MI_02 col01 / FFB5:0001`，不匹配已见协议路径 |
| 协议 | 华奋达 `GET_DEVICE_INFO=10`，不是容园82/8F | 未确认；不发送 |
| 输出类型/ID | Output、ID `0`；目标是否声明它尚待描述符证明 | 已知8字节Output不足以建立任何对应帧；ID未确认 |
| 输出长度/请求 | N未确认。仅当证明N=64，采用2.2B完整64字节一包；仅当证明N=32，采用完整三包。其他N需按同一公式重新审查，不能选择fallback32 | 无已证实请求；次数0 |
| 输入类型/ID/长度 | Input；**ID、精确长度均未确认**，必须在设备协议句柄打开前补证。不能仅凭 `reportId:0` 配置推定输入也是0 | Input8，但ID/协议未确认；不监听后试发 |
| 期望回复 | 同设备、同集合、已证实Input ID/长度；头 `55 10`。按请求地址关联、验证内容覆盖，按顺序去8字节头拼成恰好56有效内容字节；不补零、不接受截断/旧包/其他cmd。源码没有强制地址/长度校验，是否回显地址仍须实测 | 无；不套F6/F7/FC，不截短华奋达帧 |
| 解析 | `product=C[14]\|C[15]<<8`；`workMode=C[16]`；`battery=C[17]`；原始charge=C[18]。product须记录，未知型号不进入常规读取器；有效百分比建议仅1..100，0/越界/短包作无读数；充电含义未确认 | 无 |
| 前置命令 | 无已证明的握手、切模式或设置要求。只提交这个设备信息读取，不做HUB全量初始化/full ID重复读取 | 无 |
| 超时/重试建议 | 单次人工验证每包最多等500ms、**额外重发0次**；一包/三包分别最多500/1500ms回复等待预算；需要独立、可中止的I/O硬截止，不能把SDK的定时器当成sendReport取消保证。此为收紧方案，不是声称厂商默认无重发 | 查询0，重试0 |
| 当前可执行状态 | 缺描述符和Input规格，**未获准执行，当前发送0次** | 证据不够，**发送0次** |

上述字节、超时默认与解析依据分别为 `pretty/index-lp9XA374.js.txt:8707–8741`；短原文 `sendReport(jj,x)`、`timeout:o=500`、`batteryLevel:n[17]`。与原SDK不同的严格验收、额外重发0次和硬截止要求是**建议**，不是对未见硬件行为的补全。

已登记华奋达 TH65 接收器虽可归类为 `0C45:FEFA / FF60:0061 / Output0 + Input / 10`（`pretty/index-lp9XA374.js.txt:9263`），也没有其目标描述符和完整Input规格，故这里只说明路径，**不给可执行2.4G规格，更不外推至维护者的FFB5接收器**。

为避免把容园完整请求误用到 INFI75，容园直连的参考规格单列：仅对已登记、productNum已知的 `FFFF:0002` 容园型号，在 Feature ID0、64数据字节由描述符证明后，才有 `Set Feature(2.2A的82帧) -> Get Feature(0)` 的候选；预期 `B[0]==82`、长度至少3、`B[1]` 1..100、`B[2]` 0..2，回复校验规则/完整长度仍未确认。独立读取无需先切模式；建议一次查询、不重发、独立硬截止不超过3秒，而源码的 `timeout:3e3` 本身未落实该保证。**这不是 INFI75 或通用 `05AC:024F / 0C45:*` 的方案。** 原文依据仍为 `pretty/index-lp9XA374.js.txt:3606–3618`、`:520–542`。

### 3.2 打开允许协议 I/O 的句柄前必须证明

以下是项目安全准入建议，体例参考 `engine/battery_hid.py:338–358` 的逐报告证明，不是声称HUB已做这些检查。读取系统元数据如需临时句柄，应与允许协议I/O的工作句柄区分，不能以发查询代替证明。

1. 从本轮运行时枚举唯一锁定完整设备路径、物理父设备、VID/PID、精确产品名和接口/集合；存在同ID多个键盘、重复集合或归属不明就停止。
2. 描述符证明**本集合**的报告方向、逐报告ID、位宽/计数与实际数据字节长。华奋达必须证明Output0和实际Input通道，且完整报告是字节数据；不能只信SDK的第一个item.reportCount或32回退值。不能拿集合最大长度证明某个ID存在。
3. 无编号ID0须证明该方向没有其他编号报告，并按所用本地API口径核对ID槽；WebHID发送参数0与数据分离，不擅自把额外00塞进D。若按本项目含ID槽的Windows长度口径，32/64数据分别需对应33/65的输出缓冲长度；这些是待核条件，不是已有INFI75报告事实。
4. 容园Feature参考路径另需证明本集合Feature0确有64数据字节；不能用有线FF13的历史65字节Feature为FF68、FFB5或接收器作证明。
5. caps与原始描述符一致，读取失败/截断/多个合格候选均拒绝；不运行HUB同VID/PID多接口F7探针，不打开普通键盘集合补偿失败。
6. 常规读取器还需已验证的协议族、productNum/版本和无副作用/无读数规则。一次性维护者验证可以在另行明确授权、精确设备绑定且上述描述符完整后只采集候选 `10` 的结果，但不能自动转化为批量共享ID支持；失败或未知product只保留证据，不扩展命令集。

## 4. 同共享 ID 下的型号区分与必须拒绝的情况

`pretty/index-lp9XA374.js.txt:8669–8670` 的 `productNum 不匹配任何注册型号，拒绝连接` 与容园 `pretty/index-lp9XA374.js.txt:5665–5670` 的 `productNum 精确命中` 表明两族都不是“同 VID/PID 就同型号”。但是官方在读product之前已打开并发送查询，因此它不能自动解决“第一次打开前如何防误发”的问题。

建议采用两层边界：

- **查询前身份边界**：明确物理设备选择 + 精确产品名/接口/集合 + 逐报告证明 + 已确认协议来源。`05AC:024F`、`0C45:*`、`FF68`、`SONiX`、`2.4G`字样任何一项单独均不够。未知型号不靠扫opcode识别。
- **查询后型号边界**：华奋达以 `(VID,PID,product16)`，容园以 `(VID,PID,deviceID32)` 精确允许；同时保存manufacturer、版本、原始回复和取样时间。USB名字用于绑定，不代替productNum；full ID里的manufacturer也不能替代最终已验证型号。

必须拒绝协议打开/发送的情况包括：身份非唯一或物理父设备不明；产品名不属于明确目标；只有共享ID没有协议族证据；集合不同；逐报告ID/长度缺证或矛盾；只有FF13 Feature65而无FF68证明；只有FFB5的8/8/0；未知product/版本试图走常规轮询；设备休眠/离线时要求盲重发；要求切模式、恢复、写回或刷固件才能继续。不得自动在华奋达、容园v1/v2、QMK间切换，同集合也不是协议族身份证明。

## 5. 风险与未确认项清单

| 风险/差异 | 处理结论 |
| --- | --- |
| **简报背景误把FF68表、full ID与容园82关联** | 实际注册是华奋达，读取10；82/8F、64字节Feature和代理状态机均属于另一链。本报告纠正背景继续分析，没有突破边界 |
| **INFI75不在表、型号/固件未知** | 不能认定它应答82；10也仅候选。缺product/manufacturer/版本、带/无电量计固件对照与真实回复；保持未支持状态 |
| **有线FF68报告描述符缺失** | Output N、Input ID/长度、原始字节布局待证；不能把32默认值或FF13长度冒充它的规格 |
| **维护者接收器路径不匹配** | FFB5:0001与8/8/0不符合两链；接收器归属亦未确认；无法给出有证据的2.4G最小交换 |
| **只读语义的证据层级** | 主机调用链没有配置写入，不等于固件无副作用形式证明；不发送任何SET/恢复/Bootloader/校准/测试/代理路由操作来“准备查询” |
| **容园解析宽松** | 回调本身无头/长度/校验严格验证，success可带无读数；护栏对空包不足，2的配置实际不是额外2次；回复精确长度与校验未确认 |
| **华奋达重组/关联宽松** | Input未筛reportId，设备信息读未校验addr，按整包slice(8)拼接而非严格按返回len截取；短包和非预期长度可错位。示例请求确定不等于回复格式已经实测 |
| **DataView边界** | 容园 `:532–533`、华奋达 `:8734` 用 `new Uint8Array(view.buffer)`，未使用byteOffset/byteLength；需要真实捕获核对，不默认底层buffer恰与view等长 |
| **百分比/充电语义未闭合** | 容园0不是有效百分比；0状态只是不进入1/2分支，2仅对应闪电加电池图标，未证实“充满”；华奋达未限制>100、chargeStatus无语义映射；trusted是软件策略，不证明电量计能力 |
| **缓存与默认100误判** | 华奋达可失败回退缓存；容园全局复位100；状态机电量也可保留旧值。必须区分取样时间/本轮成功，不能把UI显示当新读数 |
| **超时不等于硬截止** | 容园3秒仅队列字段；华奋达500ms仅回复计时器，发送调用仍可能挂起；额外重试/轮询有多层，未来验证需有可中止的总预算 |
| **接收器代理不只是查询** | F6设路由、FC消费/丢残留；同VID/PID多接口探针可能误选。键盘0A与鼠标05不能混用；不外推至05AC接收器 |
| **协议版本字符串不可靠作族判定** | Cybrix29表标1.0.0而配置用v2；“FEA_CMD”日志还有鼠标上下文；以注册类和实际调用链为准 |
| **静态文本与覆盖边界** | pretty可能在字符串中断行；关键构包已回assets核对，但没有执行/捕获；未提供和未抓取模块、后续站点变化不在负面结论覆盖范围 |
| **本轮做不到的验收** | 无硬件应答、充电对照、休眠/唤醒、无线或不同固件实测；没有可执行的INFI75完整Input规格。不通过猜字节/联网/运行脚本绕过，明确保留未确认 |

## 6. 证据来源与复核

- 官方入口：`https://hub.epomaker.com`；来源页：`https://epomaker.com/blogs/software/infiverse-infi75-driver`，给定 `SOURCE.txt` 记录其链接文案为“Configure your Infiverse INFI75”。脚本取回日期 **2026-11-12**；本轮没有访问这些网址。
- 证据根：`.ai/evidence/epomakerhub/20261112/`。以下SHA256逐字抄自该目录 `SHA256SUMS.txt`，本轮另对主脚本、RLW SDK和OuYu实现重新计算，三者与清单一致。哈希约束的是原始assets，**不是pretty副本**。

| 用到的原始文件 | SHA256 | 用途 |
| --- | --- | --- |
| `assets/index-lp9XA374.js` | `9129c5b41abfebecdb25780ac13f0d62b0b3ee176b2ef1da3724245e66a196f6` | 主注册表、筛选、两条电量链、封包/重试/代理、UI语义；定位副本 `pretty/index-lp9XA374.js.txt` |
| `assets/hid-manager.min-DgAd_HFq.js` | `06b1d4d937778ab392b0054d33abca4dc86fd5c21e19c072fe8c2bd27a96ab29` | RLW传输、状态推送与危险入口；定位副本 `pretty/hid-manager.min-DgAd_HFq.js.txt` |
| `assets/OuYuDevice-lh0GwVzR.js` | `70bcfa371a0c81d9716b2f9ee1915d62ce9efbbf81c7bde37b04ed67fb03b909` | 核对动态注册的OuYu为EQ/灯光而非键盘电量；对应pretty同名加.txt |
| `assets/OuYuDevice-BhtTe3NS.js` | `31fb6d7f31f4a28c3b4389d1e74631cb6acaa566a7bd1b30e50643089f2ea544` | 只核对导出 `OuYuDeviceRegistrar` 指向上一个实现；没有对应pretty，不用它证明协议字节 |

负面名字/FFB5检索覆盖清单内全部14个原始JS（及已提供pretty），除以上四个之外还包括：

| 文件（均在assets/） | SHA256 |
| --- | --- |
| `AboutView-C72Zlqg3.js` | `a1d2a3e1fbd206587951af3a681bbbc1aaef414076390f525d0323f55eeab0b2` |
| `DeviceManager-BPKdDh5s.js` | `39befcba880513d2c992aa33334c676d00c16eec3962f930ae002b56bc563154` |
| `HomeView-PnNzOLCL.js` | `41b7479c08e5c77187994ddf14c7606959250bd0455ab88f2f7312a89f5a5a58` |
| `NoDeviceView-BZ0wFiva.js` | `892570811efe488c48255101b20fdf482875a6ca3547acb8d024d8cf1f7f0bd3` |
| `SendHIDView-BqAZ8Pac.js` | `f6f86b0921501ef2f3dda4cd231bc3d55d0a2056786dc5266599a63e71fb9a5e` |
| `SleepTimeView-BwY8VAiN.js` | `f265b9073a49d5a564bca6af17487a739c9b43d428f8795419092ee31c87e1ba` |
| `VendorAMouseDevice-BCDMwt68.js` | `943d3b25d269b769522eb65a84faf35aac27cb2042e4a5261797fa08e928f31b` |
| `gx2-model-Bip1SA2X.js` | `61daeb119b9194ce11dde6f292a007e4b9ad6a2bfb619f9a711087a60734101c` |
| `useReceiver-n9tiWpJm.js` | `5a7f867efda7d221ce1d7ffc3a6d2f1b50b8faade75733cd7d4b94331a6ce0e5` |
| `yxt-mouse-sdk-Q4YuY2ji.js` | `6f884c96663d4b58b64e93933d7e46f41eea89d4947462b5fd34275102b10e6f` |

其他给定证据/体例参考：

- `.ai/evidence/epomakerhub/20261112/{SOURCE.txt,SHA256SUMS.txt,pretty/README.txt}`：来源、摘要清单及机械换行限制。
- `.ai/evidence/infi75-driver-1.2/local-enumeration-20261111.txt:1–15`：2026-11-11只读枚举与2026-10-18历史长度；不是本轮运行。该文件不在HUB摘要清单内，没有冒填SHA256。
- `docs/infi75-driver.md`：桌面驱动静态分析的既有结论；没有把其中配置帧当HUB电量请求。
- `engine/compatibility.json:102–115`：`infi75_observed`及不支持边界。
- `engine/battery_hid.py:338–358`、`engine/topology.py`、`engine/upstream-protocols.md`：仅参考描述符证明和报告体例，未借用其中其他品牌命令。

本轮核对方式限PowerShell文本读取/分段筛选、`rg`检索、摘要计算及Git差异检查；预先按项目tag查询记忆只作边界背景，不作为协议事实来源。没有运行Python/Node/厂商脚本、联网、接触HID、修改代码或提交Git。
