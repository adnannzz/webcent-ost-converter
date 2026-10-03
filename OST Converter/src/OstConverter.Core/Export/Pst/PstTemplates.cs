namespace OstConverter.Core.Export.Pst;

/// <summary>
/// The column sets Outlook gives each kind of table in a PST (read from a file Outlook created; they match the templates
/// in [MS-PST] 2.4.4). The order is the order of the existence-bitmap bits.
/// </summary>
public static class PstTemplates
{
    const ushort I = PropType.Int32, S = PropType.String, B = PropType.Binary, T = PropType.Time, L = PropType.Int64, F = PropType.Bool;

    public static readonly TcColumn[] Hierarchy =
    [
        new(0x3001, S), new(0x3602, I), new(0x3603, I), new(0x360A, F), new(0x0E30, B), new(0x0E33, L), new(0x0E34, B),
        new(0x0E38, I), new(0x3613, S), new(0x6635, I), new(0x6636, I),
    ];

    public static readonly TcColumn[] Contents =
    [
        new(0x0E17, I), new(0x001A, S), new(0x0E07, I), new(0x0017, I), new(0x0042, S), new(0x0037, S), new(0x0E06, T), new(0x0039, T),
        new(0x0E08, I), new(0x0E04, S), new(0x0E03, S), new(0x0057, F), new(0x0058, F), new(0x0036, I), new(0x1097, I), new(0x0070, S),
        new(0x0071, B), new(0x3013, B), new(0x65C6, I), new(0x3008, T), new(0x0E30, B), new(0x0E33, L), new(0x0E34, B), new(0x0E3D, B),
        new(0x0E3C, B), new(0x0E38, I),
    ];

    public static readonly TcColumn[] AssocContents =
    [
        new(0x0E17, I), new(0x001A, S), new(0x0E07, I), new(0x3001, S), new(0x7003, I), new(0x7004, B), new(0x7005, B), new(0x7006, S),
        new(0x7007, I), new(0x6800, S), new(0x6803, F), new(0x6805, (ushort)(PropType.MultiValueFlag | I)), new(0x682F, S),
    ];

    public static readonly TcColumn[] SearchContents =
    [
        new(0x67F1, I), new(0x0E05, S), new(0x0E17, I), new(0x001A, S), new(0x0E07, I), new(0x0017, I), new(0x0042, S), new(0x0037, S),
        new(0x0E06, T), new(0x0E08, I), new(0x0E04, S), new(0x0E03, S), new(0x0057, F), new(0x0058, F), new(0x0036, I), new(0x0E2A, F),
        new(0x3008, T),
    ];

    public static readonly TcColumn[] ReceiveFolder = [new(0x6605, I), new(0x001A, S)];
    public static readonly TcColumn[] OutgoingQueue = [new(0x0E14, I), new(0x0E10, I), new(0x0039, T)];
    public static readonly TcColumn[] Attachments = [new(0x370B, I), new(0x0E20, I), new(0x3705, I), new(0x3704, S), new(0x3714, I)];

    public static readonly TcColumn[] Recipients =
    [
        new(0x0E0F, F), new(0x3002, S), new(0x3003, S), new(0x0FFF, B), new(0x3001, S), new(0x0C15, I), new(0x300B, B), new(0x0FF9, B),
        new(0x0FFE, I), new(0x3900, I), new(0x3A40, F), new(0x39FF, S),
    ];

    public static readonly TcColumn[] Template16 = [new(0x0E33, L), new(0x0E37, B), new(0x0E38, I)];
    public static readonly TcColumn[] Template17 = [new(0x0E3E, B), new(0x0E31, B), new(0x0E30, B), new(0x0E38, I), new(0x0E34, B), new(0x0E33, L), new(0x001A, S)];
    public static readonly TcColumn[] Template18 = [new(0x3007, T), new(0x0E33, L)];
}
