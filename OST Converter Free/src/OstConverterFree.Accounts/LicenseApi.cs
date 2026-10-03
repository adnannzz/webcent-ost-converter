using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OstConverterFree.Accounts;

public sealed record DeviceInfo(string DeviceId, string Name, bool Current);
public sealed record SignInTokens(string License, string RefreshToken, string Plan, string? Email = null);
public sealed record RefreshedLicense(string License, string Plan);
public sealed record PublicConfig(string? CoffeeUrl);

/// <summary>The server rejected the request (wrong code, revoked device, ...). <see cref="Code"/> is the server's error code.</summary>
public sealed class LicenseApiException(HttpStatusCode status, string code) : Exception($"License server said {(int)status} {code}")
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>All devices already in use when a sign-in is attempted from a new one.</summary>
public sealed class DeviceLimitException(IReadOnlyList<DeviceInfo> devices) : Exception("The device limit was reached.")
{
    public IReadOnlyList<DeviceInfo> Devices { get; } = devices;
}

/// <summary>Talks to the account server (the "free/" routes). Network failures surface as <see cref="HttpRequestException"/>.</summary>
public interface ILicenseApi
{
    Task RequestCodeAsync(string email, CancellationToken ct = default);
    Task<SignInTokens> VerifyCodeAsync(string email, string code, DeviceIdentity device, string? replaceDeviceId, string? name = null, CancellationToken ct = default);
    /// <summary>Settings the owner controls from the admin page (for now: the optional tip link).</summary>
    Task<PublicConfig> GetConfigAsync(CancellationToken ct = default);
    Task<RefreshedLicense> RefreshAsync(string refreshToken, string deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(string refreshToken, string deviceId, CancellationToken ct = default);
    Task RemoveDeviceAsync(string refreshToken, string deviceId, string targetDeviceId, CancellationToken ct = default);
}

public sealed class HttpLicenseApi : ILicenseApi
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly HttpClient _http;

    public HttpLicenseApi(string baseUrl, HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(path, body, Json, ct);
        if (!res.IsSuccessStatusCode) throw await ToException(res, ct);
        try
        {
            return await res.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new HttpRequestException("Empty response from the server.");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            // Something other than our server answered (a captive portal, a proxy error page...). Treat it as "unreachable".
            throw new HttpRequestException("The server sent an unreadable response.", e);
        }
    }

    static async Task<Exception> ToException(HttpResponseMessage res, CancellationToken ct)
    {
        ErrorBody? err = null;
        try { err = await res.Content.ReadFromJsonAsync<ErrorBody>(Json, ct); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { /* non-JSON error body */ }

        if (res.StatusCode == HttpStatusCode.Conflict && err?.Error == "device_limit")
            return new DeviceLimitException(err.Devices ?? []);
        // 5xx and unexpected statuses are server trouble, not a verdict on the account.
        if ((int)res.StatusCode >= 500) return new HttpRequestException($"The server returned {(int)res.StatusCode}.", null, res.StatusCode);
        return new LicenseApiException(res.StatusCode, err?.Error ?? "error");
    }

    public async Task<PublicConfig> GetConfigAsync(CancellationToken ct = default)
    {
        using var res = await _http.GetAsync("free/config", ct);
        if (!res.IsSuccessStatusCode) throw await ToException(res, ct);
        try { return await res.Content.ReadFromJsonAsync<PublicConfig>(Json, ct) ?? new PublicConfig(null); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { throw new HttpRequestException("The server sent an unreadable response.", e); }
    }

    public Task RequestCodeAsync(string email, CancellationToken ct = default) =>
        PostAsync<JsonElement>("free/auth/request", new { email }, ct);

    public Task<SignInTokens> VerifyCodeAsync(string email, string code, DeviceIdentity device, string? replaceDeviceId, string? name = null, CancellationToken ct = default) =>
        PostAsync<SignInTokens>("free/auth/verify", new { email, code, deviceId = device.Id, deviceName = device.Name, replaceDeviceId, name }, ct);

    public Task<RefreshedLicense> RefreshAsync(string refreshToken, string deviceId, CancellationToken ct = default) =>
        PostAsync<RefreshedLicense>("free/refresh", new { refreshToken, deviceId }, ct);

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(string refreshToken, string deviceId, CancellationToken ct = default) =>
        (await PostAsync<DeviceList>("free/devices/list", new { refreshToken, deviceId }, ct)).Devices;

    public Task RemoveDeviceAsync(string refreshToken, string deviceId, string targetDeviceId, CancellationToken ct = default) =>
        PostAsync<JsonElement>("free/devices/remove", new { refreshToken, deviceId, targetDeviceId }, ct);

    sealed record ErrorBody(string? Error, List<DeviceInfo>? Devices);
    sealed record DeviceList([property: JsonPropertyName("devices")] List<DeviceInfo> Devices);
}
