using OstConverter.Core.Export.Cfb;
using OstConverter.Core.Pff;

namespace OstConverter.Core.Export;

/// <summary>One Outlook .msg file per message: {out}/{folder}/00001 Subject.msg</summary>
public sealed class MsgExporter : IExporter
{
    readonly string _root;
    string _dir;

    public MsgExporter(string outputDir) { _root = outputDir; _dir = outputDir; }

    public void BeginFolder(string folderPath) => _dir = Path.Combine(_root, FileNames.SafePath(folderPath));

    public void Write(Message message, int index)
    {
        // Build fully in memory first, so a failure never leaves a half-written file behind.
        var bytes = CompoundFileWriter.ToBytes(MsgBuilder.Build(message));
        Directory.CreateDirectory(_dir);
        var path = FileNames.Unique(Path.Combine(_dir, $"{index:D5} {FileNames.Safe(message.Subject, 60, "(no subject)")}.msg"));
        File.WriteAllBytes(path, bytes);
        if (MimeBuilder.MessageDate(message) is { } d && d.Year > 1970) File.SetLastWriteTimeUtc(path, d);
    }

    public void EndFolder() { }
    public void Dispose() { }
}
