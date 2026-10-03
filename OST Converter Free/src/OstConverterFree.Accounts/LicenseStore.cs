using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OstConverterFree.Accounts;

/// <summary>What is kept on disk between runs.</summary>
public sealed record StoredLicense(string Email, string LicenseToken, string RefreshToken, DateTimeOffset LastSeen);

public interface ILicenseStore
{
    StoredLicense? Load();
    void Save(StoredLicense license);
    void Clear();
}

/// <summary>Stores the license encrypted with the Windows user's DPAPI key, so the file is useless on another account or PC.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiLicenseStore : ILicenseStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OstConverterFree.account.v1");
    readonly string _path;

    public DpapiLicenseStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OstConverterFree", "account.dat");
    }

    public StoredLicense? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredLicense>(plain);
        }
        catch (Exception e) when (e is CryptographicException or IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // unreadable or foreign file: behave as signed out
        }
    }

    public void Save(StoredLicense license)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(license), Entropy, DataProtectionScope.CurrentUser);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, _path, overwrite: true);
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nothing more to do */ }
    }
}

public sealed class InMemoryLicenseStore : ILicenseStore
{
    StoredLicense? _value;
    public StoredLicense? Load() => _value;
    public void Save(StoredLicense license) => _value = license;
    public void Clear() => _value = null;
}
