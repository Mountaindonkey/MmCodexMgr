using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodexInstanceManager;

internal static class ExternalCliTests
{
    private static int Count;

    public static void Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var Prefix = AgentCli.Parse(new[] { "--external-caller", "claude", "instances" });
        Require(Prefix.Command == "instances" && Prefix.OptionDict["external-caller"] == "claude", "global external caller precedes command");
        Require(!Prefix.Arguments.TryGetProperty("external_caller", out _), "caller identity stays outside task arguments");
        var Suffix = AgentCli.Parse(new[] { "inbox", "--external-caller", "claude", "--unread" });
        Require(Suffix.Arguments.GetProperty("unread").GetBoolean(), "unread supports standalone flag");
        var Explicit = AgentCli.Parse(new[] { "--external-caller", "claude", "inbox", "--unread", "false" });
        Require(!Explicit.Arguments.GetProperty("unread").GetBoolean(), "unread accepts explicit boolean");
        var Wait = AgentCli.Parse(new[] { "wait", "--external-caller", "claude", "--task-id", "task-1", "--timeout-seconds", "0" });
        Require(Wait.Arguments.GetProperty("timeout_seconds").GetInt32() == 0, "zero timeout supports immediate query");
        Reject(new[] { "--external-caller", "claude", "instances", "--caller-thread", "pretend" }, "mixed caller thread rejected");
        Reject(new[] { "instances", "--external-caller", "claude", "--caller-instance", "primary" }, "mixed caller instance rejected");
        Reject(new[] { "--external-caller", "claude", "doctor" }, "external caller cannot bypass authorization via local commands");
        Reject(new[] { "--external-caller", "claude", "instances", "--external-caller", "other" }, "duplicate identity rejected");
        Reject(new[] { "wait", "--task-id", "task-1" }, "wait requires external identity");
        Reject(new[] { "--external-caller", "claude", "wait", "--task-id", "task-1", "--timeout-seconds", "-1" }, "negative timeout rejected");
        Reject(new[] { "--external-caller", "claude", "wait", "--task-id", "task-1", "--timeout-seconds", "86401" }, "excessive timeout rejected");
        Reject(new[] { "--external-caller", "claude", "inbox-ack" }, "ack requires task id");
        await VerifyTransportAsync();
        Console.WriteLine($"PASS external CLI {Count} assertions");
    }

    private static async Task VerifyTransportAsync()
    {
        string Root = Path.Combine(Path.GetTempPath(), "ExternalCliTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        string? PreviousThread = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        string? PreviousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_THREAD_ID", null);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(Root, "unregistered-home"));
            string JsonPath = Path.Combine(Root, "start.json");
            File.WriteAllText(JsonPath, JsonSerializer.Serialize(new
            {
                request_id = "external-cli-test", instance = "primary", create_title = "Read only test",
                title = "Read a fixture", instructions = "Read the fixture and report", acceptance = "Verify fixture content",
                workspace = new { access = "read_only" }
            }), new UTF8Encoding(false));
            string PipeName = "ExternalCliTests-" + Guid.NewGuid().ToString("N");
            using var Cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var Server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var Service = Task.Run(async () =>
            {
                await Server.WaitForConnectionAsync(Cancellation.Token);
                var Message = await RelayWire.ReadAsync(Server, RelayLimits.MaxFrameBytes, Cancellation.Token);
                var Request = Message.Deserialize<RelayRequest>()!;
                await RelayWire.WriteAsync(Server, new { result = new { task_id = "task-1", thread_id = "worker-1", instance_id = "primary", created = true, session_kind = "new" } }, RelayLimits.MaxFrameBytes, Cancellation.Token);
                return Request;
            });
            using var Output = new MemoryStream();
            using var Error = new StringWriter();
            int ExitCode = await AgentCli.RunAsync(new[] { "--external-caller", "claude", "start", "--json-file", JsonPath }, Stream.Null, Output, Error, Environment.ProcessPath!, Cancellation.Token, PipeName, () => new ManagerSettings());
            var Captured = await Service;
            Require(ExitCode == 0 && Error.ToString().Length == 0, "external start works without Codex thread or registered caller instance");
            Require(Captured.ExternalCaller == "claude" && Captured.InstanceId == "" && Captured.ThreadId == "" && Captured.Command == "agent_start", "wire request carries distinct external identity without fabricated Codex caller");
            using var Result = JsonDocument.Parse(Output.ToArray());
            Require(Result.RootElement.GetProperty("task_id").GetString() == "task-1" && Result.RootElement.GetProperty("created").GetBoolean(), "start preserves structured JSON result");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_THREAD_ID", PreviousThread);
            Environment.SetEnvironmentVariable("CODEX_HOME", PreviousHome);
            if (!ManagerPaths.IsUnder(Root, Path.GetTempPath())) throw new IOException("测试清理路径越界");
            Directory.Delete(Root, true);
        }
    }

    private static void Reject(string[] Arguments, string Message)
    {
        try { AgentCli.Parse(Arguments); }
        catch (Exception Error) when (Error is InvalidOperationException or FormatException) { Require(true, Message); return; }
        throw new Exception("FAILED: " + Message);
    }

    private static void Require(bool Condition, string Message)
    {
        if (!Condition) throw new Exception("FAILED: " + Message);
        Count++;
    }
}
