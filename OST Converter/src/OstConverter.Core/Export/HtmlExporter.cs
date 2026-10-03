using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using OstConverter.Core.Pff;

namespace OstConverter.Core.Export;

/// <summary>One readable .html file per message, with attachments saved beside it in "{name}_files".</summary>
public sealed class HtmlExporter : IExporter
{
    readonly string _root;
    string _dir;

    public HtmlExporter(string outputDir) { _root = outputDir; _dir = outputDir; }

    public void BeginFolder(string folderPath)
    {
        _dir = Path.Combine(_root, FileNames.SafePath(folderPath));
    }

    public void Write(Message m, int index)
    {
        Directory.CreateDirectory(_dir);
        var baseName = $"{index:D5} {FileNames.Safe(m.Subject, 60, "(no subject)")}";
        var path = FileNames.Unique(Path.Combine(_dir, baseName + ".html"));
        baseName = Path.GetFileNameWithoutExtension(path);
        var filesDirName = baseName + "_files";

        var saved = SaveAttachments(m, Path.Combine(_dir, filesDirName), filesDirName);

        var body = m.HtmlBody;
        if (!string.IsNullOrEmpty(body))
        {
            foreach (var (cid, rel) in saved.Where(s => s.Cid is not null).Select(s => (s.Cid!, s.RelativePath)))
                body = Regex.Replace(body, "cid:" + Regex.Escape(cid), Regex.Replace(rel, "\\$", "$$$$"), RegexOptions.IgnoreCase);
        }
        else
        {
            var text = m.TextBody;
            if (string.IsNullOrEmpty(text) && m.RtfBody is { Length: > 0 } rtf) text = RtfText.ToPlainText(rtf);
            body = "<pre style=\"white-space:pre-wrap;font-family:inherit\">" + WebUtility.HtmlEncode(text ?? "") + "</pre>";
        }

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(m.Subject)).Append("</title>")
          .Append("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px}table.h{border-collapse:collapse;margin-bottom:16px}")
          .Append("table.h td{padding:2px 12px 2px 0;vertical-align:top}table.h td:first-child{color:#666}hr{border:0;border-top:1px solid #ccc}</style></head><body>");
        sb.Append("<table class=\"h\">");
        Row(sb, "From", Format(m.SenderName, m.SenderAddress));
        Row(sb, "To", Join(m.Recipients.Where(r => r.Kind == 1)));
        Row(sb, "Cc", Join(m.Recipients.Where(r => r.Kind == 2)));
        Row(sb, "Date", MimeBuilder.MessageDate(m)?.ToString("yyyy-MM-dd HH:mm 'UTC'"));
        Row(sb, "Subject", m.Subject);
        sb.Append("</table>");

        var links = saved.Where(s => !s.IsInlineImage).ToList();
        if (links.Count > 0)
        {
            sb.Append("<p>Attachments: ");
            sb.Append(string.Join(", ", links.Select(l =>
                $"<a href=\"{WebUtility.HtmlEncode(l.RelativePath)}\">{WebUtility.HtmlEncode(l.Name)}</a>")));
            sb.Append("</p>");
        }
        sb.Append("<hr>").Append(body).Append("</body></html>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    record Saved(string Name, string RelativePath, string? Cid, bool IsInlineImage);

    static List<Saved> SaveAttachments(Message m, string dir, string relDir)
    {
        var result = new List<Saved>();
        if (m.Attachments.Count == 0) return result;
        Directory.CreateDirectory(dir);
        foreach (var a in m.Attachments)
        {
            try
            {
                string name = FileNames.Safe(a.FileName, 100, "attachment");
                if (a.IsEmbeddedMessage)
                {
                    var inner = a.TryOpenEmbeddedMessage();
                    if (inner is null) continue;
                    var emlPath = FileNames.Unique(Path.Combine(dir, FileNames.Safe(inner.Subject, 80, "message") + ".eml"));
                    using (var fs = File.Create(emlPath)) MimeBuilder.Build(inner).WriteTo(fs);
                    result.Add(new Saved(Path.GetFileName(emlPath), relDir + "/" + Uri.EscapeDataString(Path.GetFileName(emlPath)), null, false));
                    continue;
                }
                var data = a.TryGetData();
                if (data is null) continue;
                var target = FileNames.Unique(Path.Combine(dir, name));
                File.WriteAllBytes(target, data);
                var cid = a.ContentId?.Trim('<', '>');
                bool inlineImg = a.IsInline && (a.MimeType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                result.Add(new Saved(a.FileName, relDir + "/" + Uri.EscapeDataString(Path.GetFileName(target)), cid, inlineImg));
            }
            catch (PffFormatException) { /* skip unreadable attachment */ }
        }
        return result;
    }

    static string Format(string name, string address) =>
        name.Length == 0 ? address : address.Length == 0 || name == address ? name : $"{name} <{address}>";

    static string Join(IEnumerable<Recipient> rs) => string.Join("; ", rs.Select(r => Format(r.Name, r.Address)));

    static void Row(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append("<tr><td>").Append(label).Append("</td><td>").Append(WebUtility.HtmlEncode(value)).Append("</td></tr>");
    }

    public void EndFolder() { }
    public void Dispose() { }
}
