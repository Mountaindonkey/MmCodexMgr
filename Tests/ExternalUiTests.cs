using System.Text.Json;
using CodexInstanceManager;

internal static class ExternalUiTests
{
    private static int Count;

    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        var Legacy = JsonSerializer.Deserialize<ManagerSettings>("{\"InstanceList\":[]}")!;
        Require(!Legacy.AllowExternalSupervisors && Legacy.ExternalSupervisorAllowList.Count == 0, "old settings leave external supervisors disabled with an empty allowlist");
        foreach (bool Dark in new[] { false, true }) VerifySettings(Dark);
        VerifyTree();
        Console.WriteLine($"PASS external supervisor UI {Count} assertions");
        return 0;
    }

    private static void VerifySettings(bool Dark)
    {
        var Settings = new ManagerSettings();
        int Saves = 0;
        using var Form = new Form { ClientSize = new Size(980, 700), Font = new Font("Microsoft YaHei UI", 10), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        using var Page = new ManagerSettingsPage(Settings: Settings, SaveSettings: Saved =>
        {
            Require(ReferenceEquals(Saved, Settings), "settings page saves the manager's shared configuration instance");
            Saves++;
        });
        Form.Controls.Add(Page);
        new AppTheme(Dark).Apply(Form);
        Form.Show();
        Application.DoEvents();
        var Enabled = Descendants(Page).OfType<CheckBox>().Single(Item => Item.Name == "AllowExternalSupervisors");
        var Names = Descendants(Page).OfType<TextBox>().Single(Item => Item.Name == "ExternalSupervisorAllowList");
        var Save = Descendants(Page).OfType<Button>().Single(Item => Item.Name == "SaveExternalSupervisors");
        Require(!Enabled.Checked && Names.Text.Length == 0, "settings UI starts with external access disabled");
        Enabled.Checked = true;
        Names.Text = " Claude \r\nclaude; Reviewer_2";
        Require(!Settings.AllowExternalSupervisors && Saves == 0, "edits wait for an explicit settings save");
        Save.PerformClick();
        Require(Saves == 1 && Settings.AllowExternalSupervisors && Settings.ExternalSupervisorAllowList.SequenceEqual(new[] { "claude", "reviewer_2" }), "save normalizes and deduplicates allowed caller names");
        Enabled.Checked = false;
        Save.PerformClick();
        Require(Saves == 2 && !Settings.AllowExternalSupervisors && Settings.ExternalSupervisorAllowList.Count == 2, "disabling external access preserves the saved names");
        foreach (var Control in Descendants(Page).Where(Item => Item is Label or CheckBox or TextBox or Button))
            Require(Control.Parent!.ClientRectangle.Contains(Control.Bounds), "external settings fit at minimum supported width: " + Control.Name);

        using var Preview = new ManagerSettingsPage(true, Settings: Settings, SaveSettings: _ => throw new Exception("preview must never save"));
        var PreviewEnabled = Descendants(Preview).OfType<CheckBox>().Single(Item => Item.Name == "AllowExternalSupervisors");
        var PreviewNames = Descendants(Preview).OfType<TextBox>().Single();
        var PreviewSave = Descendants(Preview).OfType<Button>().Single(Item => Item.Name == "SaveExternalSupervisors");
        Require(!PreviewEnabled.Enabled && PreviewNames.ReadOnly && !PreviewSave.Enabled, "preview exposes external configuration without enabling mutations");
        PreviewEnabled.Checked = true;
        PreviewSave.PerformClick();
        Require(!Settings.AllowExternalSupervisors && Saves == 2, "preview changes do not alter shared settings or persist anything");
    }

    private static void VerifyTree()
    {
        string RootPath = Path.Combine(Path.GetTempPath(), "MmExternalUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        try
        {
            var Store = new RelayStore(RootPath);
            var Owned = new List<RelayTask>();
            for (int Index = 0; Index < RelayLimits.PageSize + 5; Index++)
            {
                var Item = MakeTask("claude", "owned-" + Index);
                Owned.Add(Item);
                Store.Save(Item);
            }
            Store.Save(MakeTask("reviewer", "foreign"));
            var Legacy = new RelayTask { RequestKey = "legacy", Title = "Codex 原有任务", State = eRelayState.Accepted };
            Store.Save(Legacy);
            var Nested = MakeTask("claude", "nested");
            Nested.ParentTaskId = Owned[0].Id;
            Store.Save(Nested);
            int StoredCount = Store.Count();
            var View = new RelayTreeView(Store);
            View.Refresh(0);
            var Group = View.RowList.Single(Row => RelayTreeView.IsExternalRoot(Row.Entry) && Row.Entry.MainInstanceId == "external:claude");
            Require(Group.Depth == 0 && Group.Entry.Title == "外部主管：claude（未关联会话）", "legacy external supervisor has a depth-zero root without claiming a real Claude session");
            Require(View.RowList.Where(Row => !RelayTreeView.IsExternalRoot(Row.Entry) && Row.Entry.MainInstanceId == "external:claude").All(Row => Row.Depth == 1), "external tasks appear one level below the supervisor root");
            Require(View.RowList.Single(Row => Row.Entry.Id == Legacy.Id).Depth == 0, "Codex task roots keep their existing depth");
            Require(View.ExternalRootTasks(Group.Entry).Select(Item => Item.Id).ToHashSet().SetEquals(Owned.Select(Item => Item.Id)), "supervisor details and cancel scope include all pages and omit other callers and nested tasks");
            Require(Store.Count() == StoredCount && Store.FindTask(Group.Entry.Id) is null, "display root is not a persisted task or additional slot");
            View.Toggle(Group.Entry.Id);
            View.Refresh(0);
            Require(View.RowList.Count(Row => Row.Entry.MainInstanceId == "external:claude") == 1, "external supervisor root can collapse its tasks");
            View.Toggle(Group.Entry.Id);
            View.Refresh(0);
            Require(View.RowList.Count(Row => Row.Entry.MainInstanceId == "external:claude") > 1, "external supervisor expansion restores its visible tasks");

            using var Coordinator = new RelayCoordinator(Store, new NoSendBridge());
            using var Dialog = new RelayTaskDialog(Store, Coordinator, Owned[0].Id, new AppTheme(false));
            Require(!Descendants(Dialog).OfType<Button>().Any(Item => Item.Text == "打开主会话"), "external supervisor details do not offer a nonexistent Codex main session");
            Require(Descendants(Dialog).OfType<Button>().Any(Item => Item.Text == "打开执行会话"), "external task details retain the real worker session action");
        }
        finally
        {
            if (!ManagerPaths.IsUnder(RootPath, Path.GetTempPath())) throw new IOException("外部主管 UI 测试清理越界");
            Directory.Delete(RootPath, true);
        }
    }

    private static RelayTask MakeTask(string Caller, string Key) => new()
    {
        RequestKey = Key,
        Title = Key,
        State = eRelayState.Accepted,
        Channel = new()
        {
            MainInstance = new() { Id = "external:" + Caller, Name = "外部主管：" + Caller },
            MainThreadId = "external:" + Caller,
            MainTitle = Caller,
            WorkerInstance = new() { Id = "primary", Name = "主实例" },
            WorkerThreadId = "worker-" + Key,
            WorkerTitle = "执行 " + Key
        }
    };

    private sealed class NoSendBridge : IRelayBridge
    {
        public Task SendAsync(InstanceProfile Instance, string ThreadId, string Prompt, string Model, CancellationToken Cancellation) => throw new Exception("UI tests must not send work");
    }

    private static IEnumerable<Control> Descendants(Control Parent)
    {
        foreach (Control Child in Parent.Controls)
        {
            yield return Child;
            foreach (var Descendant in Descendants(Child)) yield return Descendant;
        }
    }

    private static void Require(bool Condition, string Message)
    {
        if (!Condition) throw new Exception("FAILED external UI: " + Message);
        Count++;
    }
}
