using System.Buffers.Binary;

namespace OstConverter.Core.Export.Pst;

/// <summary>
/// Checks the low-level structure of a Unicode PST: header and trailer checksums, signatures, B-tree ordering,
/// block trailers, and that every block and page lies inside the file, outside the others and inside allocated map space.
/// It reads the file independently of the reader used for conversion, so it can catch mistakes both share.
/// </summary>
public static class PstValidator
{
    const long FirstMap = 0x4400, MapSpan = 0x3E000;

    public static List<string> Validate(string path)
    {
        var errors = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var h = new byte[564];
        fs.ReadExactly(h);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x4E444221) { errors.Add("bad signature"); return errors; }
        if (BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(10)) != 23) { errors.Add("not a version 23 file"); return errors; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)) != PstCrc.Compute(h.AsSpan(8, 471))) errors.Add("header partial CRC mismatch");
        if (BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(524)) != PstCrc.Compute(h.AsSpan(8, 516))) errors.Add("header full CRC mismatch");

        long eof = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(184));
        long lastMap = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(192));
        long declaredFree = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(200));
        byte crypt = h[513];
        if (eof != fs.Length) errors.Add($"ibFileEof {eof} differs from file length {fs.Length}");
        if (h[248] != 2) errors.Add("allocation maps not marked valid");

        // Allocation maps
        var maps = new List<byte[]>();
        long freeBytes = 0;
        for (long k = 0; FirstMap + k * MapSpan <= lastMap; k++)
        {
            long ib = FirstMap + k * MapSpan;
            var page = ReadAt(fs, ib, 512);
            if (page[496] != 0x84 || page[497] != 0x84) errors.Add($"map page {k}: bad type");
            if (BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(500)) != PstCrc.Compute(page.AsSpan(0, 496))) errors.Add($"map page {k}: CRC mismatch");
            maps.Add(page[..496]);
            foreach (var b in page.AsSpan(0, 496)) freeBytes += (8 - System.Numerics.BitOperations.PopCount(b)) * 64L;
        }
        if (lastMap != FirstMap + (maps.Count - 1) * MapSpan) errors.Add("ibAMapLast is not the last map");
        if (freeBytes != declaredFree) errors.Add($"free space {freeBytes} differs from header value {declaredFree}");
        if (eof != FirstMap + maps.Count * MapSpan) errors.Add("file does not end at the end of its last map range");

        var used = new List<(long Start, long End, string What)>();
        void Claim(long start, long length, string what)
        {
            used.Add((start, start + length, what));
            if (start + length > eof) errors.Add($"{what} extends past the end of the file");
            long k = (start - FirstMap) / MapSpan;
            if (start < FirstMap || k >= maps.Count) { errors.Add($"{what} outside any map range"); return; }
            long first = (start - (FirstMap + k * MapSpan)) / 64, count = (length + 63) / 64;
            for (long b = first; b < first + count; b++)
                if (b >= 496 * 8 || (maps[(int)k][b >> 3] & (0x80 >> (int)(b & 7))) == 0) { errors.Add($"{what} not marked allocated at 0x{start:X}"); break; }
        }

        var nbt = new List<(ulong Nid, ulong BidData, ulong BidSub)>();
        var bbt = new List<(ulong Bid, long Ib, int Cb)>();
        int lowRefCounts = 0;
        WalkTree(fs, (ulong)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(216)), (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(224)), 0x81, errors, Claim,
            e => nbt.Add((BinaryPrimitives.ReadUInt64LittleEndian(e), BinaryPrimitives.ReadUInt64LittleEndian(e[8..]), BinaryPrimitives.ReadUInt64LittleEndian(e[16..]))), 32);
        WalkTree(fs, (ulong)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(232)), (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(240)), 0x80, errors, Claim,
            e =>
            {
                bbt.Add((BinaryPrimitives.ReadUInt64LittleEndian(e), (long)BinaryPrimitives.ReadUInt64LittleEndian(e[8..]), BinaryPrimitives.ReadUInt16LittleEndian(e[16..])));
                if (BinaryPrimitives.ReadUInt16LittleEndian(e[18..]) < 2) lowRefCounts++;
            }, 24);
        // Outlook crashes ("BBTRelease ... cRef = 1") on a file whose blocks are recorded with a single reference.
        if (lowRefCounts > 0) errors.Add($"{lowRefCounts} block(s) have a reference count below 2");

        var bids = new HashSet<ulong>();
        foreach (var (bid, ib, cb) in bbt)
        {
            if (!bids.Add(bid)) errors.Add($"duplicate block id 0x{bid:X}");
            int total = (cb + 16 + 63) / 64 * 64;
            if (ib < FirstMap || ib + total > eof) { errors.Add($"block 0x{bid:X} outside the file"); continue; }
            Claim(ib, total, $"block 0x{bid:X}");
            var blk = ReadAt(fs, ib, total);
            var t = blk.AsSpan(total - 16);
            if (BinaryPrimitives.ReadUInt16LittleEndian(t) != cb) errors.Add($"block 0x{bid:X}: size field differs from block tree");
            if (BinaryPrimitives.ReadUInt64LittleEndian(t[8..]) != bid) errors.Add($"block 0x{bid:X}: trailer has another id");
            if (BinaryPrimitives.ReadUInt16LittleEndian(t[2..]) != NdbWriter.Sig(ib, bid)) errors.Add($"block 0x{bid:X}: bad signature");
            if (BinaryPrimitives.ReadUInt32LittleEndian(t[4..]) != PstCrc.Compute(blk.AsSpan(0, cb))) errors.Add($"block 0x{bid:X}: CRC mismatch");
            if (((bid & 2) != 0) && crypt != 0 && cb >= 1 && blk[0] > 2) errors.Add($"internal block 0x{bid:X} looks encrypted");
        }
        foreach (var (nid, bidData, bidSub) in nbt)
        {
            if (bidData != 0 && !bids.Contains(bidData)) errors.Add($"node 0x{nid:X}: data block 0x{bidData:X} missing from block tree");
            if (bidSub != 0 && !bids.Contains(bidSub)) errors.Add($"node 0x{nid:X}: subnode block 0x{bidSub:X} missing from block tree");
        }

        used.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < used.Count; i++)
            if (used[i].Start < used[i - 1].End) errors.Add($"{used[i].What} overlaps {used[i - 1].What}");
        return errors;
    }

    static byte[] ReadAt(FileStream fs, long offset, int count)
    {
        var buf = new byte[count];
        fs.Position = offset;
        fs.ReadExactly(buf);
        return buf;
    }

    delegate void Visit(ReadOnlySpan<byte> entry);
    delegate void ClaimSpace(long start, long length, string what);

    static void WalkTree(FileStream fs, ulong bid, long ib, byte ptype, List<string> errors, ClaimSpace claim, Visit leaf, int leafSize, int depth = 0)
    {
        if (depth > 8) { errors.Add("B-tree too deep"); return; }
        if (ib < FirstMap || ib + 512 > fs.Length) { errors.Add($"B-tree page at 0x{ib:X} outside the file"); return; }
        claim(ib, 512, $"page 0x{ib:X}");
        var p = ReadAt(fs, ib, 512);
        if (p[496] != ptype || p[497] != ptype) errors.Add($"page 0x{ib:X}: wrong type 0x{p[496]:X2}");
        if (BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(500)) != PstCrc.Compute(p.AsSpan(0, 496))) errors.Add($"page 0x{ib:X}: CRC mismatch");
        if (BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(504)) != bid) errors.Add($"page 0x{ib:X}: id differs from its reference");
        if (BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(498)) != NdbWriter.Sig(ib, bid)) errors.Add($"page 0x{ib:X}: bad signature");

        int count = p[488], max = p[489], size = p[490], level = p[491];
        if (count == 0 || count > max || count * size > 488) { errors.Add($"page 0x{ib:X}: bad entry counts"); return; }
        ulong previous = 0;
        for (int i = 0; i < count; i++)
        {
            var e = p.AsSpan(i * size, size);
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(e);
            if (i > 0 && key <= previous) errors.Add($"page 0x{ib:X}: keys out of order");
            previous = key;
            if (level == 0) leaf(e);
            else WalkTree(fs, BinaryPrimitives.ReadUInt64LittleEndian(e[8..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(e[16..]), ptype, errors, claim, leaf, leafSize, depth + 1);
        }
    }
}
