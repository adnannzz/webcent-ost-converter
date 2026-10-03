using System.Text;
using OstConverter.Core.Conversion;
using OstConverter.Core.Export;
using Xunit;

namespace OstConverter.Tests;

public class MboxEscapingTests
{
    static string Escape(string s)
    {
        using var ms = new MemoryStream();
        MboxExporter.WriteEscaped(ms, Encoding.UTF8.GetBytes(s));
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public void Quotes_lines_that_would_look_like_message_separators()
    {
        Assert.Equal("hi\n>From me\n>>From you\n>>>From x\nFrom: header\n", Escape("hi\nFrom me\n>From you\n>>From x\nFrom: header\n"));
    }

    [Fact]
    public void Does_not_touch_ordinary_lines_and_ends_with_a_newline()
    {
        Assert.Equal("Fromage is nice\nlast line\n", Escape("Fromage is nice\nlast line"));
    }

    [Fact]
    public void Handles_empty_input() => Assert.Equal("", Escape(""));
}

public class CsvEscapingTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("", "")]
    public void Escapes_per_rfc4180(string input, string expected) => Assert.Equal(expected, CsvExporter.Escape(input));

    [Theory]
    [InlineData("=SUM(A1:A9)")]
    [InlineData("+1+1")]
    [InlineData("@cmd")]
    [InlineData("-2+3")]
    public void Neutralises_spreadsheet_formula_prefixes(string input) =>
        Assert.StartsWith("'", CsvExporter.Escape(input));

    [Fact]
    public void Writes_a_row_with_crlf_between_fields_correctly()
    {
        var sw = new StringWriter { NewLine = "\r\n" };
        CsvExporter.WriteRow(sw, ["a", "b,c", "d"]);
        Assert.Equal("a,\"b,c\",d\r\n", sw.ToString());
    }
}

public class RtfTextTests
{
    [Fact]
    public void Extracts_text_and_paragraphs_and_skips_font_tables()
    {
        var rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}{\colortbl;\red0\green0\blue0;}\f0\fs20 Hello \b world\b0\par Second line\par}";
        Assert.Equal("Hello world\nSecond line", RtfText.ToPlainText(rtf));
    }

    [Fact]
    public void Decodes_hex_and_unicode_escapes()
    {
        var backslash = '\\'; // built at runtime so no unicode-escape sequence appears in the source
        var rtf = @"{\rtf1\ansi caf\'e9 " + backslash + "u8364?}";
        Assert.Equal("café €", RtfText.ToPlainText(rtf));
    }

    [Fact]
    public void Ignores_star_destinations_and_handles_escaped_braces()
    {
        Assert.Equal("a {b} c", RtfText.ToPlainText(@"{\rtf1 a \{b\} {\*\generator Msftedit}c}"));
    }
}

public class BodyTextTests
{
    [Fact]
    public void Html_is_reduced_to_readable_text()
    {
        var html = "<html><head><style>p{color:red}</style></head><body><p>Hello&nbsp;<b>world</b> &amp; co</p><div>Line two</div><script>x()</script></body></html>";
        Assert.Equal("Hello world & co\nLine two", BodyText.FromHtml(html));
    }
}

public class FileNameTests
{
    [Theory]
    [InlineData("Re: Q3 plan / draft?", "Re_ Q3 plan _ draft_")]
    [InlineData("  trailing dots...  ", "trailing dots")]
    [InlineData("", "untitled")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    public void Safe_produces_valid_windows_names(string input, string expected) => Assert.Equal(expected, FileNames.Safe(input));

    [Fact]
    public void Safe_truncates_long_names() => Assert.Equal(80, FileNames.Safe(new string('x', 500)).Length);

    [Fact]
    public void SafePath_sanitises_each_segment()
    {
        var p = FileNames.SafePath("Inbox/Clients: A*/2024");
        Assert.Equal(["Inbox", "Clients_ A_", "2024"], p.Split(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Unique_appends_a_counter_when_the_file_exists()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var first = Path.Combine(dir, "a.eml");
            File.WriteAllText(first, "x");
            Assert.Equal(Path.Combine(dir, "a (2).eml"), FileNames.Unique(first));
            File.WriteAllText(Path.Combine(dir, "a (2).eml"), "x");
            Assert.Equal(Path.Combine(dir, "a (3).eml"), FileNames.Unique(first));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class FolderPathTests
{
    [Theory]
    [InlineData("Root - Mailbox/IPM_SUBTREE/Inbox/Clients", "Inbox/Clients")]
    [InlineData("Top of Outlook data file/Sent Items", "Sent Items")]
    [InlineData("Root/Root - Mailbox/IPM_SUBTREE/Inbox", "Root/Root - Mailbox/IPM_SUBTREE/Inbox")] // only leading containers are dropped
    [InlineData("Inbox", "Inbox")]
    public void Display_drops_leading_container_folders(string raw, string expected) => Assert.Equal(expected, FolderPaths.Display(raw));

    [Theory]
    [InlineData("Root - Mailbox/Finder/Reminders", true)]
    [InlineData("Root - Mailbox/Common Views", true)]
    [InlineData("Root - Mailbox/IPM_SUBTREE/Yammer Root/Inbound", true)]
    [InlineData("SPAM Search Folder 2", true)]
    [InlineData("Root - Mailbox/IPM_SUBTREE/Inbox", false)]
    [InlineData("Root - Mailbox/IPM_SUBTREE/Contacts/Recipient Cache", false)]
    public void IsSystem_flags_internal_folders(string raw, bool expected) => Assert.Equal(expected, FolderPaths.IsSystem(raw));
}

public class ItemKindTests
{
    [Theory]
    [InlineData("IPM.Note", ItemKind.Mail)]
    [InlineData("IPM.Note.SMIME.MultipartSigned", ItemKind.Mail)]
    [InlineData("IPM.Schedule.Meeting.Request", ItemKind.Mail)]
    [InlineData("REPORT.IPM.Note.NDR", ItemKind.Mail)]
    [InlineData("IPM.Contact", ItemKind.Contact)]
    [InlineData("IPM.DistList", ItemKind.Contact)]
    [InlineData("IPM.Appointment", ItemKind.Appointment)]
    [InlineData("IPM.Task", ItemKind.Task)]
    [InlineData("IPM.Configuration.Foo", ItemKind.Other)]
    [InlineData("IPM.StickyNote", ItemKind.Other)]
    public void Classifies_message_classes(string cls, ItemKind expected) => Assert.Equal(expected, ItemKinds.Classify(cls));
}

public class EntitlementTests
{
    [Fact]
    public void Limited_is_capped_and_unlimited_is_not()
    {
        Assert.Equal(50, Entitlement.Limited.MaxItemsPerFolder);
        Assert.True(Entitlement.Limited.IsLimited);
        Assert.Null(Entitlement.Unlimited.MaxItemsPerFolder);
        Assert.False(Entitlement.Unlimited.IsLimited);
    }

    [Fact]
    public void Default_options_are_the_limited_tier() =>
        Assert.True(new ConversionOptions { OutputDir = "x" }.Entitlement.IsLimited);
}
