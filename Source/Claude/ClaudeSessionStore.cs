using System.Text.Json;

namespace CodexInstanceManager;

/// <summary>A registered UUID is not evidence of a running Claude process. Recorded only confirms local history.</summary>
public sealed class ClaudeCodeSession
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string CliPath { get; set; } = "";
    public string CallerName { get; set; } = "claude";
    public bool Recorded { get; set; }
}

/// <summary>Own metadata registry plus bounded, read-only discovery of Claude project JSONL metadata.</summary>
public sealed class ClaudeSessionStore
{
    private const int MaxSessions = 200;
    private const int MaxProjects = 256;
    private const int MaxCandidateFiles = 2048;
    private const int HeadBytes = 128 * 1024;
    private const int TailBytes = 256 * 1024;
    private readonly string Root;
    private readonly object Gate = new();

    public ClaudeSessionStore(string? Root = null) => this.Root = Root ?? Path.Combine(ManagerPaths.Root, "claude", "sessions");

    public List<ClaudeCodeSession> List(string Caller)
    {
        lock (Gate)
        {
            string Name = ExternalSupervisor.NormalizeName(Caller);
            string Folder = Path.Combine(Root, Name);
            if (!Directory.Exists(Folder)) return new();
            var Items = new List<(ClaudeCodeSession Session, DateTime Updated)>();
            foreach (string FilePath in Directory.EnumerateFiles(Folder, "*.json").Take(1000))
            {
                try
                {
                    var Session = Find(Name, Path.GetFileNameWithoutExtension(FilePath));
                    if (Session is not null) Items.Add((Session, File.GetLastWriteTimeUtc(FilePath)));
                }
                catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { }
            }
            return Items.OrderByDescending(Item => Item.Updated).ThenBy(Item => Item.Session.Id, StringComparer.Ordinal).Select(Item => Item.Session).ToList();
        }
    }

    public ClaudeCodeSession? Find(string Caller, string Id)
    {
        lock (Gate)
        {
            string Name = ExternalSupervisor.NormalizeName(Caller);
            string NormalId = NormalizeId(Id);
            string FilePath = Path.Combine(Root, Name, NormalId + ".json");
            if (!File.Exists(FilePath)) return null;
            if (new FileInfo(FilePath).Length > 64 * 1024) throw new InvalidDataException("Claude 会话登记超过元数据限制");
            var Session = JsonSerializer.Deserialize<ClaudeCodeSession>(File.ReadAllBytes(FilePath)) ?? throw new InvalidDataException("Claude 会话登记为空");
            if (NormalizeId(Session.Id) != NormalId || ExternalSupervisor.NormalizeName(Session.CallerName) != Name) throw new InvalidDataException("Claude 会话登记身份不匹配");
            return Normalize(Session);
        }
    }

    public ClaudeCodeSession Get(string Caller, string Id) => Find(Caller, Id) ?? throw new InvalidOperationException("管理器未登记此 Claude Code 会话 " + Id);

    public void Save(ClaudeCodeSession Session)
    {
        lock (Gate)
        {
            var Item = Normalize(Session);
            string FilePath = Path.Combine(Root, Item.CallerName, Item.Id + ".json");
            byte[] Content = JsonSerializer.SerializeToUtf8Bytes(Item);
            if (File.Exists(FilePath) && File.ReadAllBytes(FilePath).AsSpan().SequenceEqual(Content)) return;
            SettingsStore.AtomicWrite(FilePath, Content);
        }
    }

    public List<ClaudeCodeSession> Discover(ClaudeCodeSettings Settings)
    {
        lock (Gate)
        {
            string Caller = ExternalSupervisor.NormalizeName(Settings.CallerName);
            string? Configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            string ConfigRoot = string.IsNullOrWhiteSpace(Configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : Path.GetFullPath(Configured, Path.IsPathFullyQualified(Settings.WorkingDirectory) ? Settings.WorkingDirectory : Environment.CurrentDirectory);
            string Projects = Path.Combine(ConfigRoot, "projects");
            if (!Directory.Exists(Projects)) return List(Caller);
            var Existing = List(Caller).ToDictionary(Item => Item.Id, StringComparer.Ordinal);
            foreach (var File in CandidateFiles(Projects).OrderByDescending(File => File.LastWriteTimeUtc).Take(MaxSessions).Reverse())
            {
                try
                {
                    var Metadata = ReadMetadata(File);
                    if (Metadata is null) continue;
                    Existing.TryGetValue(Metadata.Id, out var Previous);
                    var Merged = new ClaudeCodeSession
                    {
                        Id = Metadata.Id,
                        CallerName = Caller,
                        Title = Metadata.Title.Length != 0 ? Metadata.Title : Previous?.Title ?? "Claude Code · " + Metadata.Id[..8],
                        Cwd = Metadata.Cwd.Length != 0 ? Metadata.Cwd : Previous?.Cwd ?? "",
                        CliPath = Previous is not null && Previous.CliPath.Length > 0 ? Previous.CliPath : Settings.CliPath,
                        Recorded = true
                    };
                    Save(Merged);
                    Existing[Merged.Id] = Merged;
                }
                catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { }
            }
            return List(Caller);
        }
    }

    public static string NormalizeId(string Id)
    {
        if (!Guid.TryParseExact(Id, "D", out var Value)) throw new InvalidDataException("Claude Code 会话 ID 必须为标准 UUID");
        return Value.ToString("D");
    }

    private static ClaudeCodeSession Normalize(ClaudeCodeSession Session)
    {
        string Cwd = Session.Cwd.Trim();
        string Cli = Session.CliPath.Trim();
        if (Cwd.Length != 0 && !Path.IsPathFullyQualified(Cwd)) throw new InvalidDataException("Claude Code 会话工作目录必须为绝对路径");
        if (Cli.Length != 0 && !Path.IsPathFullyQualified(Cli)) throw new InvalidDataException("Claude Code 会话 CLI 必须为绝对路径");
        if (Session.Title.Length > 512 || Cwd.Length > 4096 || Cli.Length > 4096) throw new InvalidDataException("Claude Code 会话元数据过长");
        return new()
        {
            Id = NormalizeId(Session.Id), CallerName = ExternalSupervisor.NormalizeName(Session.CallerName),
            Title = string.IsNullOrWhiteSpace(Session.Title) ? "Claude Code · " + Session.Id[..8] : Session.Title.Trim(),
            Cwd = Cwd.Length == 0 ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(Cwd)),
            CliPath = Cli.Length == 0 ? "" : Path.GetFullPath(Cli), Recorded = Session.Recorded
        };
    }

    private static List<FileInfo> CandidateFiles(string Projects)
    {
        var Files = new List<FileInfo>();
        foreach (string Folder in Directory.EnumerateDirectories(Projects).Take(MaxProjects))
        {
            try
            {
                if ((File.GetAttributes(Folder) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var File in new DirectoryInfo(Folder).EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(File.Name), "D", out _) && (File.Attributes & FileAttributes.ReparsePoint) == 0) Files.Add(File);
                    if (Files.Count >= MaxCandidateFiles) return Files;
                }
            }
            catch (Exception Error) when (Error is IOException or UnauthorizedAccessException) { }
        }
        return Files;
    }

    private static ClaudeCodeSession? ReadMetadata(FileInfo File)
    {
        string Id = NormalizeId(Path.GetFileNameWithoutExtension(File.Name));
        string Title = "", Cwd = "";
        bool Matched = false;
        using var Input = new FileStream(File.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long Length = Input.Length;
        var Windows = Length <= HeadBytes + TailBytes ? new[] { (Offset: 0L, Count: (int)Length) } : new[] { (Offset: 0L, Count: HeadBytes), (Offset: Length - TailBytes, Count: TailBytes) };
        foreach (var Window in Windows)
            foreach (var Line in ReadLines(Input, Window.Offset, Window.Count, Length))
            {
                try
                {
                    using var Document = JsonDocument.Parse(Line);
                    var Row = Document.RootElement;
                    if (Row.ValueKind != JsonValueKind.Object || Text(Row, "sessionId") is not string SessionId || !Guid.TryParseExact(SessionId, "D", out var SessionUuid) || SessionUuid.ToString("D") != Id || Row.TryGetProperty("isSidechain", out var Sidechain) && Sidechain.ValueKind == JsonValueKind.True) continue;
                    Matched = true;
                    string? Type = Text(Row, "type");
                    if (Type == "custom-title" && Text(Row, "customTitle") is string NewTitle && !string.IsNullOrWhiteSpace(NewTitle) && NewTitle.Length <= 512) Title = NewTitle.Trim();
                    string? NewCwd = Type == "relocated" ? Text(Row, "relocatedCwd") : Text(Row, "cwd");
                    if (!string.IsNullOrWhiteSpace(NewCwd) && NewCwd.Length <= 4096 && Path.IsPathFullyQualified(NewCwd)) Cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(NewCwd));
                }
                catch (Exception Error) when (Error is JsonException or ArgumentException or InvalidOperationException) { }
            }
        return Matched ? new() { Id = Id, Title = Title, Cwd = Cwd, Recorded = true } : null;
    }

    private static IEnumerable<ReadOnlyMemory<byte>> ReadLines(FileStream Input, long Offset, int Count, long Length)
    {
        bool Boundary = Offset == 0;
        if (Offset != 0) { Input.Position = Offset - 1; Boundary = Input.ReadByte() == '\n'; }
        Input.Position = Offset;
        var Buffer = new byte[Count];
        int Read = 0, Chunk;
        while (Read < Buffer.Length && (Chunk = Input.Read(Buffer, Read, Buffer.Length - Read)) != 0) Read += Chunk;
        int Start = Boundary ? 0 : Array.IndexOf(Buffer, (byte)'\n', 0, Read) + 1;
        if (!Boundary && Start == 0) yield break;
        if (Offset == 0 && Read >= 3 && Buffer[0] == 0xEF && Buffer[1] == 0xBB && Buffer[2] == 0xBF) Start = 3;
        for (int Index = Start; Index < Read; Index++)
            if (Buffer[Index] == '\n')
            {
                if (Index > Start) yield return Buffer.AsMemory(Start, Index - Start);
                Start = Index + 1;
            }
        if (Start < Read && Offset + Read >= Length) yield return Buffer.AsMemory(Start, Read - Start);
    }

    private static string? Text(JsonElement Row, string Name) => Row.TryGetProperty(Name, out var Value) && Value.ValueKind == JsonValueKind.String ? Value.GetString() : null;
}
