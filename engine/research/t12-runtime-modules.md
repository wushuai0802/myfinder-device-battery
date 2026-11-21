# T12 内置 Python 运行时与签名方案调查

基线 `b70b621921c11ac65172e3d63fa50d64b2153b9d`。以 beta.21 的 CI 发行 ZIP 为样本，文件 SHA-256 为 `748f5da507da4b83382489c38e6a0e4e62ef8f4f3a77c1a9ded7b5b5d98afb13`。对 `engine/runtime/` 的 47 个文件逐项比较，测试所用 CPython 3.14.8 x64 副本与 ZIP 内容一致。CPython 源、hidapi wheel 与包摘要由依赖锁定。[运行时锁定文件](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/dependencies.lock.json)

## 观察方法

静态检查引擎及读取器的 import 入口，对包内 PE 读取普通和延迟导入表。用同版 Python 在 Windows 下做一次 `-I -B -X importtime` 加载记录，`unittest discover -s ./engine/tests -q` 共 1135 项通过。用测试退出时的 `sys.modules` 文件来源核对，实际在测试进程加载 12 个 `.pyd`。测试加载了 unittest/mock 的依赖，分析时将其与引擎源码入口区分。DLL 列按 PE 依赖判断，测试没有逐个记录进程 DLL 加载事件。真实设备、外部组件和运行中动态加载不在此次覆盖内。

已核查的引擎入口包括 ctypes 与 hidapi、身份哈希、工作进程队列。压缩模块和 asyncio 的加载路径经 `shutil` 与 `unittest.mock`。未见引擎直接导入这些路径。[HID 入口](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/battery_hid.py#L7-L25) · [哈希](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/server.py#L7-L21) · [隔离队列](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/engine/isolation.py#L7-L15)

## 扩展模块（23 个）

| `.pyd` | 测试已载入 | 引擎证据 | 评估移除影响 |
| --- | --- | --- | --- |
| `_asyncio.pyd` | 是 | 测试间接，经 `unittest.mock` 的 asyncio | 影响需要原生事件循环加速的 Python 调用方 |
| `_bz2.pyd` | 是 | 测试间接，由 `shutil` 导入 bz2 | 没有原生 bzip2 压缩支持 |
| `_ctypes.pyd` | 是 | 引擎使用，`battery_hid.py`、`topology.py`、`standardhid.py` 等导入 ctypes | 破坏 Windows HID 说明符和其他 ctypes 调用 |
| `_decimal.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见 | 失去 decimal 的 C 加速器，部分环境可退回纯 Python |
| `_elementtree.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见 | 失去 ElementTree 的 C 加速器 |
| `_hashlib.pyd` | 是 | 引擎使用，`server.py` 和读器导入 hashlib | 改变 OpenSSL 哈希后端，不能假定所有输入与算法继续可用 |
| `_lzma.pyd` | 是 | 测试间接，由 `shutil` 导入 lzma | 缺少 xz/lzma 压缩支持 |
| `_multiprocessing.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见。工作子进程由 subprocess 管理 | 影响依赖 multiprocessing 的 Windows 进程协作 |
| `_overlapped.pyd` | 是 | 测试间接，由 `unittest.mock` 的 Windows asyncio 导入 | 影响 Windows asyncio 的异步 I/O 支持 |
| `_queue.pyd` | 是 | 引擎使用，`isolation.py` 与 `bluetooth.py` 导入 queue | 失去原生队列实现，需复核线程协作与可能的回退 |
| `_remote_debugging.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见 | 移除会使依赖该扩展的远程堆栈调试或采样器无法加载 |
| `_socket.pyd` | 是 | 测试间接，经 `unittest.mock` 的 asyncio。未发现引擎 socket 入口 | 破坏 Python 的套接字功能 |
| `_sqlite3.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见 | sqlite3 无法使用此数据库绑定 |
| `_ssl.pyd` | 是 | 测试间接，经 `unittest.mock` 的 asyncio。程序更新的 HTTPS 在 C# | 影响使用 Python TLS 的调用方 |
| `_uuid.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见 | 影响部分系统 UUID 实现，回退行为需核对 |
| `_wmi.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见。蓝牙走 Windows 属性查询 | 影响使用该 Windows WMI 扩展的 Python 调用方 |
| `_zoneinfo.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见 | 失去时区 C 加速器，依赖者需核对回退 |
| `_zstd.pyd` | 是 | 测试间接，经 `shutil` 的 `compression.zstd` | 缺少 Python 3.14 zstd 压缩支持 |
| `hid.cp314-win_amd64.pyd` | 是 | 引擎使用，电量读取器和 HID 枚举路径使用 hid | 去除会使通用 hidapi 读取路径不可用 |
| `pyexpat.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见 | 影响采用 Expat 的 XML 解析 |
| `select.pyd` | 是 | 测试间接，由 `unittest.mock` 的 asyncio/selectors 导入 | 影响 Python socket/select 与部分异步调用 |
| `unicodedata.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见 | 影响 Python Unicode 数据和规范化调用 |
| `winsound.pyd` | 本轮未观察到 | 引擎源码及这轮测试未见。桌面音量控制在 C# | 影响使用 winsound 的 Python 调用方 |

## 动态库（9 个）

| `.dll` | 是否有本包引擎依赖 | 评估移除影响 |
| --- | --- | --- |
| `libcrypto-3.dll` | 有。`_hashlib.pyd` 的直接 PE 依赖，`_ssl.pyd` 也引用 | 移除会影响 OpenSSL 哈希与 TLS 扩展加载 |
| `libffi-8.dll` | 有。`_ctypes.pyd` 的直接 PE 依赖 | 移除会破坏 ctypes 与 HID/系统 API 路径 |
| `libssl-3.dll` | 测试依赖。`_ssl.pyd` 引用，`_ssl` 在读取器测试因 asyncio 加载 | 移除影响 Python TLS，未确认引擎直接使用 |
| `libtommath.dll` | 未在所查 PE 普通/延迟导入表中发现消费者。动态加载情况不确定 | 缺少现有消费者证据，不能判定所有路径的移除影响 |
| `python3.dll` | 稳定 ABI 的入口。未见本包的扩展通过普通/延迟导入表直接引用 | 其他稳定 ABI 扩展可能需要它 |
| `python314.dll` | 有。两个启动入口及已列出的 CPython/第三方扩展引用 | 解释器不能按现有方式启动 |
| `sqlite3.dll` | `_sqlite3.pyd` 的直接依赖，本轮未观察到该扩展被加载 | 移除影响 SQLite 绑定 |
| `vcruntime140.dll` | 有。解释器、多个扩展及其他运行时 DLL 的直接依赖 | 影响解释器与原生扩展装载 |
| `vcruntime140_1.dll` | `_wmi.pyd` 的直接依赖，本轮未观察到该扩展被加载 | 至少影响依赖它的 WMI 扩展 |

## 调试模块

`_remote_debugging.pyd` 是 CPython 面向调试和堆栈采样的 Windows 扩展，构建单元来自 `Modules/_remote_debugging_module.c`。本次引擎源码和上述测试记录中均未发现依赖。移除会破坏依赖该扩展的工具，解释器核心的其他调试能力需独立评价。此次没有变更包，也没有在删减后的包上验证启动、罕见异常路径或可移植性。[CPython 构建文件](https://github.com/python/cpython/blob/v3.14.8/PCbuild/_remote_debugging.vcxproj#L94-L97) · [模块源码](https://github.com/python/cpython/blob/v3.14.8/Modules/_remote_debugging_module.c)

## Authenticode 代码签名方案

| 方案 | 条件 | 周期与费用 | CI 改动 |
| --- | --- | --- | --- |
| SignPath Foundation 开源申请 | 采用符合其条件的 OSI 开源许可证和组件许可，公开源码、可对应的构建配置、持续安全维护，设置可审计的评审与签名审批角色。由基金会审核后使用其 HSM。当前仓库资格尚待确认。 | 公布 OSS 服务免费。准备资料与审核会影响首签时间，官方条款未承诺固定周期。内部仍需承担 CI 与审核成本。 | 记录来源仓库、commit、受审构建和产物哈希，上传对应 CI 产物至服务，由维护者审批并回取 Authenticode 文件。再做签名检查与最终归档。 [基金会](https://signpath.org/) · [申请条件](https://signpath.org/terms) |
| 商业 OV/IV/EV 证书 | 供应商按类型核验个人或组织信息，使用受支持的硬件令牌或 HSM/云签名服务。IV/OV/EV 按实际申请主体选择。 | 包括证书、受保护密钥载体、续期及可选云签名额度。Sectigo 官网示例为五年选项每年自 536.25 美元起，以最终报价为准。周期从身份核验到签发、载体/云配置及 CI 验收，尚无本项目实际周期。 | 为 CI 接入受控签名步骤与凭据，调用签名和 RFC 3161 时间戳服务。把验证结果与本次构建 SHA 绑定，同时配置轮换和审批。 [Sectigo 报价和选项](https://www.sectigo.com/ssl-certificates-tls/code-signing) · [SSL.com 证书与 HSM](https://www.ssl.com/certificates/code-signing/) |

两项方案均要先核对原样分发的 CPython 与 HID 扩展的签名范围，再签本项目产物。完成 CI 签名及验证后生成最终 ZIP 和校验清单。现有更新链的 RSA-SHA256 签名继续保护最终 ZIP 与精确资产名，代码签名用于 Windows 的二进制发布者身份。签名不能保证任何一家杀软不再误报。是否申请、签哪些文件和是否调整构建流程留给维护者审查。

本卡仅记录依赖与评估。Windows 下的发行包核验与误报提交步骤见同 PR 的用户指南。
