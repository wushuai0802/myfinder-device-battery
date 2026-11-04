# 型号数据包维护

数据包是完整快照，只给已审计协议增加型号或禁用型号，不包含代码，不开放设置写入。当前新增档案只有 Razer；`native_atk` 不受支持。

1. 编辑 [`devices/pack/source.json`](../devices/pack/source.json)，仅保留 `entries`、`disabled` 两个数组。条目格式与选档案方法见同目录 README。核对真实 VID/PID、接口/usage 和反馈来源，新型号默认 C 级。不要手写版本、时间或 keyId。
2. 运行 `python -I -B scripts/validate_device_pack.py`（也可用 `engine/runtime/python.exe`）。CI 同样生成临时完整包，再用引擎规则检查字段、档案版本、内置/包内 ID 冲突和禁用项；任何被拒绝条目都阻止发布，无需私钥。
3. 在 Windows 上准备 Python、.NET Framework 编译器、已登录且有仓库发布权限的 `gh`，明确提供与客户端 `pack-2026a` 公钥匹配的 XML 私钥。先运行：

   ```powershell
   ./scripts/Publish-DevicePack.ps1 -PrivateKeyPath '<私钥 XML 路径>' -DryRun
   ./scripts/Publish-DevicePack.ps1 -PrivateKeyPath '<私钥 XML 路径>'
   ```

   可用 `-Repository owner/repo` 指定 GitHub 仓库；客户端及固定镜像仍指向官方仓库，其他仓库的发布不会自动改变客户端来源，镜像核验也仍针对固定镜像。测试请使用假 gh/HTTP。

版本采用 UTC 日期 `yyyyMMddNN`，每日从 01 开始，大于远端指向文件及所有已有正式附件版本（包括中断留下的附件）；当日 99 用尽或远端日期在未来时停止。一次只运行一个发布者，避免并发更新指向文件。

发布前引擎和 C# 均校验，签名工具再用客户端内置公钥自验签。`device-pack` 发布必须为公开 prerelease；首次创建使用 `--latest=false`，说明为“型号数据包，不是程序版本”。先上传不可覆盖的 JSON 与 `.json.sig`，成功后仅覆盖 `device-pack-v1-latest.json`。不要把数据包发布改成正式程序版本或设为 Latest。

发布后分别从 GitHub 和固定镜像下载三份文件并验证摘要、大小及签名。镜像指向文件可能缓存 5 分钟，若不同只警告，但新正式文件和签名仍必须通过验证；网络错误、内容损坏都会报错。上传后的核验失败不代表远端未发布：先检查远端附件和指向状态，勿删除/覆盖旧正式包。首次真实发布需确认镜像路由已部署、两端下载可用，并在实际客户端核对加载版本和设备行为；离线测试不能证明这些。

`-DryRun` 也读取远端版本、生成、校验、签名，但不创建发布或上传。所有生成文件仅放在 `$env:TEMP` 唯一临时目录，结束即删除（DryRun 也不保留）；私钥只读取指定路径，不自动搜索、不打印、不复制。异常强制终止进程时可能需要手动清理对应临时目录。

回滚：编辑完整快照，删除错误新增条目或加入 `disabled`，再发布更高版本。不要降低版本或把指向文件改回旧版；客户端保留最高版本以防回滚。禁用项优先于新增与内置表，原生 ATK/蓝牙禁用不在此机制内。

离线验收：`./tests/Test-PublishDevicePack.ps1` 使用隔离源码副本、临时测试密钥和假 gh/HTTP；不会读取生产私钥或真实发布。`tests/Run.ps1` 在 Windows PowerShell 与 PowerShell 7（若安装）均运行它。
