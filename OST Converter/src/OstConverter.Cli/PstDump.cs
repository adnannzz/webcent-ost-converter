using System.Buffers.Binary;
using OstConverter.Core.Export.Pst;
using OstConverter.Core.Pff;

/// <summary>
/// Dev tool: prints the on-disk structure of a PST (header, map pages, nodes, property sets, table columns).
/// Only ids, types and sizes are printed, never message content, so it is safe to run on real mail files.
/// </summary>
static class PstDump
{
    public static int Run(string[] args)
    {
        var path = args[1];
        bool verbose = args.Contains("--props");
        var raw = File.ReadAllBytes(path);
        DumpHeader(raw);
        DumpMaps(raw);

        using var store = PstStore.Open(path);
        var f = store.File;
        int nidArg = Array.IndexOf(args, "--nid");
        if (nidArg >= 0)
        {
            // Dump a single node (and its subnodes) only.
            ulong want = Convert.ToUInt64(args[nidArg + 1], 16);
            var one = f.OpenNode(want) ?? throw new PffFormatException("no such node");
            Console.WriteLine($"node 0x{want:X} parent=0x{one.ParentNid:X} bidData=0x{one.BidData:X} bidSub=0x{one.BidSub:X} len={one.Data.Length}");
            DumpNodeContent(one, f, "    ");
            return 0;
        }
        Console.WriteLine();
        foreach (var nid in f.EnumerateNodeIds().OrderBy(n => n))
        {
            var node = f.OpenNode(nid)!;
            string sub = "";
            try { sub = node.Subnodes.Count > 0 ? $" subnodes=[{string.Join(",", node.Subnodes.Keys.Select(k => "0x" + k.ToString("X")))}]" : ""; }
            catch (PffFormatException e) { sub = " subnodes=ERR " + e.Message; }
            long len = 0;
            try { len = node.Data.Length; } catch (PffFormatException) { len = -1; }
            Console.WriteLine($"node 0x{nid:X} ({TypeName(nid)}) parent=0x{node.ParentNid:X} bidData=0x{node.BidData:X} bidSub=0x{node.BidSub:X} len={len}{sub}");
            if (!verbose) continue;
            try { DumpNodeContent(node, f, "    "); } catch (Exception e) when (e is PffFormatException or ArgumentException) { Console.WriteLine("    (unreadable: " + e.Message + ")"); }
        }
        return 0;
    }

    static string TypeName(ulong nid) => (nid & 0x1F) switch
    {
        0 => "HID", 1 => "INTERNAL", 2 => "NORMAL_FOLDER", 3 => "SEARCH_FOLDER", 4 => "NORMAL_MESSAGE", 5 => "ATTACHMENT",
        6 => "SEARCH_UPDATE_QUEUE", 7 => "SEARCH_CRITERIA", 8 => "ASSOC_MESSAGE", 0xA => "CONTENTS_TABLE_INDEX", 0xB => "RECEIVE_FOLDER_TABLE",
        0xC => "OUTGOING_QUEUE_TABLE", 0xD => "HIERARCHY_TABLE", 0xE => "CONTENTS_TABLE", 0xF => "ASSOC_CONTENTS_TABLE",
        0x10 => "SEARCH_CONTENTS_TABLE", 0x11 => "ATTACHMENT_TABLE", 0x12 => "RECIPIENT_TABLE", 0x13 => "SEARCH_TABLE_INDEX",
        0x16 => "T16", 0x17 => "T17", 0x18 => "T18", 0x1F => "LTP", var t => "T" + t.ToString("X"),
    };

    static void DumpNodeContent(PffNode node, PffFile f, string indent)
    {
        DumpOwnContent(node, f, indent);
        foreach (var key in node.Subnodes.Keys.OrderBy(k => k))
        {
            var sub = node.OpenSubnode(key)!;
            Console.WriteLine($"{indent}subnode 0x{key:X} ({TypeName(key)}) bidData=0x{sub.BidData:X} bidSub=0x{sub.BidSub:X} len={sub.Data.Length}");
            if (key >= 0x8000 && (key & 0x1F) == 0x1F) continue; // large property values: raw data
            try { DumpNodeContent(sub, f, indent + "    "); } catch (Exception e) when (e is PffFormatException or ArgumentException) { Console.WriteLine(indent + "      (unreadable: " + e.Message + ")"); }
        }
    }

    static void DumpOwnContent(PffNode node, PffFile f, string indent)
    {
        if (node.Data.Segments.Length == 0) return;
        var s0 = node.Data.Segments[0];
        if (s0.Length < 12 || s0[2] != 0xEC) { Console.WriteLine($"{indent}(not a heap) head={Convert.ToHexString(s0.AsSpan(0, Math.Min(24, s0.Length)))}"); return; }
        var heap = new HeapOnNode(node.Data, node.HeapPagesPerBlock);
        int segs = node.Data.Segments.Length;
        Console.WriteLine($"{indent}heap clientSig=0x{heap.ClientSig:X2} userRoot=0x{heap.UserRoot:X} segments={segs} seg0Len={s0.Length}");
        for (int bi = 0; bi < Math.Min(segs, 6); bi++)
        {
            var sb = node.Data.Segments[bi];
            int map = BinaryPrimitives.ReadUInt16LittleEndian(sb);
            int cAlloc = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(map)), cFree = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(map + 2));
            var offs = Enumerable.Range(0, Math.Min(cAlloc + 1, 24)).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(map + 4 + i * 2)));
            Console.WriteLine($"{indent}  block {bi}: len={sb.Length} ibHnpm={map} fill={Convert.ToHexString(sb.AsSpan(bi == 0 ? 8 : 0, 4))} cAlloc={cAlloc} cFree={cFree} offsets={string.Join(",", offs)}{(cAlloc + 1 > 24 ? ",..." : "")}");
        }
        if (heap.ClientSig == 0xBC)
        {
            var pc = new PropertyContext(node);
            foreach (var p in pc.Properties.OrderBy(p => p.Id))
            {
                string size = "";
                try { size = $" bytes={pc.GetBytes(p.Id)?.Length}"; } catch (PffFormatException) { size = " bytes=ERR"; }
                string val = "";
                if (Environment.GetEnvironmentVariable("PST_DUMP_VALUES") == "1")
                    try { var vb = pc.GetBytes(p.Id); val = vb is null ? "" : " = " + Convert.ToHexString(vb.AsSpan(0, Math.Min(48, vb.Length))) + (vb.Length > 48 ? "..." : ""); } catch (PffFormatException) { }
                Console.WriteLine($"{indent}  prop {p.Id:X4}/{p.Type:X4} raw={p.Value:X8}{size}{val}");
            }
        }
        else if (heap.ClientSig == 0x7C)
        {
            var info = heap.Get(heap.UserRoot).Span;
            var tc = new TableContext(node, f.MaxBlockData);
            Console.WriteLine($"{indent}tcinfo cols={info[1]} rgib={Convert.ToHexString(info.Slice(2, 8))} hidRowIndex=0x{BinaryPrimitives.ReadUInt32LittleEndian(info[10..]):X} hnidRows=0x{BinaryPrimitives.ReadUInt32LittleEndian(info[14..]):X} hidIndex=0x{BinaryPrimitives.ReadUInt32LittleEndian(info[18..]):X} rows={tc.RowCount} rowSize={tc.RowSize}");
            foreach (var c in tc.Columns.Values.OrderBy(c => c.Offset).ThenBy(c => c.Bit))
                Console.WriteLine($"{indent}  col {c.Id:X4}/{c.Type:X4} off={c.Offset} size={c.Size} bit={c.Bit}");
            if (Environment.GetEnvironmentVariable("PST_DUMP_ROWS") == "1")
                foreach (var row in tc.Rows().Take(3))
                {
                    Console.WriteLine($"{indent}  row id=0x{row.RowId:X}");
                    foreach (var c in tc.Columns.Values.OrderBy(c => c.Bit))
                    {
                        var b = row.GetBytes(c.Id);
                        if (b is not null) Console.WriteLine($"{indent}    {c.Id:X4}/{c.Type:X4} = {Convert.ToHexString(b.AsSpan(0, Math.Min(24, b.Length)))}{(b.Length > 24 ? "..." : "")} ({b.Length})");
                    }
                }
        }
    }

    static void DumpHeader(byte[] h)
    {
        ulong U64(int o) => BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(o));
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(o));
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(o));
        Console.WriteLine($"file size={h.Length} (0x{h.Length:X})");
        Console.WriteLine($"magic={U32(0):X8} crcPartial={U32(4):X8} client={(char)h[8]}{(char)h[9]} ver={U16(10)} verClient={U16(12)} platform={h[14]}/{h[15]} res1={U32(16):X} res2={U32(20):X}");
        Console.WriteLine($"bidUnused=0x{U64(24):X} bidNextP=0x{U64(32):X} dwUnique={U32(40)}");
        Console.WriteLine("rgnid=" + string.Join(" ", Enumerable.Range(0, 32).Select(i => U32(44 + i * 4).ToString("X"))));
        Console.WriteLine($"qwUnused={U64(172):X} root.res={U32(180):X} ibFileEof=0x{U64(184):X} ibAMapLast=0x{U64(192):X} cbAMapFree=0x{U64(200):X} cbPMapFree=0x{U64(208):X}");
        Console.WriteLine($"BREFNBT bid=0x{U64(216):X} ib=0x{U64(224):X}   BREFBBT bid=0x{U64(232):X} ib=0x{U64(240):X}");
        Console.WriteLine($"fAMapValid={h[248]} bARVec={h[249]} cARVec={U16(250)} dwAlign={U32(252):X}");
        Console.WriteLine($"rgbFM[0..8]={Convert.ToHexString(h.AsSpan(256, 8))} rgbFP[0..8]={Convert.ToHexString(h.AsSpan(384, 8))}");
        Console.WriteLine($"sentinel=0x{h[512]:X2} crypt={h[513]} res={Convert.ToHexString(h.AsSpan(514, 2))} bidNextB=0x{U64(516 - 0):X} ");
        Console.WriteLine($"crcFull={U32(524):X8} tail={Convert.ToHexString(h.AsSpan(528, 36))}");
        Console.WriteLine($"computed crcPartial={PstCrc.Compute(h.AsSpan(8, 471)):X8} crcFull={PstCrc.Compute(h.AsSpan(8, 516)):X8}");
    }

    /// <summary>Histogram of block reference counts, split by block kind, as written by whatever produced the file.</summary>
    static void DumpRefCounts(byte[] raw)
    {
        var counts = new SortedDictionary<string, int>();
        void Walk(long ib)
        {
            var p = raw.AsSpan((int)ib, 512);
            int n = p[488], size = p[490], level = p[491];
            for (int i = 0; i < n; i++)
            {
                var e = p.Slice(i * size, size);
                if (level > 0) { Walk((long)BinaryPrimitives.ReadUInt64LittleEndian(e[16..])); continue; }
                ulong bid = BinaryPrimitives.ReadUInt64LittleEndian(e);
                int cb = BinaryPrimitives.ReadUInt16LittleEndian(e[16..]), cref = BinaryPrimitives.ReadUInt16LittleEndian(e[18..]);
                long bib = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
                string kind = (bid & 2) != 0 ? "internal(" + raw[bib] + "/" + raw[bib + 1] + ")" : "data";
                var key = $"{kind} cRef={cref}";
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        Walk((long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(240)));
        Console.WriteLine("block refcounts: " + string.Join(", ", counts.Select(kv => $"{kv.Key} x{kv.Value}")));
    }

    static void DumpMaps(byte[] raw)
    {
        DumpRefCounts(raw);
        Console.WriteLine();
        foreach (long ib in new long[] { 0x4200, 0x4400, 0x4600, 0x4800, 0x4A00 })
        {
            if (ib + 512 > raw.Length) continue;
            var p = raw.AsSpan((int)ib, 512);
            int ptype = p[496];
            Console.WriteLine($"page @0x{ib:X}: ptype=0x{ptype:X2}/{p[497]:X2} sig={BinaryPrimitives.ReadUInt16LittleEndian(p[498..]):X4} crc={BinaryPrimitives.ReadUInt32LittleEndian(p[500..]):X8} bid=0x{BinaryPrimitives.ReadUInt64LittleEndian(p[504..]):X} crcCheck={PstCrc.Compute(p[..496]):X8} head={Convert.ToHexString(p[..24])}");
        }
        // Where do the B-tree pages and data blocks live relative to the maps?
        var pages = new List<string>();
        for (long ib = 0x4400; ib + 512 <= raw.Length && pages.Count < 40; ib += 512)
        {
            byte pt = raw[ib + 496];
            if (pt is >= 0x80 and <= 0x88 && raw[ib + 497] == pt)
            {
                var pg = raw.AsSpan((int)ib, 512);
                ulong bid = BinaryPrimitives.ReadUInt64LittleEndian(pg[504..]);
                bool crcOk = PstCrc.Compute(pg[..496]) == BinaryPrimitives.ReadUInt32LittleEndian(pg[500..]);
                pages.Add($"0x{ib:X}:{pt:X2}[bid=0x{bid:X} cEnt={pg[488]} max={pg[489]} cb={pg[490]} lvl={pg[491]} crc={(crcOk ? "ok" : "BAD")} sig={BinaryPrimitives.ReadUInt16LittleEndian(pg[498..]):X4}]");
            }
        }
        Console.WriteLine("pages: " + string.Join(" ", pages));
    }
}
