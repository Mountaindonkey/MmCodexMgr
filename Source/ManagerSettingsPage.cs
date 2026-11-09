namespace CodexInstanceManager;

public sealed class ManagerSettingsPage : UserControl
{
    /// <summary> 当前用户的管理器启动登记 </summary>
    private readonly StartupRegistration Startup;
    private readonly ManagerSettings Settings;
    private readonly Action<ManagerSettings> SaveSettings;
    /// <summary> 注册表更新失败时恢复实际状态 </summary>
    private bool Restoring;
    /// <summary> 当前用户的开机自启开关 </summary>
    private readonly CheckBox AutoStartInput = new() { Name = "ManagerAutoStart", Text = "开机自动启动实例管理器", AutoSize = true };
    /// <summary> 开关应用后的实际结果 </summary>
    private readonly Label StatusLabel = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly CheckBox ExternalInput = new() { Name = "AllowExternalSupervisors", Text = "允许外部主管", AutoSize = true };
    private readonly TextBox ExternalNamesInput = new() { Name = "ExternalSupervisorAllowList", Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 480, Height = 92 };
    private readonly Label ExternalStatus = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(640, 0) };

    public ManagerSettingsPage(bool PreviewOnly = false, StartupRegistration? Startup = null, ManagerSettings? Settings = null, Action<ManagerSettings>? SaveSettings = null)
    {
        this.Startup = Startup ?? new StartupRegistration(Environment.ProcessPath!);
        this.Settings = Settings ?? new ManagerSettings();
        this.SaveSettings = SaveSettings ?? SettingsStore.Save;
        Dock = DockStyle.Fill;
        Padding = new Padding(24);
        var Body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        Body.Controls.Add(new Label { Text = "管理器设置", Font = new Font("Microsoft YaHei UI", 20, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 22) });
        AutoStartInput.Checked = !PreviewOnly && this.Startup.IsEnabled;
        AutoStartInput.Enabled = !PreviewOnly;
        AutoStartInput.Margin = new Padding(0, 0, 0, 10);
        Body.Controls.Add(AutoStartInput);
        Body.Controls.Add(new Label { Text = "登录 Windows 后在系统托盘后台运行，可双击托盘图标打开。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 12) });
        Body.Controls.Add(StatusLabel);
        ExternalInput.Checked = this.Settings.AllowExternalSupervisors;
        ExternalInput.Enabled = !PreviewOnly;
        ExternalInput.Margin = new Padding(0, 28, 0, 10);
        Body.Controls.Add(ExternalInput);
        Body.Controls.Add(new Label { Text = "允许的调用方名称（每行一个，例如 claude）。仅已开启且在名单中的名称可派发和读取任务。", AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 8) });
        ExternalNamesInput.Text = string.Join(Environment.NewLine, this.Settings.ExternalSupervisorAllowList);
        ExternalNamesInput.ReadOnly = PreviewOnly;
        Body.Controls.Add(ExternalNamesInput);
        Body.Controls.Add(new Label { Text = "名称使用小写字母、数字、下划线或连字符，最长 64 个字符。保存后生效。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 6, 0, 10) });
        var SaveExternal = MainForm.MakeButton("保存外部主管设置", (_, _) => UpdateExternalSupervisors());
        SaveExternal.Name = "SaveExternalSupervisors";
        SaveExternal.Enabled = !PreviewOnly;
        Body.Controls.Add(SaveExternal);
        Body.Controls.Add(ExternalStatus);
        Controls.Add(Body);
        if (!PreviewOnly) AutoStartInput.CheckedChanged += UpdateAutoStart;
    }

    private void UpdateExternalSupervisors()
    {
        try
        {
            var NameList = ExternalNamesInput.Text.Split(new[] { '\r', '\n', ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ExternalSupervisor.NormalizeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool PreviousEnabled = Settings.AllowExternalSupervisors;
            var PreviousNames = Settings.ExternalSupervisorAllowList;
            Settings.AllowExternalSupervisors = ExternalInput.Checked;
            Settings.ExternalSupervisorAllowList = NameList;
            try { SaveSettings(Settings); }
            catch
            {
                Settings.AllowExternalSupervisors = PreviousEnabled;
                Settings.ExternalSupervisorAllowList = PreviousNames;
                throw;
            }
            ExternalNamesInput.Text = string.Join(Environment.NewLine, NameList);
            ExternalStatus.Text = Settings.AllowExternalSupervisors ? "已允许外部主管，名称名单已保存" : "已关闭外部主管，名称名单已保存";
        }
        catch (Exception Error)
        {
            Program.LogError(Error);
            ExternalStatus.Text = "外部主管设置未保存：" + Error.Message;
        }
    }

    private void UpdateAutoStart(object? Sender, EventArgs Event)
    {
        if (Restoring) return;
        try
        {
            Startup.SetEnabled(AutoStartInput.Checked);
            StatusLabel.Text = AutoStartInput.Checked ? "已开启开机自启" : "已关闭开机自启";
        }
        catch (Exception Error)
        {
            Restoring = true;
            try { AutoStartInput.Checked = !AutoStartInput.Checked; }
            finally { Restoring = false; }
            Program.LogError(Error);
            StatusLabel.Text = "开机自启设置失败 " + Error.Message;
            MessageBox.Show(this, Error.Message, "开机自启设置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
