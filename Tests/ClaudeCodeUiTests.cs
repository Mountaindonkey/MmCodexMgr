using CodexInstanceManager;

internal static class ClaudeCodeUiTests
{
    private static int Count;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "MmClaudeUi-" + Guid.NewGuid().ToString("N"));

    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(Root);
        try
        {
            foreach (bool Dark in new[] { false, true })
                foreach (var Size in new[] { new Size(760, 470), new Size(1000, 680) }) VerifyPage(Dark, Size);
            VerifyRelations();
            Console.WriteLine($"PASS Claude Code UI {Count} assertions");
            return 0;
        }
        finally { if (!ManagerPaths.IsUnder(Root, Path.GetTempPath())) throw new IOException("Claude UI 测试清理越界"); Directory.Delete(Root, true); }
    }

    private static void VerifyPage(bool Dark, Size Size)
    {
        var Settings = new ManagerSettings { ExternalSupervisorAllowList = new() { "reviewer" } };
        var Sessions = new ClaudeSessionStore(Path.Combine(Root, Guid.NewGuid().ToString("N")));
        int Saves = 0, Starts = 0, Checks = 0, Boards = 0, Probes = 0;
        string Copied = "";
        var CheckFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var Form = new Form { ClientSize = Size, Font = new Font("Microsoft YaHei UI", 10), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        using var Page = new ClaudeCodePage(Settings, () => Boards++,
            SaveSettings: Saved => { Require(ReferenceEquals(Saved, Settings), "save uses the shared manager settings object"); Saves++; },
            Check: (Saved, _) => { Checks++; CheckFinished.TrySetResult(); return Task.FromResult(new ClaudeCodeCheck("test-claude.exe", "test-version", true, "api_key", "firstParty")); },
            LaunchSession: (Saved, _, Session, Resume) => { Require(Saved.CallerName == "claude" && Directory.Exists(Saved.WorkingDirectory) && Session is not null, "launch uses the saved caller, directory and exact registered session"); Starts++; },
            Copy: Text => Copied = Text,
            ProbeManager: (Name, _) => { Require(Name == "claude", "manager probe uses the authorized external caller"); Probes++; return Task.CompletedTask; }, Sessions: Sessions);
        Form.Controls.Add(Page);
        new AppTheme(Dark).Apply(Form);
        Form.Show();
        Application.DoEvents();
        var Caller = Descendants(Page).OfType<TextBox>().Single(Item => Item.Name == "ClaudeCallerName");
        var Cwd = Descendants(Page).OfType<TextBox>().Single(Item => Item.Name == "ClaudeWorkingDirectory");
        var Enabled = Descendants(Page).OfType<CheckBox>().Single(Item => Item.Name == "ClaudeAllowSupervisor");
        var Prompt = Descendants(Page).OfType<TextBox>().Single(Item => Item.Name == "ClaudeSupervisorInstructions");
        var Title = Descendants(Page).OfType<TextBox>().Single(Item => Item.Name == "ClaudeSessionTitle");
        var SessionGrid = Descendants(Page).OfType<DataGridView>().Single(Item => Item.Name == "ClaudeSessions");
        Button Button(string Name) => Descendants(Page).OfType<Button>().Single(Item => Item.Name == Name);
        Require(Caller.Text == "claude" && Directory.Exists(Cwd.Text) && !Enabled.Checked, "initial page provides a usable directory and disabled default caller");
        Require(Saves == 0 && Starts == 0 && Checks == 0 && Settings.ClaudeCode.WorkingDirectory.Length == 0, "opening the page does not save, probe or launch a model");
        Require(Prompt.ReadOnly && SessionGrid.RowCount == 0, "initial page reads only registered sessions without inventing an active Claude process");
        Button("ClaudeLaunch").PerformClick();
        Require(Starts == 0 && !Settings.AllowExternalSupervisors, "launch cannot enable authorization implicitly");
        Button("ClaudeConnectionSettings").PerformClick();
        Application.DoEvents();
        Caller.Text = " Claude ";
        Enabled.Checked = true;
        Button("ClaudeSave").PerformClick();
        Require(Saves == 1 && Settings.AllowExternalSupervisors && Settings.ExternalSupervisorAllowList.Contains("claude") && Settings.ExternalSupervisorAllowList.Contains("reviewer"), "save enables the normalized caller while preserving other supervisors");
        Require(Settings.ClaudeCode.CallerName == "claude" && Settings.ClaudeCode.WorkingDirectory == Cwd.Text, "save persists the effective working directory and caller");
        Title.Text = "资源主管";
        Button("ClaudeLaunch").PerformClick();
        Require(Starts == 1, "saved authorized connection launches through its injected service");
        var Registered = Sessions.List("claude").Single();
        Require(Registered.Title == "资源主管" && Guid.TryParse(Registered.Id, out _) && !Registered.Recorded && SessionGrid.Rows[0].Cells["state"].Value?.ToString() == "待首条消息确认", "new session has its chosen name and accurate UUID without claiming a running process");
        Require(Prompt.Text.Contains("--external-session-id " + Registered.Id) && Prompt.Text.Contains("primary") && Prompt.Text.Contains("second") && Prompt.Text.Contains("wait") && Prompt.Text.Contains("accept"), "selected session instructions preserve the exact UUID throughout the actual workflow");
        Button("ClaudeResume").PerformClick();
        Require(Starts == 1, "unconfirmed registration cannot pretend to resume an existing Claude history");
        string SavedCwd = Cwd.Text;
        Cwd.Text = Path.Combine(Path.GetTempPath(), "MissingClaudeUi-" + Guid.NewGuid().ToString("N"));
        Button("ClaudeLaunch").PerformClick();
        Require(Starts == 1 && Saves == 1, "invalid working directory prevents launch without altering saved settings");
        Cwd.Text = SavedCwd;
        Caller.Text = "different";
        Button("ClaudeLaunch").PerformClick();
        Require(Starts == 1 && !Settings.ExternalSupervisorAllowList.Contains("different"), "unsaved identity cannot reuse another caller's authorization");
        Caller.Text = "claude";
        Button("ClaudeCopy").PerformClick();
        Require(Copied == Prompt.Text && Copied.Contains("--external-caller claude"), "copy exposes the same current supervisor instructions as the preview");
        Button("ClaudeCheck").PerformClick();
        Application.DoEvents();
        Require(CheckFinished.Task.IsCompleted && Checks == 1 && Probes == 1 && Saves == 1 && Starts == 1, "connection check probes the manager once without saving or launching work");
        Button("ClaudeBoard").PerformClick();
        Require(Boards == 1, "task board action routes to the shared board");
        Enabled.Checked = false;
        Button("ClaudeSave").PerformClick();
        Require(Saves == 2 && Settings.AllowExternalSupervisors && !Settings.ExternalSupervisorAllowList.Contains("claude") && Settings.ExternalSupervisorAllowList.Contains("reviewer"), "disabling this caller preserves other external authorizations");
        Button("ClaudeCheck").PerformClick();
        Application.DoEvents();
        Require(Checks == 2 && Probes == 1 && Saves == 2 && Starts == 1, "disabled caller can inspect local Claude state without probing the manager as an unauthorized identity");
        Settings.ExternalSupervisorAllowList.Add("claude");
        Page.RefreshAuthorization();
        Require(Enabled.Checked, "external authorization changes refresh into the existing page");
        Registered.Recorded = true;
        Sessions.Save(Registered);
        Page.RefreshSessions(false, Registered.Id);
        Button("ClaudeResume").PerformClick();
        Require(Starts == 2 && SessionGrid.Rows[0].Cells["state"].Value?.ToString() == "已发现会话", "confirmed history can resume its exact registered session");
        string ImportedId = Guid.NewGuid().ToString("D");
        Page.ImportSession(ImportedId, "已有审核主管");
        Require(Sessions.List("claude").Count == 2 && SessionGrid.CurrentRow!.Cells["id"].Value?.ToString() == ImportedId && !Sessions.Get("claude", ImportedId).Recorded, "import registers and selects the supplied UUID without claiming observed history");
        Button("ClaudeConnectionSettings").PerformClick();
        Application.DoEvents();
        foreach (var Control in Descendants(Page).Where(Item => Item.Visible && Item is Label or TextBox or CheckBox or System.Windows.Forms.Button))
            Require(Control.Parent!.ClientRectangle.Contains(Control.Bounds), $"Claude control fits dark={Dark} size={Size} name={Control.Name} bounds={Control.Bounds}");
    }

    private static void VerifyRelations()
    {
        string SessionA = Guid.NewGuid().ToString("D"), SessionB = Guid.NewGuid().ToString("D");
        var Sessions = new ClaudeSessionStore(Path.Combine(Root, "relations-sessions"));
        Sessions.Save(new() { Id = SessionA, Title = "资源主管", CallerName = "claude", Cwd = Root, Recorded = true });
        Sessions.Save(new() { Id = SessionB, Title = "审核主管", CallerName = "claude", Cwd = Root, Recorded = true });
        var Settings = new ManagerSettings
        {
            AllowExternalSupervisors = true, ExternalSupervisorAllowList = new() { "claude" },
            InstanceList = new() { new() { Id = "primary", Name = "实例一" }, new() { Id = "second", Name = "实例二" } }
        };
        var Store = new RelayStore(Path.Combine(Root, "relations-tasks"));
        RelayTask Task(string Key, string Session, string Agent, string Caller = "claude") => new()
        {
            RequestKey = Key, Title = Key, State = eRelayState.Accepted,
            Channel = new() { ExternalCaller = Caller, ExternalSessionId = Session, MainInstance = new() { Id = "external:" + Caller, Name = Caller }, MainThreadId = "external:" + Caller,
                WorkerInstance = new() { Id = Agent, Name = Agent }, WorkerTitle = "执行会话 " + Key, WorkerThreadId = Guid.NewGuid().ToString("D") }
        };
        Store.Save(Task("资源任务一", SessionA, "primary"));
        Store.Save(Task("资源任务二", SessionA, "second"));
        Store.Save(Task("审核任务", SessionB, "primary"));
        Store.Save(Task("旧主管任务", "", "second"));
        Store.Save(Task("其他调用方任务", SessionA, "primary", "reviewer"));
        using var Coordinator = new RelayCoordinator(Store, new NoSendBridge(), ExternalInbox: new ExternalInbox(Path.Combine(Root, "relations-inbox")));
        using var Form = new Form { ClientSize = new Size(980, 700), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        using var Page = new ClaudeCodePage(Settings, () => { }, SaveSettings: _ => throw new Exception("viewing relations must not save settings"), Sessions: Sessions, Store: Store, Coordinator: Coordinator);
        Form.Controls.Add(Page);
        Form.Show();
        Application.DoEvents();
        var Grid = Descendants(Page).OfType<DataGridView>().Single(Item => Item.Name == "ClaudeTaskRelations");
        var SessionGrid = Descendants(Page).OfType<DataGridView>().Single(Item => Item.Name == "ClaudeSessions");
        Require(Grid.RowCount == 4, "relations show only the current caller's tasks across all registered sessions");
        var Owned = Grid.Rows.Cast<DataGridViewRow>().Where(Row => Row.Cells["source_id"].Value?.ToString() == SessionA).ToList();
        Require(Owned.Count == 2 && Owned.All(Row => Row.Cells["source_title"].Value?.ToString() == "资源主管") && Owned.Select(Row => Row.Cells["agent"].Value?.ToString()).ToHashSet().SetEquals(new[] { "实例一", "实例二" }), "one named Claude session maps to multiple tasks and different target instances");
        Require(Owned.All(Row => Row.Cells["worker_title"].Value?.ToString()?.StartsWith("执行会话 ") == true && Guid.TryParse(Row.Cells["worker_id"].Value?.ToString(), out _)), "relations retain target session names and exact IDs");
        var Legacy = Grid.Rows.Cast<DataGridViewRow>().Single(Row => Row.Cells["task"].Value?.ToString() == "旧主管任务");
        Require(Legacy.Cells["source_title"].Value?.ToString() == "未关联 Claude 会话" && Legacy.Cells["source_id"].Value?.ToString() == "未关联", "legacy tasks without source UUID remain visibly unassociated");
        SessionGrid.CurrentCell = SessionGrid.Rows[1].Cells[0];
        Page.RefreshRelations();
        Require(Grid.RowCount == 4 && Grid.Columns.Contains("details") && Grid.Columns.Contains("open"), "selection does not hide other relations and each relation offers details and target navigation");
        var NewTask = Task("事件更新任务", SessionB, "second");
        NewTask.State = eRelayState.Running;
        Store.Save(NewTask);
        Coordinator.ProgressAsync(NewTask.Channel.WorkerInstance.Id, NewTask.Channel.WorkerThreadId, NewTask.Id, "started", "已开始只读任务").GetAwaiter().GetResult();
        Application.DoEvents();
        Require(Grid.RowCount == 5 && Grid.Rows.Cast<DataGridViewRow>().Any(Row => Row.Cells["task"].Value?.ToString() == "事件更新任务"), "coordinator changes refresh existing task records without a polling loop");
    }

    private sealed class NoSendBridge : IRelayBridge
    {
        public Task SendAsync(InstanceProfile Instance, string ThreadId, string Prompt, string Model, CancellationToken Cancellation) => throw new Exception("UI fixture must never send model tasks");
    }

    private static IEnumerable<Control> Descendants(Control Parent)
    {
        foreach (Control Child in Parent.Controls)
        {
            yield return Child;
            foreach (var Nested in Descendants(Child)) yield return Nested;
        }
    }

    private static void Require(bool Condition, string Message)
    {
        if (!Condition) throw new Exception("FAILED Claude Code UI: " + Message);
        Count++;
    }
}
