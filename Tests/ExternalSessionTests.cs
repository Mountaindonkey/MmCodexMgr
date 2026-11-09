using System.Text;
using System.Text.Json;
using CodexInstanceManager;
using Microsoft.Data.Sqlite;

internal static class ExternalSessionTests
{
    private static int Count;
    public static void Run() => Task.Run(VerifyAsync).GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        string SessionA = Guid.NewGuid().ToString("D");
        string SessionB = Guid.NewGuid().ToString("D");
        var Call = AgentCli.Parse(new[] { "--external-session-id", SessionA.ToUpperInvariant(), "--external-caller", "claude", "instances" });
        Require(Call.OptionDict["external-session-id"] == SessionA && !Call.Arguments.TryGetProperty("external_session_id", out _), "session identity is canonicalized outside task arguments");
        var Suffix = AgentCli.Parse(new[] { "instances", "--external-caller", "claude", "--external-session-id", SessionB });
        Require(Suffix.OptionDict["external-session-id"] == SessionB, "session identity also works after the command");
        Reject(() => AgentCli.Parse(new[] { "--external-session-id", SessionA, "instances" }), "session identity requires an external caller");
        Reject(() => AgentCli.Parse(new[] { "--external-caller", "claude", "--external-session-id", "not-a-uuid", "instances" }), "invalid session UUID is rejected");
        Reject(() => AgentCli.Parse(new[] { "--external-caller", "claude", "--external-session-id", SessionA, "instances", "--external-session-id", SessionB }), "duplicate session identity is rejected");
        Require(ExternalSupervisor.ThreadId("CLAUDE") == "external:claude" && ExternalSupervisor.ThreadId("CLAUDE", SessionA.ToUpperInvariant()) == "external:claude/" + SessionA, "legacy and session thread identities remain distinct and stable");
        Require(ExternalSupervisor.SessionId(ExternalSupervisor.ThreadId("claude", SessionA)) == SessionA && ExternalSupervisor.SessionId("normal-codex-thread") == "", "session helper only parses external supervisor threads");

        using var Fixture = new Fixture();
        var A = await Fixture.StartAsync(SessionA.ToUpperInvariant(), "same-request", "worker-a");
        var B = await Fixture.StartAsync(SessionB, "same-request", "worker-b");
        var Legacy = await Fixture.StartAsync("", "same-request", "legacy-worker");
        string IdA = A.GetProperty("task_id").GetString()!;
        string IdB = B.GetProperty("task_id").GetString()!;
        string IdLegacy = Legacy.GetProperty("task_id").GetString()!;
        Require(new[] { IdA, IdB, IdLegacy }.Distinct().Count() == 3, "same caller and request id in different sessions create independent tasks");
        var Tasks = new[] { Fixture.Store.Get(IdA), Fixture.Store.Get(IdB), Fixture.Store.Get(IdLegacy) };
        Require(Tasks.All(Item => Item.Channel.MainInstance.Id == "external:claude") && Tasks.Select(Item => Item.TreeRootId).Distinct().Count() == 3, "sessions share a pseudo instance but have separate task trees");
        Require(Tasks[0].Channel.MainThreadId == "external:claude/" + SessionA && Tasks[1].Channel.MainThreadId == "external:claude/" + SessionB && Tasks[2].Channel.MainThreadId == "external:claude", "task ownership binds the actual external session id");
        Require(Tasks[0].Channel.ExternalSessionId == SessionA && Tasks[1].Channel.ExternalSessionId == SessionB && Tasks[2].Channel.ExternalSessionId == "", "saved channels persist session identity while old mode stays empty");
        Require(A.GetProperty("source").GetProperty("external_session_id").GetString() == SessionA && A.GetProperty("source").GetProperty("title").GetString()!.Contains(SessionA[..8]), "start identifies the source Claude conversation");
        var StatusA = await Fixture.CallAsync(SessionA, "status", "--task-id", IdA);
        Require(StatusA.GetProperty("source").GetProperty("external_session_id").GetString() == SessionA && StatusA.GetProperty("source").GetProperty("thread_id").GetString() == "external:claude/" + SessionA, "status identifies the exact external source conversation");
        var Retry = await Fixture.StartAsync(SessionA, "same-request", "worker-a");
        Require(Retry.GetProperty("task_id").GetString() == IdA && Fixture.Bridge.SendCount == 3, "idempotent retries stay within the current external session");
        await Fixture.RejectStartAsync(SessionA, "over-a", "worker-a2", "one session cannot exceed its own cross-agent budget");
        await Fixture.RejectStartAsync(SessionA, "tree-over-a", "worker-a3", "raising current concurrency still cannot exceed the session tree budget", 2);
        await Fixture.RejectStartAsync(Guid.NewGuid().ToString("D"), "steal-worker", "worker-a", "separate external sessions still cannot occupy one active execution conversation");
        foreach (string ForeignSession in new[] { SessionB, "" })
            foreach (string Command in new[] { "status", "tree", "cancel", "wait", "inbox-ack", "accept" })
                await Fixture.RejectCallAsync(ForeignSession, new[] { Command, "--task-id", IdA }, "another conversation or legacy identity cannot use " + Command + " on this session's task");
        foreach (string Command in new[] { "status", "tree", "cancel", "wait", "inbox-ack", "accept" })
            await Fixture.RejectCallAsync(SessionA, new[] { Command, "--task-id", IdLegacy }, "session-scoped caller cannot claim legacy task through " + Command);

        await Fixture.Coordinator.ReportAsync("primary", "worker-a", IdA, "completed", "session A result", new());
        await Fixture.Coordinator.ReportAsync("primary", "worker-b", IdB, "completed", "session B result", new());
        await Fixture.Coordinator.ReportAsync("primary", "legacy-worker", IdLegacy, "completed", "legacy result", new());
        foreach (var Pair in new[] { (SessionA, IdA), (SessionB, IdB), ("", IdLegacy) })
        {
            var Inbox = await Fixture.CallAsync(Pair.Item1, "inbox");
            var Items = Inbox.GetProperty("items");
            Require(Items.GetArrayLength() == 1 && Items[0].GetProperty("task_id").GetString() == Pair.Item2, "inbox filters reports to the exact external session including legacy mode");
            var Report = await Fixture.CallAsync(Pair.Item1, "wait", "--task-id", Pair.Item2, "--timeout-seconds", "0");
            Require(Report.GetProperty("external_session_id").GetString() == Pair.Item1, "wait report carries the matching external session id");
        }
        await Fixture.CallAsync(SessionA, "accept", "--task-id", IdA);
        Fixture.Store.DeleteFinished(IdA);
        var SavedA = await Fixture.CallAsync(SessionA, "wait", "--task-id", IdA, "--timeout-seconds", "0");
        Require(SavedA.GetProperty("summary").GetString() == "session A result", "owner can recover its durable report after task history cleanup");
        foreach (string ForeignSession in new[] { SessionB, "" })
        {
            await Fixture.RejectCallAsync(ForeignSession, new[] { "wait", "--task-id", IdA, "--timeout-seconds", "0" }, "cleaned durable reports cannot be claimed by another or legacy session");
            await Fixture.RejectCallAsync(ForeignSession, new[] { "inbox-ack", "--task-id", IdA }, "another session cannot acknowledge a durable report after cleanup");
        }
        var AckA = await Fixture.CallAsync(SessionA, "inbox-ack", "--task-id", IdA);
        Require(AckA.GetProperty("read").GetBoolean(), "owner can acknowledge its durable report after cleanup");
        var InboxB = await Fixture.CallAsync(SessionB, "inbox", "--unread");
        Require(InboxB.GetProperty("items").GetArrayLength() == 1 && !InboxB.GetProperty("items")[0].GetProperty("read").GetBoolean(), "acknowledgment does not consume another external session's report");
        await Fixture.RejectRawAsync(new RelayRequest("primary", "codex-thread", "relay_accept", JsonSerializer.SerializeToElement(new { task_id = IdB })) { ExternalSessionId = SessionB }, "raw session field without external caller cannot enter legacy relay commands");
        Require(Fixture.Catalog.ExternalLookups == 0, "external sessions are never resolved through the Codex conversation catalog");
        Console.WriteLine($"PASS external supervisor sessions {Count} assertions");
    }

    private static void Reject(Action Action, string Message)
    {
        try { Action(); }
        catch (InvalidOperationException) { Require(true, Message); return; }
        throw new Exception("FAILED external sessions: " + Message);
    }
    private static void Require(bool Condition, string Message)
    {
        if (!Condition) throw new Exception("FAILED external sessions: " + Message);
        Count++;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "ExternalSessionTests-" + Guid.NewGuid().ToString("N"));
        private readonly string Pipe = "ExternalSessionTests-" + Guid.NewGuid().ToString("N");
        private readonly ManagerSettings Settings = new()
        {
            AllowExternalSupervisors = true, ExternalSupervisorAllowList = new() { "claude" },
            InstanceList = new() { new() { Id = "primary", Name = "主实例", IsPrimary = true } }
        };
        public RelayStore Store { get; }
        public FakeBridge Bridge { get; } = new();
        public FakeCatalog Catalog { get; } = new();
        public RelayCoordinator Coordinator { get; }
        private readonly RelayHost Host;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new(Path.Combine(Root, "relay"));
            Coordinator = new(Store, Bridge, ExternalInbox: new(Path.Combine(Root, "external")), OutboxRoot: Path.Combine(Root, "outbox"));
            Host = new(Coordinator, Pipe, new(Store, Coordinator, Catalog, Environment.ProcessPath!, () => Settings));
        }
        public Task<JsonElement> StartAsync(string Session, string RequestId, string Worker, int CrossLimit = 1) => CallAsync(Session, "start", "--json-file", JsonFile(new
        {
            request_id = RequestId, instance = "primary", session = Worker, title = "只读会话隔离测试", instructions = "只读取指定文件并回报", acceptance = "复核原文件",
            workspace = new { access = "read_only" }, subagent_max = 0, session_max = 1, cross_agent_max = CrossLimit,
            tree_session_max = 1, max_depth = 1, worker_limits = new { subagent_max = 0, session_max = 0, cross_agent_max = 0 }
        }));
        public Task RejectStartAsync(string Session, string RequestId, string Worker, string Message, int CrossLimit = 1) => RejectAsync(() => StartAsync(Session, RequestId, Worker, CrossLimit), Message);
        public Task RejectCallAsync(string Session, string[] Arguments, string Message) => RejectAsync(() => CallAsync(Session, Arguments), Message);
        private static async Task RejectAsync(Func<Task> Action, string Message)
        {
            try { await Action(); }
            catch (InvalidOperationException) { Require(true, Message); return; }
            throw new Exception("FAILED external sessions: " + Message);
        }
        public Task RejectRawAsync(RelayRequest Request, string Message) => RejectAsync(() => RelayHost.RequestAsync(Request, CancellationToken.None, Pipe), Message);
        public async Task<JsonElement> CallAsync(string Session, params string[] Arguments)
        {
            string[] Identity = Session.Length == 0 ? new[] { "--external-caller", "claude" } : new[] { "--external-caller", "claude", "--external-session-id", Session };
            using var Output = new MemoryStream();
            using var Error = new StringWriter();
            using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            int ExitCode = await AgentCli.RunAsync(Identity.Concat(Arguments).ToArray(), Stream.Null, Output, Error, Environment.ProcessPath!, Timeout.Token, Pipe, () => Settings, Path.Combine(Root, "outbox"));
            if (ExitCode != 0) throw new InvalidOperationException(Error.ToString());
            using var Data = JsonDocument.Parse(Output.ToArray());
            return Data.RootElement.Clone();
        }
        private string JsonFile(object Value)
        {
            string FilePath = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Value), new UTF8Encoding(false));
            return FilePath;
        }
        public void Dispose()
        {
            Host.Dispose();
            Coordinator.Dispose();
            SqliteConnection.ClearAllPools();
            if (!ManagerPaths.IsUnder(Root, Path.GetTempPath())) throw new IOException("测试清理路径越界");
            Directory.Delete(Root, true);
        }
    }
    private sealed class FakeBridge : IRelayBridge
    {
        public int SendCount { get; private set; }
        public Task SendAsync(InstanceProfile Instance, string ThreadId, string Prompt, string Model, CancellationToken Cancellation) { SendCount++; return Task.CompletedTask; }
    }
    private sealed class FakeCatalog : IAgentCatalog
    {
        public int ExternalLookups { get; private set; }
        public AgentSession Session(InstanceProfile Instance, string Id)
        {
            if (Instance.Id.StartsWith("external:", StringComparison.Ordinal)) { ExternalLookups++; throw new InvalidOperationException("external session is not a Codex session"); }
            return new(Id, Id, "", "");
        }
        public Task<List<AgentProject>> ProjectsAsync(InstanceProfile Instance, CancellationToken Cancellation) => Task.FromResult(new List<AgentProject>());
        public List<AgentSession> Sessions(InstanceProfile Instance, AgentProject? Project, string? Selector, int Offset) => new() { Session(Instance, Selector ?? "worker") };
        public Task RequireIdleAsync(InstanceProfile Instance, string ThreadId, CancellationToken Cancellation) => Task.CompletedTask;
        public Task<RelayThread> CreateAsync(InstanceProfile Instance, AgentProject? Project, string Title, string Prompt, CancellationToken Cancellation) => throw new InvalidOperationException("fixture reuses existing sessions");
    }
}
