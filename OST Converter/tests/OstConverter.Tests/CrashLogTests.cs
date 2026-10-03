using OstConverter.Core.Diagnostics;
using Xunit;

namespace OstConverter.Tests;

[Collection("CrashLog")]
public sealed class CrashLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ostconv-crash-" + Guid.NewGuid().ToString("N"));
    readonly string _original = CrashLog.Directory;

    public CrashLogTests() => CrashLog.Directory = _dir;

    public void Dispose()
    {
        CrashLog.Directory = _original;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Report_names_the_error_source_version_and_system()
    {
        Exception thrown;
        try { throw new InvalidOperationException("boom"); } catch (Exception e) { thrown = e; }

        var path = CrashLog.Write(thrown, "unit test");

        Assert.NotNull(path);
        var text = File.ReadAllText(path!);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("boom", text);
        Assert.Contains("unit test", text);
        Assert.Contains("Windows", text);
    }

    [Fact]
    public void Only_the_newest_twenty_reports_are_kept()
    {
        for (int i = 0; i < 25; i++) { CrashLog.Write(new Exception("x" + i), "test"); Thread.Sleep(2); }
        Assert.Equal(20, Directory.GetFiles(_dir, "crash-*.txt").Length);
    }

    [Fact]
    public void An_unwritable_location_never_throws()
    {
        CrashLog.Directory = Path.Combine(_dir, "a\0b");   // invalid path
        Assert.Null(CrashLog.Write(new Exception("x"), "test"));
    }
}
