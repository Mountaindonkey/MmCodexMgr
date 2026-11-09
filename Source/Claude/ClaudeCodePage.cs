using System.Text.Json;

namespace CodexInstanceManager;

public sealed class ClaudeCodePage : UserControl
{
    private readonly ManagerSettings Settings;
    private readonly Action<ManagerSettings> SaveSettings;
    private readonly Func<ClaudeCodeSettings, CancellationToken, Task<ClaudeCodeCheck>> Check;
    private readonly Action<ClaudeCodeSettings, string, ClaudeCodeSession?, bool> Launch;
    private readonly Action<string> Copy;
    private readonly Func<string, CancellationToken, Task> ProbeManager;
    private readonly ClaudeSessionStore Sessions;
    private readonly RelayStore? Store;
    private readonly RelayCoordinator? Coordinator;
    private readonly CancellationTokenSource Lifetime = new();
    private readonly TextBox CliInput = new() { Name = "ClaudeCliPath", PlaceholderText = "留空自动查找本机 claude" };
    private readonly TextBox DirectoryInput = new() { Name = "ClaudeWorkingDirectory" };
    private readonly TextBox CallerInput = new() { Name = "ClaudeCallerName", MaxLength = 64 };
    private readonly TextBox TitleInput = new() { Name = "ClaudeSessionTitle", Text = "Claude 主管", MaxLength = 120, Dock = DockStyle.Fill };
    private readonly CheckBox AllowInput = new() { Name = "ClaudeAllowSupervisor", Text = "允许此 Claude Code 主管调度 Codex", AutoSize = true };
    private readonly TextBox Instructions = new() { Name = "ClaudeSupervisorInstructions", Dock = DockStyle.Top, Height = 170, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly DataGridView SessionGrid = new() { Name = "ClaudeSessions" };
    private readonly DataGridView RelationGrid = new() { Name = "ClaudeTaskRelations" };
    private readonly Label Status = new() { Name = "ClaudeConnectionStatus", AutoSize = true, Dock = DockStyle.Fill, Tag = eThemeRole.Muted };
    private List<ClaudeCodeSession> SessionList = new();
    private bool Busy;
    private bool Refreshing;

    public ClaudeCodePage(ManagerSettings Settings, Action ShowBoard, Action<ManagerSettings>? SaveSettings = null,
        Func<ClaudeCodeSettings, CancellationToken, Task<ClaudeCodeCheck>>? Check = null,
        Action<ClaudeCodeSettings, string>? Launch = null, Action<string>? Copy = null,
        Func<string, CancellationToken, Task>? ProbeManager = null, ClaudeSessionStore? Sessions = null,
        RelayStore? Store = null, RelayCoordinator? Coordinator = null,
        Action<ClaudeCodeSettings, string, ClaudeCodeSession?, bool>? LaunchSession = null)
    {
        this.Settings = Settings;
        this.SaveSettings = SaveSettings ?? SettingsStore.Save;
        this.Check = Check ?? ClaudeCodeSupervisor.CheckAsync;
        this.Launch = LaunchSession ?? (Launch is null ? ClaudeCodeSupervisor.OpenTerminal : (Config, Cli, _, _) => Launch(Config, Cli));
        this.Copy = Copy ?? Clipboard.SetText;
        this.ProbeManager = ProbeManager ?? ProbeManagerAsync;
        this.Sessions = Sessions ?? new ClaudeSessionStore();
        this.Store = Store;
        this.Coordinator = Coordinator;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Dock = DockStyle.Fill;
        Padding = new Padding(12);
        Tag = eThemeRole.Surface;
        var Root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        Root.RowStyles.Add(new(SizeType.AutoSize));
        Root.RowStyles.Add(new(SizeType.Percent, 46));
        Root.RowStyles.Add(new(SizeType.Percent, 54));
        Root.RowStyles.Add(new(SizeType.AutoSize));
        var Card = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        Card.RowStyles.Add(new(SizeType.AutoSize));
        var DetailsRow = new RowStyle(SizeType.Absolute, 0);
        Card.RowStyles.Add(DetailsRow);
        var Header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        Header.ColumnStyles.Add(new(SizeType.Percent, 100));
        Header.ColumnStyles.Add(new(SizeType.AutoSize));
        Header.Controls.Add(new Label { Text = "Claude Code · 外部主管", Font = new Font(Font.FontFamily, 13, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 5, 0, 6) });
        var DetailsScroll = new WheelScrollPanel { Name = "ClaudeConnectionDetails", Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
        var Details = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        DetailsScroll.Controls.Add(Details);
        bool Expanded = false;
        void FitDetails()
        {
            float Height = Expanded ? Math.Min(210 * DeviceDpi / 96f, Math.Max(72 * DeviceDpi / 96f, Root.ClientSize.Height - 300 * DeviceDpi / 96f)) : 0;
            if (DetailsRow.Height != Height) DetailsRow.Height = Height;
        }
        Root.SizeChanged += (_, _) => FitDetails();
        Header.Controls.Add(MakeAction("连接设置", "ClaudeConnectionSettings", () => { Expanded = !Expanded; DetailsScroll.Visible = Expanded; FitDetails(); }));
        Card.Controls.Add(Header);
        Card.Controls.Add(DetailsScroll);
        var Explanation = new Label { AutoSize = true, Text = "沿用本机 Claude Code 登录、API Key 和模型。每条 Claude 会话可调度多个 Agent 的多条执行会话。", Tag = eThemeRole.Muted, Margin = new Padding(0, 0, 0, 8) };
        Details.Controls.Add(Explanation);
        var Fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 3, Margin = Padding.Empty };
        Fields.ColumnStyles.Add(new(SizeType.Absolute, 90));
        Fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        Fields.ColumnStyles.Add(new(SizeType.AutoSize));
        AddField(Fields, "CLI 路径", CliInput, 0);
        Fields.Controls.Add(MakeAction("选择文件", "ClaudeBrowseCli", SelectCli), 2, 0);
        AddField(Fields, "工作目录", DirectoryInput, 1);
        Fields.Controls.Add(MakeAction("选择目录", "ClaudeBrowseDirectory", SelectDirectory), 2, 1);
        AddField(Fields, "调用方名称", CallerInput, 2);
        Details.Controls.Add(Fields);
        Details.Controls.Add(AllowInput);
        var ConnectionActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        ConnectionActions.Controls.Add(MakeAction("保存连接", "ClaudeSave", Save));
        ConnectionActions.Controls.Add(Named(MainForm.MakeButton("检查连接", async (_, _) => await CheckConnectionAsync()), "ClaudeCheck"));
        ConnectionActions.Controls.Add(MakeAction("任务看板", "ClaudeBoard", ShowBoard));
        Details.Controls.Add(ConnectionActions);
        Details.Controls.Add(new Label { Text = "所选会话主管指令 · 含准确 UUID，可复制到对应 Claude Code 会话", AutoSize = true, Margin = new Padding(0, 4, 0, 5) });
        Details.Controls.Add(Instructions);
        Root.Controls.Add(Card);
        var SessionCard = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 7, 0, 7) };
        SessionCard.RowStyles.Add(new(SizeType.AutoSize));
        SessionCard.RowStyles.Add(new(SizeType.AutoSize));
        SessionCard.RowStyles.Add(new(SizeType.Percent, 100));
        var SessionHeader = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        SessionHeader.ColumnStyles.Add(new(SizeType.AutoSize));
        SessionHeader.ColumnStyles.Add(new(SizeType.Percent, 100));
        SessionHeader.Controls.Add(new Label { Text = "Claude 会话名称", AutoSize = true, Margin = new Padding(0, 6, 12, 6) });
        SessionHeader.Controls.Add(TitleInput);
        SessionCard.Controls.Add(SessionHeader);
        var SessionActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        SessionActions.Controls.Add(MakeAction("新建会话", "ClaudeLaunch", CreateSession));
        SessionActions.Controls.Add(MakeAction("继续所选会话", "ClaudeResume", ResumeSession));
        SessionActions.Controls.Add(MakeAction("刷新会话", "ClaudeRefreshSessions", () => RefreshSessions(true)));
        SessionActions.Controls.Add(MakeAction("导入已有 UUID", "ClaudeImportSession", ImportSessionDialog));
        SessionActions.Controls.Add(MakeAction("复制会话指令", "ClaudeCopy", CopyInstructions));
        SessionCard.Controls.Add(SessionActions);
        ConfigureGrid(SessionGrid);
        AddColumn(SessionGrid, "title", "Claude 会话名称", 26, 120);
        AddColumn(SessionGrid, "id", "准确会话 UUID", 29, 190);
        AddColumn(SessionGrid, "cwd", "工作目录", 25, 160);
        AddColumn(SessionGrid, "state", "记录状态", 20, 160);
        SessionGrid.SelectionChanged += (_, _) => { if (!Refreshing) { if (SelectedSession() is { } Session) TitleInput.Text = Session.Title; RefreshInstructions(); } };
        SessionGrid.CellToolTipTextNeeded += (_, Event) => { if (Event.RowIndex >= 0 && SessionGrid.Rows[Event.RowIndex].Tag is ClaudeCodeSession Session) Event.ToolTipText = $"{Session.Title}\nUUID {Session.Id}\n目录 {Session.Cwd}\n此状态只表示历史记录是否已发现，不代表进程正在运行。"; };
        SessionCard.Controls.Add(SessionGrid);
        Root.Controls.Add(SessionCard);
        var Relations = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        Relations.RowStyles.Add(new(SizeType.AutoSize));
        Relations.RowStyles.Add(new(SizeType.Percent, 100));
        Relations.Controls.Add(new Label { Text = "当前调用方的全部任务关联 · 按保存的 Claude UUID 显示，旧任务不猜测关联", AutoSize = true, Margin = new Padding(0, 0, 0, 5) });
        ConfigureGrid(RelationGrid);
        AddColumn(RelationGrid, "source_title", "来源 Claude 会话", 17, 125);
        AddColumn(RelationGrid, "source_id", "来源 UUID", 18, 190);
        AddColumn(RelationGrid, "task", "任务名", 15, 110);
        AddColumn(RelationGrid, "agent", "目标 Agent", 10, 90);
        AddColumn(RelationGrid, "worker_title", "目标会话名", 15, 120);
        AddColumn(RelationGrid, "worker_id", "目标准确 ID", 18, 190);
        AddColumn(RelationGrid, "state", "任务状态", 10, 110);
        RelationGrid.Columns.Add(new DataGridViewButtonColumn { Name = "details", HeaderText = "任务", MinimumWidth = 70, FillWeight = 8, FlatStyle = FlatStyle.Flat });
        RelationGrid.Columns.Add(new DataGridViewButtonColumn { Name = "open", HeaderText = "执行会话", MinimumWidth = 100, FillWeight = 10, FlatStyle = FlatStyle.Flat });
        RelationGrid.CellContentClick += async (_, Event) =>
        {
            if (Event.RowIndex < 0 || RelationGrid.Rows[Event.RowIndex].Tag is not RelayTask Item) return;
            if (Event.ColumnIndex == RelationGrid.Columns["details"].Index) Run(() => ShowTaskDetails(Item));
            if (Event.ColumnIndex == RelationGrid.Columns["open"].Index) await OpenWorkerAsync(Item);
        };
        RelationGrid.CellToolTipTextNeeded += (_, Event) => { if (Event.RowIndex >= 0 && RelationGrid.Rows[Event.RowIndex].Tag is RelayTask Item) Event.ToolTipText = $"任务 {Item.Title}\n任务 ID {Item.Id}\n来源 UUID {(Item.Channel.ExternalSessionId.Length == 0 ? "未关联 Claude 会话" : Item.Channel.ExternalSessionId)}\n目标 {Item.Channel.WorkerInstance.Name} / {Item.Channel.WorkerTitle}\n目标准确 ID {Item.Channel.WorkerThreadId}"; };
        Relations.Controls.Add(RelationGrid);
        Root.Controls.Add(Relations);
        Status.Margin = new Padding(0, 7, 0, 0);
        Root.Controls.Add(Status);
        Controls.Add(Root);
        CliInput.Text = Settings.ClaudeCode.CliPath;
        DirectoryInput.Text = EffectiveDirectory(Settings.ClaudeCode.WorkingDirectory);
        CallerInput.Text = Settings.ClaudeCode.CallerName;
        RefreshAuthorization();
        Run(() => RefreshSessions(false));
        CallerInput.TextChanged += (_, _) => { RefreshAuthorization(); Run(() => RefreshSessions(false)); };
        VisibleChanged += (_, _) => { if (Visible) { RefreshAuthorization(); Run(() => RefreshSessions(false)); } };
        SizeChanged += (_, _) => { int Width = Math.Max(120, ClientSize.Width - Padding.Horizontal - 8); Explanation.MaximumSize = new Size(Width, 0); Status.MaximumSize = new Size(Width, 0); };
        if (Coordinator is not null) Coordinator.Changed += OnTasksChanged;
        Status.Text = "刷新可发现本机 Claude 历史；新建登记在首条消息前显示待确认。记录状态不代表正在运行。";
        ResumeLayout(false);
        PerformAutoScale();
        PerformLayout();
    }

    private static void ConfigureGrid(DataGridView Grid)
    {
        Grid.Dock = DockStyle.Fill; Grid.ReadOnly = true; Grid.AllowUserToAddRows = false; Grid.AllowUserToDeleteRows = false;
        Grid.AllowUserToResizeRows = false; Grid.RowHeadersVisible = false; Grid.MultiSelect = false;
        Grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; Grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        Grid.BorderStyle = BorderStyle.None; Grid.RowTemplate.Height = 34;
    }
    private static void AddColumn(DataGridView Grid, string Name, string Title, int Weight, int Minimum) => Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = Name, HeaderText = Title, FillWeight = Weight, MinimumWidth = Minimum, SortMode = DataGridViewColumnSortMode.NotSortable });
    private ClaudeCodeSession? SelectedSession() => SessionGrid.CurrentRow?.Tag as ClaudeCodeSession;
    public void RefreshAuthorization() => AllowInput.Checked = IsAllowed(CallerInput.Text.Trim());
    private bool IsAllowed(string Caller) => Settings.AllowExternalSupervisors && Settings.ExternalSupervisorAllowList.Any(Name => string.Equals(Name.Trim(), Caller, StringComparison.OrdinalIgnoreCase));
    private static string EffectiveDirectory(string DirectoryPath) => DirectoryPath.Length == 0 ? Environment.CurrentDirectory : DirectoryPath;
    private static Button Named(Button Button, string Name) { Button.Name = Name; return Button; }
    private Button MakeAction(string Text, string Name, Action Action) => Named(MainForm.MakeButton(Text, (_, _) => Run(Action)), Name);
    private static void AddField(TableLayoutPanel Fields, string Title, TextBox Input, int Row)
    {
        Fields.Controls.Add(new Label { Text = Title, AutoSize = true, Margin = new Padding(0, 7, 0, 6) }, 0, Row);
        Input.Dock = DockStyle.Fill; Input.Margin = new Padding(0, 4, 10, 6); Fields.Controls.Add(Input, 1, Row);
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
        var OldSettings = Settings.ClaudeCode; var OldNames = Settings.ExternalSupervisorAllowList; bool OldEnabled = Settings.AllowExternalSupervisors;
        Settings.ClaudeCode = Candidate;
        Settings.ExternalSupervisorAllowList = OldNames.Where(Name => !string.Equals(Name.Trim(), Candidate.CallerName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (AllowInput.Checked) { Settings.ExternalSupervisorAllowList.Add(Candidate.CallerName); Settings.AllowExternalSupervisors = true; }
        try { SaveSettings(Settings); }
        catch { Settings.ClaudeCode = OldSettings; Settings.ExternalSupervisorAllowList = OldNames; Settings.AllowExternalSupervisors = OldEnabled; throw; }
        CliInput.Text = Candidate.CliPath; DirectoryInput.Text = Candidate.WorkingDirectory; CallerInput.Text = Candidate.CallerName;
        RefreshAuthorization(); RefreshSessions(false);
        Status.Text = IsAllowed(Candidate.CallerName) ? "已保存，允许此主管调度；可新建或继续所选 Claude 会话。" : "已保存，此调用方尚未允许调度；其他主管的允许状态保留。";
    }
    private ClaudeCodeSettings RequireSavedConnection()
    {
        var Candidate = InputSettings(); var Saved = Settings.ClaudeCode;
        if (!IsAllowed(Candidate.CallerName)) throw new InvalidOperationException("请先在连接设置中勾选允许此主管调度并保存连接");
        if (Candidate.CallerName != Saved.CallerName || Candidate.CliPath != Saved.CliPath || !ManagerPaths.Same(Candidate.WorkingDirectory, EffectiveDirectory(Saved.WorkingDirectory))) throw new InvalidOperationException("连接配置尚未保存，请先保存连接再启动 Claude Code");
        return Candidate;
    }
    private void CreateSession()
    {
        var Candidate = RequireSavedConnection(); string Title = TitleInput.Text.Trim();
        if (Title.Length == 0) throw new InvalidOperationException("请输入新建 Claude 会话的名称");
        var Session = new ClaudeCodeSession { Id = Guid.NewGuid().ToString("D"), Title = Title, Cwd = Candidate.WorkingDirectory, CliPath = Candidate.CliPath, CallerName = Candidate.CallerName, Recorded = false };
        Sessions.Save(Session); RefreshSessions(false, Session.Id);
        Launch(Candidate, AgentCliDeployment.LoadPath(), Session, false);
        Status.Text = "已登记并打开 Claude 会话；在终端发送首条消息后刷新以确认实际历史。UUID " + Session.Id;
    }
    private void ResumeSession()
    {
        var Candidate = RequireSavedConnection(); var Session = SelectedSession() ?? throw new InvalidOperationException("请先选择 Claude 会话");
        if (!Session.Recorded) throw new InvalidOperationException("尚未发现该 UUID 的实际历史。请先在原终端发送首条消息，再刷新会话；不会新建替代会话。");
        Launch(Candidate, AgentCliDeployment.LoadPath(), Session, true);
        Status.Text = "已请求继续所选 Claude 会话；是否运行以终端实际结果为准。UUID " + Session.Id;
    }
    public void RefreshSessions(bool Discover = false, string? SelectedId = null)
    {
        string Caller = ExternalSupervisor.NormalizeName(CallerInput.Text); SelectedId ??= SelectedSession()?.Id;
        SessionList = Discover ? Sessions.Discover(InputSettings()) : Sessions.List(Caller);
        Refreshing = true;
        try
        {
            SessionGrid.Rows.Clear();
            foreach (var Session in SessionList) { int Index = SessionGrid.Rows.Add(Session.Title, Session.Id, Session.Cwd, Session.Recorded ? "已发现会话" : "待首条消息确认"); SessionGrid.Rows[Index].Tag = Session; }
            foreach (DataGridViewRow Row in SessionGrid.Rows) if (((ClaudeCodeSession)Row.Tag!).Id == SelectedId) { SessionGrid.CurrentCell = Row.Cells[0]; break; }
            if (SessionGrid.CurrentCell is null && SessionGrid.Rows.Count != 0) SessionGrid.CurrentCell = SessionGrid.Rows[0].Cells[0];
        }
        finally { Refreshing = false; }
        if (SelectedSession() is { } Current) TitleInput.Text = Current.Title;
        RefreshInstructions(); RefreshRelations();
        if (Discover) Status.Text = "已读取本机历史并刷新登记会话；已发现不等于进程正在运行。";
    }
    public void ImportSession(string Id, string Title)
    {
        var Candidate = InputSettings();
        if (!Guid.TryParse(Id, out var Parsed)) throw new InvalidOperationException("请输入 Claude Code 的准确会话 UUID");
        string SessionId = Parsed.ToString("D"); var Existing = Sessions.Find(Candidate.CallerName, SessionId);
        Sessions.Save(new ClaudeCodeSession { Id = SessionId, Title = string.IsNullOrWhiteSpace(Title) ? Existing?.Title ?? "导入的 Claude 会话" : Title.Trim(), Cwd = Existing?.Cwd ?? Candidate.WorkingDirectory, CliPath = Existing?.CliPath ?? Candidate.CliPath, CallerName = Candidate.CallerName, Recorded = Existing?.Recorded ?? false });
        RefreshSessions(false, SessionId); Status.Text = "已登记准确 UUID；刷新发现实际历史后可继续该会话，不推测其运行状态。";
    }
    private void ImportSessionDialog()
    {
        using var Dialog = new Form { Text = "导入已有 Claude 会话", Font = Font, ClientSize = new Size(580, 180), StartPosition = FormStartPosition.CenterParent };
        var Id = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Claude Code 准确会话 UUID" }; var Title = new TextBox { Dock = DockStyle.Top, Text = TitleInput.Text, PlaceholderText = "会话显示名称" };
        var Layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16) };
        Layout.Controls.Add(new Label { Text = "输入实际 UUID，或取消后点“刷新会话”选择本机历史。", AutoSize = true }); Layout.Controls.Add(Id); Layout.Controls.Add(Title);
        var Confirm = MainForm.MakeButton("登记 UUID", (_, _) => Dialog.DialogResult = DialogResult.OK, true); Layout.Controls.Add(Confirm); Dialog.Controls.Add(Layout); Dialog.AcceptButton = Confirm;
        new AppTheme(Settings.DarkMode).Apply(Dialog);
        if (Dialog.ShowDialog(this) == DialogResult.OK) ImportSession(Id.Text.Trim(), Title.Text);
    }
    public void RefreshRelations()
    {
        RelationGrid.Rows.Clear(); if (Store is null) return;
        string Caller = ExternalSupervisor.NormalizeName(CallerInput.Text); var Known = SessionList.ToDictionary(Session => Session.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var Item in Store.ExternalTasks().Where(Item => Item.Channel.ExternalCaller == Caller).OrderByDescending(Item => Item.UpdatedAt))
        {
            string Id = Item.Channel.ExternalSessionId; string Title = Id.Length == 0 ? "未关联 Claude 会话" : Known.TryGetValue(Id, out var Session) ? Session.Title : "未登记的 Claude 会话";
            int Index = RelationGrid.Rows.Add(Title, Id.Length == 0 ? "未关联" : Id, Item.Title, AgentDisplay.Instance(Settings, Item.Channel.WorkerInstance.Id, Item.Channel.WorkerInstance.Name), Item.Channel.WorkerTitle, Item.Channel.WorkerThreadId, RelayLimits.ProgressStateText(Item.State, Item.Outcome, Item.ProgressStage), "详情", "打开执行会话");
            RelationGrid.Rows[Index].Tag = Item;
        }
    }
    private void OnTasksChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(OnTasksChanged); } catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { } return; }
        if (Visible)
        {
            try { RefreshSessions(false); }
            catch (Exception Error) { Status.Text = Error.Message; }
        }
    }
    private void ShowTaskDetails(RelayTask Item)
    {
        if (Store is null || Coordinator is null) throw new InvalidOperationException("当前页面未连接任务详情服务");
        using var Dialog = new RelayTaskDialog(Store, Coordinator, Item.Id, new AppTheme(Settings.DarkMode)); Dialog.ShowDialog(this);
    }
    private async Task OpenWorkerAsync(RelayTask Item)
    {
        if (Busy) return; Busy = true;
        try
        {
            if (Coordinator is null || Item.Channel.WorkerThreadId.Length == 0) throw new InvalidOperationException("任务尚无可打开的执行会话");
            if (Item.Channel.WorkerInstance.Id == DshRelayRunner.InstanceId) throw new InvalidOperationException("DSH 接口不提供会话导航，请在 DSH 桌面按准确 ID 选择：" + Item.Channel.WorkerThreadId);
            await Coordinator.OpenSessionAsync(Item.Channel.WorkerInstance, Item.Channel.WorkerThreadId, Lifetime.Token);
            if (!IsDisposed) Status.Text = "已请求打开执行会话 " + Item.Channel.WorkerThreadId;
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception Error) { if (!IsDisposed) Status.Text = Error.Message; }
        finally { Busy = false; }
    }
    public async Task CheckConnectionAsync()
    {
        if (Busy) return; Busy = true;
        try
        {
            var Candidate = InputSettings(); Status.Text = "正在检查 Claude Code 本机版本、认证与管理器调度通道…";
            var Report = await Check(Candidate, Lifetime.Token); if (IsDisposed) return;
            string Local = $"CLI {Report.Version} · {(Report.LoggedIn ? "认证可用" : "认证未就绪")} · {Report.AuthMethod} · {Report.ApiProvider}";
            if (Report.Error.Length != 0) Local += "\n" + Report.Error;
            if (!IsAllowed(Candidate.CallerName)) { Status.Text = Local + "\n此调用方尚未允许调度；请保存允许状态后再检查管理器通道。"; return; }
            try { await ProbeManager(Candidate.CallerName, Lifetime.Token); if (!IsDisposed) Status.Text = Local + "\n管理器通道可用，已用 " + Candidate.CallerName + " 身份读取实例列表。"; }
            catch (Exception Error) when (!Lifetime.IsCancellationRequested) { if (!IsDisposed) Status.Text = Local + "\n管理器调度通道未连接：" + Error.Message; }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception Error) { if (!IsDisposed) Status.Text = Error.Message; }
        finally { Busy = false; }
    }
    private static async Task ProbeManagerAsync(string Caller, CancellationToken Cancellation) { await RelayHost.RequestAsync(new("", "", "agent_instances", JsonSerializer.SerializeToElement(new { })) { ExternalCaller = Caller }, Cancellation); }
    private void RefreshInstructions()
    {
        try { var Session = SelectedSession(); Instructions.Text = Session is null ? "先选择、导入或新建 Claude 会话，以生成含准确 UUID 的主管指令。" : ClaudeCodeSupervisor.BuildSupervisorPrompt(AgentCliDeployment.LoadPath(), CallerInput.Text, Session.Id); }
        catch (Exception Error) { Instructions.Text = "主管指令暂不可用：" + Error.Message; }
    }
    private void CopyInstructions()
    {
        var Session = SelectedSession() ?? throw new InvalidOperationException("请先选择 Claude 会话，再复制与该 UUID 对应的主管指令");
        Copy(ClaudeCodeSupervisor.BuildSupervisorPrompt(AgentCliDeployment.LoadPath(), CallerInput.Text, Session.Id)); Status.Text = "已复制所选会话的主管指令，UUID " + Session.Id + "；不要粘贴到另一条 Claude 会话。";
    }
    private void SelectCli() { using var Dialog = new OpenFileDialog { Filter = "Claude Code|claude.exe;claude.cmd;claude.ps1|所有文件|*.*", Title = "选择本机 Claude Code CLI" }; if (Dialog.ShowDialog(this) == DialogResult.OK) CliInput.Text = Dialog.FileName; }
    private void SelectDirectory() { using var Dialog = new FolderBrowserDialog { Description = "选择 Claude Code 工作目录", UseDescriptionForTitle = true, SelectedPath = DirectoryInput.Text }; if (Dialog.ShowDialog(this) == DialogResult.OK) DirectoryInput.Text = Dialog.SelectedPath; }
    private void Run(Action Action) { if (Busy) return; try { Action(); } catch (Exception Error) { Status.Text = Error.Message; } }
    protected override void Dispose(bool Disposing) { if (Disposing) { if (Coordinator is not null) Coordinator.Changed -= OnTasksChanged; Lifetime.Cancel(); Lifetime.Dispose(); } base.Dispose(Disposing); }
}
