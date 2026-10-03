using System.Buffers.Binary;
using System.Text;

namespace OstConverter.Core.Export.Pst;

/// <summary>A message (or contact, appointment, ...) ready to be stored: its properties, recipient rows and attachments.</summary>
public sealed class PstMessageData
{
    public List<PstProp> Props { get; } = [];
    /// <summary>One list of properties per recipient.</summary>
    public List<List<PstProp>> Recipients { get; } = [];
    public List<PstAttachmentData> Attachments { get; } = [];
}

public sealed class PstAttachmentData
{
    /// <summary>Attachment properties including the data (0x3701) for by-value attachments.</summary>
    public List<PstProp> Props { get; } = [];
    /// <summary>For an attached message: the message itself (the 0x3701 property is then generated).</summary>
    public PstMessageData? Embedded { get; set; }
}

/// <summary>
/// The messaging layer of a Unicode PST ([MS-PST] 2.4): the message store, the folder tree with its hierarchy and
/// contents tables, messages with recipient and attachment tables, and the name-to-id map. Messages are written as they
/// are added; folders, tables and system nodes are written by <see cref="Complete"/>.
/// </summary>
public sealed class PstBuilder : IDisposable
{
    public const uint StoreNid = 0x21, NameMapNid = 0x61, RootFolderNid = 0x122;
    const uint AttachmentTableNid = 0x671, RecipientTableNid = 0x692;
    public const string DeletedItemsName = "Deleted Items";

    sealed class FolderNode(uint nid, uint parent, string name)
    {
        public uint Nid { get; } = nid;
        public uint Parent { get; } = parent;
        public string Name { get; } = name;
        public string? ContainerClass { get; set; }
        public string? Comment { get; set; }
        public List<FolderNode> Children { get; } = [];
        public Dictionary<string, FolderNode> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TcRow> Rows { get; } = [];
        public int Unread { get; set; }
    }

    sealed record MessageObject(ulong BidData, ulong BidSub, Dictionary<ushort, PstProp> Props, int Size);

    readonly NdbWriter _ndb;
    readonly string _displayName;
    readonly IReadOnlyList<PstProp>? _nameMap;
    readonly byte[] _recordKey = Guid.NewGuid().ToByteArray();
    readonly uint[] _next = new uint[32];
    readonly List<FolderNode> _folders = [];
    readonly Dictionary<uint, FolderNode> _byNid = [];
    readonly FolderNode _root, _ipm, _searchRoot, _deleted;
    uint _rowVersion = 1;
    bool _completed;

    /// <param name="nameMap">Properties of the source's name-to-id map (node 0x61), copied so named-property ids in messages stay valid.</param>
    public PstBuilder(string path, string displayName, IReadOnlyList<PstProp>? nameMap = null)
    {
        _displayName = displayName;
        _nameMap = nameMap;
        Array.Fill(_next, 0x400u);
        _next[NidType.NormalFolder] = 0x401;
        _next[NidType.SearchFolder] = 0x4000;
        _next[NidType.NormalMessage] = 0x10000;
        _next[NidType.AssocMessage] = 0x8000;

        _ndb = new NdbWriter(path);
        _root = Register(new FolderNode(RootFolderNid, RootFolderNid, ""));
        _ipm = AddChild(_root, "Top of Outlook data file");
        _searchRoot = AddChild(_root, "Search Root");
        _deleted = AddChild(_ipm, DeletedItemsName);
        _deleted.Comment = "Deleted Items folder";
    }

    /// <summary>Space used so far; callers start a new file when this nears the format's practical size limit.</summary>
    public long UsedBytes => _ndb.UsedBytes;

    FolderNode Register(FolderNode f)
    {
        _folders.Add(f);
        _byNid[f.Nid] = f;
        return f;
    }

    FolderNode AddChild(FolderNode parent, string name)
    {
        var f = Register(new FolderNode(NidType.Make(NidType.NormalFolder, _next[NidType.NormalFolder]++), parent.Nid, name));
        parent.Children.Add(f);
        parent.ByName[name] = f;
        return f;
    }

    /// <summary>Returns the folder for a '/'-separated path below the top of the store, creating it (and parents) when needed.</summary>
    public uint EnsureFolder(string path, string? containerClass = null)
    {
        var cur = _ipm;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            cur = cur.ByName.TryGetValue(part, out var child) ? child : AddChild(cur, part.Length > 255 ? part[..255] : part);
        if (containerClass is { Length: > 0 } && cur.ContainerClass is null && cur != _ipm) cur.ContainerClass = containerClass;
        return cur.Nid;
    }

    // ---- messages ----------------------------------------------------------------------------------------------------

    public void AddMessage(uint folderNid, PstMessageData message)
    {
        var folder = _byNid[folderNid];
        uint nid = NidType.Make(NidType.NormalMessage, _next[NidType.NormalMessage]++);
        var obj = WriteMessageObject(message);
        _ndb.AddNode(nid, obj.BidData, obj.BidSub, folderNid);
        folder.Rows.Add(BuildContentsRow(nid, obj));
        if (obj.Props.TryGetValue(0x0E07, out var flags) && flags.Value.Length >= 4 && (BitConverter.ToInt32(flags.Value) & 1) == 0) folder.Unread++;
    }

    MessageObject WriteMessageObject(PstMessageData m)
    {
        var props = new Dictionary<ushort, PstProp>();
        foreach (var p in m.Props) props[p.Id] = p;
        var subs = new List<SubnodeRef>();
        long size = 0;

        // Attachments
        var attachmentRows = new List<TcRow>();
        for (int i = 0; i < m.Attachments.Count; i++)
        {
            var a = m.Attachments[i];
            var ap = new Dictionary<ushort, PstProp>();
            foreach (var p in a.Props) ap[p.Id] = p;
            var attachmentSubs = new List<SubnodeRef>();
            long attachSize;
            if (a.Embedded is not null)
            {
                uint embeddedNid = NidType.Make(NidType.NormalMessage, _next[NidType.NormalMessage]++);
                var inner = WriteMessageObject(a.Embedded);
                attachmentSubs.Add(new SubnodeRef(embeddedNid, inner.BidData, inner.BidSub));
                var objectValue = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(objectValue, embeddedNid);
                BinaryPrimitives.WriteUInt32LittleEndian(objectValue.AsSpan(4), (uint)inner.Size);
                ap[0x3701] = new PstProp(0x3701, PropType.Object, objectValue);
                ap[0x3705] = PstProp.Int(0x3705, 5);
                attachSize = inner.Size;
            }
            else
            {
                attachSize = ap.TryGetValue(0x3701, out var data) ? data.Value.Length : 0;
                if (!ap.ContainsKey(0x3705)) ap[0x3705] = PstProp.Int(0x3705, 1);
            }
            ap[0x0E20] = PstProp.Int(0x0E20, (int)Math.Min(int.MaxValue, attachSize + 0x400));
            ap.Remove(0x0E21);
            if (!ap.ContainsKey(0x370B)) ap[0x370B] = PstProp.Int(0x370B, -1);
            if (!ap.ContainsKey(0x3714)) ap[0x3714] = PstProp.Int(0x3714, 0);
            size += attachSize;

            var (bd, bs) = WriteLtpNode(PropertyContextWriter.Build(ap.Values), attachmentSubs);
            uint attachNid = NidType.Make(NidType.Attachment, 0x401 + (uint)i);
            subs.Add(new SubnodeRef(attachNid, bd, bs));

            var row = new TcRow(attachNid, 0x17);
            row.Values[0x370B] = ap[0x370B].Value;
            row.Values[0x0E20] = ap[0x0E20].Value;
            row.Values[0x3705] = ap[0x3705].Value;
            row.Values[0x3714] = ap[0x3714].Value;
            var shortName = ap.TryGetValue(0x3704, out var sn) ? sn : ap.TryGetValue(0x3707, out var ln) ? ln : ap.TryGetValue(0x3001, out var dn) ? dn : null;
            if (shortName is { Type: PropType.String }) row.Values[0x3704] = shortName.Value;
            attachmentRows.Add(row);
        }
        if (attachmentRows.Count > 0)
        {
            var (bd, bs) = WriteLtpNode(TableContextWriter.Build(PstTemplates.Attachments, attachmentRows));
            subs.Add(new SubnodeRef(AttachmentTableNid, bd, bs));
        }

        // Recipients
        if (m.Recipients.Count > 0)
        {
            var columns = new List<TcColumn>(PstTemplates.Recipients);
            var known = columns.Select(c => c.Id).ToHashSet();
            foreach (var r in m.Recipients)
                foreach (var p in r)
                    if (known.Add(p.Id) && p.Type is not PropType.Object && p.Type < PropType.MultiValueFlag && p.Id != TableContextWriter.RowIdTag && p.Id != TableContextWriter.RowVerTag)
                        columns.Add(new TcColumn(p.Id, p.Type));
            var types = columns.ToDictionary(c => c.Id, c => c.Type);

            var rows = new List<TcRow>();
            for (int i = 0; i < m.Recipients.Count; i++)
            {
                var row = new TcRow(NidType.Make(0x13, (uint)i), 0x15);
                foreach (var p in m.Recipients[i])
                    if (types.TryGetValue(p.Id, out var t) && t == p.Type) row.Values[p.Id] = p.Value;
                rows.Add(row);
            }
            var (bd, bs) = WriteLtpNode(TableContextWriter.Build(columns, rows));
            subs.Add(new SubnodeRef(RecipientTableNid, bd, bs));
        }

        // The "has attachments" state lives in the message flags; the separate boolean property is not stored.
        props.Remove(0x0E1B);
        int flags = props.TryGetValue(0x0E07, out var f) && f.Value.Length >= 4 ? BitConverter.ToInt32(f.Value) : 0;
        flags = m.Attachments.Count > 0 ? flags | 0x10 : flags & ~0x10;
        props[0x0E07] = PstProp.Int(0x0E07, flags);

        foreach (var p in props.Values) size += p.Value.Length + 8;
        size += 0x200;
        props[0x0E08] = PstProp.Int(0x0E08, (int)Math.Min(int.MaxValue, size));

        var (bidData, bidSub) = WriteLtpNode(PropertyContextWriter.Build(props.Values), subs);
        return new MessageObject(bidData, bidSub, props, (int)Math.Min(int.MaxValue, size));
    }

    TcRow BuildContentsRow(uint nid, MessageObject m)
    {
        var row = new TcRow(nid, _rowVersion++);
        foreach (var col in PstTemplates.Contents)
            if (m.Props.TryGetValue(col.Id, out var p) && p.Type == col.Type) row.Values[col.Id] = p.Value;
        if (!row.Values.ContainsKey(0x0E17)) row.Values[0x0E17] = BitConverter.GetBytes(0);
        if (!row.Values.ContainsKey(0x0017)) row.Values[0x0017] = BitConverter.GetBytes(1);
        return row;
    }

    /// <summary>Writes a property or table context as node data, moving oversized values into subnodes.</summary>
    (ulong BidData, ulong BidSub) WriteLtpNode(LtpContent content, IEnumerable<SubnodeRef>? extra = null)
    {
        var subs = new List<SubnodeRef>(extra ?? []);
        foreach (var large in content.Large) subs.Add(new SubnodeRef(large.Nid, _ndb.WriteChunks(large.Chunks), 0));
        return (_ndb.WriteChunks(content.HeapBlocks), _ndb.WriteSubnodes(subs));
    }

    void WriteNode(uint nid, uint parent, LtpContent content)
    {
        var (bd, bs) = WriteLtpNode(content);
        _ndb.AddNode(nid, bd, bs, parent);
    }

    // ---- completion --------------------------------------------------------------------------------------------------

    public void Complete()
    {
        if (_completed) return;
        _completed = true;

        foreach (var f in _folders) WriteFolder(f);
        WriteStore();
        WriteNameMap();
        WriteSystemNodes();

        _next[NidType.HierarchyTable] = _next[NidType.ContentsTable] = _next[NidType.AssocContentsTable] = _next[NidType.NormalFolder];
        _ndb.Complete(_next);
    }

    static uint TableNid(uint folderNid, uint type) => (folderNid & ~0x1Fu) | type;

    void WriteFolder(FolderNode f)
    {
        var props = new List<PstProp>
        {
            PstProp.Str(0x3001, f.Name),
            PstProp.Int(0x3602, f.Rows.Count),
            PstProp.Int(0x3603, f.Unread),
            PstProp.Bool(0x360A, f.Children.Count > 0),
        };
        if (f.ContainerClass is not null) props.Add(PstProp.Str(0x3613, f.ContainerClass));
        if (f.Comment is not null) props.Add(PstProp.Str(0x3004, f.Comment));
        WriteNode(f.Nid, f.Parent, PropertyContextWriter.Build(props));

        var children = new List<TcRow>();
        foreach (var c in f.Children)
        {
            var row = new TcRow(c.Nid, _rowVersion++);
            row.Values[0x3001] = Encoding.Unicode.GetBytes(c.Name);
            row.Values[0x3602] = BitConverter.GetBytes(c.Rows.Count);
            row.Values[0x3603] = BitConverter.GetBytes(c.Unread);
            row.Values[0x360A] = [c.Children.Count > 0 ? (byte)1 : (byte)0];
            if (c.ContainerClass is not null) row.Values[0x3613] = Encoding.Unicode.GetBytes(c.ContainerClass);
            children.Add(row);
        }
        WriteNode(TableNid(f.Nid, NidType.HierarchyTable), 0, TableContextWriter.Build(PstTemplates.Hierarchy, children));
        WriteNode(TableNid(f.Nid, NidType.ContentsTable), 0, TableContextWriter.Build(PstTemplates.Contents, f.Rows));
        WriteNode(TableNid(f.Nid, NidType.AssocContentsTable), 0, TableContextWriter.Build(PstTemplates.AssocContents, []));
        f.Rows.Clear();
    }

    byte[] EntryId(uint nid)
    {
        var id = new byte[24];
        _recordKey.CopyTo(id, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(id.AsSpan(20), nid);
        return id;
    }

    void WriteStore()
    {
        var props = new List<PstProp>
        {
            PstProp.Bin(0x0FF9, _recordKey),
            PstProp.Str(0x3001, _displayName),
            PstProp.Int(0x35DF, 0x89),                      // IPM subtree, wastebasket and finder entry ids are valid
            PstProp.Bin(0x35E0, EntryId(_ipm.Nid)),
            PstProp.Bin(0x35E3, EntryId(_deleted.Nid)),
            PstProp.Bin(0x35E7, EntryId(_searchRoot.Nid)),
            PstProp.Bool(0x6633, true),
            PstProp.Int(0x66FA, 0x000E0011),
            PstProp.Int(0x66FC, 0x0018B00C),
            PstProp.Int(0x67FF, 0),                         // no password
        };
        WriteNode(StoreNid, 0, PropertyContextWriter.Build(props));
    }

    void WriteNameMap()
    {
        IEnumerable<PstProp> props = _nameMap ??
        [
            PstProp.Int(0x0001, 251),                       // bucket count
            PstProp.Bin(0x0002, []), PstProp.Bin(0x0003, []), PstProp.Bin(0x0004, []),
        ];
        // Outlook keeps the map's entry and string streams (0x0003, 0x0004) in subnodes however small they are, and does not
        // read named properties from a file that stores them in the heap instead.
        WriteNode(NameMapNid, 0, PropertyContextWriter.Build(props, id => id is 0x0003 or 0x0004));
    }

    /// <summary>The template tables and queues every store carries (new folders and items are modelled on them).</summary>
    void WriteSystemNodes()
    {
        WriteNode(0x60D, 0, TableContextWriter.Build(PstTemplates.Hierarchy, []));
        WriteNode(0x60E, 0, TableContextWriter.Build(PstTemplates.Contents, []));
        WriteNode(0x60F, 0, TableContextWriter.Build(PstTemplates.AssocContents, []));
        WriteNode(0x610, 0, TableContextWriter.Build(PstTemplates.SearchContents, []));

        var receive = new TcRow(1, 2);
        receive.Values[0x6605] = BitConverter.GetBytes((int)RootFolderNid);
        receive.Values[0x001A] = [];                        // default message class
        WriteNode(0x62B, 0, TableContextWriter.Build(PstTemplates.ReceiveFolder, [receive]));

        WriteNode(0x64C, 0, TableContextWriter.Build(PstTemplates.OutgoingQueue, []));
        WriteNode(AttachmentTableNid, 0, TableContextWriter.Build(PstTemplates.Attachments.Take(4).ToArray(), []));
        WriteNode(RecipientTableNid, 0, TableContextWriter.Build(PstTemplates.Recipients, []));
        WriteNode(0x6B6, 0, TableContextWriter.Build(PstTemplates.Template16, []));
        WriteNode(0x6D7, 0, TableContextWriter.Build(PstTemplates.Template17, []));
        WriteNode(0x6F8, 0, TableContextWriter.Build(PstTemplates.Template18, []));

        _ndb.AddNode(0x1E1, 0, 0, 0);                       // search management queue (empty)
        _ndb.AddNode(0x261, 0, 0, 0);                       // search domain object (empty)
    }

    public void Dispose() => _ndb.Dispose();
}
