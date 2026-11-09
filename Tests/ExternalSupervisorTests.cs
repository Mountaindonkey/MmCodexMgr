using System.Text;
using System.Text.Json;
using CodexInstanceManager;
using Microsoft.Data.Sqlite;

internal static class ExternalSupervisorTests
{
    private static int Count;

    public static void Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        Count = 0;
        VerifyLegacySettings();
        await VerifyAuthorizationAsync();
        await VerifyDispatchAndReturnAsync();
        await VerifyIsolationAsync();
        await VerifyBudgetsAndWorkspacesAsync();
        await VerifyOfflineRecoveryAsync();
        await VerifyLargeInboxPaginationAsync();
        await VerifyPendingBeforeRecoveryAsync();
        await VerifyImmediateAcceptanceAsync();
        await VerifyEventTailRecoveryAsync();
        await VerifyExternalTaskValidityAsync();
        Console.WriteLine($"PASS external supervisors {Count} assertions");
    }

    private static void VerifyLegacySettings()
    {
        var Settings = JsonSerializer.Deserialize<ManagerSettings>("{\"InstanceList\":[]}")!;
        Require(!Settings.AllowExternalSupervisors && Settings.ExternalSupervisorAllowList.Count == 0, "legacy settings keep external supervisors disabled with an empty allowlist");
        var Task = JsonSerializer.Deserialize<RelayTask>("{\"Id\":\"old-task\",\"Channel\":{\"MainThreadId\":\"old-main\"}}")!;
        Require(Task.Channel.ExternalCaller.Length == 0 && Task.Channel.MainThreadId == "old-main", "legacy task routes retain Codex caller semantics");
    }

    private static async Task VerifyAuthorizationAsync()
    {
        using var Fixture = new Fixture();
        Fixture.Settings.AllowExternalSupervisors = false;
        await Fixture.RejectExternalAsync(new[] { "instances" }, "feature is disabled by default");
        Fixture.Settings.AllowExternalSupervisors = true;
        await Fixture.RejectExternalAsync(new[] { "instances" }, "unlisted external caller is denied", "unknown");
        foreach (string Name in new[] { "../claude", "..", "a/b", "a\\b", "claude:", "" })
            await Fixture.RejectExternalAsync(new[] { "instances" }, "invalid caller name is denied: " + Name, Name);
        var Instances = await Fixture.ExternalAsync(new[] { "instances" });
        Require(Instances.GetArrayLength() >= 2, "allowed external supervisor can list registered instances without a Codex thread");
        await Fixture.RejectRawAsync(new RelayRequest("external:claude", "external:claude", "agent_instances", JsonSerializer.SerializeToElement(new { })), "pseudo instance cannot be impersonated through ordinary Codex caller fields");
        Fixture.Settings.AllowExternalSupervisors = false;
        await Fixture.RejectRawAsync(new RelayRequest("", "", "relay_accept", JsonSerializer.SerializeToElement(new { task_id = "missing" })) { ExternalCaller = "claude" }, "direct relay_accept also enforces the external gate");
        Require(Fixture.Store.Count() == 0 && Fixture.Bridge.SentList.Count == 0, "unauthorized requests never reserve tasks or send work");
    }

    private static async Task VerifyDispatchAndReturnAsync()
    {
        using var Fixture = new Fixture();
        string? SavedThread = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        JsonElement Started;
        var Request = Start("create-without-thread", "primary", null);
        Request["create_title"] = "外部主管只读测试";
        try
        {
            Environment.SetEnvironmentVariable("CODEX_THREAD_ID", null);
            Started = await Fixture.StartExternalAsync(Request);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_THREAD_ID", SavedThread); }
        string Id = Started.GetProperty("task_id").GetString()!;
        string Worker = Started.GetProperty("thread_id").GetString()!;
        var Item = Fixture.Store.Get(Id);
        Require(Worker == "created-1" && Started.GetProperty("instance_id").GetString() == "primary", "external start creates a real worker conversation and returns its instance and thread");
        Require(Started.GetProperty("created").GetBoolean() && Started.GetProperty("session_kind").GetString() == "new", "start distinguishes newly created conversations");
        Require(Item.Channel.ExternalCaller == "claude" && Item.TreeDepth == 1 && Item.ParentTaskId.Length == 0, "external caller is recorded as a depth-zero root above its depth-one worker");
        Require(Fixture.Store.Count() == 1 && Fixture.Store.Reservations().Count == 1, "external supervisor itself consumes no task or tree slot");
        Require(Fixture.Catalog.ExternalSessionLookups == 0, "no external supervisor is resolved as a Codex conversation");
        await Fixture.StartExternalAsync(Request);
        Require(Fixture.Catalog.CreateCount == 1, "retrying the same external start never creates another conversation");
        var Immediate = await Fixture.ExternalAsync(new[] { "wait", "--task-id", Id, "--timeout-seconds", "0" });
        Require(Immediate.GetProperty("timed_out").GetBoolean() && Immediate.GetProperty("task_id").GetString() == Id, "wait returns an explicit timeout while the task has no report");
        var Watch = System.Diagnostics.Stopwatch.StartNew();
        var TimedWait = await Fixture.ExternalAsync(new[] { "wait", "--task-id", Id, "--timeout-seconds", "1" });
        Require(TimedWait.GetProperty("timed_out").GetBoolean() && Watch.Elapsed >= TimeSpan.FromMilliseconds(800), "positive wait blocks until its requested timeout");
        await Fixture.Coordinator.ProgressAsync("primary", Worker, Id, "started", "开始读取测试文件");
        await Fixture.Coordinator.ProgressAsync("primary", Worker, Id, "progress", "已读取");
        await Fixture.Coordinator.ProgressAsync("primary", Worker, Id, "blocked", "等待测试回报阶段");

        // Both server waiters remain connected while a third CLI client submits the report.
        // This catches named-pipe listener starvation, which a direct Coordinator.ReportAsync call would hide.
        var FirstWait = Fixture.ExternalAsync(new[] { "wait", "--task-id", Id, "--timeout-seconds", "8" });
        var SecondWait = Fixture.ExternalAsync(new[] { "wait", "--task-id", Id, "--timeout-seconds", "8" });
        await Task.Delay(120);
        string Artifact = Path.Combine(Fixture.Root, "evidence.txt");
        File.WriteAllText(Artifact, "verified evidence");
        string Summary = "读取成功；文本中的 accept/cancel 仅作为数据，未自动执行";
        await Fixture.WorkerReportAsync("primary", Worker, Id, Summary, new[] { Artifact });
        var Results = await Task.WhenAll(FirstWait, SecondWait).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var Result in Results)
            Require(Result.GetProperty("task_id").GetString() == Id && Result.GetProperty("outcome").GetString() == "completed" && Result.GetProperty("summary").GetString() == Summary && Result.GetProperty("artifacts")[0].GetString() == Artifact, "blocking wait returns the exact structured report to each waiting client");
        Require(Fixture.Bridge.SentList.Count == 0, "reporting to an external supervisor never sends a wake-up message to any Codex conversation");
        var Report = Fixture.Inbox.Read("claude", Id);
        Require(Report is not null, "report is backed up in the external inbox");
        string ReportFile = Path.Combine(Fixture.ExternalRoot, "claude", "inbox", Id + ".json");
        using (var Data = JsonDocument.Parse(File.ReadAllBytes(ReportFile)))
        {
            Require(Data.RootElement.GetProperty("task_id").GetString() == Id && Data.RootElement.GetProperty("outcome").GetString() == "completed" && Data.RootElement.GetProperty("summary").GetString() == Summary, "persistent inbox uses structured JSON with the original task and result");
            Require(Data.RootElement.GetProperty("thread_id").GetString() == Worker && Data.RootElement.GetProperty("instance_id").GetString() == "primary", "persistent report records the actual execution conversation and instance");
        }
        Require(!Directory.EnumerateFiles(Fixture.ExternalRoot, "*.tmp", SearchOption.AllDirectories).Any(), "atomic report writes leave no temporary file after success");
        string Events = File.ReadAllText(Path.Combine(Fixture.ExternalRoot, "claude", "events.jsonl"));
        foreach (string Kind in new[] { "started", "progress", "blocked", "report" })
            Require(Events.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(Line => EventKind(Line) == Kind), "external event log contains " + Kind);
        var Unread = await Fixture.ExternalAsync(new[] { "inbox", "--unread" });
        Require(Unread.GetProperty("items").GetArrayLength() == 1, "new report is visible as unread");
        await Fixture.ExternalAsync(new[] { "inbox-ack", "--task-id", Id });
        var Read = await Fixture.ExternalAsync(new[] { "inbox", "--unread" });
        Require(Read.GetProperty("items").GetArrayLength() == 0 && Fixture.Store.Get(Id).State != eRelayState.Accepted, "ack marks the report read without accepting its outcome");
        await Fixture.Coordinator.ReportAsync("primary", Worker, Id, "completed", Summary, new() { Artifact });
        Require(Fixture.Inbox.List("claude", true).Count == 0 && File.ReadAllText(Path.Combine(Fixture.ExternalRoot, "claude", "events.jsonl")) == Events, "identical report retries preserve acknowledgment and do not duplicate events");
        var AfterAck = await Fixture.ExternalAsync(new[] { "wait", "--task-id", Id, "--timeout-seconds", "0" });
        Require(AfterAck.GetProperty("summary").GetString() == Summary, "interrupted or repeated wait can recover an acknowledged report");
        await Fixture.ExternalAsync(new[] { "accept", "--task-id", Id });
        Require(Fixture.Store.Get(Id).State == eRelayState.Accepted, "explicit external acceptance completes the task");
        Fixture.Store.DeleteFinished(Id);
        Require(Fixture.Store.FindTask(Id) is null, "accepted task history can be deleted independently of external delivery backup");
        var AfterDeletion = await Fixture.ExternalAsync(new[] { "inbox" });
        Require(AfterDeletion.GetProperty("items").EnumerateArray().Any(Report => Report.GetProperty("task_id").GetString() == Id), "inbox keeps accepted reports after task history cleanup");
        var RecoveredAfterDeletion = await Fixture.ExternalAsync(new[] { "wait", "--task-id", Id, "--timeout-seconds", "0" });
        Require(RecoveredAfterDeletion.GetProperty("summary").GetString() == Summary, "wait recovers durable report even after accepted task history was deleted");
        var AckAfterDeletion = await Fixture.ExternalAsync(new[] { "inbox-ack", "--task-id", Id });
        Require(AckAfterDeletion.GetProperty("read").GetBoolean(), "inbox-ack continues to work after accepted task history was deleted");
        var Existing = Start("existing-session", "second", "second-worker");
        var Reused = await Fixture.StartExternalAsync(Existing);
        Require(Reused.GetProperty("thread_id").GetString() == "second-worker" && Fixture.Catalog.CreateCount == 1 && Fixture.Bridge.SentList.Single().Instance == "second", "external supervisor can dispatch to an existing session on the second instance");
        Require(!Reused.GetProperty("created").GetBoolean() && Reused.GetProperty("session_kind").GetString() == "existing", "start distinguishes existing conversations");
        await Fixture.ExternalAsync(new[] { "cancel", "--task-id", Reused.GetProperty("task_id").GetString()! });
    }

    private static async Task VerifyIsolationAsync()
    {
        using var Fixture = new Fixture();
        Fixture.Settings.ExternalSupervisorAllowList.Add("other");
        var Started = await Fixture.StartExternalAsync(Start("owner-task", "primary", "owner-worker"));
        string Id = Started.GetProperty("task_id").GetString()!;
        foreach (string Command in new[] { "status", "tree", "cancel", "wait", "inbox-ack", "accept" })
            await Fixture.RejectExternalAsync(new[] { Command, "--task-id", Id }, "another allowed external supervisor cannot use " + Command + " on this caller's task", "other");
        await Fixture.Coordinator.ReportAsync("primary", "owner-worker", Id, "completed", "owner report", new());
        Require(Fixture.Inbox.List("other").Count == 0, "external inbox data is isolated by caller");
        await Fixture.RejectRawAsync(new RelayRequest("primary", "unrelated-thread", "relay_accept", JsonSerializer.SerializeToElement(new { task_id = Id })), "unrelated Codex caller cannot accept external task");
        Fixture.Settings.AllowExternalSupervisors = false;
        await Fixture.RejectRawAsync(new RelayRequest("external:claude", "external:claude", "relay_accept", JsonSerializer.SerializeToElement(new { task_id = Id })), "raw pseudo identity cannot bypass a disabled external gate through relay_accept");
        Require(Fixture.Store.Get(Id).State == eRelayState.Returned, "forged relay acceptance leaves the externally owned task unaccepted");
        await Fixture.RejectExternalAsync(new[] { "wait", "--task-id", Id, "--timeout-seconds", "0" }, "disabling the gate also blocks already persisted reports");
        Fixture.Settings.AllowExternalSupervisors = true;
        Fixture.Settings.ExternalSupervisorAllowList.Remove("claude");
        await Fixture.RejectExternalAsync(new[] { "inbox" }, "removing a caller from the allowlist blocks inbox access");
        Require(Fixture.Inbox.Read("claude", Id) is not null && Fixture.Store.Get(Id).Outcome == "completed", "revoking access preserves the previously saved result");
    }

    private static async Task VerifyBudgetsAndWorkspacesAsync()
    {
        using var Fixture = new Fixture();
        var FirstRequest = Start("cross-budget", "primary", "budget-worker");
        var First = await Fixture.StartExternalAsync(FirstRequest);
        await Fixture.RejectStartAsync(Start("cross-over", "second", "second-worker"), "external pseudo instance obeys cross_agent_max");
        await Fixture.ExternalAsync(new[] { "cancel", "--task-id", Id(First) });

        var TreeRequest = Start("tree-budget", "primary", "tree-worker");
        TreeRequest["cross_agent_max"] = 3;
        TreeRequest["tree_session_max"] = 1;
        var Tree = await Fixture.StartExternalAsync(TreeRequest);
        var OverTree = Start("tree-over", "second", "other-worker");
        OverTree["cross_agent_max"] = 3;
        OverTree["tree_session_max"] = 1;
        await Fixture.RejectStartAsync(OverTree, "external root obeys the shared tree slot limit");
        await Fixture.ExternalAsync(new[] { "cancel", "--task-id", Id(Tree) });

        var DepthRequest = Start("depth-budget", "primary", "depth-worker");
        DepthRequest["max_depth"] = 1;
        var Depth = await Fixture.StartExternalAsync(DepthRequest);
        await Fixture.RejectWorkerStartAsync(Start("depth-over", "second", "depth-child"), "primary", "depth-worker", "external descendants cannot exceed inherited maximum depth");
        await Fixture.ExternalAsync(new[] { "cancel", "--task-id", Id(Depth) });

        var InvalidLimits = Start("worker-budget-over", "primary", "invalid-worker");
        InvalidLimits["worker_limits"] = new { subagent_max = 4, session_max = 1, cross_agent_max = 1 };
        await Fixture.RejectStartAsync(InvalidLimits, "external caller cannot grant worker limits above its own limits");
        var NoCross = Start("zero-cross", "primary", "zero-worker");
        NoCross["cross_agent_max"] = 0;
        NoCross["worker_limits"] = new { subagent_max = 0, session_max = 0, cross_agent_max = 0 };
        await Fixture.RejectStartAsync(NoCross, "zero external cross-agent budget prevents dispatch");

        string WriteRoot = Path.Combine(Fixture.Root, "workspace");
        var WriterRequest = Start("writer-root", "primary", "writer-worker");
        WriterRequest["cross_agent_max"] = 3;
        WriterRequest["workspace"] = new { access = "write", write_paths = new[] { WriteRoot } };
        var Writer = await Fixture.StartExternalAsync(WriterRequest);
        var Conflict = Start("writer-conflict", "second", "conflicting-worker");
        Conflict["cross_agent_max"] = 3;
        Conflict["workspace"] = new { access = "write", write_paths = new[] { Path.Combine(WriteRoot, "child") } };
        await Fixture.RejectStartAsync(Conflict, "external sibling writers cannot overlap declared paths");
        var Outside = Start("outside-write", "second", "outside-worker");
        Outside["workspace"] = new { access = "write", write_paths = new[] { Path.Combine(Fixture.Root, "outside") } };
        await Fixture.RejectWorkerStartAsync(Outside, "primary", "writer-worker", "descendants cannot write outside the external task's declared scope");
        await Fixture.ExternalAsync(new[] { "cancel", "--task-id", Id(Writer) });
        var ReadOnly = await Fixture.StartExternalAsync(Start("readonly-root", "primary", "readonly-worker"));
        await Fixture.RejectWorkerStartAsync(Outside, "primary", "readonly-worker", "external read-only tasks cannot delegate writes");
        await Fixture.ExternalAsync(new[] { "cancel", "--task-id", Id(ReadOnly) });
        var Undeclared = Start("undeclared", "primary", "undeclared-worker");
        Undeclared.Remove("workspace");
        await Fixture.RejectStartAsync(Undeclared, "external tasks require the same explicit workspace declaration as Codex tasks");
        Require(Fixture.Store.Reservations().Count == 0, "rejected budgets and workspaces never leave reservations behind");
    }

    private static async Task VerifyOfflineRecoveryAsync()
    {
        using var Fixture = new Fixture();
        var Started = await Fixture.StartExternalAsync(Start("offline-report", "primary", "offline-worker"));
        string TaskId = Id(Started);
        Fixture.StopHost();
        var ReportRequest = new RelayRequest("primary", "offline-worker", "relay_report", JsonSerializer.SerializeToElement(new { task_id = TaskId, outcome = "completed", summary = "离线时保存的真实回报", artifacts = Array.Empty<string>() }));
        string Pending = RelayOutbox.Save(ReportRequest, Fixture.OutboxRoot);
        Fixture.RestartCoordinator();
        var Recovered = await RelayOutbox.ReplayOnceAsync(Fixture.Coordinator, CancellationToken.None, Fixture.OutboxRoot);
        Require(Recovered == (0, 0) && !File.Exists(Pending), "constructor replays the worker's offline external report exactly once before ordinary outbox replay");
        Require(Fixture.Inbox.Read("claude", TaskId) is not null && Fixture.Store.Get(TaskId).State == eRelayState.Returned, "offline replay persists the external inbox without waking a Codex supervisor");
        Require(Fixture.Bridge.SentList.All(Send => Send.Instance == "primary"), "offline replay never sends to the external pseudo instance");
        var Saved = Fixture.Store.Get(TaskId);
        Saved.State = eRelayState.Returning;
        Saved.ReturnNotificationQueued = true;
        Fixture.Store.Save(Saved);
        File.Delete(Path.Combine(Fixture.ExternalRoot, "claude", "inbox", TaskId + ".json"));
        Fixture.RestartCoordinator();
        Fixture.Coordinator.StartQueuedReturns();
        await UntilAsync(() => Fixture.Inbox.Read("claude", TaskId) is not null && Fixture.Store.Get(TaskId).State == eRelayState.Returned);
        Require(Fixture.Inbox.List("claude").Count == 1, "queued external returns survive a second restart and are delivered idempotently");
        Saved = Fixture.Store.Get(TaskId);
        Saved.State = eRelayState.ReturnUncertain;
        Saved.ReturnNotificationQueued = false;
        Fixture.Store.Save(Saved);
        File.Delete(Path.Combine(Fixture.ExternalRoot, "claude", "inbox", TaskId + ".json"));
        Fixture.RestartCoordinator();
        Require(Fixture.Inbox.Read("claude", TaskId) is not null && Fixture.Store.Get(TaskId).State == eRelayState.Returned, "restart reconstructs a saved external report even when the queued flag was already cleared");
        Fixture.StartHost();
        var Wait = await Fixture.ExternalAsync(new[] { "wait", "--task-id", TaskId, "--timeout-seconds", "0" });
        Require(Wait.GetProperty("summary").GetString() == "离线时保存的真实回报", "wait can recover the original report after manager restart");
    }

    private static async Task VerifyLargeInboxPaginationAsync()
    {
        using var Fixture = new Fixture();
        var Expected = new Dictionary<string, string>();
        for (int Index = 0; Index < 5; Index++)
        {
            string Worker = "large-worker-" + Index;
            var Started = await Fixture.StartExternalAsync(Start("large-report-" + Index, "primary", Worker));
            string Summary = new string('测', 14990) + "回报" + Index;
            Expected.Add(Id(Started), Summary);
            await Fixture.WorkerReportAsync("primary", Worker, Id(Started), Summary, Array.Empty<string>());
        }
        var Seen = new HashSet<string>();
        var Cursors = new HashSet<string>();
        string? After = null;
        int Pages = 0;
        do
        {
            string[] Arguments = After is null ? new[] { "inbox" } : new[] { "inbox", "--after", After };
            var Page = await Fixture.ExternalAsync(Arguments);
            Require(JsonSerializer.SerializeToUtf8Bytes(new { result = Page }).Length <= RelayLimits.MaxFrameBytes, "large inbox page stays within the named-pipe frame limit");
            var Items = Page.GetProperty("items");
            Require(Items.GetArrayLength() > 0, "every large-report page contains at least one item");
            foreach (var Report in Items.EnumerateArray())
            {
                string TaskId = Report.GetProperty("task_id").GetString()!;
                Require(Seen.Add(TaskId) && Expected.TryGetValue(TaskId, out string? Summary) && Report.GetProperty("summary").GetString() == Summary, "paged inbox preserves every large Unicode report once without truncation");
            }
            After = Page.GetProperty("next_after").ValueKind == JsonValueKind.Null ? null : Page.GetProperty("next_after").GetString();
            if (After is not null) Require(Cursors.Add(After), "inbox pagination cursor advances");
            Require(++Pages <= 5, "large inbox pagination terminates within the number of reports");
        }
        while (After is not null);
        Require(Pages > 1 && Seen.SetEquals(Expected.Keys), "byte-limited inbox pagination returns all five reports without omission");
    }

    private static async Task VerifyPendingBeforeRecoveryAsync()
    {
        using var Fixture = new Fixture();
        Fixture.StopHost();
        var Tasks = new List<(RelayTask Task, string Pending)>();
        foreach (string Instance in new[] { "primary", CursorRelayRunner.InstanceId, DshRelayRunner.InstanceId })
        {
            var Item = ManualExternalTask(Fixture, "pending-" + Instance, Instance, eRelayState.Running);
            Item.Channel.TimeoutMinutes = 1;
            Item.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            Item.DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(-4);
            Fixture.Store.Save(Item);
            Tasks.Add((Item, SavePending(Fixture, Item, "离线完成：" + Instance)));
        }
        var Cancelled = ManualExternalTask(Fixture, "pending-cancelled", "primary", eRelayState.Cancelled);
        Fixture.Store.Save(Cancelled);
        SavePending(Fixture, Cancelled, "撤销后的回报必须拒绝");
        Fixture.RestartCoordinator();
        foreach (var Entry in Tasks)
        {
            var Restored = Fixture.Store.Get(Entry.Task.Id);
            Require(Restored.State == eRelayState.Returned && Restored.Outcome == "completed" && Restored.Result == "离线完成：" + Entry.Task.Channel.WorkerInstance.Id, "saved offline result precedes expired deadline or runner restart interruption for " + Entry.Task.Channel.WorkerInstance.Id);
            Require(!File.Exists(Entry.Pending) && Fixture.Inbox.Read("claude", Entry.Task.Id) is not null, "restarted manager confirms pending outbox and writes inbox for " + Entry.Task.Channel.WorkerInstance.Id);
        }
        Require(Fixture.Store.Get(Cancelled.Id).State == eRelayState.Cancelled && Fixture.Store.Get(Cancelled.Id).Outcome.Length == 0 && Fixture.Inbox.Read("claude", Cancelled.Id) is null, "a genuinely cancelled task never recovers a late pending report");
        Require(Fixture.Bridge.SentList.Count == 0, "pre-deadline pending recovery starts no model and sends no wake-up messages");
        Fixture.StartHost();
        foreach (var Entry in Tasks)
        {
            var Report = await Fixture.ExternalAsync(new[] { "wait", "--task-id", Entry.Task.Id, "--timeout-seconds", "0" });
            Require(Report.GetProperty("outcome").GetString() == "completed", "wait retrieves startup-recovered report from " + Entry.Task.Channel.WorkerInstance.Id);
        }
    }

    private static async Task VerifyImmediateAcceptanceAsync()
    {
        using var Fixture = new Fixture();
        var Route = new RelayChannel { MainInstance = Fixture.Settings.InstanceList[0], MainThreadId = "slow-main", WorkerInstance = Fixture.Settings.InstanceList[1], WorkerThreadId = "slow-worker", TimeoutMinutes = 0 };
        var Slow = await Fixture.Coordinator.DispatchAsync("primary", "slow-main", "slow-codex-return", "普通 Codex 回传", "只读测试", "核对结果", Route, Workspace: new() { Access = "read_only" });
        Fixture.Bridge.BlockThreadId = "slow-main";
        var SlowReturn = Fixture.Coordinator.ReportAsync("second", "slow-worker", Slow.Id, "completed", "缓慢的桌面通知", new());
        await Fixture.Bridge.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var Fast = await Fixture.StartExternalAsync(Start("immediate-accept", "primary", "fast-worker"));
            string FastId = Id(Fast);
            var FastReport = await Fixture.Coordinator.ReportAsync("primary", "fast-worker", FastId, "completed", "外部回报不依赖其他桌面通知", new()).WaitAsync(TimeSpan.FromSeconds(2));
            Require(FastReport.Outcome == "completed" && !SlowReturn.IsCompleted, "default report notification mode completes for external callers while an unrelated Codex return remains blocked");
            await Fixture.ExternalAsync(new[] { "accept", "--task-id", FastId });
            Require(Fixture.Store.Get(FastId).State == eRelayState.Accepted && Fixture.Inbox.Read("claude", FastId)?.Summary == "外部回报不依赖其他桌面通知", "report immediately followed by acceptance preserves external inbox while another Codex return holds ReturnGate");
        }
        finally
        {
            Fixture.Bridge.BlockThreadId = null;
            Fixture.Bridge.ReleaseSend.TrySetResult();
            await SlowReturn.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task VerifyEventTailRecoveryAsync()
    {
        using var Fixture = new Fixture();
        var Started = await Fixture.StartExternalAsync(Start("torn-event-tail", "primary", "event-worker"));
        string TaskId = Id(Started);
        await Fixture.Coordinator.ProgressAsync("primary", "event-worker", TaskId, "started", "开始读取");
        await Fixture.Coordinator.ProgressAsync("primary", "event-worker", TaskId, "progress", "读取完成");
        await Fixture.Coordinator.ReportAsync("primary", "event-worker", TaskId, "completed", "事件持久化测试完成", new());
        string EventFile = Path.Combine(Fixture.ExternalRoot, "claude", "events.jsonl");
        string[] OriginalLines = File.ReadAllLines(EventFile);
        var ExpectedIds = EventIds(OriginalLines);
        File.AppendAllText(EventFile, "{\"event_id\":\"interrupted-append\",\"task_id\":", new UTF8Encoding(false));
        Fixture.RestartCoordinator();
        var RepairedIds = EventIds(File.ReadAllLines(EventFile));
        Require(RepairedIds.Count == ExpectedIds.Count && RepairedIds.Distinct().Count() == RepairedIds.Count && RepairedIds.ToHashSet().SetEquals(ExpectedIds), "restart removes a partially appended JSON tail while retaining every original event exactly once");

        // Simulate the final real event being torn during append; its durable task journal must replay it.
        string TornJournal = string.Join("\n", OriginalLines.Take(OriginalLines.Length - 1)) + "\n" + OriginalLines[^1][..(OriginalLines[^1].Length / 2)];
        File.WriteAllText(EventFile, TornJournal, new UTF8Encoding(false));
        Fixture.RestartCoordinator();
        RepairedIds = EventIds(File.ReadAllLines(EventFile));
        Require(RepairedIds.Count == ExpectedIds.Count && RepairedIds.Distinct().Count() == RepairedIds.Count && RepairedIds.ToHashSet().SetEquals(ExpectedIds), "restart reconstructs a torn real event from its task journal without malformed lines or duplicates");
    }

    private static async Task VerifyExternalTaskValidityAsync()
    {
        using var Fixture = new Fixture();
        var Item = ManualExternalTask(Fixture, "external-validity", "primary", eRelayState.Returned);
        Item.Outcome = "completed";
        Item.Result = "待外部主管验收";
        Fixture.Store.Save(Item);
        var Probe = new IdleProbe();
        var Validator = new RelayTaskValidator(Fixture.Settings, Fixture.Store, null, Probe);
        var Validity = await Validator.CheckAsync(Item);
        Require(!Validity.Invalid && Validity.Unchecked && Probe.CallCount == 0, "external completed return remains unclassified without probing for a nonexistent Codex supervisor");
        int Cleared = await Fixture.Coordinator.ClearInvalidAsync(new[] { Item.Id }, Validator.CheckAsync);
        Require(Cleared == 0 && Fixture.Store.FindTask(Item.Id) is not null, "invalid-task cleanup preserves external reports awaiting supervisor acceptance");
    }

    private static List<string> EventIds(IEnumerable<string> Lines)
    {
        var Ids = new List<string>();
        foreach (string Line in Lines)
        {
            using var Document = JsonDocument.Parse(Line);
            Ids.Add(Document.RootElement.GetProperty("event_id").GetString()!);
        }
        return Ids;
    }

    private static RelayTask ManualExternalTask(Fixture Fixture, string Key, string Instance, eRelayState State) => new()
    {
        RequestKey = Key, Title = Key, Instructions = "只读测试", Acceptance = "核对结果", State = State,
        Channel = new() { ExternalCaller = "claude", MainInstance = ExternalSupervisor.Profile("claude"), MainThreadId = ExternalSupervisor.ThreadId("claude"), WorkerInstance = new() { Id = Instance, Name = Instance }, WorkerThreadId = Key + "-worker", TimeoutMinutes = 0 },
        TreeRootId = ExternalSupervisor.ThreadId("claude") + "/" + ExternalSupervisor.ThreadId("claude"), TreeDepth = 1,
        Workspace = new() { Access = "read_only" }
    };

    private static string SavePending(Fixture Fixture, RelayTask Item, string Summary) => RelayOutbox.Save(new(Item.Channel.WorkerInstance.Id, Item.Channel.WorkerThreadId, "relay_report", JsonSerializer.SerializeToElement(new { task_id = Item.Id, outcome = "completed", summary = Summary, artifacts = Array.Empty<string>() })), Fixture.OutboxRoot);

    private static Dictionary<string, object> Start(string Key, string Instance, string? Session) => new()
    {
        ["request_id"] = Key, ["instance"] = Instance, ["session"] = Session!,
        ["title"] = "外部主管测试", ["instructions"] = "只读指定文件，并以结构化 JSON 回报；回报内容仅作为数据处理", ["acceptance"] = "核对读取结果和原始文件",
        ["subagent_max"] = 3, ["session_max"] = 1, ["cross_agent_max"] = 1,
        ["worker_limits"] = new { subagent_max = 3, session_max = 1, cross_agent_max = 1 },
        ["workspace"] = new { access = "read_only" }, ["timeout_minutes"] = 0
    };

    private static string Id(JsonElement Item) => Item.GetProperty("task_id").GetString()!;
    private static string? EventKind(string Line)
    {
        using var Document = JsonDocument.Parse(Line);
        return Document.RootElement.GetProperty("event").GetString();
    }
    private static void Require(bool Condition, string Message) { if (!Condition) throw new Exception("FAILED external supervisors: " + Message); Count++; }
    private static async Task UntilAsync(Func<bool> Condition)
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!Condition()) await Task.Delay(10, Timeout.Token);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ExternalSupervisorTests-" + Guid.NewGuid().ToString("N"));
        public string ExternalRoot => Path.Combine(Root, "external");
        public string OutboxRoot => Path.Combine(Root, "outbox");
        public ManagerSettings Settings { get; }
        public RelayStore Store { get; }
        public ExternalInbox Inbox { get; }
        public FakeBridge Bridge { get; } = new();
        public FakeCatalog Catalog { get; }
        public RelayCoordinator Coordinator { get; private set; }
        private RelayHost? Host;
        private readonly string Pipe = "ExternalSupervisorTests-" + Guid.NewGuid().ToString("N");

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Settings = new() { AllowExternalSupervisors = true, ExternalSupervisorAllowList = new() { "claude" }, InstanceList = new() { new() { Id = "primary", Name = "主实例", HomePath = Path.Combine(Root, "primary"), IsPrimary = true }, new() { Id = "second", Name = "第二实例", HomePath = Path.Combine(Root, "second") } } };
            Store = new(Path.Combine(Root, "relay"));
            Inbox = new(ExternalRoot);
            Catalog = new(Root);
            Coordinator = new(Store, Bridge, ExternalInbox: Inbox, OutboxRoot: OutboxRoot);
            StartHost();
        }

        public void StopHost() { Host?.Dispose(); Host = null; }
        public void StartHost()
        {
            if (Host is not null) throw new InvalidOperationException("fixture host already started");
            Host = new(Coordinator, Pipe, new(Store, Coordinator, Catalog, Environment.ProcessPath!, () => Settings));
        }
        public void RestartCoordinator()
        {
            StopHost();
            Coordinator.Dispose();
            Coordinator = new(Store, Bridge, ExternalInbox: Inbox, OutboxRoot: OutboxRoot);
        }

        public async Task<JsonElement> ExternalAsync(string[] Arguments, string Name = "claude")
        {
            var Result = await CliAsync(new[] { "--external-caller", Name }.Concat(Arguments).ToArray());
            if (Result.Exit != 0) throw new InvalidOperationException(Result.Error);
            return Result.Result;
        }
        public async Task RejectExternalAsync(string[] Arguments, string Message, string Name = "claude")
        {
            var Result = await CliAsync(new[] { "--external-caller", Name }.Concat(Arguments).ToArray());
            Require(Result.Exit != 0 && Result.Error.Length != 0, Message);
        }
        public Task<JsonElement> StartExternalAsync(Dictionary<string, object> Request) => ExternalAsync(new[] { "start", "--json-file", JsonFile(Request) });
        public Task RejectStartAsync(Dictionary<string, object> Request, string Message) => RejectExternalAsync(new[] { "start", "--json-file", JsonFile(Request) }, Message);
        public async Task RejectWorkerStartAsync(Dictionary<string, object> Request, string Instance, string Thread, string Message)
        {
            var Result = await CliAsync(new[] { "start", "--json-file", JsonFile(Request), "--caller-instance", Instance, "--caller-thread", Thread });
            Require(Result.Exit != 0 && Result.Error.Length != 0, Message);
        }
        public async Task WorkerReportAsync(string Instance, string Thread, string TaskId, string Summary, string[] Artifacts)
        {
            var Result = await CliAsync(new[] { "report", "--json-file", JsonFile(new { task_id = TaskId, outcome = "completed", summary = Summary, artifacts = Artifacts }), "--caller-instance", Instance, "--caller-thread", Thread });
            if (Result.Exit != 0) throw new InvalidOperationException(Result.Error);
        }
        public async Task RejectRawAsync(RelayRequest Request, string Message)
        {
            try { await RelayHost.RequestAsync(Request, CancellationToken.None, Pipe); }
            catch (InvalidOperationException) { Count++; return; }
            throw new Exception("FAILED external supervisors: " + Message);
        }
        private async Task<(int Exit, JsonElement Result, string Error)> CliAsync(string[] Arguments)
        {
            using var Input = new MemoryStream();
            using var Output = new MemoryStream();
            using var Error = new StringWriter();
            using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            int Exit = await AgentCli.RunAsync(Arguments, Input, Output, Error, Environment.ProcessPath!, Timeout.Token, Pipe, () => Settings, OutboxRoot);
            if (Exit != 0) return (Exit, default, Error.ToString());
            using var Data = JsonDocument.Parse(Output.ToArray());
            return (Exit, Data.RootElement.Clone(), Error.ToString());
        }
        private string JsonFile(object Value)
        {
            string FilePath = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Value), new UTF8Encoding(false));
            return FilePath;
        }
        public void Dispose()
        {
            StopHost();
            Coordinator.Dispose();
            SqliteConnection.ClearAllPools();
            if (!ManagerPaths.IsUnder(Root, Path.GetTempPath())) throw new IOException("测试清理路径越界");
            Directory.Delete(Root, true);
        }
    }

    private sealed class FakeBridge : IRelayBridge
    {
        public List<(string Instance, string Thread, string Prompt)> SentList { get; } = new();
        public string? BlockThreadId { get; set; }
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendAsync(InstanceProfile Instance, string ThreadId, string Prompt, string Model, CancellationToken Cancellation)
        {
            SentList.Add((Instance.Id, ThreadId, Prompt));
            if (ThreadId == BlockThreadId)
            {
                SendStarted.TrySetResult();
                return ReleaseSend.Task.WaitAsync(Cancellation);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class IdleProbe : IRelayActivityProbe
    {
        public int CallCount { get; private set; }
        public Task<RelayActivity> ReadAsync(InstanceProfile Instance, string ThreadId, CancellationToken Cancellation)
        {
            CallCount++;
            return Task.FromResult(new RelayActivity(eRelayActivity.Idle, "fixture idle"));
        }
    }

    private sealed class FakeCatalog : IAgentCatalog
    {
        private readonly string Root;
        public int ExternalSessionLookups { get; private set; }
        public int CreateCount { get; private set; }
        public FakeCatalog(string Root) => this.Root = Root;
        public Task<List<AgentProject>> ProjectsAsync(InstanceProfile Instance, CancellationToken Cancellation) => Task.FromResult(new List<AgentProject> { new("project", "测试项目", Root, true) });
        public AgentSession Session(InstanceProfile Instance, string Id)
        {
            if (Instance.Id is not ("primary" or "second")) { ExternalSessionLookups++; throw new InvalidOperationException("external supervisor is not a Codex session"); }
            return new(Id, Id, "", Root);
        }
        public List<AgentSession> Sessions(InstanceProfile Instance, AgentProject? Project, string? Selector, int Offset) => new() { Session(Instance, Selector ?? "listed-worker") };
        public Task RequireIdleAsync(InstanceProfile Instance, string ThreadId, CancellationToken Cancellation) => Task.CompletedTask;
        public Task<RelayThread> CreateAsync(InstanceProfile Instance, AgentProject? Project, string Title, string Prompt, CancellationToken Cancellation)
        {
            CreateCount++;
            Require(Prompt.Contains("report --json-file") && Prompt.Contains("CODEX_THREAD_ID"), "worker callback retains its actual Codex identity contract");
            return Task.FromResult(new RelayThread("created-" + CreateCount, Title, "active"));
        }
    }
}
