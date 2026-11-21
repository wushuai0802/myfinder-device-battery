# INFI75 接口清单采集

请在 Windows 上打开 PowerShell，进入这份工具所在的项目目录。命令需要 Python 3.10 或更新版本。工具使用零访问权限的句柄查询 Windows HID 属性和报告能力，输出保存到当前项目目录。

1. 确认 INFI75 切在 2.4G 模式，接收器已插好。按几下键，确认键盘可以输入。

2. 运行以下命令，生成 `infi75-24g.txt`。

   ```powershell
   python -I tools/evidence/hid_inventory.py --vid 05AC --pid 024F --out infi75-24g.txt
   ```

3. 切到有线模式并插上数据线。采集有线数据时暂时拔下 INFI75 的 2.4G 接收器，以区分使用相同编号的接口。

4. 运行同一条命令，输出文件改为 `infi75-wired.txt`。

   ```powershell
   python -I tools/evidence/hid_inventory.py --vid 05AC --pid 024F --out infi75-wired.txt
   ```

5. 将 `infi75-24g.txt` 和 `infi75-wired.txt` 两份文件交给 Claude 或 Codex。若其中一份显示零个接口，请一并说明当时的连接方式。

导出文件是 UTF-8 JSON，供对照两种连接的接口和报告能力。Windows 的三项 `ReportByteLength` 是每个方向的集合最大值。单个报告 ID 的 `reportByteLength` 给出能力数据能确定或推得的字节数，`lengthBasis` 记录依据。同一方向有多个 ID 时，Windows 能力数据可能缺少填充长度。无法确定的长度记为 `null`，`minimumReportByteLength` 列出可算出的下界。`00` 表示未编号报告，字节数含 Windows 报告 ID 槽位。

清单保留 VID、PID、MI、集合号、Usage、产品和厂商字符串。实例路径、序列号标记和识别字段会在导出前去除。
