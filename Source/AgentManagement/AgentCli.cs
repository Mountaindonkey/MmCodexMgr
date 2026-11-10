using System.Text;
using System.Text.Json;

namespace CodexInstanceManager;

public sealed record AgentCliInvocation(string Command, Dictionary<string, string> OptionDict, JsonElement Arguments);

public static class AgentCli
{
    public const string Help = """
        MmCodexMgr CLI
        instances
        projects --instance <实例编号|ID|名称>
        sessions --instance <实例> [--project <项目ID|名称|绝对路径>] [--session <对话ID|完整名称>] [--offset 0]
          instance=dsh 读取桌面端工作区与会话 不需要管理器或 Codex 会话身份
        dsh-config [--home <数据目录|auto>] [--server-url <本机服务地址|auto>]
          默认从运行中的桌面进程识别数据目录和端口 支持 DSH_HOME DSH_SERVER_URL 不修改 DSH 本身配置
          显式地址仅支持回环 IP 默认无需填写 路径迁移后可用 auto 恢复发现
        dsh-create --request-id <稳定标识> --title <会话名> [--project <default|工作区ID|名称|绝对路径>]
          默认创建于默认工作区 重试沿用 request-id 已创建的会话不重复创建
        dsh-rename --session-id <会话ID> --title <新名称>
        dsh-send --json-file <UTF-8 JSON文件>
          必需 session_id request_id message 沿用桌面模型 只发送一次 返回受理状态
        dsh-reply --session-id <会话ID> --request-id <原请求标识>
          按持久事件读取指定请求回复 对应轮次结束才返回 completed=true 不启动模型
        start --json-file <UTF-8 JSON文件>
          必需 request_id instance title instructions acceptance workspace
          project 可选 省略时查找实例内所有会话 新建时创建无项目会话
          instance=cursor 使用 Cursor CLI 新建必须提供 cwd 绝对目录 不使用 project
          instance=dsh 使用正在运行的 DSH 桌面服务 project 选择工作区 省略时新建于默认工作区
          DSH 沿用桌面模型 worker_limits 必须全零 管理器自动读取并回传结果 不需要操作窗口
          Cursor 可选 model cursor_cli_path 已有会话不能更换目录 CLI 或模型
          Cursor 只执行当前任务 worker_limits 必须为零 默认全部为零
          目标 session 或 create_title 二选一
          已授权自动协作时可用 create_title 自动新建任务所需会话 无需逐次确认
          Codex 目标实例未运行时自动启动并等待桥接就绪 首次使用需完成登录并已有会话上下文
          可选 subagent_max=3 session_max=1 cross_agent_max=1 timeout_minutes=60
          主会话 独立子执行者与联调会话默认各可用 3 个 Subagent 下级继承上级预算 重试保留原值 显式 0 禁用
          额度不要求凑满 每个 Subagent 必须有立即可执行的任务 原生 Subagent 是叶子 禁止递归创建或转派
          Session 与整棵联调树的数量和层级不限制原生 Subagent 额度 仍受宿主工具上限约束
          session_max 与 cross_agent_max 按本次请求校验 不锁定为已有任务的值 下级仍受上级预算约束
          timeout_minutes 范围 0 至 1440 分钟 0 表示无限等待
          worker_limits 可分别指定下级三种协作上限 默认继承本层
          tree_session_max=8 max_depth=3 控制整棵树的联调槽位与层数 不含初始主会话
          workspace={access:read_only|write,write_paths:[绝对路径]} 声明写入范围
        report --json-file <UTF-8 JSON文件>
          必需 task_id outcome(completed|needs_decision|failed) summary artifacts(绝对路径数组)
        status --task-id <任务ID>
        wait --task-id <任务ID> [--timeout-seconds 60]  外部主管阻塞等待回报 超时返回 timed_out=true
          timeout-seconds 范围 0 至 86400 0 为立即查询 wait 中断后可再次等待 不重复执行任务
        open --instance <实例> --session-id <会话ID>  自动启动实例并打开原会话 不发送执行消息
        resume --task-id <任务ID>  主会话恢复实例退出后的原任务 不创建新会话
        cleanup --state <interrupted|blocked|finished>
          清理当前主会话名下的联调记录 不删除 Codex 对话或项目文件
          interrupted=实例退出后阻塞 blocked=所有阻塞 finished=已验收或已撤销
          清理未结束记录会中断其下级联调 Cursor CLI 会停止 DSH 核对归属后请求取消 Codex 模型或脚本需另行停止
        cancel --task-id <任务ID>  中断联调 Cursor CLI 及其子进程会停止 DSH 核对归属后请求取消 Codex 模型或脚本需另行停止
        accept --task-id <任务ID>
        progress --json-file <UTF-8 JSON文件>  task_id stage(started|progress|blocked) message
        tree --task-id <任务ID> [--offset 0]
        retry-return --task-id <任务ID> --confirmed true
        inbox [--after <上一页的 next_after>] [--unread]  列出回传 外部主管返回结构化完整结果
        inbox-ack --task-id <任务ID>  外部主管把回报标为已读 不代替 accept 验收
        reconcile --task-id <任务ID> --session-id <已核对的会话ID> --confirmed true
        doctor [--instance <实例>]  无需会话身份 不启动模型
        install --instance <实例> [--skill <Skill目录>]
          CLI 与实例管理器放在同一目录 目录迁移后重新运行 install 更新 Skill 与 MCP 路径
        compat-install --instance <实例>  安装 OpenCode Go 联调输入适配 保留模型与密钥 重新加载目标实例后生效
        compat-remove --instance <实例>  恢复原服务商地址 保留其余配置 重新加载目标实例后生效
        library-skills [--source primary|library]  列出主实例或维护源库的 Skill
        library-config [--repository <仓库根>] [--skill-source <技能源目录>] [--rule-source <规则MD>] [--excluded <name逗号列表|none>]
          无参数查看本机配置 默认不排除任何 Skill 不限制技能名称或数量
        library-scan-rules  扫描各 Agent 的全局规则位置 无需启动管理器
        library-scan-skills  扫描 Cursor DSH 共享与 Claude Code 的 Skill 目录
        library-preview-agent-skills --agent <cursor|dsh|shared|claude> [--source primary|library] --plan-file <绝对路径>
          预览向其他 Agent 分发相同技能包 要求小写连字符标识 原内容先备份 保留第三方技能
          shared 对应 ~/.agents/skills 由 Cursor 与 DSH 共同发现 DSH_HOME 与 DSH_AGENTS_HOME 可覆盖默认目录
          claude 对应 ~/.claude/skills CLAUDE_CONFIG_DIR 可覆盖默认目录 无 SKILL.md 的共享资料目录不分发
        library-preview-skills --instance <目标实例> [--source primary|library] --plan-file <绝对路径>
          source 默认 primary 使用 library 可将维护源库完整安装到主实例
        library-preview-import --skill <包含SKILL.md的目录> --plan-file <绝对路径>
          导入任意 Skill 到已配置维护源库 保留其他包 不自动安装或推送
        library-preview-rules --agents <codex,Cursor,Claude Code等扫描名称> [--source <规则源MD>] --plan-file <绝对路径>
          codex 表示所有已注册 Codex 实例 预览返回原文与新内容
        library-apply --plan-file <预览JSON绝对路径>
          先阅读预览再执行 应用前重新核对指纹 变更先备份 非主目标实例需退出
        library-git-pull  快进拉取配置的源库仓库 有未提交改动则停止
        library-git-publish  收录所选维护源库与规则源 仅提交 Library 目录并推送
        mcp --instance <实例ID>
        调用方默认取 CODEX_HOME 与 CODEX_THREAD_ID
        在 Codex 外使用时显式提供 --caller-instance <实例> --caller-thread <会话ID>
        外部主管使用 --external-caller <允许列表中的名称> 可放在命令之前或之后 无需 CODEX_THREAD_ID
          同时提供 --external-session-id <Claude会话UUID> 可按真实主管会话隔离任务、预算和回报
          未提供会话ID时仅访问旧的无会话ID记录，不会合并其他主管会话
          需在管理器设置开启允许外部主管 并登记名称 默认关闭
          支持 instances projects sessions start status tree accept cancel wait inbox inbox-ack
          不可与 --caller-instance 或 --caller-thread 混用 外部主管为深度0根节点 不占槽位
        stdout 返回 JSON 失败退出码 1 详情写 stderr
        """;

    #region Execution
    public static async Task<int> RunAsync(string[] ArgumentList, Stream Input, Stream Output, TextWriter Error, string ExecutablePath, CancellationToken Cancellation, string? PipeName = null, Func<ManagerSettings>? LoadSettings = null, string? OutboxRoot = null)
    {
        using var Writer = new StreamWriter(Output, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        try
        {
            if (ArgumentList.Length == 3 && ArgumentList[0] == "compat-server" && ArgumentList[1] == "--profile")
            {
                await ResponsesCompatServer.RunProfileAsync(ArgumentList[2], Cancellation);
                return 0;
            }
            var Call = Parse(ArgumentList);
            if (Call.Command == "help") { await Writer.WriteLineAsync(Help); return 0; }
            var Settings = (LoadSettings ?? SettingsStore.Load)();
            if (Call.Command.StartsWith("dsh-", StringComparison.Ordinal))
            {
                await Writer.WriteLineAsync(JsonSerializer.Serialize(await DshCli.ExecuteAsync(Call, Settings, Cancellation)));
                return 0;
            }
            if (Call.Command is "projects" or "sessions" && !Call.OptionDict.ContainsKey("external-caller") && Call.OptionDict.GetValueOrDefault("instance") == DshRelayRunner.InstanceId)
            {
                await Writer.WriteLineAsync(JsonSerializer.Serialize(await DshCli.QueryAsync(Call.Command, Settings.Dsh, Call.Arguments, Cancellation)));
                return 0;
            }
            if (Call.Command.StartsWith("library-", StringComparison.Ordinal))
            {
                await Writer.WriteLineAsync(JsonSerializer.Serialize(await LibraryCli.ExecuteAsync(Call, Settings)));
                return 0;
            }
            if (Call.Command is "compat-install" or "compat-remove")
            {
                var Instance = AgentManagementService.ResolveInstance(Settings, Required(Call, "instance"));
                if (Call.Command == "compat-install")
                {
                    var Profile = ResponsesCompatProfile.Install(Instance, ExecutablePath);
                    await Writer.WriteLineAsync(JsonSerializer.Serialize(new { installed = true, instance_id = Instance.Id, provider = Profile.Provider, local_url = Profile.LocalUrl, upstream_url = Profile.UpstreamUrl, backup = Profile.BackupPath, reload = "重新加载目标实例后生效 不修改会话历史" }));
                }
                else
                {
                    ResponsesCompatProfile.Remove(Instance);
                    await Writer.WriteLineAsync(JsonSerializer.Serialize(new { removed = true, instance_id = Instance.Id, reload = "重新加载目标实例后生效" }));
                }
                return 0;
            }
            if (Call.Command == "mcp")
            {
                var Instance = AgentManagementService.ResolveInstance(Settings, Required(Call, "instance"));
                ResponsesCompatProfile.EnsureRunning(Instance, ExecutablePath);
                await RelayMcp.RunAsync(Instance.Id, Input, Output, Cancellation, PipeName, OutboxRoot);
                return 0;
            }
            if (Call.Command == "install")
            {
                var Instance = AgentManagementService.ResolveInstance(Settings, Required(Call, "instance"));
                await Writer.WriteLineAsync(JsonSerializer.Serialize(AgentCliDeployment.Install(Instance, ExecutablePath, Call.OptionDict.GetValueOrDefault("skill"))));
                return 0;
            }
            if (Call.Command == "doctor")
            {
                var Report = await AgentDoctor.RunAsync(Settings, ExecutablePath, Call.OptionDict.GetValueOrDefault("instance"), Cancellation);
                if (Report.InstanceId == DshRelayRunner.InstanceId) { await Writer.WriteLineAsync(JsonSerializer.Serialize(Report)); return Report.Healthy ? 0 : 1; }
                try
                {
                    await RelayHost.RequestAsync(new(Report.InstanceId == CursorRelayRunner.InstanceId ? Settings.SelectedId : Report.InstanceId, "", "agent_ping", JsonSerializer.SerializeToElement(new { })), Cancellation, PipeName);
                    Report.CheckList.Add(new("管理器服务", true, "已连接"));
                }
                catch (Exception Failure) when (Failure is IOException or TimeoutException or OperationCanceledException)
                {
                    Report.CheckList.Add(new("管理器服务", false, Failure.Message));
                }
                await Writer.WriteLineAsync(JsonSerializer.Serialize(Report));
                return Report.Healthy ? 0 : 1;
            }
            string? ExternalCaller = Call.OptionDict.GetValueOrDefault("external-caller");
            string? CallerSelector = Call.OptionDict.GetValueOrDefault("caller-instance");
            string CallerId;
            string ThreadId;
            if (ExternalCaller is not null) { CallerId = ""; ThreadId = ""; }
            else
            {
                InstanceProfile Caller;
                if (CallerSelector is not null) Caller = AgentManagementService.ResolveInstance(Settings, CallerSelector);
                else
                {
                    string Home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? ManagerPaths.DefaultHome;
                    var MatchList = Settings.InstanceList.Where(Item => ManagerPaths.Same(Item.HomePath, Home)).ToList();
                    if (MatchList.Count != 1) throw new InvalidOperationException("无法唯一确定调用方实例 请提供 --caller-instance");
                    Caller = MatchList[0];
                }
                CallerId = Caller.Id;
                ThreadId = Call.OptionDict.GetValueOrDefault("caller-thread") ?? Environment.GetEnvironmentVariable("CODEX_THREAD_ID") ?? throw new InvalidOperationException("缺少 CODEX_THREAD_ID 请提供 --caller-thread");
            }
            string Command = Call.Command switch
            {
                "instances" => "agent_instances", "projects" => "agent_projects", "sessions" => "agent_sessions", "start" => "agent_start",
                "report" => "relay_report", "status" => "agent_task", "inbox" => "agent_inbox", "wait" => "agent_wait", "inbox-ack" => "agent_inbox_ack", "cancel" => "agent_cancel", "accept" => "relay_accept",
                "progress" => "relay_progress", "tree" => "agent_tree", "retry-return" => "agent_retry_return", "reconcile" => "agent_reconcile", "open" => "agent_open", "resume" => "agent_resume", "cleanup" => "agent_cleanup",
                _ => throw new InvalidOperationException("未知命令 " + Call.Command)
            };
            var Request = new RelayRequest(CallerId, ThreadId, Command, Call.Arguments) { ExternalCaller = ExternalCaller ?? "", ExternalSessionId = Call.OptionDict.GetValueOrDefault("external-session-id") ?? "" };
            var Result = Command == "relay_report" ? await RelayOutbox.DeliverAsync(Request, Cancellation, PipeName, OutboxRoot) : await RelayHost.RequestAsync(Request, Cancellation, PipeName);
            await Writer.WriteLineAsync(Result.GetRawText());
            return 0;
        }
        catch (Exception Failure)
        {
            await Error.WriteLineAsync(JsonSerializer.Serialize(new { error = Failure.Message }));
            return 1;
        }
    }
    #endregion

    #region Arguments
    public static AgentCliInvocation Parse(string[] ArgumentList)
    {
        var OptionDict = new Dictionary<string, string>();
        int CommandIndex = 0;
        while (CommandIndex < ArgumentList.Length && ArgumentList[CommandIndex] is "--external-caller" or "--external-session-id" or "--caller-instance" or "--caller-thread")
        {
            string Key = ArgumentList[CommandIndex][2..];
            if (CommandIndex + 1 >= ArgumentList.Length || string.IsNullOrWhiteSpace(ArgumentList[CommandIndex + 1]) || ArgumentList[CommandIndex + 1].StartsWith("--", StringComparison.Ordinal)) throw new InvalidOperationException("缺少参数值 --" + Key);
            if (!OptionDict.TryAdd(Key, ArgumentList[CommandIndex + 1])) throw new InvalidOperationException("参数重复 " + Key);
            CommandIndex += 2;
        }
        string Command = CommandIndex == ArgumentList.Length ? "help" : ArgumentList[CommandIndex];
        if (Command is "--help" or "-h") Command = "help";
        string[] AllowedList = Command switch
        {
            "help" or "instances" => Array.Empty<string>(),
            "projects" => new[] { "instance" },
            "sessions" => new[] { "instance", "project", "session", "offset" },
            "start" or "report" => new[] { "json-file" },
            "dsh-send" => new[] { "json-file" },
            "dsh-config" => new[] { "home", "server-url" },
            "dsh-create" => new[] { "request-id", "title", "project" },
            "dsh-rename" => new[] { "session-id", "title" },
            "dsh-reply" => new[] { "session-id", "request-id" },
            "progress" => new[] { "json-file" },
            "doctor" => new[] { "instance" },
            "tree" => new[] { "task-id", "offset" },
            "inbox" => new[] { "after", "unread" },
            "wait" => new[] { "task-id", "timeout-seconds" },
            "inbox-ack" => new[] { "task-id" },
            "retry-return" => new[] { "task-id", "confirmed" },
            "reconcile" => new[] { "task-id", "session-id", "confirmed" },
            "open" => new[] { "instance", "session-id" },
            "cleanup" => new[] { "state" },
            "status" or "cancel" or "accept" or "resume" => new[] { "task-id" },
            "install" => new[] { "instance", "skill" },
            "compat-install" or "compat-remove" => new[] { "instance" },
            "mcp" => new[] { "instance" },
            "library-skills" => new[] { "source" },
            "library-preview-agent-skills" => new[] { "agent", "source", "plan-file" },
            "library-config" => new[] { "repository", "skill-source", "rule-source", "excluded" },
            "library-preview-import" => new[] { "skill", "plan-file" },
            "library-scan-rules" or "library-scan-skills" or "library-git-pull" or "library-git-publish" => Array.Empty<string>(),
            "library-preview-skills" => new[] { "instance", "source", "plan-file" },
            "library-preview-rules" => new[] { "agents", "source", "plan-file" },
            "library-apply" => new[] { "plan-file" },
            _ => throw new InvalidOperationException("未知命令 " + Command + " 请执行 help")
        };
        for (int Index = CommandIndex + 1; Index < ArgumentList.Length; Index++)
        {
            string Key = ArgumentList[Index];
            if (!Key.StartsWith("--", StringComparison.Ordinal)) throw new InvalidOperationException("参数必须使用 --名称 值 " + Key);
            Key = Key[2..];
            if (!AllowedList.Contains(Key) && Key is not ("caller-instance" or "caller-thread" or "external-caller" or "external-session-id")) throw new InvalidOperationException("此命令不支持参数 " + Key);
            string Value;
            if (Key == "unread" && (Index + 1 == ArgumentList.Length || ArgumentList[Index + 1].StartsWith("--", StringComparison.Ordinal))) Value = "true";
            else
            {
                if (Index + 1 >= ArgumentList.Length || ArgumentList[Index + 1].StartsWith("--", StringComparison.Ordinal)) throw new InvalidOperationException("缺少参数值 --" + Key);
                Value = ArgumentList[++Index];
            }
            if (!OptionDict.TryAdd(Key, Value) || string.IsNullOrWhiteSpace(Value)) throw new InvalidOperationException("参数重复或为空 " + Key);
        }
        if (OptionDict.ContainsKey("external-caller"))
        {
            if (OptionDict.ContainsKey("caller-instance") || OptionDict.ContainsKey("caller-thread")) throw new InvalidOperationException("--external-caller 不能与 --caller-instance 或 --caller-thread 混用");
            if (Command is not ("help" or "instances" or "projects" or "sessions" or "start" or "status" or "tree" or "accept" or "cancel" or "wait" or "inbox" or "inbox-ack")) throw new InvalidOperationException("外部主管不支持命令 " + Command);
            if (OptionDict.TryGetValue("external-session-id", out string? SessionId)) OptionDict["external-session-id"] = ExternalSupervisor.NormalizeSessionId(SessionId);
        }
        else if (Command is "wait" or "inbox-ack" || OptionDict.ContainsKey("unread") || OptionDict.ContainsKey("external-session-id")) throw new InvalidOperationException("此命令或参数需要 --external-caller");
        if (Command is "wait" or "inbox-ack" && !OptionDict.ContainsKey("task-id")) throw new InvalidOperationException("缺少 --task-id");
        if (OptionDict.TryGetValue("timeout-seconds", out string? TimeoutText) && (!int.TryParse(TimeoutText, out int TimeoutSeconds) || TimeoutSeconds is < 0 or > 86400)) throw new InvalidOperationException("timeout-seconds 范围为 0 至 86400");
        if (Command is "start" or "report" or "progress" or "dsh-send")
        {
            if (!OptionDict.TryGetValue("json-file", out string? FilePath)) throw new InvalidOperationException("缺少 --json-file");
            if (new FileInfo(FilePath).Length > RelayLimits.MaxFrameBytes / 2) throw new InvalidDataException("请求文件过大");
            using var Document = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            if (Document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("请求必须是 JSON 对象");
            string[] PropertyList = Command switch
            {
                "dsh-send" => new[] { "session_id", "request_id", "message" },
                "start" => new[] { "request_id", "instance", "project", "session", "create_title", "title", "instructions", "acceptance", "session_max", "subagent_max", "cross_agent_max", "worker_limits", "timeout_minutes", "tree_session_max", "max_depth", "workspace", "cwd", "model", "cursor_cli_path" },
                "progress" => new[] { "task_id", "stage", "message" },
                _ => new[] { "task_id", "outcome", "summary", "artifacts" }
            };
            var NameHashList = new HashSet<string>();
            foreach (var Property in Document.RootElement.EnumerateObject())
                if (!PropertyList.Contains(Property.Name) || !NameHashList.Add(Property.Name)) throw new InvalidDataException("请求参数未知或重复 " + Property.Name);
            return new(Command, OptionDict, Document.RootElement.Clone());
        }
        var Arguments = new Dictionary<string, object>();
        foreach (var Pair in OptionDict)
        {
            if (Pair.Key.StartsWith("caller-", StringComparison.Ordinal) || Pair.Key is "external-caller" or "external-session-id") continue;
            Arguments[Pair.Key.Replace('-', '_')] = Pair.Key switch { "offset" or "timeout-seconds" => int.Parse(Pair.Value), "confirmed" or "unread" => bool.Parse(Pair.Value), _ => Pair.Value };
        }
        return new(Command, OptionDict, JsonSerializer.SerializeToElement(Arguments));
    }

    private static string Required(AgentCliInvocation Call, string Name) => Call.OptionDict.GetValueOrDefault(Name) ?? throw new InvalidOperationException("缺少 --" + Name);
    #endregion
}
