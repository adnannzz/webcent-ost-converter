using OstConverter.Core.Pff;

namespace OstConverter.Core.Export;

/// <summary>One .eml file per message: {out}/{folder}/00001 Subject.eml</summary>
public sealed class EmlExporter : IExporter
{
    readonly string _root;
    string _dir;

    public EmlExporter(string outputDir) { _root = outputDir; _dir = outputDir; }

    public void BeginFolder(string folderPath)
    {
        _dir = Path.Combine(_root, FileNames.SafePath(folderPath));
    }

    public void Write(Message message, int index)
    {
        Directory.CreateDirectory(_dir);
        var mime = MimeBuilder.Build(message);
        var path = FileNames.Unique(Path.Combine(_dir, $"{index:D5} {FileNames.Safe(message.Subject, 60, "(no subject)")}.eml"));
        using (var fs = File.Create(path)) mime.WriteTo(fs);
        if (MimeBuilder.MessageDate(message) is { } d && d.Year > 1970)
            File.SetLastWriteTimeUtc(path, d);
    }

    public void EndFolder() { }
    public void Dispose() { }
}
