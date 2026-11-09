using System.IO.Pipes;
using System.Text.Json;

namespace CodexInstanceManager;

public sealed record RelayRequest(string InstanceId, string ThreadId, string Command, JsonElement Arguments)
{
    public string ExternalCaller { get; init; } = "";
}

public sealed class RelayHost : IDisposable
{
    private readonly RelayCoordinator Coordinator;
    private readonly CancellationTokenSource Lifetime = new();
    private readonly Task[] ListenerList;
    private readonly string PipeName;
    private readonly AgentManagementService? AgentService;
    public event Action<Exception>? Failed;

    public RelayHost(RelayCoordinator Coordinator, string? PipeName = null, AgentManagementService? AgentService = null)
    {
        this.Coordinator = Coordinator;
        this.PipeName = PipeName ?? RelayLimits.PipeName;
        this.AgentService = AgentService;
        ListenerList = new[] { ListenAsync(), ListenAsync() };
    }

    public void Dispose() => Lifetime.Cancel();

    public static async Task<JsonElement> RequestAsync(RelayRequest Request, CancellationToken Cancellation, string? PipeName = null)
    {
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        Timeout.CancelAfter(TimeSpan.FromSeconds(Request.Command == "agent_wait" ? WaitSeconds(Request) + 15 : RelayLimits.RequestTimeoutSeconds + 5));
        using var Client = new NamedPipeClientStream(".", PipeName ?? RelayLimits.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Client.ConnectAsync(RelayLimits.ConnectTimeoutMilliseconds, Timeout.Token);
        await RelayWire.WriteAsync(Client, Request, RelayLimits.MaxFrameBytes, Timeout.Token);
        var Response = await RelayWire.ReadAsync(Client, RelayLimits.MaxFrameBytes, Timeout.Token);
        if (Response.TryGetProperty("error", out var Error)) throw new RelayRequestException(Response.TryGetProperty("error_kind", out var Kind) ? Kind.GetString()! : "rejected", Error.GetString()!);
        return Response.GetProperty("result").Clone();
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                var Server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await Server.WaitForConnectionAsync(Lifetime.Token); }
                catch { Server.Dispose(); throw; }
                // Keep accepting while an external caller waits for a worker's report.
                _ = HandleConnectionAsync(Server);
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception Error) { Program.LogError(Error); Failed?.Invoke(Error); }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream Server)
    {
        await using (Server)
        using (var RequestTimeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token))
        {
            RequestTimeout.CancelAfter(TimeSpan.FromSeconds(RelayLimits.RequestTimeoutSeconds + 10));
            try
            {
                var Message = await RelayWire.ReadAsync(Server, RelayLimits.MaxFrameBytes, RequestTimeout.Token);
                var Request = Message.Deserialize<RelayRequest>() ?? throw new InvalidDataException("联调请求为空");
                object Result;
                try
                {
                    if (Request.Command == "agent_wait") RequestTimeout.CancelAfter(TimeSpan.FromSeconds(WaitSeconds(Request) + 10));
                    _ = CancelOnDisconnectAsync(Server, RequestTimeout);
                    Result = await ExecuteAsync(Request, RequestTimeout.Token);
                }
                catch (Exception Error)
                {
                    Program.LogError(Error);
                    Result = new { error = Error.Message, error_kind = Error is IOException or TimeoutException or OperationCanceledException ? "delivery_uncertain" : "rejected" };
                    await RelayWire.WriteAsync(Server, Result, RelayLimits.MaxFrameBytes, RequestTimeout.Token);
                    return;
                }
                await RelayWire.WriteAsync(Server, new { result = Result }, RelayLimits.MaxFrameBytes, RequestTimeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception Error) when (Error is IOException or JsonException or InvalidDataException) { Program.LogError(Error); }
            finally { RequestTimeout.Cancel(); }
        }
    }

    private static async Task CancelOnDisconnectAsync(NamedPipeServerStream Server, CancellationTokenSource Cancellation)
    {
        try { if (await Server.ReadAsync(new byte[1], Cancellation.Token) == 0) Cancellation.Cancel(); }
        catch (Exception Error) when (Error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public static int WaitSeconds(RelayRequest Request)
    {
        int Seconds = Request.Arguments.TryGetProperty("timeout_seconds", out var Value) ? Value.GetInt32() : 60;
        if (Seconds is < 0 or > 86400) throw new InvalidOperationException("timeout_seconds 范围为 0 至 86400");
        return Seconds;
    }

    private async Task<object> ExecuteAsync(RelayRequest Request, CancellationToken Cancellation)
    {
        if (Request.ExternalCaller.Length == 0 && Request.InstanceId.StartsWith("external:", StringComparison.Ordinal))
            throw new InvalidOperationException("外部主管身份必须通过 --external-caller 授权");
        if (Request.ExternalCaller.Length != 0 || Request.Command.StartsWith("agent_", StringComparison.Ordinal) || Request.Command.StartsWith("dsh_", StringComparison.Ordinal))
            return await (AgentService ?? throw new InvalidOperationException("当前管理器未启用 CLI 服务 请更新并重启管理器")).ExecuteAsync(Request, Cancellation);
        var Arguments = Request.Arguments;
        switch (Request.Command)
        {
            case "relay_channel":
                return Coordinator.Channel ?? throw new InvalidOperationException("请先绑定联调通道");
            case "relay_dispatch":
            {
                var Item = await Coordinator.DispatchAsync(Request.InstanceId, Request.ThreadId, Request.InstanceId + "/" + Request.ThreadId + "/" + Arguments.GetProperty("request_id").GetString(), Arguments.GetProperty("title").GetString()!, Arguments.GetProperty("instructions").GetString()!, Arguments.GetProperty("acceptance").GetString()!);
                return Describe(Item, Item.State == eRelayState.Running ? RelayLimits.DispatchWaitInstruction : "按已保存的任务状态继续处理 不重复派发");
            }
            case "relay_report":
                return Describe(await Coordinator.ReportAsync(Request.InstanceId, Request.ThreadId, Arguments.GetProperty("task_id").GetString()!, Arguments.GetProperty("outcome").GetString()!, Arguments.GetProperty("summary").GetString()!, Arguments.GetProperty("artifacts").EnumerateArray().Select(Item => Item.GetString()!).ToList(), WaitForNotification: false), "执行结果已保存 主会话可从收件箱读取");
            case "relay_progress":
                return Describe(await Coordinator.ProgressAsync(Request.InstanceId, Request.ThreadId, Arguments.GetProperty("task_id").GetString()!, Arguments.GetProperty("stage").GetString()!, Arguments.GetProperty("message").GetString()!));
            case "relay_accept":
                await Coordinator.ReviewAsync(Request.InstanceId, Request.ThreadId, Arguments.GetProperty("task_id").GetString()!);
                return new { accepted = true };
            default: throw new InvalidOperationException("未知联调命令 " + Request.Command);
        }
    }

    private static object Describe(RelayTask Item, string Instruction = "") => new { task_id = Item.Id, state = RelayLimits.StateText(Item.State), outcome = Item.Outcome, instruction = Instruction };
}
