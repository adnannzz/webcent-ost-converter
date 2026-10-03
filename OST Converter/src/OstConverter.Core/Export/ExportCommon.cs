using System.Text;
using System.Text.RegularExpressions;
using OstConverter.Core.Pff;

namespace OstConverter.Core.Export;

public enum ExportFormat { Eml, Mbox, Html, Csv, Msg, Pdf, Pst }

public interface IExporter : IDisposable
{
    /// <param name="folderPath">Display path relative to the mailbox root, '/'-separated. Empty for the root itself.</param>
    void BeginFolder(string folderPath);
    /// <param name="index">1-based position of the item within its folder.</param>
    void Write(Message message, int index);
    void EndFolder();
}

/// <summary>
/// An exporter that rebuilds the folder tree itself (rather than one file per item): it also wants the source folder, and
/// is told about folders that hold no items so the structure survives.
/// </summary>
public interface IFolderAwareExporter : IExporter
{
    void BeginFolder(string folderPath, Folder folder);
}

public enum ItemKind { Mail, Contact, Appointment, Task, Other }

public static class ItemKinds
{
    public static ItemKind Classify(string messageClass)
    {
        var c = messageClass ?? "";
        bool Is(string prefix) => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        if (Is("IPM.Contact") || Is("IPM.DistList")) return ItemKind.Contact;
        if (Is("IPM.Appointment")) return ItemKind.Appointment;
        if (Is("IPM.Task")) return ItemKind.Task;
        if (Is("IPM.Note") || Is("IPM.Schedule") || Is("REPORT.") || Is("IPM.Post") || Is("IPM.Recall") || c == "IPM")
            return ItemKind.Mail;
        return ItemKind.Other;
    }
}

public static class FileNames
{
    static readonly Regex Invalid = new("[\\x00-\\x1f<>:\"/\\\\|?*]+", RegexOptions.Compiled);
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Turns arbitrary text into a safe single Windows path segment.</summary>
    public static string Safe(string? s, int maxLength = 80, string fallback = "untitled")
    {
        var t = Invalid.Replace(s ?? "", "_");
        t = Regex.Replace(t, "\\s+", " ").Trim().TrimEnd('.', ' ');
        if (t.Length > maxLength) t = t[..maxLength].TrimEnd('.', ' ');
        if (t.Length == 0) t = fallback;
        if (Reserved.Contains(t.Split('.')[0])) t = "_" + t;
        return t;
    }

    public static string SafePath(string folderPath) =>
        string.Join(Path.DirectorySeparatorChar, folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => Safe(p, 60)));

    /// <summary>Returns a path that doesn't exist yet by appending " (2)", " (3)", ...</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}

public static class BodyText
{
    static readonly Regex DropBlocks = new("<(script|style|head)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Breaks = new("<(br|/p|/div|/tr|/li|/h[1-6])[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>Plain-text body: the text part if present, else RTF or HTML converted to text.</summary>
    public static string Get(Message m)
    {
        var text = m.TextBody;
        if (!string.IsNullOrWhiteSpace(text)) return text;
        if (m.RtfBody is { Length: > 0 } rtf)
        {
            var t = RtfText.ToPlainText(rtf);
            if (t.Length > 0) return t;
        }
        return m.HtmlBody is { Length: > 0 } html ? FromHtml(html) : "";
    }

    public static string FromHtml(string html)
    {
        var s = DropBlocks.Replace(html, "");
        s = Breaks.Replace(s, "\n");
        s = Tags.Replace(s, "");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, "[ \\t\\u00a0]+", " ");
        s = Regex.Replace(s, "\\s*\\n\\s*", "\n");
        return s.Trim();
    }
}

/// <summary>Minimal RTF-to-text for messages that only carry a compressed RTF body.</summary>
public static class RtfText
{
    static readonly HashSet<string> SkippedDestinations = new(StringComparer.OrdinalIgnoreCase)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "generator", "themedata", "colorschememapping", "datastore", "latentstyles",
    };

    public static string ToPlainText(string rtf)
    {
        var sb = new StringBuilder();
        var skipDepth = new Stack<bool>();
        bool skipping = false;
        int i = 0;
        while (i < rtf.Length)
        {
            char c = rtf[i];
            switch (c)
            {
                case '{':
                    skipDepth.Push(skipping);
                    i++;
                    // "{\*\dest" and known destinations are ignored along with their content.
                    if (i + 1 < rtf.Length && rtf[i] == '\\')
                    {
                        if (rtf[i + 1] == '*') skipping = true;
                        else
                        {
                            int j = i + 1;
                            while (j < rtf.Length && char.IsLetter(rtf[j])) j++;
                            if (SkippedDestinations.Contains(rtf[(i + 1)..j])) skipping = true;
                        }
                    }
                    break;
                case '}':
                    if (skipDepth.Count > 0) skipping = skipDepth.Pop();
                    i++;
                    break;
                case '\\':
                    i++;
                    if (i >= rtf.Length) break;
                    char n = rtf[i];
                    if (n is '\\' or '{' or '}') { if (!skipping) sb.Append(n); i++; }
                    else if (n == '\'')
                    {
                        if (i + 2 < rtf.Length && Uri.IsHexDigit(rtf[i + 1]) && Uri.IsHexDigit(rtf[i + 2]))
                        {
                            if (!skipping) sb.Append(Encoding.Latin1.GetString([Convert.ToByte(rtf.Substring(i + 1, 2), 16)]));
                            i += 3;
                        }
                        else i++;
                    }
                    else if (char.IsLetter(n))
                    {
                        int j = i;
                        while (j < rtf.Length && char.IsLetter(rtf[j])) j++;
                        string word = rtf[i..j];
                        int k = j;
                        if (k < rtf.Length && rtf[k] == '-') k++;
                        while (k < rtf.Length && char.IsDigit(rtf[k])) k++;
                        string arg = rtf[j..k];
                        i = k;
                        if (i < rtf.Length && rtf[i] == ' ') i++; // delimiter space belongs to the control word
                        if (skipping) break;
                        switch (word)
                        {
                            case "par": case "line": sb.Append('\n'); break;
                            case "tab": sb.Append('\t'); break;
                            case "emdash": sb.Append('—'); break;
                            case "endash": sb.Append('–'); break;
                            case "bullet": sb.Append('•'); break;
                            case "u" when int.TryParse(arg, out var code):
                                sb.Append((char)(code < 0 ? code + 65536 : code));
                                if (i < rtf.Length && rtf[i] == '?') i++; // fallback char after \uN
                                break;
                        }
                    }
                    else i++; // control symbol like \~ or \-
                    break;
                case '\r':
                case '\n':
                    i++;
                    break;
                default:
                    if (!skipping) sb.Append(c);
                    i++;
                    break;
            }
        }
        return sb.ToString().Trim();
    }
}
