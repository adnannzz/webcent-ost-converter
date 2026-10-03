using System.Text;
using OstConverter.Core.Conversion;
using OstConverter.Core.Export;
using OstConverter.Core.Export.Pst;
using OstConverter.Core.Pff;
using Xunit;

namespace OstConverter.Tests;

public sealed class PstWriterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ostconv-pst-" + Guid.NewGuid().ToString("N"));

    public PstWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    string NewPath(string name = "test.pst") => Path.Combine(_dir, name);

    static PstMessageData Mail(string subject, string body, string to = "alice@example.com", bool read = true)
    {
        var m = new PstMessageData();
        m.Props.Add(PstProp.Str(0x001A, "IPM.Note"));
        m.Props.Add(PstProp.Str(0x0037, subject));
        m.Props.Add(PstProp.Str(0x1000, body));
        m.Props.Add(PstProp.Int(0x0E07, read ? 1 : 0));
        m.Props.Add(PstProp.Time(0x0039, new DateTime(2024, 3, 5, 10, 30, 0, DateTimeKind.Utc)));
        m.Props.Add(PstProp.Str(0x0C1A, "Sender Name"));
        if (to.Length > 0) m.Recipients.Add(Recipient("Alice", to, 1));
        return m;
    }

    static List<PstProp> Recipient(string name, string address, int kind) =>
    [
        PstProp.Str(0x3001, name), PstProp.Str(0x3002, "SMTP"), PstProp.Str(0x3003, address), PstProp.Int(0x0C15, kind),
    ];

    static PstAttachmentData Attachment(string name, byte[] data)
    {
        var a = new PstAttachmentData();
        a.Props.Add(PstProp.Str(0x3707, name));
        a.Props.Add(PstProp.Str(0x3704, name));
        a.Props.Add(PstProp.Bin(0x3701, data));
        return a;
    }

    void AssertValid(string path)
    {
        var errors = PstValidator.Validate(path);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(10)));
    }

    [Fact]
    public void Empty_store_is_structurally_valid_and_has_the_standard_folders()
    {
        var path = NewPath();
        using (var b = new PstBuilder(path, "Test Store")) b.Complete();
        AssertValid(path);

        using var store = PstStore.Open(path);
        Assert.Equal("Test Store", store.DisplayName);
        var names = store.WalkFolders().Select(f => f.Folder.Name).ToList();
        Assert.Contains("Top of Outlook data file", names);
        Assert.Contains("Deleted Items", names);
        Assert.Contains("Search Root", names);
    }

    [Fact]
    public void Mail_round_trips_with_recipients_and_attachments()
    {
        var path = NewPath();
        var big = new byte[100_000];
        new Random(7).NextBytes(big);
        using (var b = new PstBuilder(path, "Test"))
        {
            var inbox = b.EnsureFolder("Inbox", "IPF.Note");
            var m = Mail("Hello ünïcode 你好", "Body text\r\nsecond line");
            m.Recipients.Add(Recipient("Bob", "bob@example.com", 2));
            m.Attachments.Add(Attachment("small.txt", Encoding.ASCII.GetBytes("tiny")));
            m.Attachments.Add(Attachment("big.bin", big));
            b.AddMessage(inbox, m);
            b.Complete();
        }
        AssertValid(path);

        using var store = PstStore.Open(path);
        var folder = store.WalkFolders().Single(f => f.Folder.Name == "Inbox").Folder;
        Assert.Equal("IPF.Note", folder.ContainerClass);
        Assert.Equal(1, folder.GetMessageCount());
        var msg = store.OpenMessage(folder.GetMessageIds().Single())!;
        Assert.Equal("Hello ünïcode 你好", msg.Subject);
        Assert.Equal("Body text\r\nsecond line", msg.TextBody);
        Assert.Equal("IPM.Note", msg.MessageClass);
        Assert.Equal(2, msg.Recipients.Count);
        Assert.Equal("alice@example.com", msg.Recipients[0].Address);
        Assert.Equal(2, msg.Recipients[1].Kind);
        Assert.Equal(2, msg.Attachments.Count);
        Assert.Equal("tiny", Encoding.ASCII.GetString(msg.Attachments[0].GetData()!));
        Assert.Equal(big, msg.Attachments[1].GetData());
        Assert.True((msg.Flags & 0x10) != 0, "has-attachment flag must be set");
        Assert.Equal(new DateTime(2024, 3, 5, 10, 30, 0, DateTimeKind.Utc), msg.SentTime);
    }

    [Fact]
    public void Folder_hierarchy_counts_and_container_classes_are_kept()
    {
        var path = NewPath();
        using (var b = new PstBuilder(path, "Test"))
        {
            var a = b.EnsureFolder("A");
            var ab = b.EnsureFolder("A/B", "IPF.Contact");
            b.AddMessage(a, Mail("one", "x", read: false));
            b.AddMessage(a, Mail("two", "y"));
            b.AddMessage(ab, Mail("three", "z"));
            Assert.Equal(ab, b.EnsureFolder("a/b"));                 // lookups ignore case, like Windows folder names
            b.Complete();
        }
        AssertValid(path);

        using var store = PstStore.Open(path);
        var all = store.WalkFolders().Where(f => FolderPaths.Display(f.Path).Length > 0).ToDictionary(f => FolderPaths.Display(f.Path), f => f.Folder);
        Assert.Equal(2, all["A"].GetMessageCount());
        Assert.Equal(1, all["A/B"].GetMessageCount());
        Assert.Equal("IPF.Contact", all["A/B"].ContainerClass);
        Assert.Equal(2, all["A"].ContentCount);
        Assert.Contains("Deleted Items", all.Keys);
    }

    [Fact]
    public void Many_messages_use_multi_level_trees_and_multi_block_tables()
    {
        var path = NewPath();
        const int count = 6000;
        using (var b = new PstBuilder(path, "Test"))
        {
            var f = b.EnsureFolder("Big");
            for (int i = 0; i < count; i++) b.AddMessage(f, Mail($"Subject number {i}", $"Body {i} " + new string('x', i % 300)));
            b.Complete();
        }
        AssertValid(path);

        using var store = PstStore.Open(path);
        var folder = store.WalkFolders().Single(x => x.Folder.Name == "Big").Folder;
        Assert.Equal(count, folder.GetMessageCount());
        var subjects = folder.GetMessageIds().Select(id => store.OpenMessage(id)!.Subject).ToHashSet();
        Assert.Equal(count, subjects.Count);
        Assert.Contains("Subject number 5999", subjects);
    }

    [Fact]
    public void Values_larger_than_a_heap_allocation_and_larger_than_a_block_are_stored_in_subnodes()
    {
        var path = NewPath();
        var body = new string('é', 30_000);                           // 60 KB of UTF-16: more than one block
        var huge = new byte[9_000_000];                                // more than 1021 blocks: needs a second tree level
        new Random(3).NextBytes(huge);
        using (var b = new PstBuilder(path, "Test"))
        {
            var f = b.EnsureFolder("Big");
            var m = Mail("Large", body);
            m.Attachments.Add(Attachment("huge.bin", huge));
            b.AddMessage(f, m);
            b.Complete();
        }
        AssertValid(path);

        using var store = PstStore.Open(path);
        var folder = store.WalkFolders().Single(x => x.Folder.Name == "Big").Folder;
        var msg = store.OpenMessage(folder.GetMessageIds().Single())!;
        Assert.Equal(body, msg.TextBody);
        Assert.Equal(huge, msg.Attachments.Single().GetData());
    }

    [Fact]
    public void Attached_messages_round_trip()
    {
        var path = NewPath();
        using (var b = new PstBuilder(path, "Test"))
        {
            var f = b.EnsureFolder("Inbox");
            var inner = Mail("Inner subject", "inner body", to: "inner@example.com");
            inner.Attachments.Add(Attachment("deep.txt", Encoding.ASCII.GetBytes("deep")));
            var outer = Mail("Outer subject", "outer body");
            outer.Attachments.Add(new PstAttachmentData { Embedded = inner, Props = { PstProp.Str(0x3001, "Inner subject") } });
            b.AddMessage(f, outer);
            b.Complete();
        }
        AssertValid(path);

        using var store = PstStore.Open(path);
        var folder = store.WalkFolders().Single(x => x.Folder.Name == "Inbox").Folder;
        var msg = store.OpenMessage(folder.GetMessageIds().Single())!;
        var att = msg.Attachments.Single();
        Assert.True(att.IsEmbeddedMessage);
        var inside = att.OpenEmbeddedMessage()!;
        Assert.Equal("Inner subject", inside.Subject);
        Assert.Equal("inner@example.com", inside.Recipients.Single().Address);
        Assert.Equal("deep", Encoding.ASCII.GetString(inside.Attachments.Single().GetData()!));
    }

    [Fact]
    public void Named_property_map_is_copied_verbatim()
    {
        var path = NewPath();
        var map = new List<PstProp>
        {
            PstProp.Int(0x0001, 251), PstProp.Bin(0x0002, new byte[32]), PstProp.Bin(0x0003, new byte[16]), PstProp.Bin(0x0004, new byte[4]),
        };
        using (var b = new PstBuilder(path, "Test", map)) b.Complete();
        AssertValid(path);

        using var store = PstStore.Open(path);
        var pc = new PropertyContext(store.File.OpenNode(0x61)!);
        Assert.Equal(32, pc.GetBytes(0x0002)!.Length);
        Assert.Equal(16, pc.GetBytes(0x0003)!.Length);
        // Outlook ignores named properties when the entry and string streams sit in the heap instead of subnodes, however small.
        Assert.Equal(2, store.File.OpenNode(0x61)!.Subnodes.Count);
    }

    [Fact]
    public void Files_spanning_several_allocation_map_ranges_are_valid()
    {
        var path = NewPath();
        var data = new byte[3_000_000];
        new Random(5).NextBytes(data);
        using (var b = new PstBuilder(path, "Test"))
        {
            var f = b.EnsureFolder("Bulk");
            for (int i = 0; i < 4; i++)                                // ~12 MB: crosses the first PMap and several AMaps
            {
                var m = Mail("Bulk " + i, "x");
                m.Attachments.Add(Attachment("data.bin", data));
                b.AddMessage(f, m);
            }
            b.Complete();
        }
        AssertValid(path);
        Assert.True(new FileInfo(path).Length > 0x4400 + 3 * 0x3E000);

        using var store = PstStore.Open(path);
        var folder = store.WalkFolders().Single(x => x.Folder.Name == "Bulk").Folder;
        foreach (var id in folder.GetMessageIds()) Assert.Equal(data, store.OpenMessage(id)!.Attachments.Single().GetData());
    }

    string BuildSource(Action<PstBuilder> fill, string name = "source.pst", IReadOnlyList<PstProp>? map = null)
    {
        var path = NewPath(name);
        using (var b = new PstBuilder(path, "Source", map))
        {
            fill(b);
            b.Complete();
        }
        return path;
    }

    [Fact]
    public void Pipeline_converts_to_pst_keeping_empty_folders_and_applying_the_free_cap()
    {
        var src = BuildSource(b =>
        {
            var inbox = b.EnsureFolder("Inbox", "IPF.Note");
            for (int i = 0; i < 60; i++) b.AddMessage(inbox, Mail("Mail " + i, "body"));
            b.EnsureFolder("Empty", "IPF.Note");
            var contact = new PstMessageData();
            contact.Props.Add(PstProp.Str(0x001A, "IPM.Contact"));
            contact.Props.Add(PstProp.Str(0x3001, "Some Person"));
            b.AddMessage(b.EnsureFolder("Contacts", "IPF.Contact"), contact);
        });

        using var store = PstStore.Open(src);
        var limitedDir = Path.Combine(_dir, "limited");
        var limited = ConversionPipeline.Run(store, new ConversionOptions { OutputDir = limitedDir, Format = ExportFormat.Pst, Entitlement = Entitlement.Limited });
        Assert.Equal(51, limited.Exported);                                  // 50 mails + the contact
        Assert.Equal(10, limited.SkippedByLimit);
        var limitedFile = Assert.Single(limited.OutputFiles);
        AssertValid(limitedFile);

        var unlimitedDir = Path.Combine(_dir, "unlimited");
        var unlimited = ConversionPipeline.Run(store, new ConversionOptions { OutputDir = unlimitedDir, Format = ExportFormat.Pst, Entitlement = Entitlement.Unlimited });
        Assert.Equal(61, unlimited.Exported);
        Assert.Empty(unlimited.Errors);

        using var result = PstStore.Open(Assert.Single(unlimited.OutputFiles));
        var folders = result.WalkFolders().ToDictionary(f => f.Folder.Name, f => f.Folder);
        Assert.Equal(60, folders["Inbox"].GetMessageCount());
        Assert.Equal(0, folders["Empty"].GetMessageCount());              // an empty folder still exists
        Assert.Equal("IPF.Contact", folders["Contacts"].ContainerClass);
        var person = result.OpenMessage(folders["Contacts"].GetMessageIds().Single())!;
        Assert.Equal("IPM.Contact", person.MessageClass);
    }

    [Fact]
    public void Pipeline_splits_into_several_valid_files_that_together_hold_every_item()
    {
        var payload = new byte[400_000];
        new Random(11).NextBytes(payload);
        var src = BuildSource(b =>
        {
            var f = b.EnsureFolder("Inbox", "IPF.Note");
            for (int i = 0; i < 12; i++)
            {
                var m = Mail("Mail " + i, "body");
                m.Attachments.Add(Attachment("data.bin", payload));
                b.AddMessage(f, m);
            }
        });

        using var store = PstStore.Open(src);
        var result = ConversionPipeline.Run(store, new ConversionOptions
        {
            OutputDir = Path.Combine(_dir, "split"), Format = ExportFormat.Pst, Entitlement = Entitlement.Unlimited, SplitSizeBytes = 1 << 20,
        });
        Assert.True(result.OutputFiles.Count >= 3, $"expected several files, got {result.OutputFiles.Count}");
        int total = 0;
        foreach (var file in result.OutputFiles)
        {
            AssertValid(file);
            using var part = PstStore.Open(file);
            total += part.WalkFolders().Sum(f => f.Folder.GetMessageCount());
        }
        Assert.Equal(12, total);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "split"), "*.partial"));
    }

    [Fact]
    public void Mapper_keeps_multi_valued_properties_converts_8_bit_strings_and_drops_store_bound_ones()
    {
        var keywords = new byte[] { 2, 0, 0, 0, 8, 0, 0, 0, 14, 0, 0, 0, 0x61, 0, 0, 0, 0x62, 0, 0, 0 };
        var src = BuildSource(b =>
        {
            var m = Mail("placeholder", "body");
            m.Props.RemoveAll(p => p.Id == 0x0037);
            m.Props.Add(new PstProp(0x0037, PropType.String8, [0x63, 0x61, 0x66, 0xE9]));       // "café" in Windows-1252
            m.Props.Add(new PstProp(0x8123, 0x101F, keywords));                                  // multi-valued, copied as is
            m.Props.Add(new PstProp(0x0E30, PropType.Binary, [1, 2, 3]));                        // sync state of the source store
            m.Props.Add(PstProp.Int(0x6700, 7));                                                 // store-internal range
            b.AddMessage(b.EnsureFolder("Inbox", "IPF.Note"), m);
        });

        using var store = PstStore.Open(src);
        var folder = store.WalkFolders().Single(f => f.Folder.Name == "Inbox").Folder;
        var original = store.OpenMessage(folder.GetMessageIds().Single())!;
        Assert.Equal("café", original.Subject);                                                 // the reader decodes 8-bit strings

        var mapped = PstMessageMapper.Map(original);
        Assert.Contains(mapped.Props, p => p.Id == 0x0037 && p.Type == PropType.String && Encoding.Unicode.GetString(p.Value) == "café");
        Assert.Equal(keywords, mapped.Props.Single(p => p.Id == 0x8123).Value);
        Assert.DoesNotContain(mapped.Props, p => p.Id is 0x0E30 or 0x6700);
    }

    [Fact]
    public void Blocks_of_a_multi_block_table_are_padded_to_full_size_except_the_last()
    {
        // Outlook shows zero items for a folder whose heap or row-matrix blocks are packed tighter than this.
        var path = NewPath();
        using (var b = new PstBuilder(path, "Test"))
        {
            var f = b.EnsureFolder("Inbox", "IPF.Note");
            for (int i = 0; i < 700; i++) b.AddMessage(f, Mail("Mail " + i, "body " + i));
            b.Complete();
        }
        AssertValid(path);

        using var store = PstStore.Open(path);
        var folder = store.WalkFolders().Single(x => x.Folder.Name == "Inbox").Folder;
        var table = store.File.OpenNode((folder.Nid & ~0x1FUL) | 0x0E)!;
        var heap = table.Data.Segments;
        Assert.True(heap.Length > 1, "test needs a heap of several blocks");
        Assert.All(heap.Take(heap.Length - 1), s => Assert.Equal(8176, s.Length));

        var rowBlocks = table.OpenSubnode(table.Subnodes.Keys.Single(k => (k & 0x1F) == 0x1F))!.Data.Segments;
        Assert.True(rowBlocks.Length > 1);
        Assert.All(rowBlocks.Take(rowBlocks.Length - 1), s => Assert.Equal(8176, s.Length));
    }

    [Fact]
    public void Page_signatures_match_values_taken_from_real_outlook_files()
    {
        // Offset below 64 KB: only the low word matters (a DList page Outlook wrote at 0x4200).
        Assert.Equal((ushort)0x435E, NdbWriter.Sig(0x4200, 0x15E));
        // Offset above 64 KB: Outlook reported "expected wSig=3209" for the page at 0x10722200 with id 0x7B, which needs the high word too.
        Assert.Equal((ushort)0x3209, NdbWriter.Sig(0x10722200, 0x7B));
    }

    [Fact]
    public void Crc_matches_a_known_value()
    {
        // Same checksum Outlook wrote for an all-zero 496-byte page body is 0; a non-trivial vector guards the table.
        Assert.Equal(0u, PstCrc.Compute(new byte[496]));
        Assert.Equal(PstCrc.Compute(Encoding.ASCII.GetBytes("123456789")), PstCrc.Compute(Encoding.ASCII.GetBytes("123456789")));
        Assert.Equal(0xCBF43926u, PstCrc.Compute(Encoding.ASCII.GetBytes("123456789"), 0xFFFFFFFF) ^ 0xFFFFFFFF);  // standard CRC-32 check value
    }
}
