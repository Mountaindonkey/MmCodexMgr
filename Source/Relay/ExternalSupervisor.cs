using System.Text.RegularExpressions;

namespace CodexInstanceManager;

public static class ExternalSupervisor
{
    private const string Prefix = "external:";
    public static string NormalizeName(string Value)
    {
        string Name = Value.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(Name, "^[a-z0-9][a-z0-9_-]{0,63}$") ||
            Regex.IsMatch(Name, "^(con|prn|aux|nul|com[0-9]|lpt[0-9])$"))
            throw new InvalidOperationException("外部主管名称须为 1 至 64 位字母、数字、下划线或连字符，首位为字母或数字，不能使用 Windows 保留名称");
        return Name;
    }

    public static bool IsExternal(InstanceProfile Instance) => Instance.Id.StartsWith(Prefix, StringComparison.Ordinal);
    public static string Name(InstanceProfile Instance) => IsExternal(Instance) ? NormalizeName(Instance.Id[Prefix.Length..]) : "";
    public static string ThreadId(string Name) => Prefix + NormalizeName(Name);
    public static InstanceProfile Profile(string Name) => new() { Id = ThreadId(Name), Name = "外部主管：" + NormalizeName(Name), Badge = "E" };

    public static RelayRequest Authorize(RelayRequest Request, ManagerSettings Settings)
    {
        if (Request.ExternalCaller.Length == 0) return Request;
        if (Request.InstanceId.Length != 0 || Request.ThreadId.Length != 0) throw new InvalidOperationException("外部主管不能混用 Codex 调用方身份");
        string Caller = NormalizeName(Request.ExternalCaller);
        if (!Settings.AllowExternalSupervisors) throw new InvalidOperationException("管理器未开启允许外部主管");
        if (!Settings.ExternalSupervisorAllowList.Any(Name => NormalizeName(Name) == Caller)) throw new InvalidOperationException("外部主管不在名称允许列表中：" + Caller);
        if (Request.Command is not ("agent_instances" or "agent_projects" or "agent_sessions" or "agent_start" or "agent_task" or "agent_tree" or "relay_accept" or "agent_cancel" or "agent_wait" or "agent_inbox" or "agent_inbox_ack"))
            throw new InvalidOperationException("外部主管不支持此命令 " + Request.Command);
        return Request with { ExternalCaller = Caller, InstanceId = ThreadId(Caller), ThreadId = ThreadId(Caller) };
    }
}
