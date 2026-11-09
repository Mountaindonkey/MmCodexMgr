using CodexInstanceManager;

internal static class ClaudeCodeUiTests
{
    private static int Count;

    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        foreach (bool Dark in new[] { false, true })
            foreach (var Size in new[] { new Size(760, 470), new Size(1000, 680) }) VerifyPage(Dark, Size);
        Console.WriteLine($"PASS Claude Code UI {Count} assertions");
        return 0;
    }

    private static void VerifyPage(bool Dark, Size Size)
    {
        var Settings = new ManagerSettings { ExternalSupervisorAllowList = new() { "reviewer" } };
        int Saves = 0, Starts = 0, Checks = 0, Boards = 0, Probes = 0;
        string Copied = "";
        var CheckFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var Form = new Form { ClientSize = Size, Font = new Font("Microsoft YaHei UI", 10), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        using var Page = new ClaudeCodePage(Settings, () => Boards++,
            SaveSettings: Saved => { Require(ReferenceEquals(Saved, Settings), "save uses the shared manager settings object"); Saves++; },
            Check: (Saved, _) => { Checks++; CheckFinished.TrySetResult(); return Task.FromResult(new ClaudeCodeCheck("test-claude.exe", "test-version", true, "api_key", "firstParty")); },
            Launch: (Saved, _) => { Require(Saved.CallerName == "claude" && Directory.Exists(Saved.WorkingDirectory), "launch uses the saved caller and real working directory"); Starts++; },
            Copy: Text => Copied = Text,
            ProbeManager: (Name, _) => { Require(Name == "claude", "manager probe uses the authorized external caller"); Probes++; return Task.CompletedTask; });
        Form.Controls.Add(Page);
        new AppTheme(Dark).Apply(Form);
        Form.Show();
        Application.DoEvents();
        var Caller = Descendants(Page).OfType<TextBox>().Single(Item => Item.Name == "ClaudeCallerName");
        var Cwd = Descendants(Page).OfType<TextBox>().Single(Item => Item.Name == "ClaudeWorkingDirectory");
        var Enabled = Descendants(Page).OfType<CheckBox>().Single(Item => Item.Name == "ClaudeAllowSupervisor");
        var Prompt = Descendants(Page).OfType<TextBox>().Single(Item => Item.Name == "ClaudeSupervisorInstructions");
        Button Button(string Name) => Descendants(Page).OfType<Button>().Single(Item => Item.Name == Name);
        Require(Caller.Text == "claude" && Directory.Exists(Cwd.Text) && !Enabled.Checked, "initial page provides a usable directory and disabled default caller");
        Require(Saves == 0 && Starts == 0 && Checks == 0 && Settings.ClaudeCode.WorkingDirectory.Length == 0, "opening the page does not save, probe or launch a model");
        Require(Prompt.ReadOnly && Prompt.Text.Contains("--external-caller claude") && Prompt.Text.Contains("primary") && Prompt.Text.Contains("second") && Prompt.Text.Contains("wait") && Prompt.Text.Contains("accept"), "read-only supervisor instructions contain the actual CLI workflow");
        Button("ClaudeLaunch").PerformClick();
        Require(Starts == 0 && !Settings.AllowExternalSupervisors, "launch cannot enable authorization implicitly");
        Caller.Text = " Claude ";
        Enabled.Checked = true;
        Button("ClaudeSave").PerformClick();
        Require(Saves == 1 && Settings.AllowExternalSupervisors && Settings.ExternalSupervisorAllowList.Contains("claude") && Settings.ExternalSupervisorAllowList.Contains("reviewer"), "save enables the normalized caller while preserving other supervisors");
        Require(Settings.ClaudeCode.CallerName == "claude" && Settings.ClaudeCode.WorkingDirectory == Cwd.Text, "save persists the effective working directory and caller");
        Button("ClaudeLaunch").PerformClick();
        Require(Starts == 1, "saved authorized connection launches through its injected service");
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
        foreach (var Control in Descendants(Page).Where(Item => Item.Visible && Item is Label or TextBox or CheckBox or System.Windows.Forms.Button))
            Require(Control.Parent!.ClientRectangle.Contains(Control.Bounds), $"Claude control fits dark={Dark} size={Size} name={Control.Name} bounds={Control.Bounds}");
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
