using System.Buffers.Binary;

namespace OstConverter.Core.Pff;

/// <summary>Heap-on-node: allocations addressed by HID (page index, slot index).</summary>
public sealed class HeapOnNode
{
    readonly byte[][] _pages;
    readonly int _pagesPerBlock;

    /// <param name="pagesPerBlock">HID page indexes count 8 KB units; a 4K-layout block (64 KB) spans 8 of them.</param>
    public HeapOnNode(NodeData data, int pagesPerBlock = 1)
    {
        _pages = data.Segments;
        _pagesPerBlock = pagesPerBlock;
        if (_pages.Length == 0 || _pages[0].Length < 12 || _pages[0][2] != 0xEC)
            throw new PffFormatException("Node does not contain a heap.");
        ClientSig = _pages[0][3];
        UserRoot = BinaryPrimitives.ReadUInt32LittleEndian(_pages[0].AsSpan(4));
    }

    public byte ClientSig { get; }
    public uint UserRoot { get; }

    public ReadOnlyMemory<byte> Get(uint hid)
    {
        if (hid == 0) return ReadOnlyMemory<byte>.Empty;
        if ((hid & 0x1F) != 0) throw new PffFormatException($"Value {hid:X} is not a heap id.");
        int page = (int)(hid >> 16), idx = (int)((hid >> 5) & 0x7FF);
        if (page % _pagesPerBlock != 0) throw new PffFormatException($"Bad HID {hid:X}.");
        page /= _pagesPerBlock;
        if (page >= _pages.Length || idx < 1) throw new PffFormatException($"Bad HID {hid:X}.");
        var seg = _pages[page];
        int map = BinaryPrimitives.ReadUInt16LittleEndian(seg);
        int cAlloc = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(map));
        if (idx > cAlloc || map + 4 + (cAlloc + 1) * 2 > seg.Length) throw new PffFormatException($"Bad HID {hid:X}.");
        int start = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(map + 4 + (idx - 1) * 2));
        int end = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(map + 4 + idx * 2));
        if (end < start || end > seg.Length) throw new PffFormatException($"Bad HID {hid:X}.");
        return seg.AsMemory(start, end - start);
    }
}

/// <summary>B-tree-on-heap. Yields (key, value) byte slices of every leaf record.</summary>
public static class Bth
{
    public static IEnumerable<(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value)> Enumerate(HeapOnNode heap, uint headerHid)
    {
        var h = heap.Get(headerHid);
        if (h.Length < 8 || h.Span[0] != 0xB5) throw new PffFormatException("Bad BTH header.");
        int cbKey = h.Span[1], cbEnt = h.Span[2], levels = h.Span[3];
        uint root = BinaryPrimitives.ReadUInt32LittleEndian(h.Span[4..]);
        if (root == 0) yield break;
        foreach (var r in Walk(heap, root, levels, cbKey, cbEnt)) yield return r;
    }

    static IEnumerable<(ReadOnlyMemory<byte>, ReadOnlyMemory<byte>)> Walk(HeapOnNode heap, uint hid, int level, int cbKey, int cbEnt)
    {
        var data = heap.Get(hid);
        if (level == 0)
        {
            int rec = cbKey + cbEnt;
            for (int o = 0; o + rec <= data.Length; o += rec)
                yield return (data.Slice(o, cbKey), data.Slice(o + cbKey, cbEnt));
            yield break;
        }
        int irec = cbKey + 4;
        for (int o = 0; o + irec <= data.Length; o += irec)
            foreach (var r in Walk(heap, BinaryPrimitives.ReadUInt32LittleEndian(data.Span[(o + cbKey)..]), level - 1, cbKey, cbEnt))
                yield return r;
    }
}

public readonly record struct RawProp(ushort Id, ushort Type, uint Value);

/// <summary>Property context: a heap whose BTH maps property ids to inline values or HNIDs.</summary>
public sealed class PropertyContext : IPropSource
{
    readonly PffNode _node;
    readonly HeapOnNode _heap;
    readonly Dictionary<ushort, RawProp> _props = new();

    public PropertyContext(PffNode node)
    {
        _node = node;
        _heap = new HeapOnNode(node.Data, node.HeapPagesPerBlock);
        if (_heap.ClientSig != 0xBC) throw new PffFormatException($"Node {node.Nid:X} is not a property context.");
        foreach (var (k, v) in Bth.Enumerate(_heap, _heap.UserRoot))
        {
            if (k.Length < 2 || v.Length < 6) continue;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(k.Span);
            _props[id] = new RawProp(id, BinaryPrimitives.ReadUInt16LittleEndian(v.Span), BinaryPrimitives.ReadUInt32LittleEndian(v.Span[2..]));
        }
    }

    public IEnumerable<RawProp> Properties => _props.Values;
    public PffNode Node => _node;
    public bool Has(ushort id) => _props.ContainsKey(id);
    public ushort? TypeOf(ushort id) => _props.TryGetValue(id, out var p) ? p.Type : null;

    public byte[]? GetBytes(ushort id)
    {
        if (!_props.TryGetValue(id, out var p)) return null;
        return LtpValues.Resolve(_heap, _node, p.Type, p.Value);
    }
}

public static class LtpValues
{
    public static bool IsInline(ushort type) => type is 0x0002 or 0x0003 or 0x000A or 0x000B or 0x0001 or 0x0004;

    /// <summary>Resolves a stored value: inline (≤4 bytes), heap allocation, or subnode data.</summary>
    public static byte[]? Resolve(HeapOnNode heap, PffNode node, ushort type, uint raw)
    {
        if (IsInline(type))
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, raw);
            return b;
        }
        if (raw == 0) return [];
        if ((raw & 0x1F) == 0) return heap.Get(raw).ToArray();
        var sub = node.OpenSubnode(raw);
        return sub?.Data.ToArray();
    }
}

public sealed class TableColumn
{
    public ushort Id { get; init; }
    public ushort Type { get; init; }
    public int Offset { get; init; }
    public int Size { get; init; }
    public int Bit { get; init; }
}

/// <summary>One table row; property lookups consult the column map and existence bitmap.</summary>
public sealed class TableRow
{
    readonly TableContext _table;
    readonly byte[] _row;
    readonly int _off;

    internal TableRow(TableContext table, uint rowId, byte[] buf, int off)
    {
        _table = table; RowId = rowId; _row = buf; _off = off;
    }

    public uint RowId { get; }

    public byte[]? GetBytes(ushort id)
    {
        if (!_table.Columns.TryGetValue(id, out var c)) return null;
        var span = _row.AsSpan(_off, _table.RowSize);
        int bm = _table.BitmapOffset;
        if ((span[bm + c.Bit / 8] & (0x80 >> (c.Bit % 8))) == 0) return null;
        var cell = span.Slice(c.Offset, c.Size);
        if (c.Size <= 8 && (LtpValues.IsInline(c.Type) || c.Type is 0x0005 or 0x0007 or 0x0014 or 0x0040 or 0x0006))
            return cell.ToArray();
        return LtpValues.Resolve(_table.Heap, _table.Node, c.Type, BinaryPrimitives.ReadUInt32LittleEndian(cell));
    }
}

/// <summary>Table context: typed columns plus a row matrix addressed through a row-index BTH.</summary>
public sealed class TableContext
{
    readonly List<(uint RowId, uint Index)> _index = [];
    readonly NodeData _rows = NodeData.Empty;
    readonly int _rowsPerBlock;

    public TableContext(PffNode node, int maxBlockData)
    {
        Node = node;
        Heap = new HeapOnNode(node.Data, node.HeapPagesPerBlock);
        if (Heap.ClientSig != 0x7C) throw new PffFormatException($"Node {node.Nid:X} is not a table context.");
        var info = Heap.Get(Heap.UserRoot).Span;
        if (info.Length < 22 || info[0] != 0x7C) throw new PffFormatException("Bad table header.");
        int cols = info[1];
        // rgib = { TCI_4b, TCI_2b, TCI_1b, TCI_bm }: the existence bitmap starts at TCI_1b and the row ends at TCI_bm.
        BitmapOffset = BinaryPrimitives.ReadUInt16LittleEndian(info[6..]);
        RowSize = BinaryPrimitives.ReadUInt16LittleEndian(info[8..]);
        if (RowSize < BitmapOffset + (cols + 7) / 8) throw new PffFormatException("Inconsistent table row layout.");
        uint hidRowIndex = BinaryPrimitives.ReadUInt32LittleEndian(info[10..]);
        uint hnidRows = BinaryPrimitives.ReadUInt32LittleEndian(info[14..]);

        var columns = new Dictionary<ushort, TableColumn>();
        for (int i = 0; i < cols; i++)
        {
            int o = 22 + i * 8;
            if (o + 8 > info.Length) throw new PffFormatException("Truncated column descriptors.");
            var col = new TableColumn
            {
                Type = BinaryPrimitives.ReadUInt16LittleEndian(info[o..]),
                Id = BinaryPrimitives.ReadUInt16LittleEndian(info[(o + 2)..]),
                Offset = BinaryPrimitives.ReadUInt16LittleEndian(info[(o + 4)..]),
                Size = info[o + 6],
                Bit = info[o + 7],
            };
            columns[col.Id] = col;
        }
        Columns = columns;

        foreach (var (k, v) in Bth.Enumerate(Heap, hidRowIndex))
            _index.Add((BinaryPrimitives.ReadUInt32LittleEndian(k.Span), BinaryPrimitives.ReadUInt32LittleEndian(v.Span)));
        _index.Sort((a, b) => a.Index.CompareTo(b.Index));

        if (hnidRows != 0)
        {
            if ((hnidRows & 0x1F) == 0) _rows = new NodeData([Heap.Get(hnidRows).ToArray()]);
            else _rows = node.OpenSubnode(hnidRows)?.Data ?? NodeData.Empty;
        }
        _rowsPerBlock = Math.Max(1, maxBlockData / Math.Max(1, RowSize));
    }

    public PffNode Node { get; }
    public HeapOnNode Heap { get; }
    public IReadOnlyDictionary<ushort, TableColumn> Columns { get; }
    public int RowSize { get; }
    public int BitmapOffset { get; }
    public int RowCount => _index.Count;
    public int RowsPerBlock => _rowsPerBlock;
    public long RowDataLength => _rows.Length;
    public int RowDataSegments => _rows.Segments.Length;
    public int MaxRowIndex => _index.Count == 0 ? -1 : (int)_index.Max(i => i.Index);

    public IEnumerable<TableRow> Rows()
    {
        foreach (var (rowId, idx) in _index)
        {
            int seg = (int)(idx / _rowsPerBlock), off = (int)(idx % _rowsPerBlock) * RowSize;
            if (seg >= _rows.Segments.Length || off + RowSize > _rows.Segments[seg].Length) continue;
            yield return new TableRow(this, rowId, _rows.Segments[seg], off);
        }
    }
}
