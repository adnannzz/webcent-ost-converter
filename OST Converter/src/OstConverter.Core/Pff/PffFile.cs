using System.Buffers.Binary;
using System.IO.Compression;

namespace OstConverter.Core.Pff;

public sealed class PffFormatException(string message) : Exception(message);

/// <summary>Block bytes of one node's data tree. Heap pages map 1:1 onto segments.</summary>
public sealed class NodeData(byte[][] segments)
{
    public byte[][] Segments { get; } = segments;
    public long Length => Segments.Sum(s => (long)s.Length);
    public static readonly NodeData Empty = new([]);

    public byte[] ToArray()
    {
        if (Segments.Length == 1) return Segments[0];
        var all = new byte[Length];
        int o = 0;
        foreach (var s in Segments) { s.CopyTo(all, o); o += s.Length; }
        return all;
    }
}

public readonly record struct SubnodeEntry(ulong BidData, ulong BidSub);

/// <summary>A node (NBT entry) with its data tree and subnodes resolved lazily.</summary>
public sealed class PffNode
{
    readonly PffFile _file;
    NodeData? _data;
    Dictionary<ulong, SubnodeEntry>? _subnodes;

    internal PffNode(PffFile file, ulong nid, ulong bidData, ulong bidSub, ulong parentNid)
    {
        _file = file; Nid = nid; BidData = bidData; BidSub = bidSub; ParentNid = parentNid;
    }

    public ulong Nid { get; }
    public ulong BidData { get; }
    public ulong BidSub { get; }
    public ulong ParentNid { get; }
    public NodeData Data => _data ??= _file.ReadDataTree(BidData);
    internal int HeapPagesPerBlock => _file.Is4K ? 8 : 1;
    public IReadOnlyDictionary<ulong, SubnodeEntry> Subnodes => _subnodes ??= _file.ReadSubnodes(BidSub);

    public PffNode? OpenSubnode(ulong nid) =>
        Subnodes.TryGetValue(nid, out var e) ? new PffNode(_file, nid, e.BidData, e.BidSub, Nid) : null;
}

/// <summary>
/// Read-only reader for the NDB layer of PST/OST files. Supports the 64-bit "Unicode" layout (ver 21/23,
/// 512-byte pages) and the 4K-page layout (ver 36/37) that current Outlook uses for OST files.
/// </summary>
public sealed class PffFile : IDisposable
{
    readonly FileStream _fs;
    readonly object _lock = new();
    readonly Dictionary<long, Page> _pages = new();
    readonly long _nbtRoot, _bbtRoot;

    public bool IsOst { get; }
    public bool Is4K { get; }
    public byte CryptMethod { get; }
    public int Version { get; }
    public string Path { get; }
    /// <summary>Largest data a block can hold; table rows are packed per block of this size.</summary>
    public int MaxBlockData => Is4K ? 65512 : 8176;

    int PageSize => Is4K ? 4096 : 512;
    int PageEntryArea => Is4K ? 4056 : 488;
    int BlockFooter => Is4K ? 24 : 16;
    int BlockAlign => Is4K ? 512 : 64;

    sealed record Page(byte[] Buf, int CEnt, int CbEnt, int Level);

    public PffFile(string path)
    {
        Path = path;
        try
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }
        catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
        {
            throw new IOException("Cannot open the file. If it belongs to Outlook, close Outlook or copy the file first.", e);
        }

        var h = new byte[564];
        if (_fs.Read(h, 0, h.Length) != h.Length || BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x4E444221)
            throw new PffFormatException("Not an Outlook PST/OST file (bad signature).");

        Version = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(10));
        IsOst = h[8] == (byte)'S' && h[9] == (byte)'O';
        if (Version is 14 or 15)
            throw new NotSupportedException("Old ANSI (Outlook 97-2002) files are not supported.");
        if (Version is not (21 or 23 or 36 or 37))
            throw new PffFormatException($"Unknown file version {Version}.");
        Is4K = Version >= 36;
        CryptMethod = h[513];
        if (CryptMethod > 2) throw new NotSupportedException($"Unsupported encryption method {CryptMethod}.");

        _nbtRoot = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(224));
        _bbtRoot = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(240));
    }

    public void Dispose() => _fs.Dispose();

    byte[] ReadAt(long offset, int count)
    {
        var buf = new byte[count];
        lock (_lock)
        {
            _fs.Seek(offset, SeekOrigin.Begin);
            _fs.ReadExactly(buf);
        }
        return buf;
    }

    Page ReadPage(long ib)
    {
        lock (_lock)
            if (_pages.TryGetValue(ib, out var cached)) return cached;
        var buf = ReadAt(ib, PageSize);
        Page p;
        if (Is4K)
            p = new Page(buf, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(4056)), buf[4060], buf[4061]);
        else
            p = new Page(buf, buf[488], buf[490], buf[491]);
        if (p.CbEnt == 0 || p.CEnt * p.CbEnt > PageEntryArea)
            throw new PffFormatException($"Corrupt B-tree page at offset {ib}.");
        lock (_lock)
        {
            if (_pages.Count > 4096) _pages.Clear();
            _pages[ib] = p;
        }
        return p;
    }

    byte[]? FindLeaf(long rootIb, ulong key)
    {
        var p = ReadPage(rootIb);
        for (int depth = 0; depth < 12; depth++)
        {
            if (p.Level == 0)
            {
                for (int i = 0; i < p.CEnt; i++)
                    if (BinaryPrimitives.ReadUInt64LittleEndian(p.Buf.AsSpan(i * p.CbEnt)) == key)
                        return p.Buf.AsSpan(i * p.CbEnt, p.CbEnt).ToArray();
                return null;
            }
            int pick = -1;
            for (int i = 0; i < p.CEnt; i++)
            {
                if (key >= BinaryPrimitives.ReadUInt64LittleEndian(p.Buf.AsSpan(i * p.CbEnt))) pick = i;
                else break;
            }
            if (pick < 0) return null;
            p = ReadPage((long)BinaryPrimitives.ReadUInt64LittleEndian(p.Buf.AsSpan(pick * p.CbEnt + 16)));
        }
        throw new PffFormatException("B-tree too deep.");
    }

    IEnumerable<byte[]> EnumerateLeaves(long ib)
    {
        var p = ReadPage(ib);
        for (int i = 0; i < p.CEnt; i++)
        {
            if (p.Level == 0) yield return p.Buf.AsSpan(i * p.CbEnt, p.CbEnt).ToArray();
            else
                foreach (var e in EnumerateLeaves((long)BinaryPrimitives.ReadUInt64LittleEndian(p.Buf.AsSpan(i * p.CbEnt + 16))))
                    yield return e;
        }
    }

    public PffNode? OpenNode(ulong nid)
    {
        var e = FindLeaf(_nbtRoot, nid);
        if (e is null) return null;
        return new PffNode(this, nid,
            BinaryPrimitives.ReadUInt64LittleEndian(e.AsSpan(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(e.AsSpan(16)),
            BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(24)));
    }

    public IEnumerable<ulong> EnumerateNodeIds() =>
        EnumerateLeaves(_nbtRoot).Select(e => BinaryPrimitives.ReadUInt64LittleEndian(e));

    public long CountBlocks() => EnumerateLeaves(_bbtRoot).LongCount();

    /// <summary>Linear scan of every block-tree leaf (diagnostics: cross-checks the keyed lookup).</summary>
    public bool ContainsBlockByScan(ulong bid) =>
        EnumerateLeaves(_bbtRoot).Any(e => BinaryPrimitives.ReadUInt64LittleEndian(e) == bid);

    /// <summary>Reads one block: decompresses (4K layout) and decrypts data blocks.</summary>
    byte[] ReadBlock(ulong bid)
    {
        var e = FindLeaf(_bbtRoot, bid) ?? throw new PffFormatException($"Block {bid:X} not found in block B-tree.");
        long ib = (long)BinaryPrimitives.ReadUInt64LittleEndian(e.AsSpan(8));
        int cb = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(16));
        int total = (cb + BlockFooter + BlockAlign - 1) / BlockAlign * BlockAlign;
        var raw = ReadAt(ib, total);

        var data = raw.AsSpan(0, cb).ToArray();
        if (Is4K)
        {
            int uncompressed = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(total - BlockFooter + 18));
            if (uncompressed != 0 && uncompressed != cb)
            {
                var outBuf = new byte[uncompressed];
                using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                try { z.ReadExactly(outBuf); }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                {
                    throw new PffFormatException($"Cannot decompress block {bid:X}: {ex.Message}");
                }
                data = outBuf;
            }
        }
        if ((bid & 2) == 0 && CryptMethod != 0) Decrypt(data, (uint)bid);
        return data;
    }

    void Decrypt(byte[] data, uint key)
    {
        if (CryptMethod == 1)
        {
            for (int i = 0; i < data.Length; i++) data[i] = CryptTables.Compressible[data[i]];
            return;
        }
        ushort salt = (ushort)((key >> 16) ^ (key & 0xFFFF));
        for (int i = 0; i < data.Length; i++, salt++)
        {
            byte lo = (byte)salt, hi = (byte)(salt >> 8);
            byte idx = data[i];
            idx += lo;
            idx = CryptTables.High1[idx];
            idx += hi;
            idx = CryptTables.High2[idx];
            idx -= hi;
            idx = CryptTables.Compressible[idx];
            idx -= lo;
            data[i] = idx;
        }
    }

    /// <summary>Resolves a data tree (single block, XBLOCK or XXBLOCK) into ordered data segments.</summary>
    internal NodeData ReadDataTree(ulong bid)
    {
        if (bid == 0) return NodeData.Empty;
        var list = new List<byte[]>();
        CollectData(bid, list, 0);
        return new NodeData([.. list]);
    }

    void CollectData(ulong bid, List<byte[]> list, int depth)
    {
        if ((bid & 2) == 0) { list.Add(ReadBlock(bid)); return; }
        if (depth > 3) throw new PffFormatException("Data tree too deep.");
        var b = ReadBlock(bid);
        if (b.Length < 8 || b[0] != 1) throw new PffFormatException($"Block {bid:X} is not an XBLOCK.");
        int cEnt = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2));
        if (8 + cEnt * 8 > b.Length) throw new PffFormatException("Corrupt XBLOCK.");
        for (int i = 0; i < cEnt; i++)
            CollectData(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8 + i * 8)), list, depth + 1);
    }

    internal Dictionary<ulong, SubnodeEntry> ReadSubnodes(ulong bidSub)
    {
        var map = new Dictionary<ulong, SubnodeEntry>();
        if (bidSub != 0) CollectSubnodes(bidSub, map, 0);
        return map;
    }

    void CollectSubnodes(ulong bid, Dictionary<ulong, SubnodeEntry> map, int depth)
    {
        if (depth > 4) throw new PffFormatException("Subnode tree too deep.");
        var b = ReadBlock(bid);
        if (b.Length < 8 || b[0] != 2) throw new PffFormatException($"Block {bid:X} is not a subnode block.");
        int level = b[1];
        int cEnt = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2));
        int size = level == 0 ? 24 : 16;
        if (8 + cEnt * size > b.Length) throw new PffFormatException("Corrupt subnode block.");
        for (int i = 0; i < cEnt; i++)
        {
            var s = b.AsSpan(8 + i * size);
            ulong nid = BinaryPrimitives.ReadUInt64LittleEndian(s) & 0xFFFFFFFF;
            if (level == 0)
                map[nid] = new SubnodeEntry(BinaryPrimitives.ReadUInt64LittleEndian(s[8..]), BinaryPrimitives.ReadUInt64LittleEndian(s[16..]));
            else
                CollectSubnodes(BinaryPrimitives.ReadUInt64LittleEndian(s[8..]), map, depth + 1);
        }
    }
}
