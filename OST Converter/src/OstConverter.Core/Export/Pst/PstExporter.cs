using OstConverter.Core.Pff;

namespace OstConverter.Core.Export.Pst;

/// <summary>
/// Writes the selected folders and items into a new Unicode PST that opens in Outlook, keeping the folder tree, container
/// classes and named properties. When the file reaches the size limit the exporter finishes it and carries on in a
/// numbered continuation file, each one a complete, self-contained PST.
/// </summary>
public sealed class PstExporter : IFolderAwareExporter
{
    /// <summary>Outlook's default limit for a Unicode PST is 50 GB; stopping well short leaves room for tables and warnings thresholds.</summary>
    public const long DefaultSplitBytes = 40L << 30;

    readonly string _dir, _baseName;
    readonly IReadOnlyList<PstProp>? _nameMap;
    readonly long _splitBytes;
    readonly List<string> _files = [];

    PstBuilder? _builder;
    string _tempPath = "", _finalPath = "";
    int _part, _generation;
    string _folderPath = "";
    string? _containerClass;
    uint _folderNid;
    int _folderGeneration = -1;

    /// <param name="baseName">File name (without extension) of the first PST; later parts get "-part2", "-part3", ...</param>
    /// <param name="nameMap">The source's name-to-id map, copied so named properties keep their ids.</param>
    public PstExporter(string outputDir, string baseName = "Mailbox", IReadOnlyList<PstProp>? nameMap = null, long splitBytes = DefaultSplitBytes)
    {
        _dir = outputDir;
        _baseName = FileNames.Safe(baseName, 100, "Mailbox");
        _nameMap = nameMap;
        _splitBytes = Math.Max(splitBytes, 1 << 20);
    }

    /// <summary>Completed PST files, in order.</summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>The name-to-id map of <paramref name="store"/> in the form the writer takes.</summary>
    public static IReadOnlyList<PstProp> NameMapOf(PstStore store) =>
        store.GetNameMapProperties()
            .Where(p => (p.Type == PropType.Int32 && p.Value.Length >= 4) || p.Type == PropType.Binary)
            .Select(p => new PstProp(p.Id, p.Type, p.Type == PropType.Int32 ? p.Value[..4] : p.Value))
            .ToList();

    public void BeginFolder(string folderPath) => Begin(folderPath, null);

    public void BeginFolder(string folderPath, Folder folder) => Begin(folderPath, folder.ContainerClass);

    void Begin(string folderPath, string? containerClass)
    {
        _folderPath = folderPath;
        _containerClass = containerClass;
        EnsureBuilder();
        _folderNid = _builder!.EnsureFolder(folderPath, containerClass);   // empty folders must exist too
        _folderGeneration = _generation;
    }

    public void Write(Message message, int index)
    {
        EnsureBuilder();
        if (_builder!.UsedBytes >= _splitBytes) Finish();
        EnsureBuilder();
        if (_folderGeneration != _generation)
        {
            _folderNid = _builder!.EnsureFolder(_folderPath, _containerClass);
            _folderGeneration = _generation;
        }
        // Mapping happens before anything is written, so an unreadable item leaves the file untouched.
        _builder!.AddMessage(_folderNid, PstMessageMapper.Map(message));
    }

    public void EndFolder() { }

    void EnsureBuilder()
    {
        if (_builder is not null) return;
        _part++;
        Directory.CreateDirectory(_dir);
        _finalPath = FileNames.Unique(Path.Combine(_dir, _part == 1 ? _baseName + ".pst" : $"{_baseName}-part{_part}.pst"));
        _tempPath = _finalPath + ".partial";
        _builder = new PstBuilder(_tempPath, _part == 1 ? _baseName : $"{_baseName} (part {_part})", _nameMap);
    }

    void Finish()
    {
        if (_builder is null) return;
        try { _builder.Complete(); }
        finally { _builder.Dispose(); }
        _builder = null;
        File.Move(_tempPath, _finalPath, overwrite: true);
        _files.Add(_finalPath);
        _generation++;
    }

    public void Dispose() => Finish();
}
