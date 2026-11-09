using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexInstanceManager;

internal static class ClaudeCodeSupervisorTests
{
    private static int Count;
    public static void Run() => Task.Run(VerifyAsync).GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        string Root = Path.Combine(Path.GetTempPath(), "ClaudeSupervisorTests-" + Guid.NewGuid().ToString("N"), "中文 'quote $literal & (round); space");
        Directory.CreateDirectory(Root);
        try
        {
            string Cli = Path.Combine(Root, "claude 'quoted $literal.ps1");
            string Callback = Path.Combine(Root, "mmcodex 'quoted $literal.exe");
            File.WriteAllText(Cli, FakeCli, new UTF8Encoding(true));
            File.WriteAllBytes(Callback, Array.Empty<byte>());
            var Settings = new ClaudeCodeSettings { CliPath = Cli, WorkingDirectory = Root, CallerName = "Claude" };
            Require(ClaudeCodeSupervisor.ResolvePath(Cli) == Path.GetFullPath(Cli), "configured CLI resolves as an absolute literal path");
            var Info = ClaudeCodeSupervisor.CreateTerminalStartInfo(Settings, Callback);
            Require(Info.UseShellExecute && Info.WindowStyle == ProcessWindowStyle.Normal && !Info.RedirectStandardInput && !Info.RedirectStandardOutput, "launch opens an interactive terminal rather than a headless model invocation");
            Require(Info.ArgumentList.Contains("-NoProfile") && Info.ArgumentList.Contains("-NoExit") && Info.ArgumentList.Contains("-EncodedCommand"), "terminal uses an encoded PowerShell command without loading a user shell profile");
            Require(Info.WorkingDirectory == ManagerPaths.Normalize(Root), "launch uses the requested literal working directory");
            Require(Info.ArgumentList.Take(2).SequenceEqual(new[] { "-ExecutionPolicy", "Bypass" }), "npm shim uses a temporary process policy without changing user or machine policy");
            string Encoded = Info.ArgumentList.Last();
            string Script = Encoding.Unicode.GetString(Convert.FromBase64String(Encoded));
            Require(!Script.Contains("Set-ExecutionPolicy", StringComparison.OrdinalIgnoreCase), "launcher never changes persistent Windows execution policies");
            Require(Encoded.Length + Info.FileName.Length + 128 < 30000, "real supervisor prompt fits the Windows process command line");
            string Prompt = ClaudeCodeSupervisor.BuildSupervisorPrompt(Callback, "CLAUDE");
            Require(Prompt.Contains("--external-caller claude") && Prompt.Contains("PowerShell") && Prompt.Contains("Bash / Git Bash"), "prompt uses a normalized external caller and covers both shell syntaxes");
            foreach (string Required in new[] { "instances", "sessions --instance", "start --json-file", "wait --task-id", "accept --task-id", "inbox-ack --task-id", "primary", "second", "write_paths", "read_only", "tree_session_max", "max_depth", "worker_limits", "request_id", "task_id" })
                Require(Prompt.Contains(Required), "supervisor workflow includes " + Required);
            foreach (string Forbidden in new[] { "--dangerously-skip-permissions", "--allow-dangerously-skip-permissions", "--permission-mode", "--bare", "--print", "--model", "--mcp-config" })
                Require(!Script.Contains(Forbidden, StringComparison.Ordinal), "launch does not override credentials, model, tools or permissions with " + Forbidden);
            string Capture = Path.Combine(Root, "captured args.json");
            // Execute the actual generated terminal command against a fake CLI; no real Claude model starts.
            Info.UseShellExecute = false;
            Info.CreateNoWindow = true;
            Info.RedirectStandardOutput = true;
            Info.RedirectStandardError = true;
            Info.ArgumentList.Remove("-NoExit");
            Info.Environment["MMCODEX_TEST_CAPTURE"] = Capture;
            Info.Environment["CODEX_HOME"] = "codex-home-fixture";
            Info.Environment["CODEX_THREAD_ID"] = "codex-thread-fixture";
            Info.Environment["ANTHROPIC_API_KEY"] = "fixture-api-key-preserve";
            using (var Cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var Child = Process.Start(Info) ?? throw new IOException("无法启动假 Claude CLI"))
            {
                var Output = Child.StandardOutput.ReadToEndAsync(Cancellation.Token);
                var Error = Child.StandardError.ReadToEndAsync(Cancellation.Token);
                try { await Child.WaitForExitAsync(Cancellation.Token); }
                finally { if (!Child.HasExited) Child.Kill(true); }
                await Task.WhenAll(Output, Error);
                Require(Child.ExitCode == 0 && File.Exists(Capture), "actual child process receives the generated launch command with metacharacter paths intact");
            }
            using (var Data = JsonDocument.Parse(File.ReadAllBytes(Capture)))
            {
                var Actual = Data.RootElement;
                var Arguments = Actual.GetProperty("arguments").EnumerateArray().Select(Value => Value.GetString()).ToArray();
                Require(Arguments.SequenceEqual(new[] { "--name", "Codex 外部主管：claude", "--append-system-prompt", Prompt }), "actual subprocess receives exactly four literal arguments and no task prompt");
                Require(Actual.GetProperty("cwd").GetString() == Root, "subprocess sees the selected cwd");
                Require(!Actual.GetProperty("hasCodexHome").GetBoolean() && !Actual.GetProperty("hasCodexThread").GetBoolean(), "launch removes only inherited Codex identity inside the child shell");
                Require(Actual.GetProperty("apiKeyPreserved").GetBoolean(), "launch preserves the existing Claude API environment");
            }
            string NativeCapture = Path.Combine(Root, "native argv.json");
            string NativeProbe = Path.ChangeExtension(typeof(ClaudeCodeSupervisorTests).Assembly.Location, ".exe");
            var NativeSettings = new ClaudeCodeSettings { CliPath = NativeProbe, WorkingDirectory = Root, CallerName = "claude" };
            var NativeInfo = ClaudeCodeSupervisor.CreateTerminalStartInfo(NativeSettings, Callback);
            Require(!NativeInfo.ArgumentList.Contains("-ExecutionPolicy"), "native Claude executable does not alter shell execution policy");
            NativeInfo.UseShellExecute = false;
            NativeInfo.CreateNoWindow = true;
            NativeInfo.RedirectStandardOutput = true;
            NativeInfo.RedirectStandardError = true;
            NativeInfo.ArgumentList.Remove("-NoExit");
            NativeInfo.Environment["MMCODEX_TEST_NATIVE_PROBE_FILE"] = NativeCapture;
            using (var Cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var Child = Process.Start(NativeInfo) ?? throw new IOException("无法启动原生 argv 探针"))
            {
                var Output = Child.StandardOutput.ReadToEndAsync(Cancellation.Token);
                var Error = Child.StandardError.ReadToEndAsync(Cancellation.Token);
                try { await Child.WaitForExitAsync(Cancellation.Token); }
                finally { if (!Child.HasExited) Child.Kill(true); }
                await Task.WhenAll(Output, Error);
                Require(Child.ExitCode == 0 && File.Exists(NativeCapture), "actual native executable receives the PowerShell-launched supervisor arguments");
            }
            using (var Data = JsonDocument.Parse(File.ReadAllBytes(NativeCapture)))
            {
                var Arguments = Data.RootElement.GetProperty("arguments").EnumerateArray().Select(Value => Value.GetString()).ToArray();
                Require(Arguments.SequenceEqual(new[] { "--name", "Codex 外部主管：claude", "--append-system-prompt", Prompt }), "native Windows argv preserves embedded JSON quotes, Unicode, multiline prompt and backslashes exactly");
                Require(Data.RootElement.GetProperty("cwd").GetString() == Root, "native executable inherits the chosen working directory");
            }
            var Check = await ClaudeCodeSupervisor.CheckAsync(Settings, CancellationToken.None);
            Require(Check.LoggedIn && Check.Version == "9.8.7" && Check.AuthMethod == "claude.ai" && Check.ApiProvider == "firstParty" && Check.Error == "", "fake CLI version and structured auth status produce the supported public health result: " + JsonSerializer.Serialize(Check));
            string HealthJson = JsonSerializer.Serialize(Check);
            Require(!HealthJson.Contains("private-email-fixture") && !HealthJson.Contains("private-api-key-fixture") && !HealthJson.Contains("private-stderr-fixture"), "health output never exposes raw auth JSON or stderr secrets");
            string Cmd = Path.ChangeExtension(Cli, ".cmd");
            File.WriteAllText(Cmd, "@echo off\r\nexit /b 99\r\n");
            Require(ClaudeCodeSupervisor.ResolvePath(Cmd) == Cli, "npm cmd resolves to its PowerShell sibling without a second cmd parser");
            File.WriteAllText(Cli, "Write-Output 'not-a-version private-api-key-fixture'; exit 1", new UTF8Encoding(true));
            var Failed = await ClaudeCodeSupervisor.CheckAsync(Settings, CancellationToken.None);
            Require(!Failed.LoggedIn && Failed.Error.Contains("退出码 1") && !JsonSerializer.Serialize(Failed).Contains("private-api-key-fixture"), "failing CLI checks return a fixed error without raw process output");
            using var Cancelled = new CancellationTokenSource();
            Cancelled.Cancel();
            await RejectAsync<OperationCanceledException>(() => ClaudeCodeSupervisor.CheckAsync(Settings, Cancelled.Token), "cancelled health check starts no further probes");
            Reject(() => ClaudeCodeSupervisor.BuildSupervisorPrompt(Callback, "../claude"), "invalid external caller is rejected before launch");
            Reject(() => ClaudeCodeSupervisor.CreateTerminalStartInfo(new() { CliPath = Cli, WorkingDirectory = "relative", CallerName = "claude" }, Callback), "relative working directory is rejected");
            Reject(() => ClaudeCodeSupervisor.CreateTerminalStartInfo(Settings, Path.Combine(Root, "missing.exe")), "missing mmcodex callback is rejected at launch");
            Console.WriteLine($"PASS Claude Code supervisor {Count} assertions");
        }
        finally
        {
            string CleanupRoot = Path.GetDirectoryName(Root)!;
            if (!ManagerPaths.IsUnder(CleanupRoot, Path.GetTempPath())) throw new IOException("测试清理路径越界");
            Directory.Delete(CleanupRoot, true);
        }
    }

    private const string FakeCli = """
        $ErrorActionPreference = 'Stop'
        if ($args[0] -eq '--version') { Write-Output '9.8.7 (Claude Code)'; exit 0 }
        if ($args[0] -eq 'auth') {
            if ($env:PSExecutionPolicyPreference -ne 'Bypass') { exit 73 }
            [Console]::Error.WriteLine('private-stderr-fixture')
            Write-Output '{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"private-email-fixture","apiKey":"private-api-key-fixture"}'
            exit 0
        }
        $result = @{
            arguments = @($args)
            cwd = (Get-Location).ProviderPath
            hasCodexHome = -not [string]::IsNullOrEmpty($env:CODEX_HOME)
            hasCodexThread = -not [string]::IsNullOrEmpty($env:CODEX_THREAD_ID)
            apiKeyPreserved = $env:ANTHROPIC_API_KEY -eq 'fixture-api-key-preserve'
        }
        [IO.File]::WriteAllText($env:MMCODEX_TEST_CAPTURE, ($result | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        exit 0
        """;

    private static void Reject(Action Action, string Message)
    {
        try { Action(); }
        catch (Exception Error) when (Error is InvalidOperationException or IOException or ArgumentException) { Require(true, Message); return; }
        throw new Exception("FAILED Claude: " + Message);
    }
    private static async Task RejectAsync<T>(Func<Task> Action, string Message) where T : Exception
    {
        try { await Action(); }
        catch (T) { Require(true, Message); return; }
        throw new Exception("FAILED Claude: " + Message);
    }
    private static void Require(bool Condition, string Message)
    {
        if (!Condition) throw new Exception("FAILED Claude: " + Message);
        Count++;
    }
}
