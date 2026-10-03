using System.Net.Http;
using Velopack;
using Velopack.Sources;

namespace OstConverter.App;

/// <summary>
/// Checks a static update feed (the folder tools/release.ps1 produces, uploaded to any web host or release page),
/// downloads a newer version in the background and applies it when the user chooses to restart. Does nothing when the
/// app was not installed by the installer (development runs, a copied folder) or no feed is configured.
/// </summary>
public sealed class UpdateService
{
    /// <summary>The https folder that holds the release files (the output of tools/release.ps1).</summary>
    public const string FeedUrl = "https://webcents.in/updates/ost-converter-free";

    readonly UpdateManager? _manager;
    UpdateInfo? _ready;

    public UpdateService()
    {
        if (FeedUrl.Contains("example.com", StringComparison.OrdinalIgnoreCase)) return;
        try { _manager = new UpdateManager(new SimpleWebSource(FeedUrl)); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { _manager = null; }
    }

    public bool IsAvailable => _manager is { IsInstalled: true };

    /// <summary>Returns the new version number once an update has been downloaded, or null when up to date or unreachable.</summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (!IsAvailable) return null;
        try
        {
            var info = await _manager!.CheckForUpdatesAsync();
            if (info is null) return null;
            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            return info.TargetFullRelease.Version.ToString();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            return null; // offline or feed down: try again at the next check
        }
    }

    public void ApplyAndRestart()
    {
        if (_ready is not null) _manager!.ApplyUpdatesAndRestart(_ready);
    }
}
