# 本机 Issue 草稿监控

Windows PowerShell 5.1 / PowerShell 7。所有入口都在本目录；不会修改设备数据包或自动发布回复。

## 依赖和运行

需要已登录的 GitHub CLI、通过 npm 安装且已登录的 Codex CLI，以及 Python 3.10+。优先使用仓库的 `engine/runtime/python.exe`，没有时使用 PATH 中的 `python`；可传 `-PythonPath`。Python 仅运行静态索引和标准库辅助程序，不加载 HID 读取器。

```powershell
.\tools\issue-watch\Issue-Watch.ps1
.\tools\issue-watch\Approve-Draft.ps1 -Issue 23
.\tools\issue-watch\Approve-Draft.ps1 -Issue 23 -Sha256 '<草稿末尾的64位摘要>'
.\tools\issue-watch\Approve-Draft.ps1 -Issue 23 -Dismiss
```

默认仓库为 `wushuai0802/myfinder-device-battery`，默认草稿目录为 `E:\AIArchive\issue-drafts`。可用 `-Repository owner/repo` 和 `-DraftDirectory <路径>` 覆盖；监控、审批和注册任务应使用一致参数。一个草稿目录只能绑定一个仓库。`-GhPath`、`-CodexPath` 支持命令名或完整路径，包括 npm 的 PowerShell shim。

监控列出所有 open issue，在本地筛选四个待处理标签，避免 GitHub 搜索结果的 1000 条上限。再次读取 issue 后确认状态和标签。每次最多尝试 5 个，未变化的草稿不占名额；没有待处理项时连 `codex exec --help` 都不调用。完整提示词（指令、兼容摘要、JSON 不可信数据块）不超过 24000 字符。过长正文/评论会被截断，但变化检测仍覆盖完整内容，包括评论编辑和删除。

每个 issue 使用一个新的空目录，stdin 传入提示词，调用 `gpt-6.1-sol` / `model_reasoning_effort=max` / `--sandbox read-only` / `--skip-git-repo-check` / `-C`。仅子进程环境移除所有名字含 TOKEN、KEY、SECRET 的变量，父进程及 `gh` 的认证不变。如果 Codex 依赖被移除的环境密钥登录，运行会失败并进入重试流程；脚本不会改认证或注入替代密钥。

每次有实际待处理项时探测一次 `codex exec --help`，按结果分别启用 `--output-schema`、`--output-last-message` 或 `-o`，否则严格解析 stdout JSON，不从日志中猜取 JSON 子串。每次模型调用默认超时 600 秒；测试可用 `-ModelTimeoutSeconds` 缩短，不能延长。独立 Windows Job 对象在完成或超时后终止子进程树，然后删除临时目录。

## 草稿、失败与审批

`<编号>.md` 包含原文及近期评论摘录、原始输入摘要、分类、理由、回复、数据包建议、追问和 SHA-256。文末隐藏元数据保存经过校验的原始 JSON，用于可靠提取回复；它也受摘要保护。

显示的 SHA-256 覆盖 UTF-8 草稿正文（包括隐藏元数据），不包含末尾 SHA-256 行自身。`state.json` 另存整个文件的 SHA-256，任何字节编辑（包括换行或摘要行）都会拒绝审批。需要调整内容时应重新起草，不能仅替换摘要来批准编辑后的文件。无 `-Sha256` 时必须输入小写 `y`；摘要匹配可跳过该输入，`-Dismiss` 同样需要确认。审批再次检查文件后，仅发送回复正文，去掉第一行维护者标记。

严格校验拒绝重复键、非标准 JSON、缺失/额外字段、错误类型/分类、超长回复或理由、@ 提及及非 github.com 链接。`packEntry` 只接受 provider/profile/match 结构；只是待人工核查的建议，静态索引不能证明协议档案正确或实机可用。

`state.json` 中的状态：

| 状态 | 含义 |
| --- | --- |
| `ready` | 草稿可审批；issue/评论变化会重新起草 |
| `error` | 起草或校验失败，下次重试 |
| `manual` | 连续 3 次失败，需人工处理，停止自动重试 |
| `posting` | 已开始发送但结果未可靠记账，需人工核对 GitHub，防止重复发帖 |
| `commented` | 已发评论但标签未完成；重新审批只补标签 |
| `drafted` / `dismissed` | 已完成发送/忽略，并加上 `triage:drafted` |

人工核对并解决 `manual` 后，可在停止监控时备份 `state.json` 并仅删除对应 issue 记录，让下一次重试。对 `posting` 必须先确认远端是否已收到评论；已收到时将该条状态改为 `commented` 后补标签，明确未收到时才能改回 `ready`。不要为恢复起草而清空整个状态文件。监控与审批共用目录文件锁，防止同时改写。

## 计划任务

```powershell
.\tools\issue-watch\Register-IssueWatch.ps1 -WhatIf
.\tools\issue-watch\Register-IssueWatch.ps1
.\tools\issue-watch\Register-IssueWatch.ps1 -Unregister
```

注册名为 `MyFinder Issue Watch`，当前用户、普通权限、每天 09:00 / 21:00、隐藏窗口、错过后补跑。使用交互用户登录，不保存密码；关机或尚未登录时不能运行，开机并登录后按调度器的补跑规则执行。注册时固定各命令的绝对路径，避免任务环境 PATH 不同。日志在草稿目录 `logs/`，只清理 30 天前的 `watch-*.log`。通知通过 WinRT 尽力发送，通知失败不影响保存。

## 离线验证

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\tools\issue-watch\Test-IssueWatch.ps1
pwsh.exe -NoProfile -NonInteractive -File .\tools\issue-watch\Test-IssueWatch.ps1
```

测试仅注入本目录的假 `gh` / `codex`，所有产物在当前 worktree 的 `tmp/` 中，结束后清理；不调用真实 GitHub/Codex，不注册计划任务，不弹真实通知。覆盖无待处理项、筛选、正常起草、不合法 JSON、提示注入、子进程环境过滤、摘要拒绝、实际草稿编辑拒绝、发送与标记重试、Dismiss、评论变化与未变化跳过、三次失败、批次上限、超时清理和三种帮助输出能力。

官方 [CLI 参数文档](https://learn.chatgpt.com/docs/developer-commands?surface=cli)列有 `--output-schema` 和 `--output-last-message` / `-o`。本任务禁止运行真实 Codex，因此本机 npm CLI 实际支持哪些参数仍未现场验证；假命令已验证三种能力分支，实际运行时的探测结果会写入日志。真实通知显示、计划任务补跑、模型质量及 GitHub 端到端发送也需维护者另行验收。
