# 厂商网页驱动型号表

从厂商的公开入口收集静态型号声明，供协议调查用。每条记录包含 VID:PID、型号、源码中的协议名或版本、最终 URL、原始文件 SHA-256、UTF-8 解码后的字符偏移。偏移从 0 起，用同一份原始资源复核。

```powershell
python -I -B engine/known_ids.py --json | Set-Content -Encoding UTF8 tmp/known-ids.json
python -I -B tools/harvest/vendor_tables.py --known-ids tmp/known-ids.json
python -I -B -m unittest discover -s tools/harvest/tests -v
```

在仓库根目录运行，先建立 tmp 目录。结果默认写入 `tmp/harvest/results.json`。`unknownIds` 比较的是项目当前的静态 ID 表。共享接收器、传输和描述符仍需核对。

VGN 取 `customDeviceType`。M HUB 取显式协议选项或音频 profile。EPOMAKER/容源记录同条声明的 vendor 和协议版本。竹林鸟记录可确认的 ZG4 登记。AULA 解析明确写在同一条模型记录里的设备与驱动字段。

工具把通过厂商 HTTPS 精确主机检查的 HTML 和 JS 当文本读取。厂家更改结构、返回访问错误或缺少可确认声明时，在 JSON 中记录错误并返回非零退出码。已有的其他厂商结果可继续审查。
