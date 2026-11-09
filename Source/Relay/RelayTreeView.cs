namespace CodexInstanceManager;

public sealed class RelayTreeView
{
    public const int MaxVisibleRows = 200;
    private readonly RelayStore Store;
    private readonly Dictionary<string, int> ExpandedDict = new();
    private readonly HashSet<string> KnownExternalRoots = new();
    private const string ExternalRootPrefix = "external-supervisor/";
    public List<RelayVisibleRow> RowList { get; } = new();
    public int RootPageCount { get; private set; }
    public bool Limited { get; private set; }

    public RelayTreeView(RelayStore Store) => this.Store = Store;
    public bool IsExpanded(string Id) => ExpandedDict.ContainsKey(Id);
    public void CollapseAll() { ExpandedDict.Clear(); KnownExternalRoots.Clear(); }
    public void Toggle(string Id)
    {
        if (!ExpandedDict.Remove(Id)) ExpandedDict.Add(Id, RelayLimits.PageSize);
    }
    public void LoadMore(string Id) => ExpandedDict[Id] += RelayLimits.PageSize;

    public void Refresh(int Offset)
    {
        RowList.Clear();
        Limited = false;
        var RootList = Store.TreePage(null, Offset);
        RootPageCount = RootList.Count;
        var ExternalGroups = RootList.Where(Entry => IsExternalCaller(Entry.MainInstanceId))
            .GroupBy(Entry => (Entry.MainInstanceId, Entry.MainThreadId))
            .ToDictionary(Group => Group.Key, Group => Group.ToList());
        var DisplayedGroups = new HashSet<(string, string)>();
        foreach (var Entry in RootList)
        {
            if (!IsExternalCaller(Entry.MainInstanceId)) { Append(Entry, 0, new HashSet<string>()); continue; }
            var Key = (Entry.MainInstanceId, Entry.MainThreadId);
            if (!DisplayedGroups.Add(Key)) continue;
            var Group = ExternalGroups[Key];
            string Id = ExternalRootPrefix + Entry.MainInstanceId + "/" + Entry.MainThreadId;
            if (KnownExternalRoots.Add(Id)) ExpandedDict[Id] = RelayLimits.PageSize;
            var Root = new RelayTreeEntry(Id, "外部主管：" + Entry.MainInstanceId["external:".Length..], eRelayState.Running, "", "", Group.Count, Entry.MainName, "", "", "", null)
            {
                MainInstanceId = Entry.MainInstanceId, MainThreadId = Entry.MainThreadId, MainTitle = Entry.MainTitle
            };
            if (RowList.Count >= MaxVisibleRows) { Limited = true; break; }
            RowList.Add(new(Root, 0));
            if (IsExpanded(Id)) foreach (var Child in Group) Append(Child, 1, new HashSet<string>());
        }
        var VisibleHashList = RowList.Where(Row => !Row.More).Select(Row => Row.Entry.Id).ToHashSet();
        foreach (string Id in ExpandedDict.Keys.Where(Id => !VisibleHashList.Contains(Id)).ToArray()) ExpandedDict.Remove(Id);
    }

    public static bool IsExternalRoot(RelayTreeEntry Entry) => Entry.Id.StartsWith(ExternalRootPrefix, StringComparison.Ordinal);
    private static bool IsExternalCaller(string InstanceId) => InstanceId.StartsWith("external:", StringComparison.Ordinal);

    /// <summary>分组操作覆盖该主管的所有根任务，不限当前显示页。</summary>
    public List<RelayTreeEntry> ExternalRootTasks(RelayTreeEntry Root)
    {
        if (!IsExternalRoot(Root)) throw new ArgumentException("不是外部主管根节点", nameof(Root));
        var Result = new List<RelayTreeEntry>();
        for (int Offset = 0; ; Offset += RelayLimits.PageSize)
        {
            var Page = Store.TreePage(null, Offset);
            Result.AddRange(Page.Where(Entry => Entry.MainInstanceId == Root.MainInstanceId && Entry.MainThreadId == Root.MainThreadId));
            if (Page.Count < RelayLimits.PageSize) return Result;
        }
    }

    private void Append(RelayTreeEntry Entry, int Depth, HashSet<string> AncestorHashList)
    {
        if (RowList.Count >= MaxVisibleRows) { Limited = true; return; }
        if (!AncestorHashList.Add(Entry.Id)) throw new InvalidDataException("协作树出现循环 " + Entry.Id);
        RowList.Add(new(Entry, Depth));
        if (ExpandedDict.TryGetValue(Entry.Id, out int LoadedCount))
        {
            for (int Offset = 0; Offset < LoadedCount && Offset < Entry.ChildCount && RowList.Count < MaxVisibleRows; Offset += RelayLimits.PageSize)
                foreach (var Child in Store.TreePage(Entry.Id, Offset)) Append(Child, Depth + 1, AncestorHashList);
            if (LoadedCount < Entry.ChildCount && RowList.Count < MaxVisibleRows) RowList.Add(new(Entry, Depth + 1, true));
            if (RowList.Count >= MaxVisibleRows && Entry.ChildCount > 0) Limited = true;
        }
        AncestorHashList.Remove(Entry.Id);
    }
}
