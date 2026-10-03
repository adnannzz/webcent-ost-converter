using System.Text.RegularExpressions;
using OstConverter.Core.Pff;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OstConverter.Core.Export;

/// <summary>What goes on the page, independent of where it came from.</summary>
public sealed record PdfContent(string Subject, string Author, IReadOnlyList<(string Label, string Value)> Header, IReadOnlyList<string> Attachments, string Body);

/// <summary>
/// One PDF per message: {out}/{folder}/00001 Subject.pdf. The body is laid out as text (HTML mail is reduced to its
/// readable text), with the header block and an attachment list; text shaping, font fallback and page breaks are
/// handled by QuestPDF.
/// </summary>
public sealed class PdfExporter : IExporter
{
    const int MaxBodyCharacters = 1_000_000;
    static readonly Regex ControlChars = new("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", RegexOptions.Compiled);

    readonly string _root;
    string _dir;

    static PdfExporter()
    {
        // QuestPDF's Community licence applies to organisations under US$1M annual revenue; a larger one needs a commercial licence.
        QuestPDF.Settings.License = LicenseType.Community;
        // This app only runs on Windows, so the fonts that ship with it are used (Segoe UI plus script-specific fallbacks).
        QuestPDF.Settings.UseSystemFonts = true;
        // One character no font has must produce a placeholder, not abort the export of the whole mailbox.
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }

    /// <summary>
    /// Primary font first; when it lacks a character, QuestPDF tries the next. Together these cover Latin, Greek, Cyrillic,
    /// Arabic, Hebrew, Chinese, Japanese, Korean, Indic scripts, Thai, symbols and emoji on a stock Windows install.
    /// </summary>
    static readonly string[] FontFamilies =
    [
        "Segoe UI", "Arial", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Nirmala UI", "Leelawadee UI",
        "Segoe UI Symbol", "Segoe UI Emoji",
    ];

    /// <summary>Forces the one-time licence setup (done by the static constructor) for callers that only use the layout.</summary>
    internal static void EnsureLicense() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PdfExporter).TypeHandle);

    public PdfExporter(string outputDir) { _root = outputDir; _dir = outputDir; }

    public void BeginFolder(string folderPath) => _dir = Path.Combine(_root, FileNames.SafePath(folderPath));

    public void Write(Message m, int index)
    {
        var pdf = BuildDocument(Describe(m)).GeneratePdf();
        Directory.CreateDirectory(_dir);
        var path = FileNames.Unique(Path.Combine(_dir, $"{index:D5} {FileNames.Safe(m.Subject, 60, "(no subject)")}.pdf"));
        File.WriteAllBytes(path, pdf);
        if (MimeBuilder.MessageDate(m) is { } d && d.Year > 1970) File.SetLastWriteTimeUtc(path, d);
    }

    internal static PdfContent Describe(Message m)
    {
        var body = BodyText.Get(m);
        if (body.Length > MaxBodyCharacters) body = body[..MaxBodyCharacters] + "\n\n[Text truncated: the message is longer than this PDF export includes.]";

        var rows = new List<(string, string)>();
        void Row(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value)); }
        Row("From", Person(m.SenderName, m.SenderAddress));
        Row("To", Join(m, 1));
        Row("Cc", Join(m, 2));
        Row("Date", MimeBuilder.MessageDate(m)?.ToLocalTime().ToString("dddd, d MMMM yyyy HH:mm"));

        var attachments = new List<string>();
        foreach (var a in m.Attachments)
        {
            string label = a.FileName;
            if (a.IsEmbeddedMessage) label += "  (attached message)";
            else if (a.TryGetData() is { } bytes) label += $"  ({FormatSize(bytes.Length)})";
            else label += "  (data missing from the source file)";
            attachments.Add(label);
        }
        return new PdfContent(m.Subject.Length > 0 ? m.Subject : "(no subject)", m.SenderName, rows, attachments, body);
    }

    /// <summary>Rasterises each page to PNG (for visual checks of layout and fonts).</summary>
    internal static IReadOnlyList<byte[]> RenderImages(PdfContent c) => BuildDocument(c).GenerateImages().ToList();

    internal static Document BuildDocument(PdfContent c)
    {
        var subject = Clean(c.Subject);
        var body = Clean(c.Body);
        if (body.Length == 0) body = "(This message has no text content.)";

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontFamily(FontFamilies).FontSize(10.5f).LineHeight(1.35f));

                page.Content().Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Text(subject).FontSize(17).SemiBold();

                    if (c.Header.Count > 0)
                        col.Item().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(h =>
                        {
                            h.Spacing(3);
                            foreach (var (label, value) in c.Header)
                                h.Item().Row(r =>
                                {
                                    r.ConstantItem(48).Text(label).FontColor(Colors.Grey.Darken1);
                                    r.RelativeItem().Text(Clean(value));
                                });
                        });

                    if (c.Attachments.Count > 0)
                        col.Item().Column(a =>
                        {
                            a.Spacing(2);
                            a.Item().Text($"Attachments ({c.Attachments.Count})").FontColor(Colors.Grey.Darken1).SemiBold();
                            foreach (var line in c.Attachments) a.Item().Text("•  " + Clean(line));
                        });

                    col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    col.Item().Text(body);
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        })
        .WithMetadata(new DocumentMetadata { Title = subject, Author = Clean(c.Author), CreationDate = DateTimeOffset.Now });
    }

    static string Clean(string s) => ControlChars.Replace(s, " ");

    static string Person(string name, string address) =>
        name.Length == 0 ? address : address.Length == 0 || name == address ? name : $"{name} <{address}>";

    static string Join(Message m, int kind) =>
        string.Join("; ", m.Recipients.Where(r => r.Kind == kind).Select(r => Person(r.Name, r.Address)));

    static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} bytes",
    };

    public void EndFolder() { }
    public void Dispose() { }
}
