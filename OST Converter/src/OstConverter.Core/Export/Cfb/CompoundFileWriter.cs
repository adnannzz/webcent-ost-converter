using System.Buffers.Binary;
using System.Text;

namespace OstConverter.Core.Export.Cfb;

/// <summary>A stream inside a compound file (the OLE structured-storage container used by .msg files).</summary>
public sealed class CfbStream(string name, byte[] data)
{
    public string Name { get; } = name;
    public byte[] Data { get; } = data;
}

/// <summary>A storage (folder) inside a compound file. Names are case-insensitive and at most 31 characters.</summary>
public sealed class CfbStorage(string name)
{
    public string Name { get; } = name;
    public Guid Clsid { get; set; }
    public List<CfbStorage> Storages { get; } = [];
    public List<CfbStream> Streams { get; } = [];

    public CfbStorage AddStorage(string childName)
    {
        var s = new CfbStorage(childName);
        Storages.Add(s);
        return s;
    }

    public CfbStream AddStream(string childName, byte[] data)
    {
        var s = new CfbStream(childName, data);
        Streams.Add(s);
        return s;
    }
}

/// <summary>
/// Writes a version-3 (512-byte sector) compound file per [MS-CFB]: header, FAT, DIFAT for large files,
/// mini-stream for small streams, and a properly balanced red-black directory tree.
/// </summary>
public static class CompoundFileWriter
{
    const int SectorSize = 512;
    const int MiniSectorSize = 64;
    const int MiniCutoff = 4096;
    const uint EndOfChain = 0xFFFFFFFE, FatSector = 0xFFFFFFFD, DifatSector = 0xFFFFFFFC, Free = 0xFFFFFFFF, NoStream = 0xFFFFFFFF;

    sealed class Entry
    {
        public string Name = "";
        public byte Type;              // 1 storage, 2 stream, 5 root
        public byte Color = 1;         // 0 red, 1 black
        public uint Left = NoStream, Right = NoStream, Child = NoStream;
        public Guid Clsid;
        public uint StartSector = EndOfChain;
        public long Size;
        public byte[]? Data;           // streams only
        public List<Entry> Children = [];
    }

    public const int MaxNameLength = 31;

    public static void Write(CfbStorage root, Stream output)
    {
        long startPosition = output.CanSeek ? output.Position : 0;
        var entries = new List<Entry>();
        var rootEntry = new Entry { Name = "Root Entry", Type = 5, Clsid = root.Clsid };
        entries.Add(rootEntry);
        AddChildren(root, rootEntry, entries);
        var ids = new Dictionary<Entry, uint>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < entries.Count; i++) ids[entries[i]] = (uint)i;
        foreach (var e in entries) LinkChildren(e, ids);

        // ---- Sector plan ----
        var miniData = new MemoryStream();
        var miniFat = new List<uint>();
        var bigStreams = new List<Entry>();
        foreach (var e in entries.Where(x => x.Type == 2))
        {
            e.Size = e.Data!.Length;
            if (e.Size == 0) continue;
            if (e.Size < MiniCutoff)
            {
                e.StartSector = (uint)miniFat.Count;
                int n = (int)((e.Size + MiniSectorSize - 1) / MiniSectorSize);
                for (int i = 0; i < n; i++) miniFat.Add(i == n - 1 ? EndOfChain : (uint)(miniFat.Count + 1));
                miniData.Write(e.Data!);
                miniData.Write(new byte[n * MiniSectorSize - e.Data!.Length]);
            }
            else bigStreams.Add(e);
        }

        long miniSize = miniData.Length;
        int miniStreamSectors = (int)((miniSize + SectorSize - 1) / SectorSize);
        int miniFatSectors = (miniFat.Count + 127) / 128;
        int dirSectors = (entries.Count + 3) / 4;
        int bigSectors = bigStreams.Sum(e => (int)((e.Size + SectorSize - 1) / SectorSize));
        int dataSectors = bigSectors + miniStreamSectors + miniFatSectors + dirSectors;

        int fatSectors = 0, difatSectors = 0;
        while (true)
        {
            int total = dataSectors + fatSectors + difatSectors;
            int needFat = (total + 127) / 128;
            int needDifat = needFat > 109 ? (needFat - 109 + 126) / 127 : 0;
            if (needFat == fatSectors && needDifat == difatSectors) break;
            fatSectors = needFat; difatSectors = needDifat;
        }
        int totalSectors = dataSectors + fatSectors + difatSectors;

        var fat = new uint[fatSectors * 128];
        Array.Fill(fat, Free);
        uint next = 0;

        uint Chain(int count)
        {
            if (count == 0) return EndOfChain;
            uint first = next;
            for (int i = 0; i < count; i++, next++) fat[next] = i == count - 1 ? EndOfChain : next + 1;
            return first;
        }

        foreach (var e in bigStreams) e.StartSector = Chain((int)((e.Size + SectorSize - 1) / SectorSize));
        rootEntry.StartSector = Chain(miniStreamSectors);
        rootEntry.Size = miniSize;
        uint miniFatStart = Chain(miniFatSectors);
        uint dirStart = Chain(dirSectors);
        uint fatStart = next;
        for (int i = 0; i < fatSectors; i++, next++) fat[next] = FatSector;
        uint difatStart = difatSectors > 0 ? next : EndOfChain;
        for (int i = 0; i < difatSectors; i++, next++) fat[next] = DifatSector;

        // ---- Header ----
        var header = new byte[SectorSize];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), dirStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), MiniCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), miniFatSectors > 0 ? miniFatStart : EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), (uint)miniFatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), difatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72), (uint)difatSectors);
        for (int i = 0; i < 109; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76 + i * 4), i < fatSectors ? fatStart + (uint)i : Free);
        output.Write(header);

        // ---- Sectors, in the order they were allocated ----
        var pad = new byte[SectorSize];
        void WriteSectors(ReadOnlySpan<byte> data)
        {
            output.Write(data);
            int rem = data.Length % SectorSize;
            if (rem != 0) output.Write(pad.AsSpan(0, SectorSize - rem));
        }

        foreach (var e in bigStreams) WriteSectors(e.Data!);
        WriteSectors(miniData.GetBuffer().AsSpan(0, (int)miniSize));

        var miniFatBytes = new byte[miniFatSectors * SectorSize];
        Array.Fill(miniFatBytes, (byte)0xFF);
        for (int i = 0; i < miniFat.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(i * 4), miniFat[i]);
        output.Write(miniFatBytes);

        var dir = new byte[dirSectors * SectorSize];
        for (int i = 0; i < dirSectors * 4; i++)
            WriteDirEntry(dir.AsSpan(i * 128, 128), i < entries.Count ? entries[i] : null);
        output.Write(dir);

        var fatBytes = new byte[fatSectors * SectorSize];
        for (int i = 0; i < fat.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(fatBytes.AsSpan(i * 4), fat[i]);
        output.Write(fatBytes);

        for (int d = 0; d < difatSectors; d++)
        {
            var sec = new byte[SectorSize];
            for (int i = 0; i < 127; i++)
            {
                int fatIndex = 109 + d * 127 + i;
                BinaryPrimitives.WriteUInt32LittleEndian(sec.AsSpan(i * 4), fatIndex < fatSectors ? fatStart + (uint)fatIndex : Free);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(sec.AsSpan(508), d == difatSectors - 1 ? EndOfChain : difatStart + (uint)d + 1);
            output.Write(sec);
        }
        if (output.CanSeek && output.Position - startPosition != (long)(totalSectors + 1) * SectorSize)
            throw new InvalidOperationException("Compound file layout is inconsistent."); // a bug guard: sector count must match the plan
    }

    static void AddChildren(CfbStorage storage, Entry parent, List<Entry> all)
    {
        foreach (var s in storage.Storages)
        {
            Validate(s.Name);
            var e = new Entry { Name = s.Name, Type = 1, Clsid = s.Clsid };
            parent.Children.Add(e);
            all.Add(e);
            AddChildren(s, e, all);
        }
        foreach (var s in storage.Streams)
        {
            Validate(s.Name);
            var e = new Entry { Name = s.Name, Type = 2, Data = s.Data };
            parent.Children.Add(e);
            all.Add(e);
        }
    }

    static void Validate(string name)
    {
        if (name.Length == 0 || name.Length > MaxNameLength) throw new ArgumentException($"Compound file names must be 1-{MaxNameLength} characters: '{name}'.");
    }

    /// <summary>CFB orders siblings by name length first, then by upper-cased UTF-16 code units.</summary>
    static int Compare(Entry a, Entry b)
    {
        if (a.Name.Length != b.Name.Length) return a.Name.Length.CompareTo(b.Name.Length);
        return string.CompareOrdinal(a.Name.ToUpperInvariant(), b.Name.ToUpperInvariant());
    }

    /// <summary>
    /// Arranges one storage's children as a balanced binary tree (median split) and colours it so the red-black rules hold:
    /// when the tree isn't perfectly full, the nodes on its deepest level are red and everything else is black.
    /// </summary>
    static void LinkChildren(Entry parent, Dictionary<Entry, uint> ids)
    {
        var kids = parent.Children;
        if (kids.Count == 0) return;
        kids.Sort(Compare);
        for (int i = 1; i < kids.Count; i++)
            if (Compare(kids[i - 1], kids[i]) == 0)
                throw new ArgumentException($"Duplicate name '{kids[i].Name}' in a compound file storage.");

        int maxDepth = Depth(0, kids.Count - 1);
        bool perfect = ((kids.Count + 1) & kids.Count) == 0; // count + 1 is a power of two
        parent.Child = Link(kids, 0, kids.Count - 1, 0, maxDepth, perfect, ids);
    }

    static int Depth(int lo, int hi) => lo > hi ? -1 : 1 + Math.Max(Depth(lo, (lo + hi) / 2 - 1), Depth((lo + hi) / 2 + 1, hi));

    static uint Link(List<Entry> kids, int lo, int hi, int depth, int maxDepth, bool perfect, Dictionary<Entry, uint> ids)
    {
        if (lo > hi) return NoStream;
        int mid = (lo + hi) / 2;
        var e = kids[mid];
        e.Left = Link(kids, lo, mid - 1, depth + 1, maxDepth, perfect, ids);
        e.Right = Link(kids, mid + 1, hi, depth + 1, maxDepth, perfect, ids);
        e.Color = (byte)(!perfect && depth == maxDepth ? 0 : 1);
        return ids[e];
    }

    static void WriteDirEntry(Span<byte> d, Entry? e)
    {
        if (e is null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d[68..], NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(d[72..], NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(d[76..], NoStream);
            return;
        }
        var name = Encoding.Unicode.GetBytes(e.Name);
        name.CopyTo(d);
        BinaryPrimitives.WriteUInt16LittleEndian(d[64..], (ushort)(name.Length + 2));
        d[66] = e.Type;
        d[67] = e.Color;
        BinaryPrimitives.WriteUInt32LittleEndian(d[68..], e.Left);
        BinaryPrimitives.WriteUInt32LittleEndian(d[72..], e.Right);
        BinaryPrimitives.WriteUInt32LittleEndian(d[76..], e.Child);
        e.Clsid.TryWriteBytes(d[80..96]);
        BinaryPrimitives.WriteUInt32LittleEndian(d[116..], e.StartSector);
        BinaryPrimitives.WriteUInt64LittleEndian(d[120..], (ulong)e.Size);
    }

    public static byte[] ToBytes(CfbStorage root)
    {
        using var ms = new MemoryStream();
        Write(root, ms);
        return ms.ToArray();
    }
}
