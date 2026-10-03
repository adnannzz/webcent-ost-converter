using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace OstConverterFree.Accounts;

public sealed record LicenseClaims(string Email, string Plan, string DeviceId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

public enum TokenStatus { Valid, Malformed, BadSignature, Expired, WrongDevice }

public sealed record TokenResult(TokenStatus Status, LicenseClaims? Claims)
{
    public bool IsValid => Status == TokenStatus.Valid;
}

/// <summary>Verifies the server's Ed25519-signed account tokens (compact JWS, alg EdDSA) entirely offline.</summary>
public sealed class TokenVerifier
{
    /// <summary>The server stamps account tokens with this audience.</summary>
    public const string Audience = "ost-free";

    readonly Ed25519PublicKeyParameters _key;

    public TokenVerifier(string publicKeyPem)
    {
        var b64 = string.Concat(publicKeyPem.Split('\n', '\r')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
        _key = PublicKeyFactory.CreateKey(Convert.FromBase64String(b64)) as Ed25519PublicKeyParameters
               ?? throw new ArgumentException("The public key is not an Ed25519 key.", nameof(publicKeyPem));
    }

    public TokenResult Verify(string? token, string deviceId, DateTimeOffset now)
    {
        var bad = new TokenResult(TokenStatus.Malformed, null);
        if (string.IsNullOrWhiteSpace(token)) return bad;
        var parts = token.Split('.');
        if (parts.Length != 3) return bad;

        byte[] header, payload, signature;
        try
        {
            header = Base64Url.Decode(parts[0]);
            payload = Base64Url.Decode(parts[1]);
            signature = Base64Url.Decode(parts[2]);
        }
        catch (FormatException) { return bad; }

        try
        {
            using var h = JsonDocument.Parse(header);
            // Only EdDSA is accepted: this blocks "alg":"none" and algorithm-confusion tricks.
            if (!h.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "EdDSA") return bad;
        }
        catch (JsonException) { return bad; }

        if (signature.Length != 64) return new TokenResult(TokenStatus.BadSignature, null);
        var signer = new Ed25519Signer();
        signer.Init(false, _key);
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        signer.BlockUpdate(signed, 0, signed.Length);
        if (!signer.VerifySignature(signature)) return new TokenResult(TokenStatus.BadSignature, null);

        LicenseClaims claims;
        try
        {
            using var p = JsonDocument.Parse(payload);
            var r = p.RootElement;
            // Only account tokens made for this app: a token issued for any other product or audience is not accepted here.
            if (r.GetProperty("aud").GetString() != Audience || r.GetProperty("plan").GetString() != "member") return bad;
            claims = new LicenseClaims(
                r.GetProperty("sub").GetString() ?? "",
                "member",
                r.GetProperty("device_id").GetString() ?? "",
                DateTimeOffset.FromUnixTimeSeconds(r.GetProperty("iat").GetInt64()),
                DateTimeOffset.FromUnixTimeSeconds(r.GetProperty("exp").GetInt64()));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return bad;
        }

        if (!string.Equals(claims.DeviceId, deviceId, StringComparison.Ordinal)) return new TokenResult(TokenStatus.WrongDevice, claims);
        if (claims.ExpiresAt <= now) return new TokenResult(TokenStatus.Expired, claims);
        return new TokenResult(TokenStatus.Valid, claims);
    }
}

static class Base64Url
{
    public static byte[] Decode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        return Convert.FromBase64String(t);
    }
}
