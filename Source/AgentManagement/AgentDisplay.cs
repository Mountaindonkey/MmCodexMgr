namespace CodexInstanceManager;

public static class AgentDisplay
{
    public static string Instance(ManagerSettings Settings, string InstanceId, string RecordedName)
    {
        if (InstanceId == CursorRelayRunner.InstanceId) return "Cursor";
        if (InstanceId.Equals("dsh", StringComparison.OrdinalIgnoreCase)) return "DSH";
        return Settings.InstanceList.FirstOrDefault(Item => Item.Id == InstanceId)?.Name ?? RecordedName;
    }

    public static string Session(ManagerSettings Settings, string InstanceId, string RecordedName, string Title, string ThreadId) =>
        Instance(Settings, InstanceId, RecordedName) + "：" + (Title.Length == 0 ? ThreadId : Title);

    public static string ExternalSession(string InstanceId, string Title, string ThreadId)
    {
        string Caller = InstanceId["external:".Length..];
        string Id = ExternalSupervisor.SessionId(ThreadId);
        return Id.Length == 0 ? "外部主管：" + Caller + "（未关联会话）"
            : "外部主管：" + Caller + " / " + (Title.Length == 0 ? "Claude Code 会话" : Title) + " [" + Id[..8] + "]";
    }
}
