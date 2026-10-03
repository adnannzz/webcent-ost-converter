using System.Buffers.Binary;
using System.Text;

namespace OstConverter.Core.Pff;

public interface IPropSource
{
    byte[]? GetBytes(ushort id);
    ushort? TypeOf(ushort id);
}

public static class PropTag
{
    public const ushort MessageClass = 0x001A, Importance = 0x0017, Subject = 0x0037, ClientSubmitTime = 0x0039,
        SentRepresentingName = 0x0042, SentRepresentingEmail = 0x0065, TransportHeaders = 0x007D,
        SenderName = 0x0C1A, SenderAddrType = 0x0C1E, SenderEmail = 0x0C1F, RecipientType = 0x0C15,
        DeliveryTime = 0x0E06, MessageFlags = 0x0E07, Body = 0x1000, Rtf = 0x1009, Html = 0x1013,
        InternetMessageId = 0x1035, DisplayName = 0x3001, AddrType = 0x3002, EmailAddress = 0x3003,
        CreationTime = 0x3007, LastModified = 0x3008, ContentCount = 0x3602, Subfolders = 0x360A,
        ContainerClass = 0x3613, AttachData = 0x3701, AttachFilename = 0x3704, AttachMethod = 0x3705,
        AttachLongFilename = 0x3707, AttachMime = 0x370E, AttachContentId = 0x3712, AttachFlags = 0x3714,
        InternetCodepage = 0x3FDE, MessageCodepage = 0x3FFD, SmtpAddress = 0x39FE, SenderSmtp = 0x5D01,
        SentRepresentingSmtp = 0x5D02;
}

public static class PropDecode
{
    static PropDecode() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding CodePage(int cp)
    {
        try { return Encoding.GetEncoding(cp); } catch (Exception) { return Encoding.GetEncoding(1252); }
    }

    public static string? String(IPropSource src, ushort id, Encoding fallback)
    {
        var b = src.GetBytes(id);
        if (b is null) return null;
        var t = src.TypeOf(id);
        var s = t == 0x001E ? fallback.GetString(b) : Encoding.Unicode.GetString(b);
        return s.TrimEnd('\0');
    }

    public static int? Int32(IPropSource src, ushort id)
    {
        var b = src.GetBytes(id);
        return b is { Length: >= 4 } ? BinaryPrimitives.ReadInt32LittleEndian(b)
             : b is { Length: 2 } ? BinaryPrimitives.ReadInt16LittleEndian(b) : null;
    }

    public static DateTime? FileTime(IPropSource src, ushort id)
    {
        var b = src.GetBytes(id);
        if (b is not { Length: >= 8 }) return null;
        long ft = BinaryPrimitives.ReadInt64LittleEndian(b);
        if (ft <= 0 || ft > DateTime.MaxValue.Ticks - DateTime.FromFileTimeUtc(0).Ticks) return null;
        return DateTime.FromFileTimeUtc(ft);
    }
}

/// <summary>One stored property: its id, MAPI type and raw value bytes (empty for object and multi-valued types).</summary>
public readonly record struct RawProperty(ushort Id, ushort Type, byte[] Value);

/// <summary>An attachment's properties and contents as stored (see <see cref="Attachment.ReadRaw"/>).</summary>
public sealed record RawAttachment(IReadOnlyList<RawProperty> Properties, byte[]? Data, bool DataMissing);

public sealed class Recipient
{
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    /// <summary>1 = To, 2 = Cc, 3 = Bcc.</summary>
    public int Kind { get; init; } = 1;
}

static class PropertyDump
{
    public static List<RawProperty> Read(PropertyContext pc, Action<string> warn, string what)
    {
        var list = new List<RawProperty>();
        foreach (var p in pc.Properties)
        {
            // Object properties (embedded messages) are handled by the caller; their value is not data.
            if (p.Type == 0x000D) { list.Add(new RawProperty(p.Id, p.Type, [])); continue; }
            try { list.Add(new RawProperty(p.Id, p.Type, pc.GetBytes(p.Id) ?? [])); }
            catch (PffFormatException e) { warn($"{what} property 0x{p.Id:X4} could not be read: {e.Message}"); }
        }
        return list;
    }
}

public sealed class Attachment
{
    readonly PropertyContext _pc;
    readonly Encoding _enc;
    readonly int _maxBlock;

    readonly NameMap _names;
    readonly Action<string> _warn;

    internal Attachment(PropertyContext pc, Encoding enc, int maxBlock, NameMap names, Action<string> warn)
    {
        _pc = pc; _enc = enc; _maxBlock = maxBlock; _names = names; _warn = warn;
    }

    /// <summary>File contents, or null (with a warning on the owning message) if the data is missing from the file.</summary>
    public byte[]? TryGetData()
    {
        try { return GetData(); }
        catch (PffFormatException e) { _warn($"Attachment \"{FileName}\" could not be read: {e.Message}"); return null; }
    }

    public Message? TryOpenEmbeddedMessage()
    {
        try { return OpenEmbeddedMessage(); }
        catch (PffFormatException e) { _warn($"Embedded message \"{FileName}\" could not be read: {e.Message}"); return null; }
    }

    public string FileName => PropDecode.String(_pc, PropTag.AttachLongFilename, _enc)
        ?? PropDecode.String(_pc, PropTag.AttachFilename, _enc) ?? PropDecode.String(_pc, PropTag.DisplayName, _enc) ?? "attachment";
    public string? MimeType => PropDecode.String(_pc, PropTag.AttachMime, _enc);
    public string? ContentId => PropDecode.String(_pc, PropTag.AttachContentId, _enc);
    /// <summary>1 = by value, 5 = embedded message, 6 = OLE, 7 = by reference.</summary>
    public int Method => PropDecode.Int32(_pc, PropTag.AttachMethod) ?? 1;
    public bool IsEmbeddedMessage => Method == 5 && _pc.TypeOf(PropTag.AttachData) == 0x000D;
    public bool IsInline => ((PropDecode.Int32(_pc, PropTag.AttachFlags) ?? 0) & 4) != 0 && ContentId is not null;

    /// <summary>File contents for by-value attachments (null for embedded messages).</summary>
    public byte[]? GetData() => IsEmbeddedMessage ? null : _pc.GetBytes(PropTag.AttachData);

    /// <summary>Every stored property. Values that can't be read are skipped with a warning on the owning message.</summary>
    public IReadOnlyList<RawProperty> GetRawProperties() => PropertyDump.Read(_pc, _warn, $"Attachment \"{FileName}\"");

    /// <summary>
    /// All properties except the file contents, plus the contents read once (so large attachments are not read twice).
    /// <see cref="RawAttachment.DataMissing"/> is set when the data exists but can't be read from the file.
    /// </summary>
    public RawAttachment ReadRaw()
    {
        var props = new List<RawProperty>();
        byte[]? data = null;
        bool missing = false;
        foreach (var p in _pc.Properties)
        {
            if (p.Type == 0x000D) { props.Add(new RawProperty(p.Id, p.Type, [])); continue; }
            try
            {
                var bytes = _pc.GetBytes(p.Id) ?? [];
                if (p.Id == PropTag.AttachData) data = bytes; else props.Add(new RawProperty(p.Id, p.Type, bytes));
            }
            catch (PffFormatException e)
            {
                if (p.Id == PropTag.AttachData) missing = true;
                _warn($"Attachment \"{FileName}\" {(p.Id == PropTag.AttachData ? "data" : $"property 0x{p.Id:X4}")} could not be read: {e.Message}");
            }
        }
        return new RawAttachment(props, data, missing);
    }

    public Message? OpenEmbeddedMessage()
    {
        if (!IsEmbeddedMessage) return null;
        // The object value names a subnode of the attachment node that holds the full message. Outlook stores it as an
        // 8-byte heap value (subnode id, size); a value that is itself a subnode id is accepted too.
        ulong nid = _pc.Properties.First(p => p.Id == PropTag.AttachData).Value;
        if ((nid & 0x1F) == 0)
        {
            var bytes = _pc.GetBytes(PropTag.AttachData);
            if (bytes is not { Length: >= 4 }) return null;
            nid = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        var node = _pc.Node.OpenSubnode(nid);
        return node is null ? null : new Message(node, _maxBlock, _names);
    }
}

/// <summary>One message (or contact, appointment, task, note: all share the same property-context layout).</summary>
public sealed class Message
{
    readonly PffNode _node;
    readonly PropertyContext _pc;
    readonly Encoding _enc;
    List<Recipient>? _recipients;
    List<Attachment>? _attachments;

    readonly NameMap _names;

    internal Message(PffNode node, int maxBlock, NameMap names)
    {
        _node = node;
        _names = names;
        MaxBlock = maxBlock;
        _pc = new PropertyContext(node);
        _enc = PropDecode.CodePage(PropDecode.Int32(_pc, PropTag.MessageCodepage) ?? 1252);
    }

    public ulong Nid => _node.Nid;
    public IPropSource Properties => _pc;
    internal Encoding StringEncoding => _enc;

    /// <summary>Every stored property of the message (used to copy a message faithfully into another format).</summary>
    public IReadOnlyList<RawProperty> GetRawProperties() => PropertyDump.Read(_pc, Warn, "Message");
    public string MessageClass => PropDecode.String(_pc, PropTag.MessageClass, _enc) ?? "IPM.Note";
    public string Subject => CleanSubject(PropDecode.String(_pc, PropTag.Subject, _enc));
    /// <summary>Runs a property read; a missing data block becomes a warning and a null value instead of losing the whole message.</summary>
    T? Safe<T>(string what, Func<T?> read)
    {
        try { return read(); }
        catch (PffFormatException e) { Warn($"{what} could not be read: {e.Message}"); return default; }
    }

    public string? TextBody => Safe("Message body", () => PropDecode.String(_pc, PropTag.Body, _enc));
    public string? TransportHeaders => PropDecode.String(_pc, PropTag.TransportHeaders, _enc);
    public string? InternetMessageId => PropDecode.String(_pc, PropTag.InternetMessageId, _enc);
    public DateTime? SentTime => PropDecode.FileTime(_pc, PropTag.ClientSubmitTime);
    public DateTime? ReceivedTime => PropDecode.FileTime(_pc, PropTag.DeliveryTime);
    public int Flags => PropDecode.Int32(_pc, PropTag.MessageFlags) ?? 0;
    public bool IsRead => (Flags & 1) != 0;

    public string SenderName => PropDecode.String(_pc, PropTag.SenderName, _enc)
        ?? PropDecode.String(_pc, PropTag.SentRepresentingName, _enc) ?? "";

    /// <summary>SMTP address when known; Exchange-internal (X500) addresses are replaced by the SMTP property.</summary>
    public string SenderAddress
    {
        get
        {
            var smtp = PropDecode.String(_pc, PropTag.SenderSmtp, _enc) ?? PropDecode.String(_pc, PropTag.SentRepresentingSmtp, _enc);
            if (!string.IsNullOrEmpty(smtp)) return smtp;
            var type = PropDecode.String(_pc, PropTag.SenderAddrType, _enc);
            var addr = PropDecode.String(_pc, PropTag.SenderEmail, _enc) ?? PropDecode.String(_pc, PropTag.SentRepresentingEmail, _enc) ?? "";
            return string.Equals(type, "EX", StringComparison.OrdinalIgnoreCase) ? "" : addr;
        }
    }

    public string? HtmlBody => Safe("HTML body", () =>
    {
        var b = _pc.GetBytes(PropTag.Html);
        if (b is null) return null;
        int cp = PropDecode.Int32(_pc, PropTag.InternetCodepage) ?? 0;
        var enc = cp == 0 || cp == 65001 ? new UTF8Encoding(false) : PropDecode.CodePage(cp);
        return enc.GetString(b);
    });

    /// <summary>RTF text if the message only has a compressed RTF body.</summary>
    public string? RtfBody => Safe("RTF body", () =>
    {
        var b = _pc.GetBytes(PropTag.Rtf);
        return b is null ? null : Lzfu.TryDecompress(b, out var rtf) ? Encoding.Latin1.GetString(rtf) : null;
    });

    public string? GetString(ushort tag) => PropDecode.String(_pc, tag, _enc);
    public DateTime? GetDate(ushort tag) => PropDecode.FileTime(_pc, tag);
    public int? GetInt(ushort tag) => PropDecode.Int32(_pc, tag);

    /// <summary>Named-property accessors: the id is looked up per file via the name-to-id map.</summary>
    public string? GetNamedString(Guid set, uint lid) => _names.Resolve(set, lid) is { } t ? GetString(t) : null;
    public DateTime? GetNamedDate(Guid set, uint lid) => _names.Resolve(set, lid) is { } t ? GetDate(t) : null;
    public int? GetNamedInt(Guid set, uint lid) => _names.Resolve(set, lid) is { } t ? GetInt(t) : null;

    readonly List<string> _warnings = [];
    /// <summary>Non-fatal problems met while reading this message (e.g. an attachment whose data block is missing).</summary>
    public IReadOnlyList<string> Warnings => _warnings;
    void Warn(string text) { if (!_warnings.Contains(text)) _warnings.Add(text); }

    /// <summary>Every column of every recipient row, for copying recipients faithfully into another store.</summary>
    public IReadOnlyList<IReadOnlyList<RawProperty>> GetRecipientRows()
    {
        var rows = new List<IReadOnlyList<RawProperty>>();
        try
        {
            var node = FindSubnode(0x12);
            if (node is null) return rows;
            var table = new TableContext(node, MaxBlock);
            foreach (var row in table.Rows())
            {
                var props = new List<RawProperty>();
                foreach (var col in table.Columns.Values.OrderBy(c => c.Id))
                {
                    if (col.Id is 0x67F2 or 0x67F3) continue;
                    try
                    {
                        var bytes = row.GetBytes(col.Id);
                        if (bytes is not null) props.Add(new RawProperty(col.Id, col.Type, bytes));
                    }
                    catch (PffFormatException e) { Warn($"Recipient property 0x{col.Id:X4} could not be read: {e.Message}"); }
                }
                rows.Add(props);
            }
        }
        catch (PffFormatException e) { Warn("Recipient list could not be read: " + e.Message); }
        return rows;
    }

    public IReadOnlyList<Recipient> Recipients => _recipients ??= LoadRecipients();
    public IReadOnlyList<Attachment> Attachments => _attachments ??= LoadAttachments();

    static string CleanSubject(string? s)
    {
        // Subjects may carry a prefix marker: 0x01 <len-char> <prefix> ... (normalized-subject encoding).
        if (s is { Length: >= 2 } && s[0] == '\u0001') return s[2..];
        return s ?? "";
    }

    PffNode? FindSubnode(int type) =>
        _node.Subnodes.Keys.Where(n => (n & 0x1F) == (ulong)type).Select(_node.OpenSubnode).FirstOrDefault();

    List<Recipient> LoadRecipients()
    {
        var list = new List<Recipient>();
        try { FillRecipients(list); }
        catch (PffFormatException e) { Warn("Recipient list could not be read: " + e.Message); }
        return list;
    }

    void FillRecipients(List<Recipient> list)
    {
        var node = FindSubnode(0x12);
        if (node is null) return;
        var table = new TableContext(node, MaxBlock);
        foreach (var row in table.Rows())
        {
            var src = new RowSource(row, table);
            var name = PropDecode.String(src, PropTag.DisplayName, _enc) ?? "";
            var smtp = PropDecode.String(src, PropTag.SmtpAddress, _enc);
            var type = PropDecode.String(src, PropTag.AddrType, _enc);
            var addr = PropDecode.String(src, PropTag.EmailAddress, _enc);
            list.Add(new Recipient
            {
                Name = name,
                Address = !string.IsNullOrEmpty(smtp) ? smtp : string.Equals(type, "EX", StringComparison.OrdinalIgnoreCase) ? "" : addr ?? "",
                Kind = PropDecode.Int32(src, PropTag.RecipientType) ?? 1,
            });
        }
    }

    List<Attachment> LoadAttachments()
    {
        var list = new List<Attachment>();
        try
        {
            var node = FindSubnode(0x11);
            if (node is null) return list;
            var table = new TableContext(node, MaxBlock);
            foreach (var row in table.Rows())
            {
                try
                {
                    var sub = _node.OpenSubnode(row.RowId);
                    if (sub is null) { Warn($"Attachment 0x{row.RowId:X} is listed but missing from the file."); continue; }
                    list.Add(new Attachment(new PropertyContext(sub), _enc, MaxBlock, _names, Warn));
                }
                catch (PffFormatException e) { Warn($"Attachment 0x{row.RowId:X} could not be read: {e.Message}"); }
            }
        }
        catch (PffFormatException e) { Warn("Attachment list could not be read: " + e.Message); }
        return list;
    }

    internal int MaxBlock { get; }

    sealed class RowSource(TableRow row, TableContext table) : IPropSource
    {
        public byte[]? GetBytes(ushort id) => row.GetBytes(id);
        public ushort? TypeOf(ushort id) => table.Columns.TryGetValue(id, out var c) ? c.Type : null;
    }
}

public sealed class Folder
{
    readonly PstStore _store;
    internal Folder(PstStore store, ulong nid, string name, int contentCount, string? containerClass)
    {
        _store = store; Nid = nid; Name = name; ContentCount = contentCount; ContainerClass = containerClass;
    }

    public ulong Nid { get; }
    public string Name { get; }
    public int ContentCount { get; }
    /// <summary>"IPF.Note", "IPF.Contact", "IPF.Appointment", ... or null.</summary>
    public string? ContainerClass { get; }

    /// <summary>Number of rows in the contents table (cheap: no messages are loaded).</summary>
    public int GetMessageCount() => _store.CountMessages(this);
    /// <summary>Message ids from the contents table. Open each with <see cref="PstStore.OpenMessage"/> so one bad item can't end the folder.</summary>
    public IEnumerable<ulong> GetMessageIds() => _store.LoadMessageIds(this);
    public IEnumerable<Folder> GetSubfolders() => _store.LoadSubfolders(this);
    public IEnumerable<Message> GetMessages() => _store.LoadMessages(this);
}

public sealed class PstStore : IDisposable
{
    public const ulong RootFolderNid = 0x122;
    readonly Encoding _enc = Encoding.GetEncoding(1252);

    PstStore(PffFile file) { File = file; }

    public PffFile File { get; }
    public string DisplayName { get; private set; } = "";
    public NameMap Names { get; private set; } = NameMap.Empty;
    public Folder Root { get; private set; } = null!;

    public static PstStore Open(string path)
    {
        PropDecode.CodePage(1252);
        var store = new PstStore(new PffFile(path));
        try { store.Init(); }
        catch { store.Dispose(); throw; }
        return store;
    }

    void Init()
    {
        var storeNode = File.OpenNode(0x21) ?? throw new PffFormatException("Message store node missing.");
        DisplayName = PropDecode.String(new PropertyContext(storeNode), PropTag.DisplayName, _enc) ?? "";
        Names = NameMap.Load(File);
        Root = LoadFolder(RootFolderNid) ?? throw new PffFormatException("Root folder missing.");
    }

    public void Dispose() => File.Dispose();

    /// <summary>The raw properties of the name-to-id map (node 0x61); copying them keeps named-property ids valid in another store.</summary>
    public IReadOnlyList<RawProperty> GetNameMapProperties()
    {
        var node = File.OpenNode(0x61);
        if (node is null) return [];
        return PropertyDump.Read(new PropertyContext(node), _ => { }, "Name map");
    }

    Folder? LoadFolder(ulong nid)
    {
        var node = File.OpenNode(nid);
        if (node is null) return null;
        var pc = new PropertyContext(node);
        var name = PropDecode.String(pc, PropTag.DisplayName, _enc) ?? "";
        if (nid == RootFolderNid && name.Length == 0) name = DisplayName.Length > 0 ? DisplayName : "Root";
        return new Folder(this, nid, name, PropDecode.Int32(pc, PropTag.ContentCount) ?? 0,
            PropDecode.String(pc, PropTag.ContainerClass, _enc));
    }

    static ulong TableNid(ulong folderNid, uint type) => (folderNid & ~0x1FUL) | type;

    internal IEnumerable<Folder> LoadSubfolders(Folder parent)
    {
        var node = File.OpenNode(TableNid(parent.Nid, 0x0D));
        if (node is null) yield break;
        var table = new TableContext(node, File.MaxBlockData);
        foreach (var row in table.Rows())
        {
            var f = LoadFolder(row.RowId);
            if (f is not null) yield return f;
        }
    }

    internal int CountMessages(Folder folder)
    {
        var node = File.OpenNode(TableNid(folder.Nid, 0x0E));
        return node is null ? 0 : new TableContext(node, File.MaxBlockData).RowCount;
    }

    internal IEnumerable<ulong> LoadMessageIds(Folder folder)
    {
        var node = File.OpenNode(TableNid(folder.Nid, 0x0E));
        if (node is null) return [];
        return new TableContext(node, File.MaxBlockData).Rows().Select(r => (ulong)r.RowId).ToList();
    }

    /// <summary>Opens one message; null if its node is missing. Throws <see cref="PffFormatException"/> if its data is damaged.</summary>
    public Message? OpenMessage(ulong nid)
    {
        var node = File.OpenNode(nid);
        return node is null ? null : new Message(node, File.MaxBlockData, Names);
    }

    internal IEnumerable<Message> LoadMessages(Folder folder)
    {
        var node = File.OpenNode(TableNid(folder.Nid, 0x0E));
        if (node is null) yield break;
        var table = new TableContext(node, File.MaxBlockData);
        foreach (var row in table.Rows())
        {
            var msgNode = File.OpenNode(row.RowId);
            if (msgNode is null) continue;
            yield return new Message(msgNode, File.MaxBlockData, Names);
        }
    }

    public IEnumerable<(Folder Folder, string Path)> WalkFolders()
    {
        var stack = new Stack<(Folder, string)>();
        stack.Push((Root, ""));
        while (stack.Count > 0)
        {
            var (f, path) = stack.Pop();
            yield return (f, path);
            var kids = f.GetSubfolders().ToList();
            for (int i = kids.Count - 1; i >= 0; i--)
                stack.Push((kids[i], path.Length == 0 ? kids[i].Name : path + "/" + kids[i].Name));
        }
    }
}
