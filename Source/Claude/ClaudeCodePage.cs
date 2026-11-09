using System.Text.Json;

namespace CodexInstanceManager;

/// <summary>Local Claude Code is a supervisor; Codex sessions remain the execution targets.</summary>
public sealed class ClaudeCodePage : UserControl
{
    private readonly ManagerSettings Settings;
    private readonly Action<ManagerSettings> SaveSettings;
    private readonly Func<ClaudeCodeSettings, CancellationToken, Task<ClaudeCodeCheck>> Check;
    private readonly Action<ClaudeCodeSettings, string> Launch;
    private readonly Action<string> Copy;
    private readonly Func<string, CancellationToken, Task> ProbeManager;
    private readonly CancellationTokenSource Lifetime = new();
    private readonly TextBox CliInput = new() { Name = "ClaudeCliPath", PlaceholderText = "留空自动查找本机 claude" };
    private readonly TextBox DirectoryInput = new() { Name = "ClaudeWorkingDirectory" };
    private readonly TextBox CallerInput = new() { Name = "ClaudeCallerName", MaxLength = 64 };
    private readonly CheckBox AllowInput = new() { Name = "ClaudeAllowSupervisor", Text = "允许此 Claude Code 主管调度 Codex", AutoSize = true };
    private readonly TextBox Instructions = new() { Name = "ClaudeSupervisorInstructions", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label Status = new() { Name = "ClaudeConnectionStatus", AutoSize = true, Dock = DockStyle.Fill, Tag = eThemeRole.Muted };
    private bool Busy;

    public ClaudeCodePage(ManagerSettings Settings, Action ShowBoard, Action<ManagerSettings>? SaveSettings = null,
        Func<ClaudeCodeSettings, CancellationToken, Task<ClaudeCodeCheck>>? Check = null,
        Action<ClaudeCodeSettings, string>? Launch = null, Action<string>? Copy = null,
        Func<string, CancellationToken, Task>? ProbeManager = null)
    {
        this.Settings = Settings;
        this.SaveSettings = SaveSettings ?? SettingsStore.Save;
        this.Check = Check ?? ClaudeCodeSupervisor.CheckAsync;
        this.Launch = Launch ?? ClaudeCodeSupervisor.OpenTerminal;
        this.Copy = Copy ?? Clipboard.SetText;
        this.ProbeManager = ProbeManager ?? ProbeManagerAsync;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Dock = DockStyle.Fill;
        Padding = new Padding(16);
        Tag = eThemeRole.Surface;
        var Root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Margin = Padding.Empty };
        for (int Index = 0; Index < 6; Index++) Root.RowStyles.Add(new(SizeType.AutoSize));
        Root.RowStyles.Add(new(SizeType.Percent, 100));
        Root.RowStyles.Add(new(SizeType.AutoSize));
        Root.Controls.Add(new Label { Text = "Claude Code · 外部主管", Font = new Font(Font.FontFamily, 13, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        var Explanation = new Label { AutoSize = true, Text = "使用本机 Claude Code 的现有登录、API Key 和模型。由它调用管理器 CLI 向 Codex 派发任务、等待回报并验收。", Margin = new Padding(0, 0, 0, 10), Tag = eThemeRole.Muted };
        Root.Controls.Add(Explanation);
        var Fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 3, Margin = Padding.Empty };
        Fields.ColumnStyles.Add(new(SizeType.Absolute, 90));
        Fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        Fields.ColumnStyles.Add(new(SizeType.AutoSize));
        AddField(Fields, "CLI 路径", CliInput, 0);
        Fields.Controls.Add(MakeAction("选择文件", "ClaudeBrowseCli", SelectCli), 2, 0);
        AddField(Fields, "工作目录", DirectoryInput, 1);
        Fields.Controls.Add(MakeAction("选择目录", "ClaudeBrowseDirectory", SelectDirectory), 2, 1);
        AddField(Fields, "调用方名称", CallerInput, 2);
        Root.Controls.Add(Fields);
        AllowInput.Margin = new Padding(0, 6, 0, 6);
        Root.Controls.Add(AllowInput);
        var Actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 6) };
        Actions.Controls.Add(MakeAction("保存连接", "ClaudeSave", Save));
        var CheckButton = MainForm.MakeButton("检查连接", async (_, _) => await CheckConnectionAsync());
        Actions.Controls.Add(Named(CheckButton, "ClaudeCheck"));
        Actions.Controls.Add(MakeAction("启动 Claude Code", "ClaudeLaunch", OpenTerminal));
        Actions.Controls.Add(MakeAction("复制主管指令", "ClaudeCopy", CopyInstructions));
        Actions.Controls.Add(MakeAction("任务看板", "ClaudeBoard", ShowBoard));
        Root.Controls.Add(Actions);
        Root.Controls.Add(new Label { Text = "主管指令 · 启动时加载，也可复制到已有 Claude Code 会话", AutoSize = true, Margin = new Padding(0, 0, 0, 5) });
        Root.Controls.Add(Instructions);
        Status.Margin = new Padding(0, 8, 0, 0);
        Root.Controls.Add(Status);
        Controls.Add(Root);
        CliInput.Text = Settings.ClaudeCode.CliPath;
        DirectoryInput.Text = EffectiveDirectory(Settings.ClaudeCode.WorkingDirectory);
        CallerInput.Text = Settings.ClaudeCode.CallerName;
        RefreshAuthorization();
        RefreshInstructions();
        CliInput.TextChanged += (_, _) => RefreshInstructions();
        DirectoryInput.TextChanged += (_, _) => RefreshInstructions();
        CallerInput.TextChanged += (_, _) => { RefreshAuthorization(); RefreshInstructions(); };
        VisibleChanged += (_, _) => { if (Visible) RefreshAuthorization(); };
        SizeChanged += (_, _) =>
        {
            int Width = Math.Max(120, ClientSize.Width - Padding.Horizontal - 8);
            Explanation.MaximumSize = new Size(Width, 0);
            Status.MaximumSize = new Size(Width, 0);
        };
        Status.Text = "保存连接后启动 Claude Code，再在终端输入你的实际任务。检查连接不会发送模型任务。";
        ResumeLayout(false);
        PerformAutoScale();
        PerformLayout();
    }

    public void RefreshAuthorization() => AllowInput.Checked = IsAllowed(CallerInput.Text.Trim());

    private bool IsAllowed(string Caller) => Settings.AllowExternalSupervisors && Settings.ExternalSupervisorAllowList.Any(Name => string.Equals(Name.Trim(), Caller, StringComparison.OrdinalIgnoreCase));

    private static string EffectiveDirectory(string DirectoryPath) => DirectoryPath.Length == 0 ? Environment.CurrentDirectory : DirectoryPath;

    private static Button Named(Button Button, string Name) { Button.Name = Name; return Button; }
    private Button MakeAction(string Text, string Name, Action Action) => Named(MainForm.MakeButton(Text, (_, _) => Run(Action)), Name);

    private static void AddField(TableLayoutPanel Fields, string Title, TextBox Input, int Row)
    {
        Fields.Controls.Add(new Label { Text = Title, AutoSize = true, Margin = new Padding(0, 7, 0, 6) }, 0, Row);
        Input.Dock = DockStyle.Fill;
        Input.Margin = new Padding(0, 4, 10, 6);
        Fields.Controls.Add(Input, 1, Row);
    }

    private ClaudeCodeSettings InputSettings()
    {
        string Cwd = DirectoryInput.Text.Trim();
        if (Cwd.Length == 0) Cwd = Environment.CurrentDirectory;
        if (!Path.IsPathFullyQualified(Cwd) || !Directory.Exists(Cwd)) throw new DirectoryNotFoundException("Claude Code 工作目录必须是存在的绝对目录");
        return new() { CliPath = CliInput.Text.Trim(), WorkingDirectory = ManagerPaths.Normalize(Cwd), CallerName = ExternalSupervisor.NormalizeName(CallerInput.Text) };
    }

    private void Save()
    {
        var Candidate = InputSettings();
        if (Candidate.CliPath.Length != 0) Candidate.CliPath = ClaudeCodeSupervisor.ResolvePath(Candidate.CliPath);
        var OldSettings = Settings.ClaudeCode;
        var OldNames = Settings.ExternalSupervisorAllowList;
        bool OldEnabled = Settings.AllowExternalSupervisors;
        Settings.ClaudeCode = Candidate;
        Settings.ExternalSupervisorAllowList = OldNames.Where(Name => !string.Equals(Name.Trim(), Candidate.CallerName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (AllowInput.Checked) Settings.ExternalSupervisorAllowList.Add(Candidate.CallerName);
        if (AllowInput.Checked) Settings.AllowExternalSupervisors = true;
        try { SaveSettings(Settings); }
        catch
        {
            Settings.ClaudeCode = OldSettings;
            Settings.ExternalSupervisorAllowList = OldNames;
            Settings.AllowExternalSupervisors = OldEnabled;
            throw;
        }
        CliInput.Text = Candidate.CliPath;
        DirectoryInput.Text = Candidate.WorkingDirectory;
        CallerInput.Text = Candidate.CallerName;
        RefreshAuthorization();
        RefreshInstructions();
        Status.Text = IsAllowed(Candidate.CallerName) ? "已保存 · 允许 " + Candidate.CallerName + " 调度 Codex；可启动 Claude Code。" : "已保存 · 此调用方尚未允许调度，其他主管的允许状态保留。";
    }

    private void OpenTerminal()
    {
        var Candidate = InputSettings();
        var Saved = Settings.ClaudeCode;
        if (!IsAllowed(Candidate.CallerName)) throw new InvalidOperationException("请先勾选允许此主管调度并保存连接");
        if (Candidate.CallerName != Saved.CallerName || Candidate.CliPath != Saved.CliPath || !ManagerPaths.Same(Candidate.WorkingDirectory, EffectiveDirectory(Saved.WorkingDirectory)))
            throw new InvalidOperationException("连接配置尚未保存，请先保存连接再启动 Claude Code");
        Launch(Candidate, AgentCliDeployment.LoadPath());
        Status.Text = "已打开 Claude Code 交互终端并加载主管指令；请在终端输入实际任务，按原有工具权限确认。";
    }

    public async Task CheckConnectionAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var Candidate = InputSettings();
            Status.Text = "正在检查 Claude Code 本机版本、认证与管理器调度通道…";
            var Report = await Check(Candidate, Lifetime.Token);
            if (IsDisposed) return;
            string Local = $"CLI {Report.Version} · {(Report.LoggedIn ? "认证可用" : "认证未就绪")} · {Report.AuthMethod} · {Report.ApiProvider}";
            if (Report.Error.Length != 0) Local += "\n" + Report.Error;
            if (!IsAllowed(Candidate.CallerName)) { Status.Text = Local + "\n此调用方尚未允许调度；请保存允许状态后再检查管理器通道。"; return; }
            try
            {
                await ProbeManager(Candidate.CallerName, Lifetime.Token);
                if (!IsDisposed) Status.Text = Local + "\n管理器通道可用 · 已用 " + Candidate.CallerName + " 身份读取实例列表。";
            }
            catch (Exception Error) when (!Lifetime.IsCancellationRequested)
            {
                if (!IsDisposed) Status.Text = Local + "\n管理器调度通道未连接：" + Error.Message;
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception Error) { if (!IsDisposed) Status.Text = Error.Message; }
        finally { Busy = false; }
    }

    private static async Task ProbeManagerAsync(string Caller, CancellationToken Cancellation)
    {
        await RelayHost.RequestAsync(new("", "", "agent_instances", JsonSerializer.SerializeToElement(new { })) { ExternalCaller = Caller }, Cancellation);
    }

    private void RefreshInstructions()
    {
        try { Instructions.Text = ClaudeCodeSupervisor.BuildSupervisorPrompt(AgentCliDeployment.LoadPath(), CallerInput.Text); }
        catch (Exception Error) { Instructions.Text = "主管指令暂不可用：" + Error.Message; }
    }

    private void CopyInstructions()
    {
        string Text = ClaudeCodeSupervisor.BuildSupervisorPrompt(AgentCliDeployment.LoadPath(), CallerInput.Text);
        Copy(Text);
        Status.Text = "主管指令已复制；可粘贴到已打开的 Claude Code 会话，再输入实际任务。";
    }

    private void SelectCli()
    {
        using var Dialog = new OpenFileDialog { Filter = "Claude Code|claude.exe;claude.cmd;claude.ps1|所有文件|*.*", Title = "选择本机 Claude Code CLI" };
        if (Dialog.ShowDialog(this) == DialogResult.OK) CliInput.Text = Dialog.FileName;
    }

    private void SelectDirectory()
    {
        using var Dialog = new FolderBrowserDialog { Description = "选择 Claude Code 工作目录", UseDescriptionForTitle = true, SelectedPath = DirectoryInput.Text };
        if (Dialog.ShowDialog(this) == DialogResult.OK) DirectoryInput.Text = Dialog.SelectedPath;
    }

    private void Run(Action Action)
    {
        if (Busy) return;
        try { Action(); }
        catch (Exception Error) { Status.Text = Error.Message; }
    }

    protected override void Dispose(bool Disposing)
    {
        if (Disposing) { Lifetime.Cancel(); Lifetime.Dispose(); }
        base.Dispose(Disposing);
    }
}
