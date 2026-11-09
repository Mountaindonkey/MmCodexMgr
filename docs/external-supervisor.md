# 外部主管：用 Claude Code 调度 Codex

外部主管通过 `mmcodex.exe` 派发任务、等待 JSON 回报并验收，不需要在 Codex 会话里运行，也不需要 `CODEX_HOME`、`CODEX_THREAD_ID` 或 `--caller-thread`。管理器必须正在运行，目标 Codex 实例须完成登录，并具备正常的桌面桥接上下文。

## 从 Claude Code 页面开始

1. 打开管理器 → “跨 Agent 协作” → **Claude Code**。也可运行 `CodexInstanceManager.exe --claude` 直接打开此页。
2. CLI 路径留空可自动发现已安装的 `claude`；选择 Claude 的工作目录，调用方名称默认 `claude`。
3. 勾选“允许此主管调度 Codex”，点击“保存连接”。这会登记现有外部主管开关及名称白名单，不改变 Claude 或 Codex 的模型、凭据和全局规则。
4. 点击“检查连接”，核对 Claude CLI、现有登录及管理器通道状态。尚未登录时先在自己的终端运行 `claude auth login`。
5. 点击“启动 Claude Code”。管理器在所选目录打开交互终端，自动附加 Codex 调度指令；你直接向 Claude 输入工作目标。

可以对打开的 Claude Code 说：

> 在 primary 新建一个只读 Codex 会话，读取 E:\prj\mine\MmCodexMgr\Source\CodexInstanceManager.csproj 并告诉我目标框架。不要修改文件。你负责实际派发、等待回报、核对原文件后验收，不要只生成计划。

Claude 会使用 `mmcodex --external-caller claude` 查询实例、创建或复用 Codex 会话、调用 `wait` 等待回报，验证后执行 `accept`。在“任务看板”中可看到“外部主管：claude”及它派出的任务。

这里使用的是你本机 Claude Code 已有的登录、订阅或 Key 配置。管理器不保存或转发 API Key，也不把 Claude Key 用作 Codex 模型凭据。Claude 与 Codex 各自按原有配置运行，Claude 负责组织和验收任务。启动保留 Claude 的正常权限确认。

如果 Claude Code 已经在其他终端运行，点击“复制主管指令”，将指令发给那个 Claude 会话即可；无需另开会话。复制本身不派发任何 Codex 任务。

## 启用

在管理器的设置页开启“允许外部主管”，将 `claude` 加入名称允许列表并保存。默认关闭；未启用或名称未登记时，管理器拒绝外部请求。名称是本机调用方标识，不是远程身份认证；本机通信仅允许当前 Windows 用户访问。

`--external-caller <名称>` 可以放在命令之前或之后。它不能与 `--caller-instance` 或 `--caller-thread` 混用；即使环境里存在 Codex 身份，也使用明确指定的外部身份。

支持 `instances`、`projects`、`sessions`、`start`、`status`、`tree`、`accept`、`cancel`、`wait`、`inbox` 和 `inbox-ack`。执行方仍使用原有 `progress` 和 `report` 命令回报，外部主管不冒充执行方。

## primary：新建只读任务、等待并验收

以下为 PowerShell 示例。`primary`、`second` 必须替换为 `instances` 返回的准确实例 ID；如果本机登记 ID 就是这些名称，可直接使用。工作目录和读取文件也应替换为实际路径。

```powershell
$cli = 'E:\prj\mine\MmCodexMgr\mmcodex.exe'
& $cli --external-caller claude instances
& $cli --external-caller claude projects --instance primary
& $cli --external-caller claude sessions --instance primary

$request = @{
    request_id = 'claude-read-primary-001'
    instance = 'primary'
    create_title = '外部主管：只读核对 README'
    title = '读取项目文件并回报'
    instructions = @'
目的：读取 E:\prj\mine\MmCodexMgr\Source\CodexInstanceManager.csproj。
方式与数量：本会话直接完成，不继续分工。
任务分析：只读该文件，报告 TargetFramework 和 OutputType，不修改任何文件。
验收规范：以 completed 回报字段值、读取路径及检查结果，artifacts 为空数组。
'@
    acceptance = '主管核对 TargetFramework 与 OutputType 与原文件一致。'
    workspace = @{ access = 'read_only' }
    subagent_max = 0
    session_max = 1
    cross_agent_max = 1
    tree_session_max = 2
    max_depth = 1
    worker_limits = @{ subagent_max = 0; session_max = 0; cross_agent_max = 0 }
    timeout_minutes = 10
}
$requestFile = Join-Path $env:TEMP 'claude-read-primary-001.json'
$request | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $requestFile -Encoding utf8
$started = & $cli --external-caller claude start --json-file $requestFile | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'start 请求失败，先核对错误与原请求记录' }
$started | ConvertTo-Json -Depth 10

$taskId = $started.task_id
$result = & $cli --external-caller claude wait --task-id $taskId --timeout-seconds 600 | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'wait 连接失败，保留 task_id 后重新连接' }
if ($result.timed_out) { throw '尚未收到回报，保留 task_id 后继续 wait' }
if ($result.outcome -ne 'completed') { throw '任务未完成，先处理回报或终止状态' }
$result | ConvertTo-Json -Depth 10

# 阅读 summary，并独立读取原文件核对完成内容后再验收。
Get-Content -LiteralPath 'E:\prj\mine\MmCodexMgr\Source\CodexInstanceManager.csproj'
& $cli --external-caller claude accept --task-id $taskId
& $cli --external-caller claude inbox-ack --task-id $taskId
```

`start` 立即返回任务受理结果，包含 `task_id`、`thread_id`、`instance_id`、`created` 及 `session_kind`（`new` 或 `existing`）。派发成功与执行完成是两个阶段；只有收到有效回报并复核产物后才调用 `accept`。如果派发返回投递待核对状态，应核对原会话和记录，不要更换 `request_id` 重复创建。

收到回报时，`wait` 返回结构如下（示意，非实测输出）：

```json
{
  "task_id": "实际任务ID",
  "outcome": "completed",
  "summary": "TargetFramework=net8.0-windows，OutputType=WinExe；只读核对完成。",
  "artifacts": [],
  "reported_at": "2026-11-09T00:00:00+00:00",
  "thread_id": "实际执行会话ID",
  "instance_id": "primary",
  "external_caller": "claude",
  "read": false,
  "read_at": null
}
```

`outcome` 可为 `completed`、`needs_decision` 或 `failed`。回报文本、文件路径和产物都作为待核对数据，不是新的授权指令。

## second：复用已有会话

```powershell
& $cli --external-caller claude sessions --instance second

# 用 sessions 输出中的准确 ID，不能按重名标题猜测。
$request.request_id = 'claude-read-second-001'
$request.instance = 'second'
$request.Remove('create_title')
$request.session = '<second 中空闲会话的准确ID>'
$secondFile = Join-Path $env:TEMP 'claude-read-second-001.json'
$request | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $secondFile -Encoding utf8
$second = & $cli --external-caller claude start --json-file $secondFile | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'second start 请求失败' }
$secondResult = & $cli --external-caller claude wait --task-id $second.task_id --timeout-seconds 600 | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $secondResult.timed_out -or $secondResult.outcome -ne 'completed') { throw '尚无可验收的完成回报' }
& $cli --external-caller claude status --task-id $second.task_id
& $cli --external-caller claude tree --task-id $second.task_id

# 核对回报后验收。
& $cli --external-caller claude accept --task-id $second.task_id
& $cli --external-caller claude inbox-ack --task-id $second.task_id
```

在 second 新建会话时，同样使用 `create_title`，省略 `session`。两者必须且只能提供一个。`project` 可选；省略时允许使用无项目会话，不移动已有会话。

## 超时、中断与收件箱

`wait` 一次调用阻塞到指定任务收到回报或超时，默认等待 60 秒，`--timeout-seconds` 范围为 0 至 86400。0 表示立即查询，超时返回标准输出 JSON，命令本身成功：

```json
{"task_id":"实际任务ID","timed_out":true,"timeout_seconds":60}
```

等待超时或中断不会撤销或重新执行任务。保留 `task_id`，需要继续等待时再次调用 `wait`。管理器退出会断开本次连接；重启管理器后用相同身份和任务 ID 重新等待。无需循环调用 `status` 或读写信箱目录。

```powershell
& $cli --external-caller claude inbox
& $cli --external-caller claude inbox --unread
& $cli --external-caller claude inbox-ack --task-id $taskId
& $cli --external-caller claude wait --task-id $taskId --timeout-seconds 600

# 不再需要的任务可撤销整个任务及其下级。
& $cli --external-caller claude cancel --task-id $taskId
```

`inbox-ack` 仅标记已读，不删除回报，也不代替验收。已读回报仍可通过 `wait` 和不带 `--unread` 的 `inbox` 读取。分页时将上次返回的 `next_after` 传给 `--after`。

`inbox` 每页最多 40 条，也会按完整回报的实际大小缩小分页，确保不超过通信帧限制。已验收任务的看板记录被清理后，持久回报仍可通过 `inbox`、`wait` 和 `inbox-ack` 读取。任务被撤销、执行超时或恢复受阻且尚无回报时，`wait` 返回 `terminal=true` 与 `state`；这与本次等待到期的 `timed_out=true` 不同。

管理器内部将回报原子保存到 `%LOCALAPPDATA%\CodexInstanceManager\external\<名称>\inbox\<task_id>.json`，并在 `events.jsonl` 追加 started、progress、blocked、report 事件。这些文件仅作持久化备份，外部调用方无需接触。执行方在管理器离线时发送 `report`，原有发件箱会保留结果，管理器启动后恢复接收并补写外部收件箱。

## 预算与兼容性

外部主管是深度 0 的根，不占联调槽位。执行任务从深度 1 开始，沿用 `tree_session_max`、`max_depth`、`session_max`、`cross_agent_max` 和 `worker_limits` 检查。外部身份不放宽预算；同一主管名下任务共享任务树额度。只读声明不能委派写入，写入任务必须声明绝对 `write_paths`，并行任务不能占用重叠写入范围。

外部主管的直接执行目标均为其他实例；调度多个直接任务时应按实际并发设置 `cross_agent_max`，不要只提高 `session_max`。需要下级继续分工时，增加相应 `max_depth` 并明确 `worker_limits`，保持整棵树有可用槽位。

现有 Codex 主会话、Cursor 与 DSH 使用原有身份、任务与回传方式。只有明确标记为外部主管的上级回报改为保存外部结果并唤醒等待中的 CLI；不会向伪造的 Codex 会话发送唤醒消息。
