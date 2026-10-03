using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace OstConverterFree.Accounts;

/// <summary>A stable, non-reversible identifier for this PC, used to count devices against an account.</summary>
public sealed record DeviceIdentity(string Id, string Name)
{
    const string Salt = "OstConverterFree.device.v1";

    public static DeviceIdentity Current()
    {
        var machine = ReadMachineGuid() ?? Environment.MachineName;
        // The Windows user's SID is mixed in, so each Windows account on a shared PC counts as its own device.
        var user = CurrentUserSid() ?? Environment.UserName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Salt}|{machine}|{user}"));
        return new DeviceIdentity(Convert.ToHexString(hash)[..32].ToLowerInvariant(), Environment.MachineName);
    }

    static string? CurrentUserSid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    static string? ReadMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
