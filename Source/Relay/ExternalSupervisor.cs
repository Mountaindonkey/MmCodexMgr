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
    public static string NormalizeSessionId(string Value)
    {
        if (Value.Length == 0) return "";
        if (!Guid.TryParse(Value, out var Session)) throw new InvalidOperationException("外部主管会话 ID 必须是有效 UUID");
        return Session.ToString("D");
    }
    public static string ThreadId(string Name, string SessionId = "")
    {
        string Session = NormalizeSessionId(SessionId);
        return Prefix + NormalizeName(Name) + (Session.Length == 0 ? "" : "/" + Session);
    }
    public static string SessionId(string ThreadId)
    {
        if (!ThreadId.StartsWith(Prefix, StringComparison.Ordinal)) return "";
        int Separator = ThreadId.IndexOf('/');
        return Separator < 0 ? "" : NormalizeSessionId(ThreadId[(Separator + 1)..]);
    }
    public static InstanceProfile Profile(string Name) => new() { Id = ThreadId(Name), Name = "外部主管：" + NormalizeName(Name), Badge = "E" };

    public static RelayRequest Authorize(RelayRequest Request, ManagerSettings Settings)
    {
        if (Request.ExternalCaller.Length == 0)
        {
            if (Request.ExternalSessionId.Length != 0) throw new InvalidOperationException("外部主管会话 ID 需要 --external-caller");
            return Request;
        }
        if (Request.InstanceId.Length != 0 || Request.ThreadId.Length != 0) throw new InvalidOperationException("外部主管不能混用 Codex 调用方身份");
        string Caller = NormalizeName(Request.ExternalCaller);
        string Session = NormalizeSessionId(Request.ExternalSessionId);
        if (!Settings.AllowExternalSupervisors) throw new InvalidOperationException("管理器未开启允许外部主管");
        if (!Settings.ExternalSupervisorAllowList.Any(Name => NormalizeName(Name) == Caller)) throw new InvalidOperationException("外部主管不在名称允许列表中：" + Caller);
        if (Request.Command is not ("agent_instances" or "agent_projects" or "agent_sessions" or "agent_start" or "agent_task" or "agent_tree" or "relay_accept" or "agent_cancel" or "agent_wait" or "agent_inbox" or "agent_inbox_ack"))
            throw new InvalidOperationException("外部主管不支持此命令 " + Request.Command);
        return Request with { ExternalCaller = Caller, ExternalSessionId = Session, InstanceId = ThreadId(Caller), ThreadId = ThreadId(Caller, Session) };
    }
}
