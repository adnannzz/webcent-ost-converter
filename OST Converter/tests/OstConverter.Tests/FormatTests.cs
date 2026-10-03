using OstConverter.Core.Conversion;
using OstConverter.Core.Export;
using Xunit;

namespace OstConverter.Tests;

public class FormatTests
{
    [Theory]
    [MemberData(nameof(AllFormats))]
    public void Every_export_format_has_an_exporter(ExportFormat format)
    {
        // Adding a value to ExportFormat without wiring an exporter would otherwise only fail when a user picks it.
        using var exporter = ConversionPipeline.CreateExporter(format, Path.GetTempPath());
        Assert.NotNull(exporter);
    }

    public static IEnumerable<object[]> AllFormats() => Enum.GetValues<ExportFormat>().Select(f => new object[] { f });
}

public class PdfLayoutTests
{
    static PdfContent Sample(string body, int attachments = 0) => new(
        "Quarterly report: café 你好",
        "Test Sender",
        [("From", "Test Sender <sender@example.com>"), ("To", "Alice <alice@example.com>")],
        Enumerable.Range(1, attachments).Select(i => $"file{i}.pdf  ({i} KB)").ToList(),
        body);

    [Fact]
    public void Produces_a_valid_pdf_with_header_and_trailer()
    {
        PdfExporter.EnsureLicense();
        var pdf = PdfExporter.BuildDocument(Sample("Hello world")).GeneratePdfBytes();
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.Contains("%%EOF", System.Text.Encoding.ASCII.GetString(pdf, pdf.Length - 32, 32));
    }

    [Fact]
    public void Long_text_flows_onto_several_pages_and_short_text_stays_on_one()
    {
        PdfExporter.EnsureLicense();
        var shortPages = PdfExporter.RenderImages(Sample("short")).Count;
        var longBody = string.Join("\n", Enumerable.Range(1, 400).Select(i => $"Line {i} with enough words to look like a real paragraph of an e-mail."));
        var longPages = PdfExporter.RenderImages(Sample(longBody)).Count;
        Assert.Equal(1, shortPages);
        Assert.True(longPages >= 4, $"expected several pages, got {longPages}");
    }

    [Fact]
    public void Empty_body_control_characters_and_unusual_scripts_do_not_break_rendering()
    {
        PdfExporter.EnsureLicense();
        Assert.NotEmpty(PdfExporter.RenderImages(Sample("")));                                   // no text at all
        Assert.NotEmpty(PdfExporter.RenderImages(Sample("a\u0000b\u0007c\u001Bd")));              // control characters
        Assert.NotEmpty(PdfExporter.RenderImages(Sample("مرحبا שלום नमस्ते \U0001F600 \U000E0041"))); // RTL, Indic, emoji, an unassigned tag char
    }

    [Fact]
    public void Many_attachments_are_listed_without_error() =>
        Assert.NotEmpty(PdfExporter.RenderImages(Sample("body", attachments: 60)));
}

static class PdfTestExtensions
{
    public static byte[] GeneratePdfBytes(this QuestPDF.Infrastructure.IDocument document) => QuestPDF.Fluent.GenerateExtensions.GeneratePdf(document);
}
