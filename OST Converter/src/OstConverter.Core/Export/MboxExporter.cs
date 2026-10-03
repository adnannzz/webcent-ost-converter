using System.Globalization;
using MimeKit;
using OstConverter.Core.Pff;

namespace OstConverter.Core.Export;

/// <summary>One mboxrd file per folder: {out}/{folder}.mbox (readable by Thunderbird, Apple Mail, etc.).</summary>
public sealed class MboxExporter(string outputDir) : IExporter
{
    static readonly FormatOptions Unix = CreateUnix();
    FileStream? _out;
    string? _relPath;

    static FormatOptions CreateUnix()
    {
        var o = FormatOptions.Default.Clone();
        o.NewLineFormat = NewLineFormat.Unix;
        return o;
    }

    public void BeginFolder(string folderPath)
    {
        // The file is created on the first message, so folders with nothing to export leave no empty files behind.
        _relPath = FileNames.SafePath(folderPath.Length == 0 ? "Mailbox" : folderPath);
    }

    public void Write(Message message, int index)
    {
        if (_relPath is null) throw new InvalidOperationException("BeginFolder was not called.");
        if (_out is null)
        {
            var path = FileNames.Unique(Path.Combine(outputDir, _relPath + ".mbox"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _out = File.Create(path);
        }
        var mime = MimeBuilder.Build(message);
        using var ms = new MemoryStream();
        mime.WriteTo(Unix, ms);

        var sender = mime.From.Mailboxes.FirstOrDefault()?.Address;
        if (string.IsNullOrEmpty(sender) || sender.Contains(' ')) sender = "MAILER-DAEMON";
        var date = (MimeBuilder.MessageDate(message) ?? DateTime.UtcNow).ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture);
        var line = System.Text.Encoding.ASCII.GetBytes($"From {sender} {date}\n");
        _out.Write(line);
        WriteEscaped(_out, ms.GetBuffer().AsSpan(0, (int)ms.Length));
        _out.WriteByte((byte)'\n');
    }

    /// <summary>mboxrd quoting: any line of the form ">*From " gets one more '>' so readers don't split messages.</summary>
    internal static void WriteEscaped(Stream output, ReadOnlySpan<byte> data)
    {
        int start = 0;
        while (start < data.Length)
        {
            int end = data[start..].IndexOf((byte)'\n');
            int lineEnd = end < 0 ? data.Length : start + end + 1;
            var line = data[start..lineEnd];
            int i = 0;
            while (i < line.Length && line[i] == (byte)'>') i++;
            if (line[i..].StartsWith("From "u8)) output.WriteByte((byte)'>');
            output.Write(line);
            start = lineEnd;
        }
        if (data.Length > 0 && data[^1] != (byte)'\n') output.WriteByte((byte)'\n');
    }

    public void EndFolder()
    {
        _out?.Dispose();
        _out = null;
    }

    public void Dispose() => EndFolder();
}
