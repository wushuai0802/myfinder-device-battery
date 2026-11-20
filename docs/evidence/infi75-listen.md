# INFI75 被动监听

在 Windows 的 PowerShell 中进入工具所在的仓库目录。项目 Python 运行时包含 hidapi。工具只打开下列厂商集合并记录键盘主动发来的输入帧。

| 连接 | 产品串 | 监听集合 |
|---|---|---|
| 2.4G | `2.4G Wireless Keyboard` | `FFB5:0001` |
| 有线 | `INFI75-1.1` | `FFFF:0001`、`FF13:0001`、`FF68:0061` |

导出为 UTF-8 JSONL。每帧含相对时间、连接类型、集合和原始十六进制。输入事件标签后按回车，标签独立记录。默认监听 15 分钟，最长 30 分钟。时间到或按 Ctrl+C 后，控制台会显示各集合的帧数。

## 第一轮 2.4G

1. 把键盘切到 2.4G 并连上接收器，拔掉充电线。

2. 运行下方命令。使用系统 Python 时，命令入口为 `python -I tools/evidence/hid_listen.py --minutes 15 --out infi75-24g-listen.jsonl`，并确保该 Python 已包含 hidapi。

   ```powershell
   & .\engine\runtime\python.exe -I tools/evidence/hid_listen.py --minutes 15 --out infi75-24g-listen.jsonl
   ```

3. 在监听窗口输入 `静置` 并回车，放着键盘不动 2 分钟。
4. 输入 `开始打字`。切到另一个文本窗口打一段无意义的字，持续约 1 分钟，再回到监听窗口输入 `停止打字`。示例输入请使用不含个人信息的文字。
5. 看一眼键盘小屏幕，输入 `屏幕电量 xx`。再输入 `插上充电线` 并插线，静置 2 分钟，再输入 `屏幕电量 xx`。
6. 输入 `屏幕电量 xx` 和 `拔掉充电线` 后拔线，静置 2 分钟，再输入 `屏幕电量 xx`。
7. 输入 `等待休眠` 并放着键盘不动。休眠后按一个键唤醒，再输入 `唤醒`。
8. 时间到后文件保存在当前仓库目录。

## 第二轮 有线

切到有线模式并插数据线。用下方命令监听约 5 分钟，依次做静置、无意义打字和屏幕电量记录。

```powershell
& .\engine\runtime\python.exe -I tools/evidence/hid_listen.py --minutes 5 --out infi75-wired-listen.jsonl
```

把两份 `.jsonl` 文件交给 Claude 分析。如果一轮帧数为 0，也请保留文件和控制台给出的帧数。
