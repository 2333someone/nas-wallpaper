using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NasWallpaper.Services.Update;

public sealed record UpdateInfo(
    string Version,
    string Tag,
    string Notes,
    string PageUrl,
    string? AssetName,
    string? DownloadUrl,
    long Size);

/// <summary>
/// 直接以 GitHub Releases 作为更新源：检查 /releases/latest，
/// 把 tag 当版本号，把 .exe 资产当安装包。
/// </summary>
public static class UpdateService
{
    private const string RepositoryPrefix = "https://api.github.com/repos/";
    private static readonly HttpClient Client = CreateClient();

    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 1, 0);

    /// <summary>接受 owner/repo 或 https://github.com/owner/repo 两种写法。</summary>
    public static bool TryNormalizeRepository(string? raw, out string repository)
    {
        repository = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var value = raw.Trim().TrimEnd('/');

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            value = segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : "";
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        repository = parts[0] + "/" + parts[1];
        return true;
    }

    /// <summary>
    /// 先走 GitHub API；某些网络下 api.github.com 会被拦（403/超时），
    /// 这时退回网页方式：从 releases/latest 的跳转拿 tag，再从资产片段里找 .exe。
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(string repository, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            return await CheckViaApiAsync(repository, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            return await CheckViaWebAsync(repository, ct).ConfigureAwait(false);
        }
    }

    private static async Task<UpdateInfo?> CheckViaWebAsync(string repository, CancellationToken ct)
    {
        var tag = await GetLatestTagAsync(repository, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tag))
            throw new InvalidOperationException("仓库不存在，或还没有发布过 Release。");

        var normalized = tag.TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(normalized, out var version))
            throw new InvalidOperationException($"无法识别版本号“{tag}”，建议 tag 使用 v1.2.3 形式。");
        if (version <= CurrentVersion) return null;

        var downloadUrl = await FindAssetAsync(repository, tag, ct).ConfigureAwait(false);
        return new UpdateInfo(version.ToString(), tag, "",
            $"https://github.com/{repository}/releases/tag/{tag}",
            Path.GetFileName(new Uri(downloadUrl).AbsolutePath), downloadUrl, 0);
    }

    private static async Task<string?> GetLatestTagAsync(string repository, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://github.com/{repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (NAS-Wallpaper)");
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

        var location = response.Headers.Location?.ToString()
            ?? response.RequestMessage?.RequestUri?.ToString() ?? "";
        var index = location.IndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
        return index >= 0
            ? Uri.UnescapeDataString(location[(index + "/releases/tag/".Length)..].TrimEnd('/'))
            : null;
    }

    private static async Task<string> FindAssetAsync(string repository, string tag, CancellationToken ct)
    {
        var fragmentUrl = $"https://github.com/{repository}/releases/expanded_assets/{Uri.EscapeDataString(tag)}";
        var html = await Client.GetStringAsync(fragmentUrl, ct).ConfigureAwait(false);

        string? preferred = null;
        string? fallback = null;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            html, "href=\"(?<u>[^\"]*/releases/download/[^\"]+\\.exe)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var value = match.Groups["u"].Value;
            var url = value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? value
                : "https://github.com" + value;
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            if (name.Equals("NAS Wallpaper.exe", StringComparison.OrdinalIgnoreCase)) return url;
            if (IsPreferredAsset(name)) preferred ??= url;
            fallback ??= url;
        }

        return preferred
            ?? fallback
            ?? $"https://github.com/{repository}/releases/download/{tag}/NAS Wallpaper.exe";
    }

    /// <summary>同一版本可能同时提供自包含版和框架依赖版，自包含版更适合直接替换。</summary>
    private static bool IsPreferredAsset(string name) =>
        name.Contains("NAS Wallpaper", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("framework", StringComparison.OrdinalIgnoreCase);

    private static async Task<UpdateInfo?> CheckViaApiAsync(string repository, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{RepositoryPrefix}{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("仓库不存在，或还没有发布过 Release。");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub 返回 {(int)response.StatusCode} {response.ReasonPhrase}",
                null, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var bodyValue) ? bodyValue.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var pageValue) ? pageValue.GetString() ?? "" : "";

        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var version))
            throw new InvalidOperationException($"无法识别版本号“{tag}”，建议 tag 使用 v1.2.3 形式。");

        if (version <= CurrentVersion) return null;

        string? assetName = null;
        string? downloadUrl = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            string? fallbackName = null, fallbackUrl = null;
            long fallbackSize = 0;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                var bytes = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var b) ? b : 0;

                if (name.Equals("NAS Wallpaper.exe", StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name; downloadUrl = url; size = bytes;
                    break;
                }
                if (IsPreferredAsset(name))
                {
                    assetName = name; downloadUrl = url; size = bytes;
                }
                else if (fallbackName is null)
                {
                    fallbackName = name; fallbackUrl = url; fallbackSize = bytes;
                }
            }
            assetName ??= fallbackName;
            downloadUrl ??= fallbackUrl;
            if (size == 0) size = fallbackSize;
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("这个 Release 里没有 .exe 资产，无法自动更新。");

        return new UpdateInfo(version.ToString(), tag, notes, page, assetName, downloadUrl, size);
    }

    public static async Task DownloadAsync(
        UpdateInfo info, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            throw new InvalidOperationException("没有可下载的地址。");

        using var response = await Client.GetAsync(
            info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? info.Size;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous);

        var buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if (total > 0) progress?.Report((double)received / total);
        }
    }

    /// <summary>
    /// 用新文件替换当前 exe 并重启。运行中的 exe 允许改名，所以先改名再把新文件放到原位；
    /// 新进程带 --wait-for-exit 参数，等旧进程退出后再启动。
    /// </summary>
    public static void ApplyAndRestart(string downloadedExe)
    {
        var current = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位当前程序路径。");
        var backup = current + ".old";

        try
        {
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        File.Move(current, backup);
        try
        {
            File.Move(downloadedExe, current);
        }
        catch
        {
            try { File.Move(backup, current); } catch (Exception ex) when (ex is IOException) { }
            throw;
        }

        Process.Start(new ProcessStartInfo(current, $"--wait-for-exit {Environment.ProcessId}")
        {
            UseShellExecute = false
        });
    }

    /// <summary>启动时清理上一次更新留下的 .old 文件。</summary>
    public static void CleanupOldVersion()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current)) return;
        try
        {
            var backup = current + ".old";
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NAS-Wallpaper", "1.0"));
        return client;
    }
}
