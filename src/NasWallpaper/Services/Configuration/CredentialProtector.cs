using System.Security.Cryptography;
using System.Text;

namespace NasWallpaper.Services.Configuration;

public static class CredentialProtector
{
    private static readonly byte[] Entropy = "NAS Wallpaper v1"u8.ToArray();

    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
