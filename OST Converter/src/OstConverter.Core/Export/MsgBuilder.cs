using System.Buffers.Binary;
using System.Text;
using OstConverter.Core.Export.Cfb;
using OstConverter.Core.Pff;

namespace OstConverter.Core.Export;

/// <summary>
/// Builds an Outlook .msg ([MS-OXMSG]) from a decoded message: a compound file holding a property stream, one stream per
/// variable-length property, a storage per recipient and per attachment, and nested messages for embedded mail.
/// </summary>
public static class MsgBuilder
{
    const ushort TypeInt16 = 0x0002, TypeInt32 = 0x0003, TypeFloat = 0x0004, TypeDouble = 0x0005, TypeCurrency = 0x0006,
        TypeError = 0x000A, TypeBool = 0x000B, TypeObject = 0x000D, TypeInt64 = 0x0014, TypeString8 = 0x001E,
        TypeString = 0x001F, TypeTime = 0x0040, TypeBinary = 0x0102;

    static readonly Guid MessageClsid = new("00020D0B-0000-0000-C000-000000000046");

    // Properties tied to the source store (entry ids, record keys, table row ids, ...) mean nothing in a standalone file.
    static readonly HashSet<ushort> StoreSpecific =
    [
        0x003F, 0x0041, 0x0043, 0x0C19, 0x0E0F, 0x0FF9, 0x0FFA, 0x0FFB, 0x0FFC, 0x0FFD, 0x0FFF, 0x3000, 0x0E30, 0x0E33, 0x0E34, 0x0E38,
        0x3010, 0x3013, 0x300B, 0x0E21, 0x0FFE, 0x3FE3, 0x3FE4,
    ];

    public static CfbStorage Build(Message message, int depth = 0) => BuildMessage(message, embedded: false, depth, null);

    /// <summary>Test hook: builds <paramref name="outer"/> with <paramref name="inner"/> attached as an embedded message.</summary>
    internal static CfbStorage BuildWithEmbedded(Message outer, Message inner) => BuildMessage(outer, embedded: false, 0, [inner]);

    static CfbStorage BuildMessage(Message m, bool embedded, int depth, IReadOnlyList<Message>? extraEmbedded)
    {
        var root = new CfbStorage(embedded ? "message" : "Root Entry") { Clsid = embedded ? default : MessageClsid };
        var recipients = m.Recipients;
        var attachments = m.Attachments;

        // --- message properties ---
        var props = new PropertyTable();
        var enc = m.StringEncoding;
        foreach (var p in m.GetRawProperties())
        {
            if (!IsCopyable(p.Id, p.Type)) continue;
            props.Add(p.Id, p.Type, p.Value, enc);
        }
        // Stores keep a two-character marker at the start of the subject; write the clean subject instead.
        props.Set(0x0037, TypeString, m.Subject);
        props.Set(0x0E1D, TypeString, m.Subject);
        props.Remove(0x003D); // subject prefix
        // A standalone file can't resolve an Exchange (X500) address, so write the SMTP one whenever we know it.
        if (m.SenderAddress.Length > 0)
        {
            props.Set(0x0C1E, TypeString, "SMTP");
            props.Set(0x0C1F, TypeString, m.SenderAddress);
            props.Set(0x0064, TypeString, "SMTP");
            props.Set(0x0065, TypeString, m.SenderAddress);
        }
        props.Set(0x340D, TypeInt32, 0x00040000); // store supports Unicode, as Outlook writes it
        props.Set(0x3FFD, TypeInt32, 65001);       // our strings are written as UTF-16; this is the body codepage hint

        // --- named property map (empty: named properties are not copied) ---
        var names = root.AddStorage("__nameid_version1.0");
        names.AddStream("__substg1.0_00020102", []);
        names.AddStream("__substg1.0_00030102", []);
        names.AddStream("__substg1.0_00040102", []);

        // --- recipients ---
        for (int i = 0; i < recipients.Count; i++)
        {
            var r = recipients[i];
            var s = root.AddStorage($"__recip_version1.0_#{i:X8}");
            var rp = new PropertyTable();
            rp.Set(0x0C15, TypeInt32, r.Kind is >= 1 and <= 3 ? r.Kind : 1);
            rp.Set(0x3001, TypeString, r.Name.Length > 0 ? r.Name : r.Address);
            rp.Set(0x3002, TypeString, "SMTP");
            rp.Set(0x3003, TypeString, r.Address);
            rp.Set(0x39FE, TypeString, r.Address);
            rp.Set(0x5FF6, TypeString, r.Name.Length > 0 ? r.Name : r.Address);
            rp.Set(0x3000, TypeInt32, i);
            rp.Set(0x0FFE, TypeInt32, 6);   // PR_OBJECT_TYPE = MAPI_MAILUSER
            rp.Set(0x3900, TypeInt32, 0);   // PR_DISPLAY_TYPE
            rp.WriteTo(s, new byte[8]);
        }

        // --- attachments ---
        // An attachment whose data is missing from the source file is left out (the message carries a warning),
        // the same as the EML exporter, so the count written below always matches what is actually stored.
        int written = 0;
        foreach (var a in attachments)
        {
            byte[]? data = null;
            Message? inner = null;
            if (a.IsEmbeddedMessage) { inner = depth < 4 ? a.TryOpenEmbeddedMessage() : null; if (inner is null) continue; }
            else { data = a.TryGetData(); if (data is null) continue; }

            int i = written++;
            var s = root.AddStorage($"__attach_version1.0_#{i:X8}");
            var ap = new PropertyTable();
            foreach (var p in a.GetRawProperties())
            {
                if (p.Id == 0x3701 || !IsCopyable(p.Id, p.Type, attachment: true)) continue;
                ap.Add(p.Id, p.Type, p.Value, enc);
            }
            ap.Set(0x0E21, TypeInt32, i);          // PR_ATTACH_NUM
            ap.Set(0x0FFE, TypeInt32, 7);          // PR_OBJECT_TYPE = MAPI_ATTACH
            ap.Set(0x370B, TypeInt32, -1);         // PR_RENDERING_POSITION: not placed in the body

            if (inner is not null) AddEmbedded(s, ap, inner, depth);
            else
            {
                ap.Set(0x3705, TypeInt32, 1);      // ATTACH_BY_VALUE
                ap.Add(0x3701, TypeBinary, data!, enc);
                // Some files only carry the long name; MSG readers want both names plus the extension.
                var name = a.FileName;
                ap.SetIfMissing(0x3707, TypeString, name);
                ap.SetIfMissing(0x3704, TypeString, name);
                ap.SetIfMissing(0x3001, TypeString, name);
                var ext = Path.GetExtension(name);
                if (ext.Length > 0) ap.SetIfMissing(0x3703, TypeString, ext);
            }
            ap.WriteTo(s, new byte[8]);
        }

        foreach (var inner in extraEmbedded ?? [])
        {
            int i = written++;
            var s = root.AddStorage($"__attach_version1.0_#{i:X8}");
            var ap = new PropertyTable();
            ap.Set(0x0E21, TypeInt32, i);
            ap.Set(0x0FFE, TypeInt32, 7);
            ap.Set(0x370B, TypeInt32, -1);
            ap.Set(0x3001, TypeString, inner.Subject);
            AddEmbedded(s, ap, inner, depth);
            ap.WriteTo(s, new byte[8]);
        }

        // Readers (Outlook included) trust these flags over the attachment storages, so they must match what was written.
        props.SetBool(0x0E1B, written > 0);                                        // PR_HASATTACH
        props.UpdateInt32(0x0E07, flags => written > 0 ? flags | 0x10 : flags & ~0x10); // MSGFLAG_HASATTACH

        var header = new byte[embedded ? 24 : 32];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)recipients.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)written);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)recipients.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)written);
        props.WriteTo(root, header);
        return root;
    }

    /// <summary>Stores a whole message inside an attachment: a storage holding the nested message, plus the object marker property.</summary>
    static void AddEmbedded(CfbStorage attachStorage, PropertyTable props, Message inner, int depth)
    {
        props.Set(0x3705, TypeInt32, 5); // ATTACH_EMBEDDED_MSG
        var nested = BuildMessage(inner, embedded: true, depth + 1, null);
        var obj = attachStorage.AddStorage("__substg1.0_3701000D");
        foreach (var st in nested.Storages) obj.Storages.Add(st);
        foreach (var st in nested.Streams) obj.Streams.Add(st);
        props.AddObjectMarker(0x3701);
    }

    static bool IsCopyable(ushort id, ushort type, bool attachment = false)
    {
        if (id >= 0x8000) return false;                    // named properties: ids are per file and not mapped here
        if (id is >= 0x6600 and <= 0x67FF) return false;   // store-internal range
        if (StoreSpecific.Contains(id) && !(attachment && id is 0x0FFE or 0x0E21)) return false;
        return type is TypeInt16 or TypeInt32 or TypeFloat or TypeDouble or TypeCurrency or TypeError or TypeBool or TypeInt64
            or TypeString8 or TypeString or TypeTime or TypeBinary;
    }

    /// <summary>The property stream plus the substorage streams for variable-length values.</summary>
    sealed class PropertyTable
    {
        readonly SortedDictionary<(ushort Id, ushort Type), byte[]> _values = [];
        readonly HashSet<ushort> _objectMarkers = [];

        public void Add(ushort id, ushort type, byte[] value, Encoding source)
        {
            if (type == TypeString8)
            {
                // Everything is written as UTF-16 so no codepage bookkeeping is needed.
                RemoveOtherTypes(id);
                _values[(id, TypeString)] = Encoding.Unicode.GetBytes(source.GetString(value).TrimEnd('\0'));
                return;
            }
            RemoveOtherTypes(id);
            _values[(id, type)] = type == TypeString ? TrimTerminator(value) : value;
        }

        public void Set(ushort id, ushort type, string value) => Add(id, TypeString, Encoding.Unicode.GetBytes(value), Encoding.Unicode);

        public void Set(ushort id, ushort type, int value)
        {
            RemoveOtherTypes(id);
            _values[(id, TypeInt32)] = BitConverter.GetBytes(value);
        }

        public void SetBool(ushort id, bool value)
        {
            RemoveOtherTypes(id);
            _values[(id, TypeBool)] = [value ? (byte)1 : (byte)0];
        }

        /// <summary>Changes a 32-bit property in place (starting from 0 when it isn't there).</summary>
        public void UpdateInt32(ushort id, Func<int, int> change)
        {
            int current = _values.TryGetValue((id, TypeInt32), out var v) && v.Length >= 4 ? BitConverter.ToInt32(v) : 0;
            Set(id, TypeInt32, change(current));
        }

        public void SetIfMissing(ushort id, ushort type, string value)
        {
            if (!_values.Keys.Any(k => k.Id == id)) Set(id, type, value);
        }

        public void AddObjectMarker(ushort id) => _objectMarkers.Add(id);

        public void Remove(ushort id) => RemoveOtherTypes(id);

        void RemoveOtherTypes(ushort id)
        {
            foreach (var k in _values.Keys.Where(k => k.Id == id).ToList()) _values.Remove(k);
        }

        static byte[] TrimTerminator(byte[] utf16)
        {
            int len = utf16.Length;
            while (len >= 2 && utf16[len - 1] == 0 && utf16[len - 2] == 0) len -= 2;
            return len == utf16.Length ? utf16 : utf16[..len];
        }

        public void WriteTo(CfbStorage storage, byte[] header)
        {
            var entries = new List<byte[]>();
            foreach (var ((id, type), value) in _values)
            {
                var e = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(e, ((uint)id << 16) | type);
                BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), 0x00000006); // readable + writable

                switch (type)
                {
                    case TypeString:
                    case TypeBinary:
                    {
                        // For strings the recorded size includes the 2-byte terminator; the stream itself does not.
                        uint size = (uint)(value.Length + (type == TypeString ? 2 : 0));
                        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), size);
                        storage.AddStream($"__substg1.0_{id:X4}{type:X4}", value);
                        break;
                    }
                    case TypeBool: e[8] = value.Length > 0 && value[0] != 0 ? (byte)1 : (byte)0; break;
                    case TypeInt16: Array.Copy(value, 0, e, 8, Math.Min(2, value.Length)); break;
                    case TypeInt32 or TypeFloat or TypeError: Array.Copy(value, 0, e, 8, Math.Min(4, value.Length)); break;
                    default: Array.Copy(value, 0, e, 8, Math.Min(8, value.Length)); break; // double, currency, int64, time
                }
                entries.Add(e);
            }
            foreach (var id in _objectMarkers)
            {
                var e = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(e, ((uint)id << 16) | TypeObject);
                BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), 0x00000006);
                BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), 0xFFFFFFFF); // size of an object is not recorded
                BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), 0x00000001);
                entries.Add(e);
            }
            var stream = new byte[header.Length + entries.Count * 16];
            header.CopyTo(stream, 0);
            for (int i = 0; i < entries.Count; i++) entries[i].CopyTo(stream, header.Length + i * 16);
            storage.AddStream("__properties_version1.0", stream);
        }
    }
}
