using System.Buffers.Binary;

namespace OstConverter.Core.Export.Pst;

public static class PropType
{
    public const ushort Null = 0x0001, Int16 = 0x0002, Int32 = 0x0003, Float = 0x0004, Double = 0x0005, Currency = 0x0006, AppTime = 0x0007,
        Error = 0x000A, Bool = 0x000B, Object = 0x000D, Int64 = 0x0014, String8 = 0x001E, String = 0x001F, Time = 0x0040, Guid = 0x0048,
        Binary = 0x0102, MultiValueFlag = 0x1000;

    /// <summary>Types whose value (at most four bytes) is stored directly in a property context's value field.</summary>
    public static bool IsInline(ushort type) => type is Null or Int16 or Int32 or Float or Error or Bool;

    /// <summary>Width of a table cell: fixed for scalar types, a four-byte heap or subnode id for everything else.</summary>
    public static int CellSize(ushort type) => type switch
    {
        Int16 => 2,
        Bool => 1,
        Int32 or Float or Error => 4,
        Int64 or Double or Currency or AppTime or Time => 8,
        _ => 4,
    };

    public static bool IsFixedCell(ushort type) => type is Int16 or Bool or Int32 or Float or Error or Int64 or Double or Currency or AppTime or Time;
}

/// <summary>One property in on-disk form: inline types hold their little-endian bytes, strings are UTF-16 without terminator.</summary>
public sealed record PstProp(ushort Id, ushort Type, byte[] Value)
{
    public static PstProp Int(ushort id, int v) => new(id, PropType.Int32, BitConverter.GetBytes(v));
    public static PstProp Long(ushort id, long v) => new(id, PropType.Int64, BitConverter.GetBytes(v));
    public static PstProp Bool(ushort id, bool v) => new(id, PropType.Bool, [v ? (byte)1 : (byte)0]);
    public static PstProp Str(ushort id, string v) => new(id, PropType.String, System.Text.Encoding.Unicode.GetBytes(v));
    public static PstProp Bin(ushort id, byte[] v) => new(id, PropType.Binary, v);
    public static PstProp Time(ushort id, DateTime utc) => new(id, PropType.Time, BitConverter.GetBytes(utc.ToFileTimeUtc()));
}

/// <summary>A value too large for the heap: it lives in a subnode, split into chunks that become the blocks of its data tree.</summary>
public sealed record LargeValue(uint Nid, IReadOnlyList<byte[]> Chunks);

/// <summary>Everything a property or table context needs to be stored as a node: heap blocks plus the subnodes holding oversized values.</summary>
public sealed class LtpContent
{
    public required byte[][] HeapBlocks { get; init; }
    public required List<LargeValue> Large { get; init; }
}

/// <summary>Heap-on-node ([MS-PST] 2.3.1): variable-size allocations addressed by heap id, packed into blocks of at most 8176 bytes.</summary>
public sealed class HeapBuilder(byte clientSig)
{
    public const int MaxAllocation = 3580;
    const int BlockLimit = NdbWriter.MaxBlockData, MaxAllocationsPerBlock = 2047;

    readonly List<List<byte[]>> _blocks = [[]];
    readonly List<int> _used = [0];

    static int Overhead(int block) => block == 0 ? 12 : block % 128 == 8 ? 66 : 2;

    bool Fits(int block, int length)
    {
        int n = _blocks[block].Count + 1;
        int end = Overhead(block) + _used[block] + length;
        end += end & 1;                                     // the page map starts on an even offset
        return n <= MaxAllocationsPerBlock && end + 4 + 2 * (n + 1) <= BlockLimit;
    }

    public uint Alloc(byte[] data)
    {
        if (data.Length is 0 or > MaxAllocation) throw new ArgumentException($"A heap allocation must be 1..{MaxAllocation} bytes (was {data.Length}).");
        // First fit among the most recent blocks, so earlier blocks fill up completely the way Outlook's do.
        int b = -1;
        for (int c = Math.Max(0, _blocks.Count - 8); c < _blocks.Count; c++)
            if (Fits(c, data.Length)) { b = c; break; }
        if (b < 0)
        {
            if (_blocks.Count >= 0xFFFF) throw new NotSupportedException("The heap is too large.");
            _blocks.Add([]);
            _used.Add(0);
            b = _blocks.Count - 1;
        }
        _blocks[b].Add(data);
        _used[b] += data.Length;
        return (uint)b << 16 | (uint)_blocks[b].Count << 5;
    }

    /// <summary>Replaces an allocation of the same size (used to fill in headers whose contents depend on later allocations).</summary>
    public void Replace(uint hid, byte[] data)
    {
        var list = _blocks[(int)(hid >> 16)];
        int i = (int)((hid >> 5) & 0x7FF) - 1;
        if (list[i].Length != data.Length) throw new ArgumentException("Replacement must have the same size.");
        list[i] = data;
    }

    /// <summary>Free-space class of a block, as Outlook records it: 0 for plenty (3.5 KB or more) up to 15 for next to nothing.</summary>
    static int FillLevel(int free) => free switch
    {
        >= 3584 => 0, >= 2560 => 1, >= 2048 => 2, >= 1792 => 3, >= 1536 => 4, >= 1280 => 5, >= 1024 => 6, >= 768 => 7,
        >= 512 => 8, >= 256 => 9, >= 128 => 10, >= 64 => 11, >= 32 => 12, >= 16 => 13, >= 8 => 14, _ => 15,
    };

    public byte[][] Build(uint userRoot)
    {
        var result = new byte[_blocks.Count][];
        var levels = new int[_blocks.Count];
        for (int b = 0; b < _blocks.Count; b++)
        {
            var allocs = _blocks[b];
            int overhead = Overhead(b);
            int end = overhead + _used[b];
            int map = end + (end & 1);
            int mapEnd = map + 4 + 2 * (allocs.Count + 1);
            // Every block but the last is padded to the full block size, as in files Outlook writes.
            var buf = new byte[b < _blocks.Count - 1 ? BlockLimit : mapEnd];
            levels[b] = FillLevel(BlockLimit - mapEnd);
            BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)map);
            if (b == 0)
            {
                buf[2] = 0xEC;
                buf[3] = clientSig;
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), userRoot);
            }
            int pos = overhead;
            var m = buf.AsSpan(map);
            BinaryPrimitives.WriteUInt16LittleEndian(m, (ushort)allocs.Count);
            for (int i = 0; i < allocs.Count; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(m[(4 + i * 2)..], (ushort)pos);
                allocs[i].CopyTo(buf, pos);
                pos += allocs[i].Length;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(m[(4 + allocs.Count * 2)..], (ushort)pos);
            result[b] = buf;
        }

        // Fill-level hints: 4 bits per block, the first eight in the heap header and the rest in the bitmap headers.
        for (int b = 0; b < result.Length; b++)
        {
            int group = b < 8 ? 0 : 8 + (b - 8) / 128 * 128;
            int index = b - group;
            int byteAt = (group == 0 ? 8 : 2) + index / 2;
            result[group][byteAt] |= (byte)(index % 2 == 0 ? levels[b] : levels[b] << 4);
        }
        return result;
    }
}

/// <summary>Stores values in the heap when they fit and in subnodes when they don't, handing back the id a context records for them.</summary>
public sealed class ValueStore(HeapBuilder heap)
{
    readonly List<LargeValue> _large = [];
    uint _nextIndex = 0x401;

    public List<LargeValue> Large => _large;

    /// <summary>Returns the heap id or subnode id of the value, or 0 for an empty value (which has no storage).</summary>
    public uint Store(byte[] value)
    {
        if (value.Length == 0) return 0;
        if (value.Length <= HeapBuilder.MaxAllocation) return heap.Alloc(value);
        return AddLarge(Split(value, NdbWriter.MaxBlockData));
    }

    public uint AddLarge(IReadOnlyList<byte[]> chunks)
    {
        uint nid = NidType.Make(NidType.Ltp, _nextIndex++);
        _large.Add(new LargeValue(nid, chunks));
        return nid;
    }

    public static List<byte[]> Split(byte[] data, int chunk)
    {
        var list = new List<byte[]>();
        for (int o = 0; o < data.Length; o += chunk) list.Add(data.AsSpan(o, Math.Min(chunk, data.Length - o)).ToArray());
        return list;
    }
}

public static class BthWriter
{
    /// <summary>
    /// Builds a B-tree-on-heap over fixed-size records that are already sorted by key, and returns the heap id of its header.
    /// Pass <paramref name="headerHid"/> to fill in a header allocated earlier (so it keeps the first heap id).
    /// </summary>
    public static uint Build(HeapBuilder heap, int cbKey, int cbEnt, ReadOnlySpan<byte> records, uint headerHid = 0)
    {
        if (headerHid == 0) headerHid = heap.Alloc(new byte[8]);
        int recSize = cbKey + cbEnt, count = records.Length / recSize;
        uint root = 0;
        byte levels = 0;

        if (count > 0)
        {
            var level = new List<(byte[] Key, uint Hid)>();
            int perLeaf = HeapBuilder.MaxAllocation / recSize;
            for (int i = 0; i < count; i += perLeaf)
            {
                int n = Math.Min(perLeaf, count - i);
                level.Add((records.Slice(i * recSize, cbKey).ToArray(), heap.Alloc(records.Slice(i * recSize, n * recSize).ToArray())));
            }
            int perIndex = HeapBuilder.MaxAllocation / (cbKey + 4);
            while (level.Count > 1)
            {
                levels++;
                var next = new List<(byte[] Key, uint Hid)>();
                for (int i = 0; i < level.Count; i += perIndex)
                {
                    int n = Math.Min(perIndex, level.Count - i);
                    var buf = new byte[n * (cbKey + 4)];
                    for (int j = 0; j < n; j++)
                    {
                        level[i + j].Key.CopyTo(buf, j * (cbKey + 4));
                        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(j * (cbKey + 4) + cbKey), level[i + j].Hid);
                    }
                    next.Add((level[i].Key, heap.Alloc(buf)));
                }
                level = next;
            }
            root = level[0].Hid;
        }

        var header = new byte[8];
        header[0] = 0xB5;
        header[1] = (byte)cbKey;
        header[2] = (byte)cbEnt;
        header[3] = levels;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), root);
        heap.Replace(headerHid, header);
        return headerHid;
    }
}

public static class PropertyContextWriter
{
    static uint InlineValue(byte[] value)
    {
        uint v = 0;
        for (int i = 0; i < Math.Min(4, value.Length); i++) v |= (uint)value[i] << (8 * i);
        return v;
    }

    /// <summary>
    /// Builds a property context ([MS-PST] 2.3.3). Duplicate ids keep the last value.
    /// <paramref name="alwaysSubnode"/> names properties whose (non-empty) value must live in a subnode however small it is.
    /// </summary>
    public static LtpContent Build(IEnumerable<PstProp> props, Func<ushort, bool>? alwaysSubnode = null)
    {
        var sorted = new SortedDictionary<ushort, PstProp>();
        foreach (var p in props) sorted[p.Id] = p;

        var heap = new HeapBuilder(0xBC);
        var values = new ValueStore(heap);
        uint header = heap.Alloc(new byte[8]);

        var recs = new byte[sorted.Count * 8];
        int o = 0;
        foreach (var p in sorted.Values)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(recs.AsSpan(o), p.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(recs.AsSpan(o + 2), p.Type);
            uint hnid;
            hnid = PropType.IsInline(p.Type) ? InlineValue(p.Value)
                 : p.Value.Length > 0 && alwaysSubnode?.Invoke(p.Id) == true ? values.AddLarge(ValueStore.Split(p.Value, NdbWriter.MaxBlockData))
                 : values.Store(p.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(recs.AsSpan(o + 4), hnid);
            o += 8;
        }
        BthWriter.Build(heap, 2, 6, recs, header);
        return new LtpContent { HeapBlocks = heap.Build(header), Large = values.Large };
    }
}

public sealed record TcColumn(ushort Id, ushort Type);

public sealed class TcRow(uint rowId, uint rowVer)
{
    public uint RowId { get; } = rowId;
    public uint RowVer { get; } = rowVer;
    /// <summary>Cell values in the same on-disk form as <see cref="PstProp.Value"/>.</summary>
    public Dictionary<ushort, byte[]> Values { get; } = [];
}

public static class TableContextWriter
{
    public const ushort RowIdTag = 0x67F2, RowVerTag = 0x67F3;

    /// <summary>
    /// Builds a table context ([MS-PST] 2.3.4). <paramref name="columns"/> are the data columns; the two row-id columns
    /// every table starts with are added here.
    /// </summary>
    public static LtpContent Build(IReadOnlyList<TcColumn> columns, IReadOnlyList<TcRow> rows)
    {
        var all = new List<TcColumn> { new(RowIdTag, PropType.Int32), new(RowVerTag, PropType.Int32) };
        all.AddRange(columns);
        int n = all.Count;

        // Row layout: four- and eight-byte cells, then two-byte cells, then one-byte cells, then the existence bitmap.
        var offsets = new int[n];
        int pos = 0;
        for (int i = 0; i < n; i++) if (PropType.CellSize(all[i].Type) >= 4) { offsets[i] = pos; pos += PropType.CellSize(all[i].Type); }
        int tci4 = pos;
        for (int i = 0; i < n; i++) if (PropType.CellSize(all[i].Type) == 2) { offsets[i] = pos; pos += 2; }
        int tci2 = pos;
        for (int i = 0; i < n; i++) if (PropType.CellSize(all[i].Type) == 1) { offsets[i] = pos; pos += 1; }
        int tci1 = pos;
        int rowSize = tci1 + (n + 7) / 8;

        var heap = new HeapBuilder(0x7C);
        var values = new ValueStore(heap);
        uint indexHeader = heap.Alloc(new byte[8]);
        uint info = heap.Alloc(new byte[22 + 8 * n]);

        var matrix = new byte[(long)rows.Count * rowSize];
        var index = new (uint RowId, uint Index)[rows.Count];
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var cells = matrix.AsSpan(r * rowSize, rowSize);
            BinaryPrimitives.WriteUInt32LittleEndian(cells, row.RowId);
            BinaryPrimitives.WriteUInt32LittleEndian(cells[4..], row.RowVer);
            cells[tci1] |= 0x80 | 0x40;                      // bits 0 and 1: the two row-id columns are always present
            for (int c = 2; c < n; c++)
            {
                if (!row.Values.TryGetValue(all[c].Id, out var v)) continue;
                var cell = cells.Slice(offsets[c], PropType.CellSize(all[c].Type));
                if (PropType.IsFixedCell(all[c].Type)) v.AsSpan(0, Math.Min(cell.Length, v.Length)).CopyTo(cell);
                else BinaryPrimitives.WriteUInt32LittleEndian(cell, values.Store(v));
                cells[tci1 + c / 8] |= (byte)(0x80 >> (c % 8));
            }
            index[r] = (row.RowId, (uint)r);
        }

        uint hnidRows = 0;
        if (rows.Count > 0)
        {
            if (matrix.Length <= HeapBuilder.MaxAllocation) hnidRows = heap.Alloc(matrix);
            else
            {
                // Rows never straddle blocks, so each block of the data tree carries a whole number of rows. Outlook pads
                // every block but the last to the full block size and does not read tables whose blocks are packed tighter.
                int perBlock = NdbWriter.MaxBlockData / rowSize;
                var chunks = new List<byte[]>();
                for (int r = 0; r < rows.Count; r += perBlock)
                {
                    int inBlock = Math.Min(perBlock, rows.Count - r);
                    var chunk = new byte[r + inBlock < rows.Count ? NdbWriter.MaxBlockData : inBlock * rowSize];
                    matrix.AsSpan(r * rowSize, inBlock * rowSize).CopyTo(chunk);
                    chunks.Add(chunk);
                }
                hnidRows = values.AddLarge(chunks);
            }
        }

        Array.Sort(index, (a, b) => a.RowId.CompareTo(b.RowId));
        var recs = new byte[index.Length * 8];
        for (int i = 0; i < index.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(recs.AsSpan(i * 8), index[i].RowId);
            BinaryPrimitives.WriteUInt32LittleEndian(recs.AsSpan(i * 8 + 4), index[i].Index);
        }
        BthWriter.Build(heap, 4, 4, recs, indexHeader);

        var tc = new byte[22 + 8 * n];
        tc[0] = 0x7C;
        tc[1] = (byte)n;
        BinaryPrimitives.WriteUInt16LittleEndian(tc.AsSpan(2), (ushort)tci4);
        BinaryPrimitives.WriteUInt16LittleEndian(tc.AsSpan(4), (ushort)tci2);
        BinaryPrimitives.WriteUInt16LittleEndian(tc.AsSpan(6), (ushort)tci1);
        BinaryPrimitives.WriteUInt16LittleEndian(tc.AsSpan(8), (ushort)rowSize);
        BinaryPrimitives.WriteUInt32LittleEndian(tc.AsSpan(10), indexHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(tc.AsSpan(14), hnidRows);
        // hidIndex (bytes 18..21) is deprecated and stays zero.
        int d = 22;
        foreach (var c in Enumerable.Range(0, n).OrderBy(i => (uint)all[i].Id << 16 | all[i].Type))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tc.AsSpan(d), all[c].Type);
            BinaryPrimitives.WriteUInt16LittleEndian(tc.AsSpan(d + 2), all[c].Id);
            BinaryPrimitives.WriteUInt16LittleEndian(tc.AsSpan(d + 4), (ushort)offsets[c]);
            tc[d + 6] = (byte)PropType.CellSize(all[c].Type);
            tc[d + 7] = (byte)c;
            d += 8;
        }
        heap.Replace(info, tc);
        return new LtpContent { HeapBlocks = heap.Build(info), Large = values.Large };
    }
}
