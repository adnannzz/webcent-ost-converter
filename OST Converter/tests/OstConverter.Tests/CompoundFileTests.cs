using OstConverter.Core.Export.Cfb;
using Xunit;

namespace OstConverter.Tests;

public class CompoundFileTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("cfb-").FullName;
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    string Write(CfbStorage root)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".cfb");
        using (var fs = File.Create(path)) CompoundFileWriter.Write(root, fs);
        return path;
    }

    static byte[] Bytes(int length, int seed)
    {
        var rng = new Random(seed);
        var b = new byte[length];
        rng.NextBytes(b);
        return b;
    }

    [Fact]
    public void Small_streams_in_the_mini_stream_round_trip_through_Windows()
    {
        var root = new CfbStorage("Root Entry") { Clsid = new Guid("00020D0B-0000-0000-C000-000000000046") };
        var a = Bytes(1, 1); var b = Bytes(63, 2); var c = Bytes(64, 3); var d = Bytes(65, 4); var e = Bytes(4095, 5);
        root.AddStream("a", a); root.AddStream("b", b); root.AddStream("c", c); root.AddStream("d", d); root.AddStream("e", e);

        var r = OleReader.Read(Write(root));
        Assert.Equal(new Guid("00020D0B-0000-0000-C000-000000000046"), r.RootClsid);
        Assert.Equal(a, r.Streams["a"]);
        Assert.Equal(b, r.Streams["b"]);
        Assert.Equal(c, r.Streams["c"]);
        Assert.Equal(d, r.Streams["d"]);
        Assert.Equal(e, r.Streams["e"]);
    }

    [Fact]
    public void Streams_around_the_4096_byte_boundary_and_empty_streams_work()
    {
        var root = new CfbStorage("Root Entry");
        var cases = new Dictionary<string, byte[]>
        {
            ["empty"] = [], ["s4095"] = Bytes(4095, 1), ["s4096"] = Bytes(4096, 2), ["s4097"] = Bytes(4097, 3),
            ["s512"] = Bytes(512, 4), ["s513"] = Bytes(5000, 5), ["s100k"] = Bytes(100_000, 6),
        };
        foreach (var (n, data) in cases) root.AddStream(n, data);

        var r = OleReader.Read(Write(root));
        foreach (var (n, data) in cases) Assert.Equal(data, r.Streams[n]);
    }

    [Fact]
    public void Nested_storages_keep_their_structure_and_contents()
    {
        var root = new CfbStorage("Root Entry");
        var recip = root.AddStorage("__recip_version1.0_#00000000");
        recip.AddStream("__properties_version1.0", Bytes(300, 1));
        var attach = root.AddStorage("__attach_version1.0_#00000000");
        attach.AddStream("__substg1.0_37010102", Bytes(20_000, 2));
        var embedded = attach.AddStorage("__substg1.0_3701000D");
        embedded.AddStream("__properties_version1.0", Bytes(100, 3));
        embedded.AddStorage("deep").AddStream("x", Bytes(10, 4));

        var r = OleReader.Read(Write(root));
        Assert.Contains("__recip_version1.0_#00000000", r.Storages);
        Assert.Contains("__attach_version1.0_#00000000/__substg1.0_3701000D/deep", r.Storages);
        Assert.Equal(Bytes(20_000, 2), r.Streams["__attach_version1.0_#00000000/__substg1.0_37010102"]);
        Assert.Equal(Bytes(10, 4), r.Streams["__attach_version1.0_#00000000/__substg1.0_3701000D/deep/x"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(31)]
    [InlineData(200)]
    public void Directory_trees_of_any_size_are_valid(int count)
    {
        // Exercises the balanced red-black tree and multi-sector directories with names of varying length.
        var root = new CfbStorage("Root Entry");
        var expected = new Dictionary<string, byte[]>();
        for (int i = 0; i < count; i++)
        {
            var name = "stream_" + new string('x', i % 9) + i;
            var data = Bytes(10 + i, i);
            root.AddStream(name, data);
            expected[name] = data;
        }
        var r = OleReader.Read(Write(root));
        Assert.Equal(count, r.Streams.Count);
        foreach (var (n, data) in expected) Assert.Equal(data, r.Streams[n]);
    }

    [Fact]
    public void Names_are_matched_case_insensitively_and_ordering_is_by_length_then_text()
    {
        var root = new CfbStorage("Root Entry");
        root.AddStream("__substg1.0_0037001F", [1]);
        root.AddStream("__properties_version1.0", [2]);
        root.AddStream("__substg1.0_1000001F", [3]);
        root.AddStream("a", [4]);
        var r = OleReader.Read(Write(root));
        Assert.Equal([4], r.Streams["A"]);
        Assert.Equal([2], r.Streams["__PROPERTIES_VERSION1.0"]);
    }

    [Fact]
    public void A_file_large_enough_to_need_DIFAT_sectors_is_readable()
    {
        // More than 109 FAT sectors (about 7 MB) requires DIFAT chaining.
        var root = new CfbStorage("Root Entry");
        var big = Bytes(9_000_000, 42);
        root.AddStream("big", big);
        root.AddStream("small", Bytes(100, 1));
        var r = OleReader.Read(Write(root));
        Assert.Equal(big, r.Streams["big"]);
        Assert.Equal(Bytes(100, 1), r.Streams["small"]);
    }

    [Fact]
    public void Several_large_streams_and_many_small_ones_coexist()
    {
        var root = new CfbStorage("Root Entry");
        var map = new Dictionary<string, byte[]>();
        for (int i = 0; i < 6; i++) map["big" + i] = Bytes(1_500_000 + i * 777, 100 + i);
        for (int i = 0; i < 60; i++) map["small" + i] = Bytes(1 + i * 60, 200 + i);
        foreach (var (n, d) in map) root.AddStream(n, d);
        var r = OleReader.Read(Write(root));
        foreach (var (n, d) in map) Assert.Equal(d, r.Streams[n]);
    }

    [Fact]
    public void Invalid_and_duplicate_names_are_rejected()
    {
        var tooLong = new CfbStorage("Root Entry");
        tooLong.AddStream(new string('n', 32), []);
        Assert.Throws<ArgumentException>(() => CompoundFileWriter.ToBytes(tooLong));

        var dup = new CfbStorage("Root Entry");
        dup.AddStream("Same", [1]);
        dup.AddStream("same", [2]);
        Assert.Throws<ArgumentException>(() => CompoundFileWriter.ToBytes(dup));
    }
}
