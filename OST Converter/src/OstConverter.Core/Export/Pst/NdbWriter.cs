using System.Buffers.Binary;
using OstConverter.Core.Pff;

namespace OstConverter.Core.Export.Pst;

public static class NidType
{
    public const uint Hid = 0x00, Internal = 0x01, NormalFolder = 0x02, SearchFolder = 0x03, NormalMessage = 0x04, Attachment = 0x05,
        SearchUpdateQueue = 0x06, SearchCriteria = 0x07, AssocMessage = 0x08, ReceiveFolderTable = 0x0B, OutgoingQueueTable = 0x0C,
        HierarchyTable = 0x0D, ContentsTable = 0x0E, AssocContentsTable = 0x0F, SearchContentsTable = 0x10, AttachmentTable = 0x11,
        RecipientTable = 0x12, Ltp = 0x1F;

    public static uint Make(uint type, uint index) => (index << 5) | type;
}

/// <summary>A subnode as listed in a subnode block: its id and the blocks that hold its data and its own subnodes.</summary>
public readonly record struct SubnodeRef(uint Nid, ulong BidData, ulong BidSub);

/// <summary>
/// Writes the NDB layer of a Unicode PST ([MS-PST] 2.2): blocks, data trees, subnode trees, the node and block B-trees,
/// allocation maps and the header. Blocks are appended as they are produced; the B-tree pages, maps and header are
/// written when the file is completed. The layout follows what Outlook itself writes (version 23, 512-byte pages,
/// 64-byte block alignment, "compressible" encryption, one whole allocation-map range per file).
/// </summary>
public sealed class NdbWriter : IDisposable
{
    public const int MaxBlockData = 8176;
    const int BlockTrailerSize = 16, PageSize = 512;
    const long DListOffset = 0x4200, FirstMapOffset = 0x4400, MapSpan = 0x3E000, FirstDataOffset = 0x4800;
    const int XBlockMaxEntries = (MaxBlockData - 8) / 8;
    const int SubnodeMaxEntries = (MaxBlockData - 8) / 24, SubnodeIndexMaxEntries = (MaxBlockData - 8) / 16;

    readonly FileStream _fs;
    readonly bool _encrypt;
    readonly List<byte[]> _maps = [];                       // one allocation bitmap per map range (1 bit per 64 bytes)
    readonly List<BbtEntry> _bbt = [];
    readonly List<NbtEntry> _nbt = [];
    long _cursor = FirstDataOffset;
    ulong _nextBlockIndex = 1;                              // bid = index << 2 (+2 for internal blocks)
    ulong _nextPageBid = 0x20;
    bool _completed;

    readonly record struct BbtEntry(ulong Bid, long Ib, ushort Cb);
    readonly record struct NbtEntry(uint Nid, ulong BidData, ulong BidSub, uint Parent);

    public NdbWriter(string path, bool encrypt = true)
    {
        _encrypt = encrypt;
        _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
    }

    /// <summary>Bytes of file space used so far (for splitting very large conversions across files).</summary>
    public long UsedBytes => _cursor;
    public int NodeCount => _nbt.Count;

    // ---- allocation ----------------------------------------------------------------------------------------------

    static long MapStart(long k) => FirstMapOffset + k * MapSpan;
    /// <summary>End of the system pages at the start of map range k: the map itself, plus an obsolete page map every eighth range.</summary>
    static long ReservedEnd(long k) => MapStart(k) + (k % 8 == 0 ? 1024 : 512);

    long Allocate(int size, int align)
    {
        long start = (_cursor + align - 1) / align * align;
        while (true)
        {
            long k = (start - FirstMapOffset) / MapSpan;
            if (start < ReservedEnd(k)) start = ReservedEnd(k);
            if (start + size <= MapStart(k + 1)) break;
            start = ReservedEnd(k + 1);                     // would run into the next range's system pages: skip them
        }
        _cursor = start + size;
        MarkAllocated(start, size);
        return start;
    }

    void MarkAllocated(long ib, int size)
    {
        long k = (ib - FirstMapOffset) / MapSpan;
        var map = MapFor(k);
        long first = (ib - MapStart(k)) / 64, count = (size + 63) / 64;
        for (long b = first; b < first + count; b++) map[b >> 3] |= (byte)(0x80 >> (int)(b & 7));
    }

    byte[] MapFor(long k)
    {
        while (_maps.Count <= k) _maps.Add(new byte[496]);
        return _maps[(int)k];
    }

    // ---- blocks --------------------------------------------------------------------------------------------------

    ulong NewBid(bool internalBlock) => (_nextBlockIndex++ << 2) | (internalBlock ? 2UL : 0UL);

    ulong WriteBlock(ReadOnlySpan<byte> data, bool internalBlock)
    {
        if (data.Length > MaxBlockData) throw new ArgumentException("Block data is too large.", nameof(data));
        ulong bid = NewBid(internalBlock);
        int total = (data.Length + BlockTrailerSize + 63) / 64 * 64;
        long ib = Allocate(total, 64);

        var buf = new byte[total];
        data.CopyTo(buf);
        if (_encrypt && !internalBlock) PstCrypt.Encrypt(buf.AsSpan(0, data.Length));
        var t = buf.AsSpan(total - BlockTrailerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(t, (ushort)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(t[2..], Sig(ib, bid));
        BinaryPrimitives.WriteUInt32LittleEndian(t[4..], PstCrc.Compute(buf.AsSpan(0, data.Length)));
        BinaryPrimitives.WriteUInt64LittleEndian(t[8..], bid);

        Put(ib, buf);
        _bbt.Add(new BbtEntry(bid, ib, (ushort)data.Length));
        return bid;
    }

    /// <summary>Page/block signature: the low and high halves of the low 32 bits of (offset XOR id), XORed together.</summary>
    internal static ushort Sig(long ib, ulong bid)
    {
        uint x = (uint)ib ^ (uint)bid;
        return (ushort)((x & 0xFFFF) ^ (x >> 16));
    }

    void Put(long ib, byte[] buf)
    {
        if (_fs.Position != ib) _fs.Position = ib;
        _fs.Write(buf);
    }

    /// <summary>
    /// Stores pre-split chunks (each at most <see cref="MaxBlockData"/> bytes) as one data tree and returns the bid that
    /// identifies it: a plain block for one chunk, otherwise an XBLOCK, or an XXBLOCK for more than 1021 chunks.
    /// Heap pages and table row blocks need this form because their chunk boundaries are meaningful.
    /// </summary>
    public ulong WriteChunks(IReadOnlyList<byte[]> chunks)
    {
        if (chunks.Count == 0) return 0;
        if (chunks.Count == 1) return WriteBlock(chunks[0], false);

        var leafBids = new List<(ulong Bid, long Bytes)>(chunks.Count);
        foreach (var c in chunks) leafBids.Add((WriteBlock(c, false), c.Length));

        if (leafBids.Count <= XBlockMaxEntries) return WriteIndexBlock(1, leafBids);

        var level2 = new List<(ulong Bid, long Bytes)>();
        for (int i = 0; i < leafBids.Count; i += XBlockMaxEntries)
        {
            var part = leafBids.GetRange(i, Math.Min(XBlockMaxEntries, leafBids.Count - i));
            level2.Add((WriteIndexBlock(1, part), part.Sum(p => p.Bytes)));
        }
        if (level2.Count > XBlockMaxEntries) throw new NotSupportedException("A single value is too large for the PST format.");
        return WriteIndexBlock(2, level2);
    }

    ulong WriteIndexBlock(byte level, List<(ulong Bid, long Bytes)> children)
    {
        var b = new byte[8 + 8 * children.Count];
        b[0] = 0x01;                                        // XBLOCK / XXBLOCK
        b[1] = level;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)children.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)children.Sum(c => c.Bytes));
        for (int i = 0; i < children.Count; i++) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8 + i * 8), children[i].Bid);
        return WriteBlock(b, true);
    }

    /// <summary>Stores arbitrary-length data as a data tree; returns 0 for empty data (a node without data has no block).</summary>
    public ulong WriteData(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        if (data.Length <= MaxBlockData) return WriteBlock(data, false);
        var chunks = new List<byte[]>();
        for (int o = 0; o < data.Length; o += MaxBlockData) chunks.Add(data.Slice(o, Math.Min(MaxBlockData, data.Length - o)).ToArray());
        return WriteChunks(chunks);
    }

    /// <summary>Stores a subnode tree (SLBLOCK, or SIBLOCK over several SLBLOCKs) and returns its bid; 0 when there are no subnodes.</summary>
    public ulong WriteSubnodes(IEnumerable<SubnodeRef> subnodes)
    {
        var list = subnodes.OrderBy(s => s.Nid).ToList();
        if (list.Count == 0) return 0;
        for (int i = 1; i < list.Count; i++)
            if (list[i].Nid == list[i - 1].Nid) throw new InvalidOperationException($"Duplicate subnode id 0x{list[i].Nid:X}.");

        var leaves = new List<(uint FirstNid, ulong Bid)>();
        for (int i = 0; i < list.Count; i += SubnodeMaxEntries)
        {
            var part = list.GetRange(i, Math.Min(SubnodeMaxEntries, list.Count - i));
            var b = new byte[8 + 24 * part.Count];
            b[0] = 0x02;
            b[1] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)part.Count);
            for (int j = 0; j < part.Count; j++)
            {
                var s = b.AsSpan(8 + j * 24);
                BinaryPrimitives.WriteUInt64LittleEndian(s, part[j].Nid);
                BinaryPrimitives.WriteUInt64LittleEndian(s[8..], part[j].BidData);
                BinaryPrimitives.WriteUInt64LittleEndian(s[16..], part[j].BidSub);
            }
            leaves.Add((part[0].Nid, WriteBlock(b, true)));
        }
        if (leaves.Count == 1) return leaves[0].Bid;
        if (leaves.Count > SubnodeIndexMaxEntries) throw new NotSupportedException("Too many subnodes for one node.");

        var index = new byte[8 + 16 * leaves.Count];
        index[0] = 0x02;
        index[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(index.AsSpan(2), (ushort)leaves.Count);
        for (int j = 0; j < leaves.Count; j++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(8 + j * 16), leaves[j].FirstNid);
            BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(16 + j * 16), leaves[j].Bid);
        }
        return WriteBlock(index, true);
    }

    public void AddNode(uint nid, ulong bidData, ulong bidSub, uint parentNid) => _nbt.Add(new NbtEntry(nid, bidData, bidSub, parentNid));

    // ---- completion ------------------------------------------------------------------------------------------------

    /// <param name="nextNodeIndex">Per node type, the next unused index (header field rgnid).</param>
    public void Complete(uint[] nextNodeIndex)
    {
        if (_completed) return;
        _completed = true;
        if (_nbt.Count == 0) throw new InvalidOperationException("A PST needs at least one node.");

        _nbt.Sort((a, b) => a.Nid.CompareTo(b.Nid));
        _bbt.Sort((a, b) => a.Bid.CompareTo(b.Bid));
        for (int i = 1; i < _nbt.Count; i++)
            if (_nbt[i].Nid == _nbt[i - 1].Nid) throw new InvalidOperationException($"Duplicate node id 0x{_nbt[i].Nid:X}.");

        var nbtRoot = WriteTree(_nbt, 32, 15, 0x81, (e, s) =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(s, e.Nid);
            BinaryPrimitives.WriteUInt64LittleEndian(s[8..], e.BidData);
            BinaryPrimitives.WriteUInt64LittleEndian(s[16..], e.BidSub);
            BinaryPrimitives.WriteUInt32LittleEndian(s[24..], e.Parent);
        }, e => e.Nid);

        var bbtRoot = WriteTree(_bbt, 24, 20, 0x80, (e, s) =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(s, e.Bid);
            BinaryPrimitives.WriteUInt64LittleEndian(s[8..], (ulong)e.Ib);
            BinaryPrimitives.WriteUInt16LittleEndian(s[16..], e.Cb);
            // Reference count. Outlook writes 2 for ordinary blocks (the owning node or parent block plus its own
            // bookkeeping reference) and rejects a file where a block it later releases is recorded with 1.
            BinaryPrimitives.WriteUInt16LittleEndian(s[18..], 2);
        }, e => e.Bid);

        long lastMap = Math.Max(0, (_cursor - 1 - FirstMapOffset) / MapSpan);
        long eof = MapStart(lastMap + 1);
        long freeBytes = WriteMaps(lastMap);
        WriteDList();

        _fs.SetLength(eof);
        _fs.Position = 0;
        _fs.Write(BuildHeader(nextNodeIndex, eof, MapStart(lastMap), freeBytes, nbtRoot, bbtRoot));
        _fs.Flush(true);
    }

    (ulong Bid, long Ib) WriteTree<T>(List<T> items, int entrySize, int maxEntries, byte ptype, EncodeEntry<T> encode, Func<T, ulong> keyOf)
    {
        // Leaf level, then index levels of (key, bid, ib) entries until a single root page remains.
        var level = new List<(ulong Key, ulong Bid, long Ib)>();
        for (int i = 0; i < items.Count; i += maxEntries)
        {
            int n = Math.Min(maxEntries, items.Count - i);
            var page = new byte[PageSize];
            for (int j = 0; j < n; j++) encode(items[i + j], page.AsSpan(j * entrySize, entrySize));
            var (bid, ib) = WritePage(page, n, maxEntries, entrySize, 0, ptype);
            level.Add((keyOf(items[i]), bid, ib));
        }
        byte lvl = 0;
        while (level.Count > 1)
        {
            lvl++;
            var next = new List<(ulong Key, ulong Bid, long Ib)>();
            for (int i = 0; i < level.Count; i += 20)
            {
                int n = Math.Min(20, level.Count - i);
                var page = new byte[PageSize];
                for (int j = 0; j < n; j++)
                {
                    var s = page.AsSpan(j * 24, 24);
                    BinaryPrimitives.WriteUInt64LittleEndian(s, level[i + j].Key);
                    BinaryPrimitives.WriteUInt64LittleEndian(s[8..], level[i + j].Bid);
                    BinaryPrimitives.WriteUInt64LittleEndian(s[16..], (ulong)level[i + j].Ib);
                }
                var (bid, ib) = WritePage(page, n, 20, 24, lvl, ptype);
                next.Add((level[i].Key, bid, ib));
            }
            level = next;
        }
        return (level[0].Bid, level[0].Ib);
    }

    delegate void EncodeEntry<T>(T item, Span<byte> dest);

    (ulong Bid, long Ib) WritePage(byte[] page, int count, int maxEntries, int entrySize, byte level, byte ptype)
    {
        ulong bid = _nextPageBid++;
        long ib = Allocate(PageSize, PageSize);
        page[488] = (byte)count;
        page[489] = (byte)maxEntries;
        page[490] = (byte)entrySize;
        page[491] = level;
        WritePageTrailer(page, ptype, ib, bid);
        Put(ib, page);
        return (bid, ib);
    }

    static void WritePageTrailer(byte[] page, byte ptype, long ib, ulong bid, bool zeroSig = false)
    {
        var t = page.AsSpan(496);
        t[0] = ptype;
        t[1] = ptype;
        BinaryPrimitives.WriteUInt16LittleEndian(t[2..], zeroSig ? (ushort)0 : Sig(ib, bid));
        BinaryPrimitives.WriteUInt32LittleEndian(t[4..], PstCrc.Compute(page.AsSpan(0, 496)));
        BinaryPrimitives.WriteUInt64LittleEndian(t[8..], bid);
    }

    /// <summary>Writes every allocation map (and the obsolete page maps) and returns the number of free bytes in all ranges.</summary>
    long WriteMaps(long lastMap)
    {
        long free = 0;
        for (long k = 0; k <= lastMap; k++)
        {
            var bits = MapFor(k);
            // The system pages at the start of the range occupy space too.
            MarkSystem(bits, k % 8 == 0 ? 16 : 8);
            for (int i = 0; i < bits.Length; i++) free += (8 - System.Numerics.BitOperations.PopCount(bits[i])) * 64L;

            var page = new byte[PageSize];
            bits.CopyTo(page, 0);
            WritePageTrailer(page, 0x84, MapStart(k), (ulong)MapStart(k), zeroSig: true);
            Put(MapStart(k), page);

            if (k % 8 == 0)
            {
                var pmap = new byte[PageSize];
                Array.Fill(pmap, (byte)0xFF, 0, 496);       // obsolete: every page reported as allocated
                WritePageTrailer(pmap, 0x83, MapStart(k) + 512, (ulong)(MapStart(k) + 512), zeroSig: true);
                Put(MapStart(k) + 512, pmap);
            }
        }
        return free;
    }

    static void MarkSystem(byte[] bits, int bitCount)
    {
        for (int b = 0; b < bitCount; b++) bits[b >> 3] |= (byte)(0x80 >> (b & 7));
    }

    void WriteDList()
    {
        var page = new byte[PageSize];
        WritePageTrailer(page, 0x86, DListOffset, _nextPageBid);
        Put(DListOffset, page);
    }

    byte[] BuildHeader(uint[] nextNodeIndex, long eof, long lastMapOffset, long freeBytes, (ulong Bid, long Ib) nbt, (ulong Bid, long Ib) bbt)
    {
        var h = new byte[564];
        var s = h.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x4E444221);        // "!BDN"
        h[8] = (byte)'S'; h[9] = (byte)'M';                              // wMagicClient
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], 23);           // Unicode PST
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], 19);
        h[14] = 1; h[15] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(s[32..], _nextPageBid);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 32);           // dwUnique
        for (int i = 0; i < 32; i++) BinaryPrimitives.WriteUInt32LittleEndian(s[(44 + i * 4)..], nextNodeIndex[i]);
        BinaryPrimitives.WriteUInt64LittleEndian(s[184..], (ulong)eof);
        BinaryPrimitives.WriteUInt64LittleEndian(s[192..], (ulong)lastMapOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(s[200..], (ulong)freeBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(s[208..], 0);           // cbPMapFree: page maps are obsolete
        BinaryPrimitives.WriteUInt64LittleEndian(s[216..], nbt.Bid);
        BinaryPrimitives.WriteUInt64LittleEndian(s[224..], (ulong)nbt.Ib);
        BinaryPrimitives.WriteUInt64LittleEndian(s[232..], bbt.Bid);
        BinaryPrimitives.WriteUInt64LittleEndian(s[240..], (ulong)bbt.Ib);
        h[248] = 2;                                                      // fAMapValid: VALID_AMAP2
        Array.Fill(h, (byte)0xFF, 256, 128);                             // rgbFM, rgbFP: obsolete, all ones
        Array.Fill(h, (byte)0xFF, 384, 128);
        h[512] = 0x80;                                                   // bSentinel
        h[513] = (byte)(_encrypt ? 1 : 0);                               // bCryptMethod
        BinaryPrimitives.WriteUInt64LittleEndian(s[516..], _nextBlockIndex << 2);
        BinaryPrimitives.WriteUInt32LittleEndian(s[524..], PstCrc.Compute(s.Slice(8, 516)));
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], PstCrc.Compute(s.Slice(8, 471)));
        return h;
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>The "compressible encryption" PST method: a fixed byte permutation (the inverse of the one the reader applies).</summary>
static class PstCrypt
{
    static readonly byte[] Encode = Build();

    static byte[] Build()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[CryptTables.Compressible[i]] = (byte)i;
        return t;
    }

    public static void Encrypt(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++) data[i] = Encode[data[i]];
    }
}
