using System.IO;
using System.Runtime.InteropServices;

namespace NasWallpaper.Services.Sources;

/// <summary>
/// 当你填了账号密码时，用 Windows 的“映射网络驱动器”接口建立一次 SMB 会话，
/// 后续的目录枚举与文件读取就走这条已认证的会话。
/// </summary>
internal static class SmbConnection
{
    private const int ResourceTypeDisk = 1;
    private const int ErrorAlreadyAssigned = 1219;
    private static readonly HashSet<string> Connected = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    public static void EnsureConnected(string uncPath, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var share = ShareRoot(uncPath);
        if (share is null) return;

        lock (Sync)
        {
            if (!Connected.Add(share)) return;
        }

        var resource = new NetResource
        {
            Type = ResourceTypeDisk,
            RemoteName = share
        };
        var code = WNetAddConnection2(ref resource, password, username, 0);
        if (code is 0 or ErrorAlreadyAssigned) return;

        lock (Sync) Connected.Remove(share);
        throw new IOException(code switch
        {
            1326 => "SMB 登录失败：账号或密码不正确。",
            53 => "SMB 登录失败：找不到网络路径，请检查 NAS 地址。",
            86 => "SMB 登录失败：密码错误。",
            1311 => "SMB 登录失败：没有可用的登录服务器。",
            5 => "SMB 登录失败：访问被拒绝。",
            _ => $"SMB 登录失败（错误码 {code}）。"
        });
    }

    /// <summary>把 \\host\share\dir\sub 收敛成 \\host\share。</summary>
    internal static string? ShareRoot(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        ref NetResource resource, string? password, string? username, int flags);
}
