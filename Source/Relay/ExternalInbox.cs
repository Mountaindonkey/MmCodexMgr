using System.Text.Json;

namespace CodexInstanceManager;

public sealed record ExternalEvent(string EventId, string TaskId, string Event, string Message, DateTimeOffset At, string ThreadId, string InstanceId);

public sealed class ExternalReport
{
    public string TaskId { get; set; } = "";
    public string ExternalCaller { get; set; } = "";
    public string ExternalSessionId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Artifacts { get; set; } = new();
    public DateTimeOffset ReportedAt { get; set; }
    public string ThreadId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public bool Read { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Internal durable backup; callers receive reports through the CLI pipe.</summary>
public sealed class ExternalInbox
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly string Root;
    private readonly object Gate = new();
    public ExternalInbox(string? Root = null) => this.Root = Root ?? Path.Combine(ManagerPaths.Root, "external");
    private string Folder(string Name) => Path.Combine(Root, ExternalSupervisor.NormalizeName(Name));
    private string ReportPath(string Name, string TaskId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(TaskId, "^[a-zA-Z0-9_-]{1,128}$")) throw new InvalidOperationException("任务 ID 不合法");
        return Path.Combine(Folder(Name), "inbox", TaskId + ".json");
    }
    public ExternalReport? Read(string Name, string TaskId)
    {
        lock (Gate)
        {
            string FilePath = ReportPath(Name, TaskId);
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<ExternalReport>(File.ReadAllBytes(FilePath), JsonOptions) ?? throw new InvalidDataException("外部回报为空") : null;
        }
    }
    public List<ExternalReport> List(string Name, bool Unread = false, string After = "", string? SessionId = null)
    {
        lock (Gate)
        {
            string DirectoryPath = Path.Combine(Folder(Name), "inbox");
            if (!Directory.Exists(DirectoryPath)) return new();
            return Directory.EnumerateFiles(DirectoryPath, "*.json").Select(FilePath => JsonSerializer.Deserialize<ExternalReport>(File.ReadAllBytes(FilePath), JsonOptions) ?? throw new InvalidDataException("外部回报为空"))
                .Where(Item => (!Unread || !Item.Read) && (SessionId is null || Item.ExternalSessionId == SessionId) && string.CompareOrdinal(Item.TaskId, After) > 0).OrderBy(Item => Item.TaskId, StringComparer.Ordinal).Take(RelayLimits.PageSize + 1).ToList();
        }
    }
    public ExternalReport Ack(string Name, string TaskId)
    {
        lock (Gate)
        {
            var Report = Read(Name, TaskId) ?? throw new InvalidOperationException("任务尚无外部回报 " + TaskId);
            if (!Report.Read)
            {
                Report.Read = true;
                Report.ReadAt = DateTimeOffset.UtcNow;
                SettingsStore.AtomicWrite(ReportPath(Name, TaskId), JsonSerializer.SerializeToUtf8Bytes(Report, JsonOptions));
            }
            return Report;
        }
    }
    public void SaveReport(RelayTask Item)
    {
        lock (Gate)
        {
            string Name = Item.Channel.ExternalCaller;
            if (Name.Length == 0 || Item.Outcome.Length == 0) return;
            string FilePath = ReportPath(Name, Item.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (!File.Exists(FilePath))
                SettingsStore.AtomicWrite(FilePath, JsonSerializer.SerializeToUtf8Bytes(new ExternalReport
                {
                    TaskId = Item.Id, ExternalCaller = Name, ExternalSessionId = Item.Channel.ExternalSessionId, Outcome = Item.Outcome, Summary = Item.Result,
                    Artifacts = Item.ArtifactList, ReportedAt = Item.ReportedAt ?? Item.UpdatedAt,
                    ThreadId = Item.Channel.WorkerThreadId, InstanceId = Item.Channel.WorkerInstance.Id
                }, JsonOptions));
            SyncEvents(Item);
        }
    }
    public void SyncEvents(RelayTask Item)
    {
        lock (Gate)
        {
            if (Item.Channel.ExternalCaller.Length == 0 || Item.ExternalEvents.Count == 0) return;
            string DirectoryPath = Folder(Item.Channel.ExternalCaller);
            Directory.CreateDirectory(DirectoryPath);
            string FilePath = Path.Combine(DirectoryPath, "events.jsonl");
            var Seen = new HashSet<string>(StringComparer.Ordinal);
            // Only newline-terminated records are committed; the task journal replays a torn tail.
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 0)
            {
                using var Tail = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                long Position = Tail.Length - 1;
                Tail.Position = Position;
                if (Tail.ReadByte() != '\n')
                {
                    while (Position >= 0)
                    {
                        Tail.Position = Position;
                        if (Tail.ReadByte() == '\n') break;
                        Position--;
                    }
                    Tail.SetLength(Position + 1);
                    Tail.Flush(true);
                }
            }
            if (File.Exists(FilePath))
                foreach (string Line in File.ReadLines(FilePath))
                {
                    try { using var Data = JsonDocument.Parse(Line); Seen.Add(Data.RootElement.GetProperty("event_id").GetString()!); }
                    catch (JsonException) { /* A damaged record is replayed from the task journal. */ }
                }
            foreach (var Event in Item.ExternalEvents.Where(Event => !Seen.Contains(Event.EventId)))
            {
                using var Output = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                byte[] Data = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Event, JsonOptions) + "\n");
                Output.Write(Data);
                Output.Flush(true);
            }
        }
    }
}
