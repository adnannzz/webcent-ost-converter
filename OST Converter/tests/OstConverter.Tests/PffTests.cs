using System.Text;
using OstConverter.Core.Pff;
using Xunit;

namespace OstConverter.Tests;

public class CryptTableTests
{
    [Theory]
    [InlineData("Compressible")]
    [InlineData("High1")]
    [InlineData("High2")]
    public void Table_is_a_permutation(string name)
    {
        var t = (byte[])typeof(CryptTables).GetField(name)!.GetValue(null)!;
        Assert.Equal(256, t.Length);
        Assert.Equal(256, t.Distinct().Count());
    }
}

public class LzfuTests
{
    static byte[] Stream(uint type, byte[] payload, int rawSize)
    {
        var s = new List<byte>();
        s.AddRange(BitConverter.GetBytes(payload.Length + 12)); // compressed size
        s.AddRange(BitConverter.GetBytes(rawSize));
        s.AddRange(BitConverter.GetBytes(type));
        s.AddRange(BitConverter.GetBytes(0u)); // crc (unchecked)
        s.AddRange(payload);
        return [.. s];
    }

    [Fact]
    public void Dictionary_has_the_specified_length() => Assert.Equal(207, Lzfu.Dictionary.Length);

    [Fact]
    public void Uncompressed_MELA_is_passed_through()
    {
        var data = Encoding.ASCII.GetBytes("{\\rtf1 hi}");
        Assert.True(Lzfu.TryDecompress(Stream(0x414C454D, data, data.Length), out var result));
        Assert.Equal(data, result);
    }

    [Fact]
    public void Literals_and_a_dictionary_reference_decode()
    {
        // control 0x02: item 0 literal 'X', item 1 reference (offset 0, length 2 -> "{\"), items 2..7 literal.
        byte[] payload = [0x02, (byte)'X', 0x00, 0x00, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f'];
        Assert.True(Lzfu.TryDecompress(Stream(0x75465A4C, payload, 9), out var result));
        Assert.Equal("X{\\abcdef", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Rejects_unknown_type_and_short_input()
    {
        Assert.False(Lzfu.TryDecompress(new byte[8], out _));
        Assert.False(Lzfu.TryDecompress(Stream(0x12345678, [1, 2, 3], 3), out _));
    }
}

public class HeapTests
{
    /// <summary>One heap page with two allocations: "AB" at 12 and "CDE" at 14.</summary>
    static byte[] SinglePage()
    {
        var p = new byte[64];
        int map = 20;
        BitConverter.GetBytes((ushort)map).CopyTo(p, 0);
        p[2] = 0xEC; p[3] = 0xBC;
        BitConverter.GetBytes(0x20u).CopyTo(p, 4);
        "AB"u8.CopyTo(p.AsSpan(12));
        "CDE"u8.CopyTo(p.AsSpan(14));
        BitConverter.GetBytes((ushort)2).CopyTo(p, map);     // cAlloc
        BitConverter.GetBytes((ushort)12).CopyTo(p, map + 4); // rgibAlloc[0]
        BitConverter.GetBytes((ushort)14).CopyTo(p, map + 6);
        BitConverter.GetBytes((ushort)17).CopyTo(p, map + 8);
        return p;
    }

    [Fact]
    public void Resolves_hids_to_allocations()
    {
        var heap = new HeapOnNode(new NodeData([SinglePage()]));
        Assert.Equal("AB", Encoding.ASCII.GetString(heap.Get(0x20).Span));
        Assert.Equal("CDE", Encoding.ASCII.GetString(heap.Get(0x40).Span));
        Assert.Equal(0xBC, heap.ClientSig);
    }

    [Fact]
    public void Four_k_layout_maps_page_numbers_in_8k_units_to_blocks()
    {
        var heap = new HeapOnNode(new NodeData([SinglePage(), SinglePage()]), pagesPerBlock: 8);
        Assert.Equal("CDE", Encoding.ASCII.GetString(heap.Get((8u << 16) | 0x40).Span)); // block 1
        Assert.Throws<PffFormatException>(() => heap.Get((3u << 16) | 0x20));             // not on a block boundary
        Assert.Throws<PffFormatException>(() => heap.Get((16u << 16) | 0x20));            // block 2 doesn't exist
    }

    [Fact]
    public void Rejects_non_heap_and_bad_ids()
    {
        var heap = new HeapOnNode(new NodeData([SinglePage()]));
        Assert.Throws<PffFormatException>(() => heap.Get(0x21));  // node id, not a heap id
        Assert.Throws<PffFormatException>(() => heap.Get(0x60));  // slot 3 of 2
        Assert.Throws<PffFormatException>(() => new HeapOnNode(new NodeData([new byte[32]])));
    }
}
