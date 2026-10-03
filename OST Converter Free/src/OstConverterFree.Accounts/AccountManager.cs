using System.Net;
using OstConverter.Core.Conversion;

namespace OstConverterFree.Accounts;

public enum RefreshOutcome
{
    Updated,       // got a fresh token
    NotSignedIn,   // nothing to refresh
    Offline,       // server unreachable: the current token stays in force until it expires
    Revoked,       // the server no longer knows this device: back to signed out
}

public enum SignInOutcome { Success, InvalidCode, DeviceLimit, Offline, RejectedToken, Blocked }

public sealed record SignInResult(SignInOutcome Outcome, IReadOnlyList<DeviceInfo>? Devices = null);

/// <summary>
/// Single source of truth for what this PC may do: signed in (a valid, signed account token) means everything, signed out
/// means preview only. The token is verified offline on every query, so the app keeps working without a network until it
/// expires (7 days after the last successful refresh).
/// </summary>
public sealed class AccountManager : IEntitlementProvider
{
    /// <summary>Signed out: nothing may be converted (previewing is unaffected and unlimited).</summary>
    public static readonly Entitlement PreviewOnly = new(0);

    readonly ILicenseApi _api;
    readonly ILicenseStore _store;
    readonly TokenVerifier _verifier;
    readonly DeviceIdentity _device;
    readonly Func<DateTimeOffset> _clock;
    readonly object _gate = new();

    StoredLicense? _stored;

    public AccountManager(ILicenseApi api, ILicenseStore store, TokenVerifier verifier, DeviceIdentity device, Func<DateTimeOffset>? clock = null)
    {
        _api = api; _store = store; _verifier = verifier; _device = device;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? Changed;

    public DeviceIdentity Device => _device;
    /// <summary>True when this PC holds a trustworthy account token (not merely a saved one that has expired or been tampered with).</summary>
    public bool IsSignedIn { get { lock (_gate) return Active() is not null; } }
    public string? AccountEmail { get { lock (_gate) return Active() is null ? null : _stored?.Email; } }

    public Entitlement Current { get { lock (_gate) return Active() is not null ? Entitlement.Unlimited : PreviewOnly; } }
    public string PlanName => IsSignedIn ? "Account" : "Signed out";

    /// <summary>The verified claims, or null when there is no trustworthy token (none, forged, expired, other PC, clock rolled back).</summary>
    LicenseClaims? Active()
    {
        if (_stored is null) return null;
        var now = _clock();
        // A clock set far back would stretch an expired token's life; refuse to trust it until the clock is sane again.
        if (now < _stored.LastSeen - LicenseConfig.ClockRollbackTolerance) return null;
        var result = _verifier.Verify(_stored.LicenseToken, _device.Id, now);
        return result.IsValid ? result.Claims : null;
    }

    /// <summary>Loads the saved token at startup. Never throws; a damaged file just means "signed out".</summary>
    public void Load()
    {
        lock (_gate) _stored = _store.Load();
        Touch();
        RaiseChanged();
    }

    /// <summary>
    /// Records that the app ran at the current time, but only ever moves the record forward. Without this, someone could
    /// let a token expire offline and then set the clock back to make it look valid again.
    /// </summary>
    void Touch()
    {
        lock (_gate)
        {
            if (_stored is null) return;
            var now = _clock();
            if (now <= _stored.LastSeen || now < _stored.LastSeen - LicenseConfig.ClockRollbackTolerance) return;
            _stored = _stored with { LastSeen = now };
            _store.Save(_stored);
        }
    }

    void SetStored(StoredLicense? value)
    {
        lock (_gate)
        {
            _stored = value;
            if (value is null) _store.Clear(); else _store.Save(value);
        }
        RaiseChanged();
    }

    void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public Task RequestCodeAsync(string email, CancellationToken ct = default) => _api.RequestCodeAsync(email.Trim(), ct);

    /// <summary>The tip link the owner set on the admin page, only if it is an https address; null when unset or the server is unreachable.</summary>
    public async Task<string?> GetCoffeeUrlAsync(CancellationToken ct = default)
    {
        try
        {
            var url = (await _api.GetConfigAsync(ct)).CoffeeUrl;
            return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.AbsoluteUri : null;
        }
        catch (Exception e) when (IsNetworkError(e) || e is LicenseApiException) { return null; }
    }

    /// <summary>Signs in (creating the account on first use) with the emailed 6-digit code.</summary>
    public async Task<SignInResult> SignInAsync(string email, string code, string? replaceDeviceId = null, string? name = null, CancellationToken ct = default)
    {
        email = email.Trim();
        try
        {
            var tokens = await _api.VerifyCodeAsync(email, code.Trim(), _device, replaceDeviceId, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), ct);
            var check = _verifier.Verify(tokens.License, _device.Id, _clock());
            if (!check.IsValid) return new SignInResult(SignInOutcome.RejectedToken);
            SetStored(new StoredLicense(email.ToLowerInvariant(), tokens.License, tokens.RefreshToken, _clock()));
            return new SignInResult(SignInOutcome.Success);
        }
        catch (DeviceLimitException e) { return new SignInResult(SignInOutcome.DeviceLimit, e.Devices); }
        catch (LicenseApiException e) when (e.Status == HttpStatusCode.Forbidden) { return new SignInResult(SignInOutcome.Blocked); }
        catch (LicenseApiException e) when (e.Status is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            return new SignInResult(SignInOutcome.InvalidCode);
        }
        catch (Exception e) when (IsNetworkError(e)) { return new SignInResult(SignInOutcome.Offline); }
    }

    /// <summary>Asks the server for a fresh token. Call on startup, then daily.</summary>
    public async Task<RefreshOutcome> RefreshAsync(CancellationToken ct = default)
    {
        Touch();
        StoredLicense? s;
        lock (_gate) s = _stored;
        if (s is null) return RefreshOutcome.NotSignedIn;
        try
        {
            var fresh = await _api.RefreshAsync(s.RefreshToken, _device.Id, ct);
            var check = _verifier.Verify(fresh.License, _device.Id, _clock());
            if (!check.IsValid) return RefreshOutcome.Offline; // never replace a good token with one we can't verify
            SetStored(s with { LicenseToken = fresh.License, LastSeen = _clock() });
            return RefreshOutcome.Updated;
        }
        catch (LicenseApiException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            SetStored(null); // the server removed this device or the account: stop honouring the old token
            return RefreshOutcome.Revoked;
        }
        catch (Exception e) when (IsNetworkError(e)) { return RefreshOutcome.Offline; }
    }

    /// <summary>Signs out, freeing this device's slot on the server when possible.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        StoredLicense? s;
        lock (_gate) s = _stored;
        if (s is not null)
        {
            try { await _api.RemoveDeviceAsync(s.RefreshToken, _device.Id, _device.Id, ct); }
            catch (Exception e) when (IsNetworkError(e) || e is LicenseApiException) { /* signing out locally still works offline */ }
        }
        SetStored(null);
    }

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        var s = RequireSignedIn();
        return await _api.ListDevicesAsync(s.RefreshToken, _device.Id, ct);
    }

    public async Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        var s = RequireSignedIn();
        await _api.RemoveDeviceAsync(s.RefreshToken, _device.Id, deviceId, ct);
        if (deviceId == _device.Id) SetStored(null);
    }

    StoredLicense RequireSignedIn()
    {
        lock (_gate) return _stored ?? throw new InvalidOperationException("Not signed in.");
    }

    public static bool IsNetworkError(Exception e) =>
        e is HttpRequestException or TaskCanceledException or TimeoutException or OperationCanceledException;
}
