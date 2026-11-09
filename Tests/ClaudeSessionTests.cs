using System.Text;
using System.Text.Json;
using CodexInstanceManager;

internal static class ClaudeSessionTests
{
    private static int Count;
    public static void Run()
    {
        Count = 0;
        string Root = Path.Combine(Path.GetTempPath(), "ClaudeSessionTests-" + Guid.NewGuid().ToString("N"));
        string? PreviousConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Directory.CreateDirectory(Root);
        try
        {
            string Config = Path.Combine(Root, "claude-config");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Config);
            var Store = new ClaudeSessionStore(Path.Combine(Root, "registered"));
            string PendingId = Guid.NewGuid().ToString("D");
            var Pending = new ClaudeCodeSession { Id = PendingId, Title = "等待实际历史确认", Cwd = Root, CliPath = Path.Combine(Root, "claude.exe"), CallerName = "CLAUDE" };
            Store.Save(Pending);
            Require(Store.List("claude").Single().Id == PendingId && !Store.Get("claude", PendingId).Recorded, "registration retains the exact UUID without claiming the session started or was recorded by Claude");
            Require(Store.List("another").Count == 0 && Store.Find("another", PendingId) is null, "registered sessions are isolated by external caller");
            var Settings = new ClaudeCodeSettings { CallerName = "claude", WorkingDirectory = Root, CliPath = Path.Combine(Root, "new-claude.exe") };
            Require(Store.Discover(Settings).Single().Id == PendingId && !Store.Get("claude", PendingId).Recorded, "missing history preserves a pending registration without confirming it");

            string Project = Path.Combine(Config, "projects", "E--fixture-project");
            Directory.CreateDirectory(Project);
            string RecordedId = Guid.NewGuid().ToString("D");
            string OtherId = Guid.NewGuid().ToString("D");
            string WrongId = Guid.NewGuid().ToString("D");
            string Transcript = Path.Combine(Project, RecordedId + ".jsonl");
            File.WriteAllText(Transcript,
                Line(new { type = "user", sessionId = RecordedId, cwd = Root, message = new { content = "PRIVATE_BODY_DO_NOT_STORE", customTitle = "NOT_A_SESSION_TITLE" } }) +
                Line(new { type = "custom-title", sessionId = RecordedId, customTitle = "旧名称" }) +
                Line(new { type = "custom-title", sessionId = WrongId, customTitle = "错误会话名称" }) +
                Line(new { type = "custom-title", sessionId = RecordedId, customTitle = "真正的会话名称" }) +
                "{\"type\":\"custom-title\",\"sessionId\":", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Project, OtherId + ".jsonl"),
                Line(new { type = "user", sessionId = OtherId, cwd = Root, message = new { content = "ANOTHER_PRIVATE_BODY" } }) +
                Line(new { type = "custom-title", sessionId = OtherId, customTitle = "真正的会话名称" }), new UTF8Encoding(false));
            string Nested = Path.Combine(Project, "subagents");
            Directory.CreateDirectory(Nested);
            string NestedId = Guid.NewGuid().ToString("D");
            File.WriteAllText(Path.Combine(Nested, NestedId + ".jsonl"), Line(new { type = "user", sessionId = NestedId, cwd = Root }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Project, "not-a-session.jsonl"), Line(new { type = "user", sessionId = WrongId, cwd = Root }), new UTF8Encoding(false));
            var Discovered = Store.Discover(Settings);
            Require(Discovered.Count == 3 && Discovered.Count(Item => Item.Title == "真正的会话名称") == 2, "discovery merges genuine UUID sessions without conflating identical names");
            var Recorded = Store.Get("claude", RecordedId);
            Require(Recorded.Recorded && Recorded.Cwd == Root && Recorded.Title == "真正的会话名称", "history confirms the actual UUID cwd and last matching custom-title metadata");
            Require(Store.Find("claude", NestedId) is null && Store.Find("claude", WrongId) is null, "subagent history and non-UUID filenames do not become supervisor sessions");
            string Persisted = string.Join("\n", Directory.EnumerateFiles(Path.Combine(Root, "registered"), "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
            Require(!Persisted.Contains("PRIVATE_BODY") && !Persisted.Contains("NOT_A_SESSION_TITLE") && !Persisted.Contains("错误会话名称"), "only session metadata is persisted and message bodies never become titles or stored content");

            File.WriteAllText(Path.Combine(Project, PendingId + ".jsonl"),
                Line(new { type = "user", sessionId = PendingId, cwd = Root }) +
                Line(new { type = "custom-title", sessionId = PendingId, customTitle = "确认后的名称" }), new UTF8Encoding(false));
            Store.Discover(Settings);
            var Confirmed = Store.Get("claude", PendingId);
            Require(Confirmed.Recorded && Confirmed.Title == "确认后的名称" && Confirmed.CliPath == Pending.CliPath, "real history confirms a pending registration and preserves its explicitly selected CLI path");
            string Relocated = Path.Combine(Root, "moved-workspace");
            Directory.CreateDirectory(Relocated);
            File.AppendAllText(Transcript, "\n" + Line(new { type = "relocated", sessionId = RecordedId, relocatedCwd = Relocated }), new UTF8Encoding(false));
            var SourceHash = SettingsStore.FileHash(Transcript);
            Store.Discover(Settings);
            Require(Store.Get("claude", RecordedId).Cwd == Relocated, "explicit relocated metadata updates the resumable working directory");

            string LargeId = Guid.NewGuid().ToString("D");
            File.WriteAllText(Path.Combine(Project, LargeId + ".jsonl"),
                Line(new { type = "user", sessionId = LargeId, cwd = Root, message = new { content = new string('x', 2 * 1024 * 1024) } }) +
                Line(new { type = "custom-title", sessionId = LargeId, customTitle = "大日志尾部元数据" }) +
                Line(new { type = "assistant", sessionId = LargeId, cwd = Root, message = new { content = "PRIVATE_LARGE_BODY" } }), new UTF8Encoding(false));
            Store.Discover(Settings);
            Require(Store.Get("claude", LargeId).Recorded && Store.Get("claude", LargeId).Title == "大日志尾部元数据" && Store.Get("claude", LargeId).Cwd == Root, "bounded head-tail discovery skips a huge message while retaining complete metadata records in the tail");
            var Reopened = new ClaudeSessionStore(Path.Combine(Root, "registered"));
            Require(Reopened.Get("claude", RecordedId).Title == Recorded.Title && Reopened.Get("claude", PendingId).Recorded, "session identities and confirmation survive manager restart");
            Reject(() => Store.Get("claude", "../invalid"), "unsafe session IDs cannot escape the registry");
            Reject(() => Store.List("../claude"), "unsafe caller names cannot escape the registry");
            Reject(() => Store.Save(new() { Id = "not-a-uuid", CallerName = "claude", Title = "invalid" }), "new registrations require an exact UUID");
            Reject(() => Store.Save(new() { Id = Guid.NewGuid().ToString("D"), CallerName = "claude", Title = "invalid", Cwd = "relative" }), "registry refuses relative working directories");
            Require(SettingsStore.FileHash(Transcript) == SourceHash, "discovery never modifies original Claude history");
            Console.WriteLine($"PASS Claude sessions {Count} assertions");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", PreviousConfig);
            if (!ManagerPaths.IsUnder(Root, Path.GetTempPath())) throw new IOException("测试清理路径越界");
            Directory.Delete(Root, true);
        }
    }

    private static string Line(object Value) => JsonSerializer.Serialize(Value) + "\n";
    private static void Reject(Action Action, string Message)
    {
        try { Action(); }
        catch (Exception Error) when (Error is InvalidDataException or InvalidOperationException or IOException or ArgumentException) { Require(true, Message); return; }
        throw new Exception("FAILED Claude sessions: " + Message);
    }
    private static void Require(bool Condition, string Message) { if (!Condition) throw new Exception("FAILED Claude sessions: " + Message); Count++; }
}
