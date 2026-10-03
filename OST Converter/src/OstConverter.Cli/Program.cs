using OstConverter.Core.Conversion;
using OstConverter.Core.Export;
using OstConverter.Core.Export.Pst;
using OstConverter.Core.Pff;

if (args.Length < 1)
{
    Console.Error.WriteLine("Webcent OST Converter command-line tool (developer utility)\n" +
        "usage: ostcli <file.ost|pst> [--count]                     list the folder tree\n" +
        "       ostcli convert <file> <outDir> --format pst|eml|mbox|msg|pdf|html|csv [--unlimited] [--from yyyy-mm-dd] [--to yyyy-mm-dd] [--folder <name>] [--split-mb <n>]\n" +
        "       ostcli demo <out.pst>                               write a small mailbox of invented messages\n" +
        "       ostcli pst-validate <file.pst>                      check the structure of a PST\n" +
        "       ostcli pst-dump <file> [--props] [--nid <hex>]      inspect nodes and properties");
    return 2;
}

if (args[0] == "pst-dump" && args.Length >= 2) return PstDump.Run(args);
if (args[0] == "demo") return DemoMailbox.Run(args);

if (args[0] == "prop-stats" && args.Length >= 2)
{
    // Which properties do real items carry? Prints id/type counts only (never values).
    using var ps = PstStore.Open(args[1]);
    var tally = new SortedDictionary<(ushort, ushort), (int Messages, int Attachments)>();
    var classes = new SortedDictionary<string, int>();
    foreach (var (folder, _) in ps.WalkFolders())
        foreach (var nid in folder.GetMessageIds().Take(300))
        {
            try
            {
                var m = ps.OpenMessage(nid);
                if (m is null) continue;
                classes[m.MessageClass] = classes.GetValueOrDefault(m.MessageClass) + 1;
                foreach (var p in m.GetRawProperties()) { var k = (p.Id, p.Type); var c = tally.GetValueOrDefault(k); tally[k] = (c.Messages + 1, c.Attachments); }
                foreach (var a in m.Attachments)
                    foreach (var p in a.GetRawProperties()) { var k = (p.Id, p.Type); var c = tally.GetValueOrDefault(k); tally[k] = (c.Messages, c.Attachments + 1); }
            }
            catch (PffFormatException) { }
        }
    Console.WriteLine("classes: " + string.Join(", ", classes.Select(kv => $"{kv.Key}={kv.Value}")));
    foreach (var ((id, type), (n, a)) in tally) Console.WriteLine($"{id:X4}/{type:X4} msgs={n} attachs={a}");
    return 0;
}

if (args[0] == "pst-samples" && args.Length >= 2)
{
    // Small synthetic PSTs, each adding one feature, for bisecting what a consumer such as Outlook rejects.
    Directory.CreateDirectory(args[1]);
    static PstMessageData Msg(string subject, string body, bool rcpt, int attach, int attachSize = 20)
    {
        var m = new PstMessageData();
        m.Props.Add(PstProp.Str(0x001A, "IPM.Note")); m.Props.Add(PstProp.Str(0x0037, subject)); m.Props.Add(PstProp.Str(0x1000, body));
        m.Props.Add(PstProp.Int(0x0E07, 1)); m.Props.Add(PstProp.Time(0x0039, new DateTime(2024, 3, 5, 10, 30, 0, DateTimeKind.Utc)));
        if (rcpt) m.Recipients.Add([PstProp.Str(0x3001, "Alice"), PstProp.Str(0x3002, "SMTP"), PstProp.Str(0x3003, "alice@example.com"), PstProp.Int(0x0C15, 1)]);
        for (int i = 0; i < attach; i++)
        {
            var a = new PstAttachmentData();
            a.Props.Add(PstProp.Str(0x3707, $"file{i}.bin")); a.Props.Add(PstProp.Str(0x3704, $"file{i}.bin")); a.Props.Add(PstProp.Bin(0x3701, new byte[attachSize]));
            m.Attachments.Add(a);
        }
        return m;
    }
    void Make(string name, Action<PstBuilder> fill)
    {
        using var b = new PstBuilder(Path.Combine(args[1], name + ".pst"), "Sample " + name);
        fill(b);
        b.Complete();
    }
    Make("s0-empty", _ => { });
    Make("s1-folder", b => b.EnsureFolder("Inbox", "IPF.Note"));
    Make("s2-one-mail", b => b.AddMessage(b.EnsureFolder("Inbox", "IPF.Note"), Msg("One", "body", false, 0)));
    Make("s3-recipient", b => b.AddMessage(b.EnsureFolder("Inbox", "IPF.Note"), Msg("One", "body", true, 0)));
    Make("s4-attachment", b => b.AddMessage(b.EnsureFolder("Inbox", "IPF.Note"), Msg("One", "body", true, 1)));
    Make("s5-many", b => { var f = b.EnsureFolder("Inbox", "IPF.Note"); for (int i = 0; i < 700; i++) b.AddMessage(f, Msg("Mail " + i, "body " + i, true, 0)); });
    Make("s6-bigvalues", b => b.AddMessage(b.EnsureFolder("Inbox", "IPF.Note"), Msg("Big", new string('x', 40000), true, 1, 300000)));
    Make("s7-tree", b => { foreach (var n in new[] { "A", "A/B", "A/B/C", "D" }) b.AddMessage(b.EnsureFolder(n, "IPF.Note"), Msg("In " + n, "b", false, 0)); });
    foreach (var n in new[] { 10, 25, 40, 100, 200, 400, 450, 500, 550, 600 })
        Make($"n{n:D3}", b => { var f = b.EnsureFolder("Inbox", "IPF.Note"); for (int i = 0; i < n; i++) b.AddMessage(f, Msg("Mail " + i, "body " + i, true, 0)); });
    Console.WriteLine("samples written");
    return 0;
}

if (args[0] == "pst-extract" && args.Length >= 4)
{
    // Writes one real item into small PSTs, each with a different part removed, to find which part a consumer chokes on.
    using var xs = PstStore.Open(args[1]);
    var msg = xs.OpenMessage(Convert.ToUInt64(args[2], 16)) ?? throw new PffFormatException("no such item");
    Directory.CreateDirectory(args[3]);
    PstMessageData Build(bool recipients, bool attachments, bool large)
    {
        var d = PstMessageMapper.Map(msg);
        if (!recipients) d.Recipients.Clear();
        if (!attachments) d.Attachments.Clear();
        if (!large) d.Props.RemoveAll(p => p.Value.Length > 3580);
        return d;
    }
    foreach (var (name, r, a, l) in new[] { ("full", true, true, true), ("no-attach", true, false, true), ("no-recip", false, true, true), ("no-large", true, true, false), ("bare", false, false, false) })
    {
        using var b = new PstBuilder(Path.Combine(args[3], name + ".pst"), "Extract " + name, PstExporter.NameMapOf(xs));
        b.AddMessage(b.EnsureFolder("Inbox", "IPF.Note"), Build(r, a, l));
        b.Complete();
    }
    // Property-group variants (recipients kept, attachments and oversized values dropped).
    void Variant(string name, Predicate<PstProp> drop)
    {
        var d = Build(true, false, false);
        d.Props.RemoveAll(drop);
        using var b = new PstBuilder(Path.Combine(args[3], name + ".pst"), "Extract " + name, PstExporter.NameMapOf(xs));
        b.AddMessage(b.EnsureFolder("Inbox", "IPF.Note"), d);
        b.Complete();
    }
    Variant("v-nonamed", p => p.Id >= 0x8000);
    Variant("v-noflags", p => p.Id == 0x0E07);
    Variant("v-nosize", p => p.Id == 0x0E08);
    Variant("v-nokeys", p => p.Id is 0x6619 or 0x65E2 or 0x65E3 or 0x300B or 0x0F03);
    Variant("v-nocp", p => p.Id is 0x3FDE or 0x3FF1 or 0x3FFA);
    Variant("v-minimal", p => p.Id is not (0x001A or 0x0037 or 0x3007 or 0x3008 or 0x0039 or 0x0E06));
    Console.WriteLine("variants written");
    return 0;
}

if (args[0] == "pst-mapcheck" && args.Length >= 3)
{
    // One trivial message plus the name map copied from <source>: shows whether the map alone upsets a consumer.
    using var ms2 = PstStore.Open(args[1]);
    var tiny = new PstMessageData();
    tiny.Props.Add(PstProp.Str(0x001A, "IPM.Note")); tiny.Props.Add(PstProp.Str(0x0037, "Tiny")); tiny.Props.Add(PstProp.Str(0x1000, "body"));
    tiny.Recipients.Add([PstProp.Str(0x3001, "Alice"), PstProp.Str(0x3002, "SMTP"), PstProp.Str(0x3003, "alice@example.com"), PstProp.Int(0x0C15, 1)]);
    Directory.CreateDirectory(args[2]);
    using var b2 = new PstBuilder(Path.Combine(args[2], "mapcheck.pst"), "Map check", PstExporter.NameMapOf(ms2));
    b2.AddMessage(b2.EnsureFolder("Inbox", "IPF.Note"), tiny);
    b2.Complete();
    Console.WriteLine("written");
    return 0;
}

if (args[0] == "find-subject" && args.Length >= 3)
{
    // Locates items by the first 12 hex digits of the SHA-256 of their subject (as printed by pst-manifest / verify-pst).
    using var fs2 = PstStore.Open(args[1]);
    foreach (var (folder, raw) in fs2.WalkFolders())
        foreach (var nid in folder.GetMessageIds())
        {
            var m = fs2.OpenMessage(nid);
            if (m is null) continue;
            var h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(m.Subject)))[..12];
            if (string.Equals(h, args[2], StringComparison.OrdinalIgnoreCase)) Console.WriteLine($"nid=0x{nid:X} folder={FolderPaths.Display(raw)} recipients={m.Recipients.Count} attachments={m.Attachments.Count}");
        }
    return 0;
}

if (args[0] == "heap-check" && args.Length >= 2)
{
    // Verifies the page map of every heap block of every node and subnode (offsets in range, ascending, exact block length).
    using var hs = PstStore.Open(args[1]);
    int heaps = 0, blocks = 0, bad = 0;
    void CheckNode(PffNode n, string what)
    {
        if ((n.Nid & 0x1F) == 0x1F) return;
        NodeData data;
        try { data = n.Data; } catch (PffFormatException) { return; }
        if (data.Segments.Length == 0 || data.Segments[0].Length < 12 || data.Segments[0][2] != 0xEC) { }
        else
        {
            heaps++;
            for (int i = 0; i < data.Segments.Length; i++)
            {
                var s = data.Segments[i];
                blocks++;
                if (Environment.GetEnvironmentVariable("HEAP_LEN") is { } want && int.TryParse(want, out var wl) && s.Length == wl)
                    Console.WriteLine($"{what} block {i}/{data.Segments.Length} len={s.Length} sig=0x{s[2]:X2}/0x{s[3]:X2} map={System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(s)} nodeType=0x{n.Nid & 0x1F:X}");
                int map = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(s);
                string problem = "";
                if (map + 4 > s.Length) problem = "map outside block";
                else
                {
                    int c = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(map));
                    int mapEnd = map + 4 + 2 * (c + 1);
                    bool padded = i < data.Segments.Length - 1 && s.Length == 8176 && mapEnd <= 8176;   // non-final blocks are padded to full size
                    if (mapEnd != s.Length && !padded) problem = $"block length {s.Length} != map end {mapEnd} (map {map}, allocs {c})";
                    else
                    {
                        int prev = i == 0 ? 12 : 2;
                        for (int a = 0; a <= c && problem.Length == 0; a++)
                        {
                            int off = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(map + 4 + a * 2));
                            if (off < prev || off > map) problem = $"alloc {a} offset {off} (prev {prev}, map {map})";
                            prev = off;
                        }
                    }
                }
                if (problem.Length > 0) { bad++; if (bad <= 10) Console.WriteLine($"{what} block {i}/{data.Segments.Length}: {problem}"); }
            }
        }
        foreach (var k in n.Subnodes.Keys) { var sub = n.OpenSubnode(k); if (sub is not null) CheckNode(sub, $"{what}/0x{k:X}"); }
    }
    foreach (var nid in hs.File.EnumerateNodeIds()) { var n = hs.File.OpenNode(nid); if (n is not null) CheckNode(n, $"0x{nid:X}"); }
    Console.WriteLine($"heaps={heaps} blocks={blocks} problems={bad}");
    return bad == 0 ? 0 : 1;
}

if (args[0] == "pst-validate" && args.Length >= 2)
{
    var problems = OstConverter.Core.Export.Pst.PstValidator.Validate(args[1]);
    Console.WriteLine(problems.Count == 0 ? "structure OK" : $"{problems.Count} problem(s):");
    foreach (var p in problems.Take(30)) Console.WriteLine("  " + p);
    return problems.Count == 0 ? 0 : 1;
}

if (args[0] == "msg-manifest" && args.Length >= 3)
{
    // Expected values for each message the pipeline would export, as hashes/counts only (no mail content),
    // keyed "<display folder>/<NNNNN>". Used to compare what Outlook reads from generated files against the source.
    using var ms = PstStore.Open(args[1]);
    var rows = new List<string>();
    static string H(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12];
    static string Norm(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
    foreach (var (folder, raw) in ms.WalkFolders())
    {
        if (FolderPaths.IsSystem(raw)) continue;
        var display = FolderPaths.Display(raw);
        int index = 0;
        foreach (var nid in folder.GetMessageIds())
        {
            try
            {
                var m = ms.OpenMessage(nid);
                if (m is null || ItemKinds.Classify(m.MessageClass) != ItemKind.Mail) continue;
                var text = m.TextBody ?? "";
                var atts = m.Attachments;
                var a0 = atts.Count > 0 ? atts[0].FileName : "";
                rows.Add($"{{\"key\":\"{display.Replace("\\", "/")}/{++index:D5}\",\"subject\":\"{H(m.Subject)}\",\"hasText\":{(text.Length > 0 ? "true" : "false")},\"body\":\"{H(Norm(text))}\"," +
                         $"\"recipients\":{m.Recipients.Count},\"attachments\":{atts.Count},\"attachment0\":\"{(a0.Length > 0 ? H(a0) : "")}\"}}");
            }
            catch (PffFormatException) { }
        }
    }
    File.WriteAllText(args[2], "[" + string.Join(",\n", rows) + "]");
    Console.WriteLine($"manifest rows: {rows.Count}");
    return 0;
}

if (args[0] == "pst-manifest" && args.Length >= 3)
{
    // Expected fingerprint of every item a PST conversion carries over, as hashes/counts only (no mail content):
    // "subject|body|recipients|attachments", grouped by display folder path. tools/verify-pst.ps1 compares them
    // with what Outlook itself reads from the generated PST.
    using var pm = PstStore.Open(args[1]);
    static string H(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12];
    static string Norm(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
    var entries = new List<string>();
    foreach (var (folder, raw) in pm.WalkFolders())
    {
        if (FolderPaths.IsSystem(raw)) continue;
        var display = FolderPaths.Display(raw).Replace("\\", "/");
        foreach (var nid in folder.GetMessageIds())
        {
            try
            {
                var m = pm.OpenMessage(nid);
                if (m is null) continue;
                var text = m.TextBody ?? "";
                int atts = 0;
                var attHashes = new List<string>();
                foreach (var a in m.Attachments)
                {
                    if (a.IsEmbeddedMessage) { if (a.TryOpenEmbeddedMessage() is null) continue; atts++; attHashes.Add("emb"); continue; }
                    var content = a.ReadRaw();
                    if (content.DataMissing) continue;
                    atts++;
                    attHashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.Data ?? []))[..12]);
                }
                attHashes.Sort(StringComparer.Ordinal);
                var subj = m.Subject;
                entries.Add($"{{\"f\":\"{display}\",\"p\":\"{H(subj)}|{(text.Length > 0 ? H(Norm(text)) : "-")}|{m.Recipients.Count}|{atts}\"," +
                            $"\"a\":\"{string.Join(";", attHashes)}\",\"c\":\"{m.MessageClass}\",\"l\":{subj.Length},\"n\":{subj.Count(ch => ch > 127)},\"h\":{(string.IsNullOrEmpty(m.HtmlBody) ? 0 : 1)},\"w\":{m.Warnings.Count}}}");
            }
            catch (PffFormatException) { }
        }
    }
    File.WriteAllText(args[2], "[" + string.Join(",\n", entries) + "]");
    Console.WriteLine($"manifest entries: {entries.Count}");
    return 0;
}

if (args[0] == "msg-embedded" && args.Length >= 3)
{
    // Dev check for the embedded-message path: writes a .msg whose attachment is another real message.
    using var es = PstStore.Open(args[1]);
    var mails = es.WalkFolders().Where(f => !FolderPaths.IsSystem(f.Path))
        .SelectMany(f => f.Folder.GetMessageIds().Take(30).Select(id => es.OpenMessage(id)))
        .Where(m => m is not null && ItemKinds.Classify(m.MessageClass) == ItemKind.Mail).Take(2).ToList();
    var bytes = OstConverter.Core.Export.Cfb.CompoundFileWriter.ToBytes(OstConverter.Core.Export.MsgBuilder.BuildWithEmbedded(mails[0]!, mails[1]!));
    File.WriteAllBytes(args[2], bytes);
    static string H12(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12];
    Console.WriteLine($"outer subject={H12(mails[0]!.Subject)} inner subject={H12(mails[1]!.Subject)} innerRecipients={mails[1]!.Recipients.Count} outerAttachments(source)={mails[0]!.Attachments.Count}");
    return 0;
}

if (args[0] == "pdf-sample" && args.Length >= 2)
{
    // Renders synthetic multilingual text (not mail) to PNG pages so the layout and font fallback can be inspected.
    OstConverter.Core.Export.PdfExporter.EnsureLicense();
    var sample = new OstConverter.Core.Export.PdfContent(
        "Quarterly report: cafÃ© menu, â‚¬1,250 and ä½ å¥½",
        "Test Sender",
        [("From", "Test Sender <sender@example.com>"), ("To", "Alice <alice@example.com>; Ù…Ø­Ù…Ø¯ <m@example.com>"), ("Date", "Saturday, 3 October 2026 12:00")],
        ["report.xlsx  (48 KB)", "photo.png  (1.2 MB)"],
        "Latin: The quick brown fox jumps over the lazy dog. Ã€Ã‰ÃŽÃ•Ãœ Ã Ã©Ã®ÃµÃ¼ ÃŸ Ã±\n" +
        "Greek: Î“ÎµÎ¹Î± ÏƒÎ¿Ï… ÎºÏŒÏƒÎ¼Îµ   Cyrillic: ÐŸÑ€Ð¸Ð²ÐµÑ‚, Ð¼Ð¸Ñ€\n" +
        "CJK: ä½ å¥½ï¼Œä¸–ç•Œ  ã“ã‚“ã«ã¡ã¯ä¸–ç•Œ  ì•ˆë…•í•˜ì„¸ìš”\n" +
        "Arabic: Ù…Ø±Ø­Ø¨Ø§ Ø¨Ø§Ù„Ø¹Ø§Ù„Ù…   Hebrew: ×©×œ×•× ×¢×•×œ×\n" +
        "Devanagari: à¤¨à¤®à¤¸à¥à¤¤à¥‡ à¤¦à¥à¤¨à¤¿à¤¯à¤¾   Thai: à¸ªà¸§à¸±à¸ªà¸”à¸µà¸Šà¸²à¸§à¹‚à¸¥à¸\n" +
        "Symbols: âœ“ â€¢ â†’ Â© Â® â„¢ Â½   Emoji: \U0001F600 \U0001F4CE\n\n" +
        string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Line {i}: a long paragraph that should wrap across the page width so we can see line breaking and pagination working properly for a lot of text.")));
    var images = OstConverter.Core.Export.PdfExporter.RenderImages(sample);
    for (int i = 0; i < images.Count; i++) File.WriteAllBytes($"{args[1]}-{i + 1}.png", images[i]);
    Console.WriteLine($"pages: {images.Count}");
    return 0;
}

if (args[0] == "check-output" && args.Length >= 2)
{
    // Re-parses what the exporters wrote, to catch malformed output. Prints counts only.
    int emls = 0, bad = 0, withAtt = 0, noDate = 0, noFrom = 0, noSubject = 0, mboxMsgs = 0, mboxFiles = 0, cidLeft = 0, htmls = 0;
    foreach (var f in Directory.EnumerateFiles(args[1], "*.eml", SearchOption.AllDirectories))
    {
        emls++;
        try
        {
            var m = MimeKit.MimeMessage.Load(f);
            if (m.Attachments.Any()) withAtt++;
            if (m.Headers[MimeKit.HeaderId.Date] is null) noDate++;
            if (m.From.Count == 0) noFrom++;
            if (string.IsNullOrEmpty(m.Subject)) noSubject++;
        }
        catch (Exception ex)
        {
            bad++;
            Console.WriteLine($"  unparsable: {Path.GetFileName(Path.GetDirectoryName(f))}/{Path.GetFileName(f)[..5]} {ex.GetType().Name}: {ex.Message}");
        }
    }
    foreach (var f in Directory.EnumerateFiles(args[1], "*.mbox", SearchOption.AllDirectories))
    {
        mboxFiles++;
        using var fs = File.OpenRead(f);
        var parser = new MimeKit.MimeParser(fs, MimeKit.MimeFormat.Mbox);
        int inFile = 0;
        while (!parser.IsEndOfStream)
        {
            try { parser.ParseMessage(); mboxMsgs++; inFile++; }
            catch (Exception ex) { bad++; Console.WriteLine($"  mbox {Path.GetFileName(f)}: after {inFile} msgs, {ex.GetType().Name}: {ex.Message} (pos {fs.Position}/{fs.Length})"); break; }
        }
    }
    foreach (var f in Directory.EnumerateFiles(args[1], "*.html", SearchOption.AllDirectories))
    {
        htmls++;
        if (File.ReadAllText(f).Contains("src=\"cid:", StringComparison.OrdinalIgnoreCase))
        {
            cidLeft++;
            var filesDir = Path.ChangeExtension(f, null) + "_files";
            Console.WriteLine($"  unresolved cid: {Path.GetFileName(Path.GetDirectoryName(f))}/{Path.GetFileName(f)[..5]} attachmentsSaved={(Directory.Exists(filesDir) ? Directory.GetFiles(filesDir).Length : 0)}");
        }
    }
    Console.WriteLine($"eml={emls} withAttachments={withAtt} noDate={noDate} noFrom={noFrom} noSubject={noSubject} | mboxFiles={mboxFiles} mboxMessages={mboxMsgs} | html={htmls} unresolvedCid={cidLeft} | unparsable={bad}");
    return 0;
}

if (args[0] == "convert")
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: ostcli convert <file> <outDir> --format pst|eml|mbox|msg|pdf|html|csv [--unlimited] [--from yyyy-mm-dd] [--to yyyy-mm-dd] [--folder <name>] [--split-mb <n>]"); return 2; }
    string Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
    if (!Enum.TryParse<ExportFormat>(Opt("--format"), true, out var format)) { Console.Error.WriteLine("--format must be pst, eml, mbox, msg, pdf, html or csv"); return 2; }
    try
    {
        using var s = PstStore.Open(args[1]);
        var opts = new ConversionOptions
        {
            OutputDir = args[2],
            Format = format,
            Entitlement = args.Contains("--unlimited") ? Entitlement.Unlimited : Entitlement.Limited,
            From = DateTime.TryParse(Opt("--from"), out var from) ? from : null,
            To = DateTime.TryParse(Opt("--to"), out var to) ? to : null,
            // --folder "Inbox" converts just the folder(s) with that display path (dev aid for isolating problems).
            FolderNids = Opt("--folder") is { Length: > 0 } fname
                ? s.WalkFolders().Where(f => string.Equals(FolderPaths.Display(f.Path), fname, StringComparison.OrdinalIgnoreCase)).Select(f => f.Folder.Nid).ToHashSet()
                : null,
            SplitSizeBytes = long.TryParse(Opt("--split-mb"), out var splitMb) ? splitMb << 20 : OstConverter.Core.Export.Pst.PstExporter.DefaultSplitBytes,
        };
        var progress = new Progress<ConversionProgress>(p => Console.Write($"\r{p.Done}/{p.Total}   "));
        var r = ConversionPipeline.Run(s, opts, progress);
        Console.WriteLine();
        Console.WriteLine($"exported={r.Exported} notApplicable={r.SkippedNotApplicable} byDate={r.SkippedByDate} byLimit={r.SkippedByLimit} failed={r.Errors.Count(e => !e.IsWarning)} warnings={r.Errors.Count(e => e.IsWarning)} in {r.Duration.TotalSeconds:F1}s");
        foreach (var e in r.Errors.Take(10)) Console.WriteLine($"  {(e.IsWarning ? "warning" : "error")} [{e.Folder}] {e.Item}: {e.Message}");
        return 0;
    }
    catch (Exception e) when (e is PffFormatException or NotSupportedException or IOException)
    {
        Console.Error.WriteLine("error: " + e.Message);
        return 1;
    }
}

try
{
    using var store = PstStore.Open(args[0]);
    var f = store.File;
    Console.WriteLine($"{(f.IsOst ? "OST" : "PST")} v{f.Version}  4K={f.Is4K}  crypt={f.CryptMethod}  store=\"{store.DisplayName}\"");

    if (args.Contains("--nodes"))
    {
        var byType = new SortedDictionary<int, int>();
        int missing = 0, total2 = 0;
        foreach (var nid in f.EnumerateNodeIds())
        {
            total2++;
            int type = (int)(nid & 0x1F);
            byType[type] = byType.GetValueOrDefault(type) + 1;
            var node = f.OpenNode(nid);
            if (node is null) { missing++; continue; }
            if (type == 2)
            {
                var pc = new PropertyContext(node);
                Console.WriteLine($"folder nid=0x{nid:X} parent=0x{node.ParentNid:X} name=\"{PropDecode.String(pc, PropTag.DisplayName, System.Text.Encoding.Latin1)}\"");
            }
        }
        Console.WriteLine($"nodes={total2} lookup-misses={missing} blocks={f.CountBlocks()}");
        Console.WriteLine("by type: " + string.Join(", ", byType.Select(kv => $"0x{kv.Key:X}={kv.Value}")));
        var sp = new PropertyContext(f.OpenNode(0x21)!);
        Console.WriteLine("store props: " + string.Join(" ", sp.Properties.Select(p => $"{p.Id:X4}/{p.Type:X4}")));
        return 0;
    }

    if (args.Contains("--messages"))
    {
        int msgs = 0, subj = 0, text = 0, html = 0, rtf = 0, noBody = 0, sender = 0, withRcpt = 0, rcpts = 0, atts = 0, attBytes0 = 0, embedded = 0, embeddedOk = 0, dated = 0;
        long attTotal = 0;
        var errors = new SortedDictionary<string, int>();
        void Fail(Exception e)
        {
            var k = e.GetType().Name + ": " + e.Message;
            if (Environment.GetEnvironmentVariable("OST_DEBUG") == "1")
                k += " @ " + string.Join(" < ", (e.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Contains("OstConverter")).Take(4).Select(l => l.Split(" in ")[0].Replace("at OstConverter.Core.", "")));
            errors[k] = errors.GetValueOrDefault(k) + 1;
        }
        foreach (var (folder, _) in store.WalkFolders())
        {
            IEnumerator<Message> it;
            try { it = folder.GetMessages().GetEnumerator(); } catch (Exception e) { Fail(e); continue; }
            while (true)
            {
                Message m;
                try { if (!it.MoveNext()) break; m = it.Current; } catch (Exception e) { Fail(e); break; }
                msgs++;
                try
                {
                    if (m.Subject.Length > 0) subj++;
                    bool t = !string.IsNullOrEmpty(m.TextBody), h = !string.IsNullOrEmpty(m.HtmlBody), r = !t && !h && !string.IsNullOrEmpty(m.RtfBody);
                    if (t) text++; if (h) html++; if (r) rtf++; if (!t && !h && !r) noBody++;
                    if (m.SenderName.Length > 0 || m.SenderAddress.Length > 0) sender++;
                    if (m.SentTime is not null || m.ReceivedTime is not null) dated++;
                    var rc = m.Recipients; if (rc.Count > 0) withRcpt++; rcpts += rc.Count;
                    foreach (var a in m.Attachments)
                    {
                        atts++;
                        if (a.IsEmbeddedMessage) { embedded++; var em = a.OpenEmbeddedMessage(); if (em is not null) { _ = em.Subject; _ = em.Recipients.Count; embeddedOk++; } }
                        else { var d = a.GetData(); if (d is null || d.Length == 0) attBytes0++; else attTotal += d.Length; }
                    }
                }
                catch (Exception e) { Fail(e); }
            }
        }
        Console.WriteLine($"messages={msgs} subject={subj} text={text} html={html} rtfOnly={rtf} noBody={noBody} sender={sender} dated={dated} withRecipients={withRcpt} recipients={rcpts}");
        Console.WriteLine($"attachments={atts} (empty/missing data={attBytes0}, {attTotal / 1024} KB read) embedded={embedded} embeddedOpened={embeddedOk}");
        foreach (var (k, v) in errors) Console.WriteLine($"  ERROR x{v}: {k}");
        return 0;
    }

    int blockArg = Array.IndexOf(args, "--hasblock");
    if (blockArg >= 0)
    {
        foreach (var hex in args[(blockArg + 1)..])
        {
            ulong bid = Convert.ToUInt64(hex, 16);
            Console.WriteLine($"block 0x{bid:X}: linear scan finds it = {f.ContainsBlockByScan(bid)}");
        }
        return 0;
    }

    int heapArg = Array.IndexOf(args, "--heap");
    if (heapArg >= 0)
    {
        ulong nid = Convert.ToUInt64(args[heapArg + 1], 16);
        var n = f.OpenNode(nid) ?? throw new PffFormatException("no such node");
        Console.WriteLine($"node 0x{nid:X}: segments={n.Data.Segments.Length} total={n.Data.Length}");
        for (int i = 0; i < n.Data.Segments.Length && i < 12; i++)
        {
            var s = n.Data.Segments[i];
            int map = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(s);
            int cAlloc = map + 4 <= s.Length ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(map)) : -1;
            Console.WriteLine($"  seg {i}: len={s.Length} ibHnpm={map} cAlloc={cAlloc} head={Convert.ToHexString(s.AsSpan(0, Math.Min(14, s.Length)))}");
        }
        Console.WriteLine($"  subnodes={n.Subnodes.Count}: " + string.Join(", ", n.Subnodes.Keys.Take(8).Select(k => $"0x{k:X}")));
        if (args.Contains("--bth"))
        {
            var hp = new HeapOnNode(n.Data, f.Is4K ? 8 : 1);
            var info = hp.Get(hp.UserRoot).Span;
            uint hidIdx = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(info[10..]);
            uint hnidRows = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(info[14..]);
            var hdr = hp.Get(hidIdx).Span;
            Console.WriteLine($"  tcinfo: cols={info[1]} rgib={Convert.ToHexString(info.Slice(2, 8))} hidRowIndex=0x{hidIdx:X} hnidRows=0x{hnidRows:X}");
            Console.WriteLine($"  bth hdr: {Convert.ToHexString(hdr)}");
            uint root = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]);
            var ra = hp.Get(root).Span;
            Console.WriteLine($"  bth root hid=0x{root:X} len={ra.Length} head={Convert.ToHexString(ra.Slice(0, Math.Min(48, ra.Length)))}");
        }
        return 0;
    }

    if (args.Contains("--tables"))
    {
        foreach (var (folder, path) in store.WalkFolders().Take(12))
            foreach (var t in new uint[] { 0x0D, 0x0E })
            {
                var tn = f.OpenNode((folder.Nid & ~0x1FUL) | t);
                if (tn is null) continue;
                var tc = new TableContext(tn, f.MaxBlockData);
                int yielded = tc.Rows().Count();
                int unresolved = tc.Rows().Count(r => f.OpenNode(r.RowId) is null);
                Console.WriteLine($"{folder.Name,-22} t=0x{t:X} rows={tc.RowCount} yielded={yielded} unresolvedNid={unresolved} rowSize={tc.RowSize} rpb={tc.RowsPerBlock} dataLen={tc.RowDataLength} segs={tc.RowDataSegments} maxIdx={tc.MaxRowIndex}");
            }
        return 0;
    }

    bool count = args.Contains("--count");
    long total = 0;
    foreach (var (folder, path) in store.WalkFolders())
    {
        int depth = path.Length == 0 ? 0 : path.Count(c => c == '/') + 1;
        int actual = 0, rows = folder.GetMessageCount();
        if (count && rows > 0)
            foreach (var _ in folder.GetMessages()) actual++;
        total += rows;
        Console.WriteLine($"{new string(' ', depth * 2)}{folder.Name}  [counter {folder.ContentCount}, rows {rows}{(count ? $", loaded {actual}" : "")}] {folder.ContainerClass}");
    }
    Console.WriteLine($"total items (per folder counters): {total}");
    return 0;
}
catch (Exception e) when (e is PffFormatException or NotSupportedException or IOException)
{
    Console.Error.WriteLine("error: " + e.Message);
    if (Environment.GetEnvironmentVariable("OST_DEBUG") == "1") Console.Error.WriteLine(e.StackTrace);
    return 1;
}

