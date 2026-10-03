using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace OstConverter.Core.Diagnostics;

/// <summary>
/// Writes unexpected errors to a local text file so a customer can attach it to a support request. Nothing is sent
/// anywhere. A report holds the exception type, message and stack plus app and Windows versions; it never includes
/// mail content (the converter's own messages carry node ids, not subjects), but the message may name a file path.
/// </summary>
public static class CrashLog
{
    const int KeepFiles = 20;

    /// <summary>Folder for the reports; tests point it at a temp folder.</summary>
    public static string Directory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OST Converter", "logs");

    /// <summary>Writes a report and returns its path, or null if even that failed (logging must never throw).</summary>
    public static string? Write(Exception exception, string source)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            File.WriteAllText(path, Format(exception, source), new UTF8Encoding(false));
            Prune();
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string Format(Exception exception, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Webcent OST Converter error report");
        sb.AppendLine($"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Source:  {source}");
        sb.AppendLine($"Version: {Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0)}");
        sb.AppendLine($"System:  {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine();
        sb.AppendLine(exception.ToString());
        return sb.ToString();
    }

    static void Prune()
    {
        foreach (var old in new DirectoryInfo(Directory).GetFiles("crash-*.txt").OrderByDescending(f => f.Name).Skip(KeepFiles))
        {
            try { old.Delete(); } catch (IOException) { }
        }
    }
}
