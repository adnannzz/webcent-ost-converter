using System.Diagnostics;

namespace OstConverter.App;

/// <summary>Opens the optional "Buy me a coffee" payment page. The address comes from the server (the owner sets it on the admin page).</summary>
public static class SupportLink
{
    /// <summary>Only an https address is ever opened.</summary>
    public static void Open(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
