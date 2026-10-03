using System.Text;
using OstConverter.Core.Pff;

namespace OstConverter.Core.Export;

/// <summary>
/// One CSV per folder. The column set follows the item type (mail index, contacts, calendar, tasks).
/// UTF-8 with BOM so Excel detects the encoding.
/// </summary>
public sealed class CsvExporter(string outputDir) : IExporter
{
    string _folder = "";
    readonly Dictionary<ItemKind, StreamWriter> _writers = new();

    public void BeginFolder(string folderPath)
    {
        _folder = folderPath.Length == 0 ? "Mailbox" : folderPath;
    }

    public void Write(Message m, int index)
    {
        var kind = ItemKinds.Classify(m.MessageClass);
        if (kind == ItemKind.Other) return;
        if (!_writers.TryGetValue(kind, out var w))
        {
            var suffix = _writers.Count == 0 ? "" : "." + kind.ToString().ToLowerInvariant();
            var path = FileNames.Unique(Path.Combine(outputDir, FileNames.SafePath(_folder) + suffix + ".csv"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            w = new StreamWriter(path, false, new UTF8Encoding(true)) { NewLine = "\r\n" };
            WriteRow(w, Header(kind));
            _writers[kind] = w;
        }
        WriteRow(w, Fields(kind, m));
    }

    static string[] Header(ItemKind kind) => kind switch
    {
        ItemKind.Mail => ["Date Sent", "Date Received", "From", "From Address", "To", "Cc", "Subject", "Read", "Attachments", "Attachment Names", "Message ID", "Body Preview"],
        ItemKind.Contact => ["Display Name", "First Name", "Last Name", "Company", "Job Title", "Email 1", "Email 2", "Email 3", "Business Phone", "Home Phone", "Mobile Phone",
            "Business Street", "Business City", "Business State", "Business Postal Code", "Business Country", "Birthday", "Notes"],
        ItemKind.Appointment => ["Subject", "Start", "End", "All Day", "Location", "Organizer", "Required Attendees", "Optional Attendees", "Notes"],
        _ => ["Subject", "Start Date", "Due Date", "Status", "Notes"],
    };

    static string[] Fields(ItemKind kind, Message m) => kind switch
    {
        ItemKind.Mail =>
        [
            Date(m.SentTime), Date(m.ReceivedTime), m.SenderName, m.SenderAddress,
            Join(m, 1), Join(m, 2), m.Subject, m.IsRead ? "Yes" : "No",
            m.Attachments.Count.ToString(), string.Join("; ", m.Attachments.Select(a => a.FileName)),
            m.InternetMessageId ?? "", Preview(m),
        ],
        ItemKind.Contact =>
        [
            m.GetString(PropTag.DisplayName) ?? "", m.GetString(0x3A06) ?? "", m.GetString(0x3A11) ?? "", m.GetString(0x3A16) ?? "", m.GetString(0x3A17) ?? "",
            Email(m, 0x8082, 0x8083), Email(m, 0x8092, 0x8093), Email(m, 0x80A2, 0x80A3),
            m.GetString(0x3A08) ?? "", m.GetString(0x3A09) ?? "", m.GetString(0x3A1C) ?? "",
            m.GetString(0x3A29) ?? "", m.GetString(0x3A27) ?? "", m.GetString(0x3A28) ?? "", m.GetString(0x3A2A) ?? "", m.GetString(0x3A26) ?? "",
            Date(m.GetDate(0x3A42)), BodyText.Get(m),
        ],
        ItemKind.Appointment =>
        [
            m.Subject, Date(m.GetNamedDate(NameMap.PsetidAppointment, 0x820D)), Date(m.GetNamedDate(NameMap.PsetidAppointment, 0x820E)),
            m.GetNamedInt(NameMap.PsetidAppointment, 0x8215) is > 0 ? "Yes" : "No",
            m.GetNamedString(NameMap.PsetidAppointment, 0x8208) ?? "", m.SenderName,
            Join(m, 1), Join(m, 2), BodyText.Get(m),
        ],
        _ =>
        [
            m.Subject, Date(m.GetNamedDate(NameMap.PsetidTask, 0x8104)), Date(m.GetNamedDate(NameMap.PsetidTask, 0x8105)),
            m.GetNamedInt(NameMap.PsetidTask, 0x8101) switch { 0 => "Not started", 1 => "In progress", 2 => "Completed", 3 => "Waiting", 4 => "Deferred", _ => "" },
            BodyText.Get(m),
        ],
    };

    static string Email(Message m, uint typeLid, uint addrLid)
    {
        var addr = m.GetNamedString(NameMap.PsetidAddress, addrLid) ?? "";
        var type = m.GetNamedString(NameMap.PsetidAddress, typeLid);
        return string.Equals(type, "EX", StringComparison.OrdinalIgnoreCase) ? "" : addr; // X500 DNs are not usable addresses
    }

    static string Date(DateTime? d) => d is null ? "" : d.Value.ToString("yyyy-MM-dd HH:mm:ss");

    static string Join(Message m, int kind) =>
        string.Join("; ", m.Recipients.Where(r => r.Kind == kind).Select(r => r.Address.Length > 0 && r.Name != r.Address ? $"{r.Name} <{r.Address}>" : r.Name));

    static string Preview(Message m)
    {
        var t = BodyText.Get(m);
        if (string.IsNullOrEmpty(t)) return "";
        t = System.Text.RegularExpressions.Regex.Replace(t, "\\s+", " ").Trim();
        return t.Length > 300 ? t[..300] : t;
    }

    internal static void WriteRow(TextWriter w, IReadOnlyList<string> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) w.Write(',');
            w.Write(Escape(fields[i]));
        }
        w.WriteLine();
    }

    internal static string Escape(string s)
    {
        // Prefix formula triggers so spreadsheet apps don't execute cell content from untrusted mail.
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public void EndFolder()
    {
        foreach (var w in _writers.Values) w.Dispose();
        _writers.Clear();
    }

    public void Dispose() => EndFolder();
}
