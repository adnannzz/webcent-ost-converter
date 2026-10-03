using System.Buffers.Binary;

namespace OstConverter.Core.Pff;

/// <summary>
/// Name-to-ID map (node 0x61). Outlook assigns ids 0x8000+ to named properties per file,
/// so contact e-mail, appointment times etc. must be looked up by (GUID, numeric id).
/// </summary>
public sealed class NameMap
{
    public static readonly Guid PsetidAddress = new("00062004-0000-0000-C000-000000000046");
    public static readonly Guid PsetidAppointment = new("00062002-0000-0000-C000-000000000046");
    public static readonly Guid PsetidTask = new("00062003-0000-0000-C000-000000000046");
    public static readonly Guid PsetidCommon = new("00062008-0000-0000-C000-000000000046");

    static readonly Guid PsMapi = new("00020328-0000-0000-C000-000000000046");
    static readonly Guid PsPublicStrings = new("00020329-0000-0000-C000-000000000046");

    readonly Dictionary<(Guid, uint), ushort> _numeric = new();

    public static NameMap Empty { get; } = new();

    public static NameMap Load(PffFile file)
    {
        var map = new NameMap();
        var node = file.OpenNode(0x61);
        if (node is null) return map;
        try
        {
            var pc = new PropertyContext(node);
            var guids = pc.GetBytes(0x0002) ?? [];
            var entries = pc.GetBytes(0x0003) ?? [];
            for (int i = 0; i + 8 <= entries.Length; i += 8)
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i));
                ushort guidField = BinaryPrimitives.ReadUInt16LittleEndian(entries.AsSpan(i + 4));
                ushort propIdx = BinaryPrimitives.ReadUInt16LittleEndian(entries.AsSpan(i + 6));
                if ((guidField & 1) != 0) continue; // string-named property
                int gi = guidField >> 1;
                Guid g;
                if (gi == 1) g = PsMapi;
                else if (gi == 2) g = PsPublicStrings;
                else if (gi >= 3 && (gi - 3) * 16 + 16 <= guids.Length) g = new Guid(guids.AsSpan((gi - 3) * 16, 16));
                else continue;
                map._numeric[(g, id)] = (ushort)(0x8000 + propIdx);
            }
        }
        catch (PffFormatException) { /* a damaged map just means no named properties */ }
        return map;
    }

    public ushort? Resolve(Guid set, uint lid) => _numeric.TryGetValue((set, lid), out var id) ? id : null;
}
