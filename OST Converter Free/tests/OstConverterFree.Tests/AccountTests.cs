using System.Net;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using OstConverterFree.Accounts;

namespace OstConverterFree.Tests;

/// <summary>Signs tokens the same way the server does (compact JWS, EdDSA) so the verifier is tested against the real format.</summary>
sealed class TestIssuer
{
    readonly Ed25519PrivateKeyParameters _private;
    public string PublicPem { get; }

    public TestIssuer()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        _private = (Ed25519PrivateKeyParameters)pair.Private;
        var der = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(pair.Public).GetDerEncoded();
        PublicPem = "-----BEGIN PUBLIC KEY-----\n" + Convert.ToBase64String(der) + "\n-----END PUBLIC KEY-----";
    }

    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string B64(string s) => B64(Encoding.UTF8.GetBytes(s));

    public string Token(string deviceId, DateTimeOffset now, string plan = "member", string? aud = "ost-free",
        TimeSpan? lifetime = null, string email = "user@example.com", string alg = "EdDSA")
    {
        var payload = new Dictionary<string, object>
        {
            ["sub"] = email, ["plan"] = plan, ["device_id"] = deviceId,
            ["iat"] = now.ToUnixTimeSeconds(), ["exp"] = (now + (lifetime ?? TimeSpan.FromDays(7))).ToUnixTimeSeconds(),
        };
        if (aud is not null) payload["aud"] = aud;
        var head = B64($"{{\"alg\":\"{alg}\"}}");
        var body = B64(JsonSerializer.Serialize(payload));
        var signer = new Ed25519Signer();
        signer.Init(true, _private);
        var msg = Encoding.ASCII.GetBytes(head + "." + body);
        signer.BlockUpdate(msg, 0, msg.Length);
        return head + "." + body + "." + B64(signer.GenerateSignature());
    }
}

public class TokenVerifierTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    readonly TestIssuer _issuer = new();
    TokenVerifier Verifier => new(_issuer.PublicPem);

    [Fact]
    public void Accepts_a_valid_account_token()
    {
        var r = Verifier.Verify(_issuer.Token("dev1", Now), "dev1", Now.AddHours(1));
        Assert.True(r.IsValid);
        Assert.Equal("user@example.com", r.Claims!.Email);
    }

    [Fact]
    public void Rejects_a_token_for_another_product_even_if_correctly_signed()
    {
        // Tokens for other products carry a different plan and no (or another) audience.
        Assert.False(Verifier.Verify(_issuer.Token("dev1", Now, plan: "other", aud: null), "dev1", Now).IsValid);
        Assert.False(Verifier.Verify(_issuer.Token("dev1", Now, plan: "other"), "dev1", Now).IsValid);
        Assert.False(Verifier.Verify(_issuer.Token("dev1", Now, aud: "someone-else"), "dev1", Now).IsValid);
        Assert.False(Verifier.Verify(_issuer.Token("dev1", Now, aud: null), "dev1", Now).IsValid);
    }

    [Fact]
    public void Rejects_expired_wrong_device_wrong_key_and_edited_tokens()
    {
        Assert.Equal(TokenStatus.Expired, Verifier.Verify(_issuer.Token("dev1", Now), "dev1", Now.AddDays(7).AddSeconds(1)).Status);
        Assert.Equal(TokenStatus.WrongDevice, Verifier.Verify(_issuer.Token("dev1", Now), "dev2", Now).Status);
        Assert.Equal(TokenStatus.BadSignature, Verifier.Verify(new TestIssuer().Token("dev1", Now), "dev1", Now).Status);

        var parts = _issuer.Token("dev1", Now).Split('.');
        var edited = TestIssuer.B64(Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1]))).Replace("user@example.com", "other@example.com"));
        Assert.Equal(TokenStatus.BadSignature, Verifier.Verify($"{parts[0]}.{edited}.{parts[2]}", "dev1", Now).Status);
    }

    [Fact]
    public void Rejects_alg_none_and_other_algorithms()
    {
        Assert.Equal(TokenStatus.Malformed, Verifier.Verify(_issuer.Token("dev1", Now, alg: "none"), "dev1", Now).Status);
        Assert.Equal(TokenStatus.Malformed, Verifier.Verify(_issuer.Token("dev1", Now, alg: "HS256"), "dev1", Now).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b.c")]
    public void Rejects_garbage(string? token) => Assert.False(Verifier.Verify(token, "dev1", Now).IsValid);

    static string Pad(string s) { s = s.Replace('-', '+').Replace('_', '/'); return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='); }
}

sealed class FakeApi : ILicenseApi
{
    public Func<string, string, string?, SignInTokens> Verify = (_, _, _) => throw new LicenseApiException(HttpStatusCode.Unauthorized, "invalid_code");
    public Func<string, RefreshedLicense> Refresh = _ => throw new HttpRequestException("offline");
    public List<string> Removed { get; } = [];
    public List<string> Requested { get; } = [];
    public bool RemoveThrowsOffline;

    public Task RequestCodeAsync(string email, CancellationToken ct = default) { Requested.Add(email); return Task.CompletedTask; }
    public string? LastName;
    public string? CoffeeUrl;
    public bool ConfigThrows;
    public Task<SignInTokens> VerifyCodeAsync(string email, string code, DeviceIdentity device, string? replace, string? name = null, CancellationToken ct = default)
    {
        LastName = name;
        return Task.FromResult(Verify(email, code, replace));
    }
    public Task<PublicConfig> GetConfigAsync(CancellationToken ct = default) =>
        ConfigThrows ? throw new HttpRequestException("offline") : Task.FromResult(new PublicConfig(CoffeeUrl));
    public Task<RefreshedLicense> RefreshAsync(string refreshToken, string deviceId, CancellationToken ct = default) => Task.FromResult(Refresh(refreshToken));
    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(string refreshToken, string deviceId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeviceInfo>>([]);
    public Task RemoveDeviceAsync(string refreshToken, string deviceId, string target, CancellationToken ct = default)
    {
        if (RemoveThrowsOffline) throw new HttpRequestException("offline");
        Removed.Add(target);
        return Task.CompletedTask;
    }
}

public class AccountManagerTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly DeviceIdentity Device = new("dev1", "TEST-PC");

    readonly TestIssuer _issuer = new();
    readonly FakeApi _api = new();
    readonly InMemoryLicenseStore _store = new();
    DateTimeOffset _now = T0;

    AccountManager NewManager()
    {
        var m = new AccountManager(_api, _store, new TokenVerifier(_issuer.PublicPem), Device, () => _now);
        m.Load();
        return m;
    }

    SignInTokens Tokens() => new(_issuer.Token(Device.Id, _now), "refresh-1", "member", "user@example.com");

    [Fact]
    public void Signed_out_means_preview_only_with_nothing_convertible()
    {
        var m = NewManager();
        Assert.False(m.IsSignedIn);
        Assert.Equal(0, m.Current.MaxItemsPerFolder);
        Assert.True(m.Current.IsLimited);
    }

    [Fact]
    public async Task Signing_in_unlocks_everything_and_survives_a_restart_offline()
    {
        _api.Verify = (_, _, _) => Tokens();
        var m = NewManager();
        var result = await m.SignInAsync("User@Example.com", " 123456 ");

        Assert.Equal(SignInOutcome.Success, result.Outcome);
        Assert.True(m.IsSignedIn);
        Assert.False(m.Current.IsLimited);
        Assert.Null(m.Current.MaxItemsPerFolder);
        Assert.Equal("user@example.com", m.AccountEmail);

        var restarted = NewManager();
        Assert.False(restarted.Current.IsLimited);
    }

    [Fact]
    public async Task Wrong_code_and_network_failure_are_reported_and_change_nothing()
    {
        var m = NewManager();
        Assert.Equal(SignInOutcome.InvalidCode, (await m.SignInAsync("a@b.co", "000000")).Outcome);
        _api.Verify = (_, _, _) => throw new HttpRequestException("down");
        Assert.Equal(SignInOutcome.Offline, (await m.SignInAsync("a@b.co", "000000")).Outcome);
        Assert.False(m.IsSignedIn);
    }

    [Fact]
    public async Task Device_limit_returns_the_devices_and_the_retry_names_one_to_replace()
    {
        string? replaced = null;
        _api.Verify = (_, _, replace) =>
        {
            if (replace is null) throw new DeviceLimitException([new DeviceInfo("old1", "Office", false), new DeviceInfo("old2", "Laptop", false)]);
            replaced = replace;
            return Tokens();
        };
        var m = NewManager();

        var first = await m.SignInAsync("a@b.co", "123456");
        Assert.Equal(SignInOutcome.DeviceLimit, first.Outcome);
        Assert.Equal(["old1", "old2"], first.Devices!.Select(d => d.DeviceId));
        Assert.Equal(SignInOutcome.Success, (await m.SignInAsync("a@b.co", "123456", "old2")).Outcome);
        Assert.Equal("old2", replaced);
    }

    [Fact]
    public async Task A_token_for_another_product_or_another_key_is_never_accepted_at_sign_in()
    {
        var m = NewManager();
        _api.Verify = (_, _, _) => new SignInTokens(_issuer.Token(Device.Id, _now, plan: "other", aud: null), "r", "other", "a@b.co");
        Assert.Equal(SignInOutcome.RejectedToken, (await m.SignInAsync("a@b.co", "1")).Outcome);
        _api.Verify = (_, _, _) => new SignInTokens(new TestIssuer().Token(Device.Id, _now), "r", "member", "a@b.co");
        Assert.Equal(SignInOutcome.RejectedToken, (await m.SignInAsync("a@b.co", "1")).Outcome);
        Assert.False(m.IsSignedIn);
    }

    [Fact]
    public async Task Refresh_extends_the_token_and_revocation_signs_out()
    {
        _api.Verify = (_, _, _) => Tokens();
        var m = NewManager();
        await m.SignInAsync("a@b.co", "1");

        _now = T0.AddDays(5);
        _api.Refresh = _ => new RefreshedLicense(_issuer.Token(Device.Id, _now), "member");
        Assert.Equal(RefreshOutcome.Updated, await m.RefreshAsync());
        _now = T0.AddDays(10); // beyond the first token's life, but the refreshed one is still good
        Assert.False(m.Current.IsLimited);

        _api.Refresh = _ => throw new LicenseApiException(HttpStatusCode.Unauthorized, "revoked");
        Assert.Equal(RefreshOutcome.Revoked, await m.RefreshAsync());
        Assert.False(m.IsSignedIn);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task Offline_refresh_keeps_access_until_the_token_expires_then_it_is_preview_only()
    {
        _api.Verify = (_, _, _) => Tokens();
        var m = NewManager();
        await m.SignInAsync("a@b.co", "1");

        _now = T0.AddDays(6);
        Assert.Equal(RefreshOutcome.Offline, await m.RefreshAsync()); // the fake throws HttpRequestException by default
        Assert.False(m.Current.IsLimited);

        _now = T0.AddDays(7).AddMinutes(1);
        Assert.True(m.Current.IsLimited);
        Assert.False(m.IsSignedIn);
    }

    [Fact]
    public async Task Setting_the_clock_back_does_not_revive_an_expired_token()
    {
        _api.Verify = (_, _, _) => Tokens();
        await NewManager().SignInAsync("a@b.co", "1");

        _now = T0.AddDays(10);
        Assert.True(NewManager().Current.IsLimited);
        _now = T0.AddDays(3); // clock set back so the token looks valid again
        Assert.True(NewManager().Current.IsLimited);
        Assert.Equal(T0.AddDays(10), _store.Load()!.LastSeen);
    }

    [Fact]
    public void A_stored_token_for_another_pc_or_a_hand_edited_one_is_ignored()
    {
        _store.Save(new StoredLicense("a@b.co", _issuer.Token("some-other-pc", _now), "r", _now));
        Assert.True(NewManager().Current.IsLimited);

        var parts = _issuer.Token(Device.Id, _now).Split('.');
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1]))).Replace("user@example.com", "x@y.zz");
        _store.Save(new StoredLicense("a@b.co", $"{parts[0]}.{TestIssuer.B64(payload)}.{parts[2]}", "r", _now));
        Assert.True(NewManager().Current.IsLimited);
    }

    [Fact]
    public async Task Sign_out_frees_the_device_slot_and_works_offline_too()
    {
        _api.Verify = (_, _, _) => Tokens();
        var m = NewManager();
        await m.SignInAsync("a@b.co", "1");
        await m.SignOutAsync();
        Assert.Equal([Device.Id], _api.Removed);
        Assert.False(m.IsSignedIn);

        await m.SignInAsync("a@b.co", "1");
        _api.RemoveThrowsOffline = true;
        await m.SignOutAsync();
        Assert.False(m.IsSignedIn);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task Device_calls_need_a_sign_in_and_removing_this_pc_signs_out()
    {
        var m = NewManager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.ListDevicesAsync());
        Assert.Equal(RefreshOutcome.NotSignedIn, await m.RefreshAsync());

        _api.Verify = (_, _, _) => Tokens();
        await m.SignInAsync("a@b.co", "1");
        await m.RemoveDeviceAsync("other");
        Assert.True(m.IsSignedIn);
        await m.RemoveDeviceAsync(Device.Id);
        Assert.False(m.IsSignedIn);
    }

    [Fact]
    public async Task The_name_is_sent_trimmed_and_an_empty_name_is_not_sent()
    {
        _api.Verify = (_, _, _) => Tokens();
        var m = NewManager();
        await m.SignInAsync("a@b.co", "1", null, "  Alice Example ");
        Assert.Equal("Alice Example", _api.LastName);
        await m.SignInAsync("a@b.co", "1", null, "   ");
        Assert.Null(_api.LastName);
    }

    [Fact]
    public async Task A_suspended_account_is_told_so_and_is_not_signed_in()
    {
        _api.Verify = (_, _, _) => throw new LicenseApiException(HttpStatusCode.Forbidden, "blocked");
        var m = NewManager();
        Assert.Equal(SignInOutcome.Blocked, (await m.SignInAsync("a@b.co", "1", null, "Alice")).Outcome);
        Assert.False(m.IsSignedIn);
    }

    [Theory]
    [InlineData("https://pay.example/tip", "https://pay.example/tip")]
    [InlineData("http://pay.example/tip", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task The_tip_link_is_only_used_when_it_is_an_https_address(string? served, string? expected)
    {
        _api.CoffeeUrl = served;
        Assert.Equal(expected, await NewManager().GetCoffeeUrlAsync());
    }

    [Fact]
    public async Task An_unreachable_server_just_means_no_tip_button()
    {
        _api.ConfigThrows = true;
        Assert.Null(await NewManager().GetCoffeeUrlAsync());
    }

    [Fact]
    public async Task Requesting_a_code_trims_the_email()
    {
        await NewManager().RequestCodeAsync("  a@b.co ");
        Assert.Equal(["a@b.co"], _api.Requested);
    }

    static string Pad(string s) { s = s.Replace('-', '+').Replace('_', '/'); return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='); }
}

/// <summary>
/// Runs the real client against a real server only when asked: set OSTFREE_LIVE_URL, OSTFREE_LIVE_EMAIL and OSTFREE_LIVE_CODE
/// (a code the server just sent). Uses the development public key, so point it at a development server, not production.
/// </summary>
public class LiveServerTests
{
    [Fact]
    public async Task Sign_in_refresh_devices_and_sign_out_work_against_a_real_server()
    {
        var url = Environment.GetEnvironmentVariable("OSTFREE_LIVE_URL");
        var email = Environment.GetEnvironmentVariable("OSTFREE_LIVE_EMAIL");
        var code = Environment.GetEnvironmentVariable("OSTFREE_LIVE_CODE");
        if (url is null || email is null || code is null) return;

        var m = new AccountManager(new HttpLicenseApi(url), new InMemoryLicenseStore(), new TokenVerifier(LicenseConfig.DevPublicKeyPem),
            new DeviceIdentity("live-test-pc", "LIVE-TEST"));
        Assert.Equal(SignInOutcome.Success, (await m.SignInAsync(email, code, null, "Live Test")).Outcome);
        Assert.True(m.IsSignedIn);
        Assert.False(m.Current.IsLimited);
        Assert.Equal(email.ToLowerInvariant(), m.AccountEmail);
        Assert.Equal(RefreshOutcome.Updated, await m.RefreshAsync());
        var devices = await m.ListDevicesAsync();
        Assert.Contains(devices, d => d.DeviceId == "live-test-pc" && d.Current);
        await m.SignOutAsync();
        Assert.False(m.IsSignedIn);
    }
}
