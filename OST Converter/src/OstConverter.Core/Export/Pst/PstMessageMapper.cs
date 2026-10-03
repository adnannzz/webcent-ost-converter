using System.Text;
using OstConverter.Core.Pff;

namespace OstConverter.Core.Export.Pst;

/// <summary>
/// Turns a decoded source item into a <see cref="PstMessageData"/>: properties are copied as stored (so named-property ids
/// stay valid against the copied name map), normalised to forms a Unicode PST holds, and properties that only mean something
/// inside the source mailbox are left behind.
/// </summary>
internal static class PstMessageMapper
{
    const int MaxEmbeddingDepth = 6;

    public static PstMessageData Map(Message m, int depth = 0)
    {
        var data = new PstMessageData();
        var enc = m.StringEncoding;

        foreach (var p in m.GetRawProperties())
            if (Convert(p, enc) is { } pp) data.Props.Add(pp);

        foreach (var row in m.GetRecipientRows())
        {
            var list = new List<PstProp>();
            foreach (var p in row)
                if (Convert(p, enc, recipient: true) is { } pp) list.Add(pp);
            data.Recipients.Add(list);
        }

        foreach (var a in m.Attachments)
        {
            var ad = new PstAttachmentData();
            if (a.IsEmbeddedMessage)
            {
                // Message-within-message: nesting is capped so a damaged or cyclic file can't recurse without end.
                var inner = depth < MaxEmbeddingDepth ? a.TryOpenEmbeddedMessage() : null;
                if (inner is null) continue;
                ad.Embedded = Map(inner, depth + 1);
                foreach (var p in a.GetRawProperties())
                    if (p.Id != 0x3701 && Convert(p, enc) is { } pp) ad.Props.Add(pp);
            }
            else
            {
                var raw = a.ReadRaw();
                if (raw.DataMissing) continue;               // already reported as a warning on the message, like the other exporters
                foreach (var p in raw.Properties)
                    if (Convert(p, enc) is { } pp) ad.Props.Add(pp);
                ad.Props.Add(PstProp.Bin(0x3701, raw.Data ?? []));
            }
            data.Attachments.Add(ad);
        }
        return data;
    }

    /// <summary>Properties that identify the object inside the source store or its sync state and are meaningless (or misleading) elsewhere.</summary>
    static bool IsStoreBound(ushort id) =>
        id is 0x0E09 or 0x0E21 or 0x0E30 or 0x0E33 or 0x0E34 or 0x0E38 or 0x0E3C or 0x0E3D or 0x0E1B or 0x3000
            or >= 0x0FF9 and <= 0x0FFF
        || (id is >= 0x6600 and <= 0x67FF && id is not (0x6619 or 0x661D));   // keep the body-prefix and best-body hints

    static PstProp? Convert(RawProperty p, Encoding enc, bool recipient = false)
    {
        if (IsStoreBound(p.Id) && !(recipient && p.Id is 0x0FF9 or 0x0FFE or 0x0FFF)) return null;
        var v = p.Value;
        switch (p.Type)
        {
            case PropType.Int16: return v.Length >= 2 ? new PstProp(p.Id, p.Type, v[..2]) : null;
            case PropType.Int32 or PropType.Float or PropType.Error: return v.Length >= 4 ? new PstProp(p.Id, p.Type, v[..4]) : null;
            case PropType.Bool: return v.Length >= 1 ? new PstProp(p.Id, p.Type, [v[0] != 0 ? (byte)1 : (byte)0]) : null;
            case PropType.Int64 or PropType.Double or PropType.Currency or PropType.AppTime or PropType.Time:
                return v.Length == 8 ? new PstProp(p.Id, p.Type, v) : null;
            case PropType.Guid: return v.Length == 16 ? new PstProp(p.Id, p.Type, v) : null;
            case PropType.String8:
                // Everything is stored as UTF-16 so the destination needs no code-page bookkeeping.
                return new PstProp(p.Id, PropType.String, Encoding.Unicode.GetBytes(enc.GetString(v).TrimEnd('\0')));
            case PropType.String: return new PstProp(p.Id, p.Type, v);
            case PropType.Binary: return new PstProp(p.Id, p.Type, v);
            case 0x1002 or 0x1003 or 0x1004 or 0x1005 or 0x1006 or 0x1007 or 0x1014 or 0x101F or 0x1040 or 0x1048 or 0x1102:
                return new PstProp(p.Id, p.Type, v);
            default: return null;                            // objects, null, multi-valued 8-bit strings, unknown types
        }
    }
}
