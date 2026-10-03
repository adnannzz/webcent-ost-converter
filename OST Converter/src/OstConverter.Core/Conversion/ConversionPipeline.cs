using System.Diagnostics;
using System.Text;
using OstConverter.Core.Export;
using OstConverter.Core.Pff;

namespace OstConverter.Core.Conversion;

/// <summary>What the current license allows. The pipeline enforces it, so it can't be bypassed from the UI layer.</summary>
public sealed record Entitlement(int? MaxItemsPerFolder)
{
    public const int LimitedItemsPerFolder = 50;
    public static Entitlement Limited { get; } = new(LimitedItemsPerFolder);
    public static Entitlement Unlimited { get; } = new((int?)null);
    public bool IsLimited => MaxItemsPerFolder is not null;
}

public sealed class ConversionOptions
{
    public required string OutputDir { get; init; }
    public ExportFormat Format { get; init; } = ExportFormat.Eml;
    /// <summary>Folders to convert (by node id). Null means every user-visible folder.</summary>
    public IReadOnlySet<ulong>? FolderNids { get; init; }
    public bool IncludeSystemFolders { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public Entitlement Entitlement { get; init; } = Entitlement.Limited;
    /// <summary>PST output only: start a new file once the current one reaches this size (Outlook's default PST limit is 50 GB).</summary>
    public long SplitSizeBytes { get; init; } = Export.Pst.PstExporter.DefaultSplitBytes;
}

public sealed record ConversionProgress(string Folder, long Done, long Total);
/// <summary>A problem with one item. Warnings mean the item was exported but incomplete (e.g. an attachment was missing).</summary>
public sealed record ItemError(string Folder, string Item, string Message, bool IsWarning = false);

public sealed class ConversionResult
{
    public long Exported { get; internal set; }
    public long SkippedByDate { get; internal set; }
    public long SkippedNotApplicable { get; internal set; }
    public long SkippedByLimit { get; internal set; }
    public List<ItemError> Errors { get; } = [];
    public bool Cancelled { get; internal set; }
    public TimeSpan Duration { get; internal set; }
    public string? LogPath { get; internal set; }
    /// <summary>Files that make up a single-output format (the PST file and its continuation parts).</summary>
    public List<string> OutputFiles { get; } = [];
    /// <summary>True when the per-folder item limit left items out.</summary>
    public bool Limited => SkippedByLimit > 0;
}

public static class FolderPaths
{
    static readonly string[] HiddenRoots = ["Root - Mailbox", "IPM_SUBTREE", "Top of Personal Folders", "Top of Outlook data file"];

    static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Root - Public", "Common Views", "Finder", "Shortcuts", "Views", "~MAPISP(Internal)", "Drizzle", "Shared Data",
        "Search Root", "IPM_VIEWS", "IPM_COMMON_VIEWS", "ItemProcSearch", "Yammer Root", "PersonMetadata",
        "Conversation Action Settings", "Quick Step Settings", "ExternalContacts", "Conversation History",
    };

    /// <summary>User-facing path: the container folders every store has are dropped from the front.</summary>
    public static string Display(string rawPath)
    {
        var parts = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 0 && HiddenRoots.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) parts.RemoveAt(0);
        return string.Join('/', parts);
    }

    public static bool IsSystem(string rawPath)
    {
        foreach (var part in rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (SystemNames.Contains(part) || part.StartsWith("SPAM Search Folder", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith("MS-OLK-", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

public static class ConversionPipeline
{
    public static ConversionResult Run(PstStore store, ConversionOptions options, IProgress<ConversionProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ConversionResult();
        Directory.CreateDirectory(options.OutputDir);

        // A PST rebuilds the folder tree, so folders without items are kept too.
        bool keepEmptyFolders = options.Format == ExportFormat.Pst;
        var folders = store.WalkFolders()
            .Where(f => options.FolderNids is { } sel ? sel.Contains(f.Folder.Nid) : options.IncludeSystemFolders || !FolderPaths.IsSystem(f.Path))
            .Select(f => (f.Folder, Path: FolderPaths.Display(f.Path), Count: SafeCount(f.Folder)))
            .Where(f => keepEmptyFolders || f.Count > 0)
            .ToList();

        long total = folders.Sum(f => (long)f.Count), done = 0;
        var exporter = CreateExporter(options.Format, options.OutputDir, store, options);
        try
        {
            foreach (var (folder, path, count) in folders)
            {
                if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                if (exporter is IFolderAwareExporter aware) aware.BeginFolder(path, folder); else exporter.BeginFolder(path);
                try
                {
                    int index = 0, seen = 0;
                    foreach (var nid in folder.GetMessageIds())
                    {
                        if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                        if (options.Entitlement.MaxItemsPerFolder is { } cap && index >= cap)
                        {
                            long remaining = count - seen;
                            result.SkippedByLimit += remaining;
                            done += remaining;
                            break;
                        }
                        seen++;
                        done++;
                        if (done % 25 == 0 || done == total) progress?.Report(new ConversionProgress(path, done, total));
                        try
                        {
                            var msg = store.OpenMessage(nid);
                            if (msg is null) { result.Errors.Add(new ItemError(path, $"0x{nid:X}", "Item data is missing from the file.")); continue; }
                            if (!Applies(options.Format, msg)) { result.SkippedNotApplicable++; continue; }
                            if (!InRange(msg, options)) { result.SkippedByDate++; continue; }
                            exporter.Write(msg, index + 1);
                            index++;
                            result.Exported++;
                            foreach (var w in msg.Warnings) result.Errors.Add(new ItemError(path, $"0x{nid:X}", w, IsWarning: true));
                        }
                        catch (Exception e) when (e is PffFormatException or IOException or NotSupportedException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
                        {
                            result.Errors.Add(new ItemError(path, $"0x{nid:X}", e.Message));
                        }
                    }
                }
                catch (Exception e) when (e is PffFormatException or IOException)
                {
                    result.Errors.Add(new ItemError(path, "(folder)", e.Message));
                }
                finally { exporter.EndFolder(); }
            }
        }
        finally
        {
            // Disposing finishes single-file outputs such as the PST, so problems here are reported rather than lost.
            try { exporter.Dispose(); }
            catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException)
            {
                result.Errors.Add(new ItemError("", "(output file)", "The output could not be completed: " + e.Message));
            }
        }
        if (exporter is Export.Pst.PstExporter pst) result.OutputFiles.AddRange(pst.Files);

        progress?.Report(new ConversionProgress("", done, total));
        sw.Stop();
        result.Duration = sw.Elapsed;
        result.LogPath = WriteLog(options, result);
        return result;
    }

    static int SafeCount(Folder f)
    {
        try { return f.GetMessageCount(); } catch (PffFormatException) { return 0; }
    }

    internal static IExporter CreateExporter(ExportFormat format, string dir, PstStore? source = null, ConversionOptions? options = null) => format switch
    {
        ExportFormat.Eml => new EmlExporter(dir),
        ExportFormat.Mbox => new MboxExporter(dir),
        ExportFormat.Html => new HtmlExporter(dir),
        ExportFormat.Csv => new CsvExporter(dir),
        ExportFormat.Msg => new MsgExporter(dir),
        ExportFormat.Pdf => new PdfExporter(dir),
        ExportFormat.Pst => new Export.Pst.PstExporter(dir,
            source is null ? "Mailbox" : Path.GetFileNameWithoutExtension(source.File.Path),
            source is null ? null : Export.Pst.PstExporter.NameMapOf(source),
            options?.SplitSizeBytes ?? Export.Pst.PstExporter.DefaultSplitBytes),
        _ => throw new NotSupportedException($"Format {format} is not supported yet."),
    };

    static bool Applies(ExportFormat format, Message m)
    {
        if (format == ExportFormat.Pst) return true;          // a PST carries every kind of item
        var kind = ItemKinds.Classify(m.MessageClass);
        return format == ExportFormat.Csv ? kind != ItemKind.Other : kind == ItemKind.Mail;
    }

    static bool InRange(Message m, ConversionOptions o)
    {
        if (o.From is null && o.To is null) return true;
        var d = MimeBuilder.MessageDate(m);
        if (d is null) return true; // can't judge, so keep it
        return (o.From is null || d >= o.From) && (o.To is null || d < o.To.Value.Date.AddDays(1));
    }

    static string WriteLog(ConversionOptions o, ConversionResult r)
    {
        var path = Path.Combine(o.OutputDir, "conversion-log.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"Conversion finished {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({r.Duration.TotalSeconds:F1}s){(r.Cancelled ? " - CANCELLED" : "")}");
        sb.AppendLine($"Format: {o.Format}");
        sb.AppendLine($"Exported: {r.Exported}");
        sb.AppendLine($"Skipped (not applicable to this format): {r.SkippedNotApplicable}");
        sb.AppendLine($"Skipped (outside date range): {r.SkippedByDate}");
        if (r.SkippedByLimit > 0)
            sb.AppendLine($"NOT exported (per-folder item limit of {o.Entitlement.MaxItemsPerFolder} items per folder): {r.SkippedByLimit}");
        sb.AppendLine($"Items that failed: {r.Errors.Count(e => !e.IsWarning)}");
        sb.AppendLine($"Exported with warnings: {r.Errors.Count(e => e.IsWarning)}");
        foreach (var e in r.Errors) sb.AppendLine($"  {(e.IsWarning ? "WARNING" : "ERROR")} [{e.Folder}] {e.Item}: {e.Message}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }
}
