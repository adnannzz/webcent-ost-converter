namespace OstConverterFree.Accounts;

/// <summary>
/// Build-time settings. Before shipping: point <see cref="ApiBaseUrl"/> at the production license server and replace
/// <see cref="PublicKeyPem"/> with the public half of the production signing key (server: <c>npm run gen-keys</c>).
/// The private key never leaves the server.
/// </summary>
public static class LicenseConfig
{
    /// <summary>Public key of the DEVELOPMENT signing key. Tokens signed by any other key are rejected.</summary>
    public const string DevPublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MCowBQYDK2VwAyEAdxxgfiB+eWpbgVyMVO8O1r8UnIFxF2B6r6/1Yzqh+MQ=\n" +
        "-----END PUBLIC KEY-----";

    public const string DevApiBaseUrl = "http://localhost:8787";

    /// <summary>
    /// Release builds: the public half of the production signing key (the private half exists only on the license
    /// server, plus an offline backup) and the production license server. tools/release.ps1 refuses to package if
    /// either is reset to a development or example value.
    /// </summary>
    public const string ProductionPublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MCowBQYDK2VwAyEAfkVHT598ESYuanpAzUtIuHw143uwO52p9nsT+eJSpTo=\n" +
        "-----END PUBLIC KEY-----";
    public const string ProductionApiBaseUrl = "https://license.webcents.in";

    public static string PublicKeyPem =>
#if DEBUG
        DevPublicKeyPem;
#else
        ProductionPublicKeyPem;
#endif

    public static string ApiBaseUrl
    {
        get
        {
#if DEBUG
            // Debug builds can point at another server; release builds cannot be redirected through the environment.
            if (Environment.GetEnvironmentVariable("OSTCONV_API_URL") is { Length: > 0 } url) return url;
            return DevApiBaseUrl;
#else
            return ProductionApiBaseUrl;
#endif
        }
    }
    /// <summary>A signed token is trusted until it expires; the server issues them for 7 days, which is the offline grace period.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    /// <summary>If the clock is set back by more than this since the license was last seen, the license is not trusted.</summary>
    public static readonly TimeSpan ClockRollbackTolerance = TimeSpan.FromHours(24);
}
