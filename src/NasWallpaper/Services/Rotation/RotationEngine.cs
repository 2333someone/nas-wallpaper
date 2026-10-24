using NasWallpaper.Models;
using NasWallpaper.Services.Cache;
using NasWallpaper.Services.Configuration;
using NasWallpaper.Services.Sources;
using NasWallpaper.Services.Wallpaper;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NasWallpaper.Services.Rotation;

public sealed class RotationEngine : IAsyncDisposable
{
    private const int RecentWindow = 10;

    private readonly ConfigStore _store;
    private ImageCache _cache;
    private string _cacheRoot;
    private readonly IWallpaperService _wallpaper;
    private readonly CancellationTokenSource _exit = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _monitorLocks = new();
    private readonly List<Task> _backgroundCopies = [];
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _network = new(2, 2);
    private readonly Dictionary<string, MonitorRuntime> _states = new();
    private readonly object _sync = new();
    private AppConfig _config;
    private Task? _loop;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDisplayCheck = DateTimeOffset.MinValue;

    public event Action? Changed;

    public RotationEngine(ConfigStore store, AppConfig config, IWallpaperService wallpaper)
    {
        _store = store;
        _config = config.Clone();
        _wallpaper = wallpaper;
        _cache = CreateCache(store.CacheDirectory);
        _cacheRoot = _cache.Root;
        EnsureMonitors();
        SaveState();
    }

    public AppConfig Snapshot { get { lock (_sync) return _config.Clone(); } }

    public IReadOnlyList<MonitorStatus> GetStatuses()
    {
        lock (_sync)
            return _config.Monitors.Select(m =>
            {
                var state = _states.TryGetValue(m.MonitorId, out var runtime) ? runtime : null;
                var message = state?.Message ?? "等待配置";
                if (_config.Paused && state?.Files is not null) message = "轮播已暂停";
                var displayName = state?.CurrentName;
                if (string.IsNullOrWhiteSpace(displayName) && state?.CurrentImage is { } current)
                    displayName = Path.GetFileName(current);
                return new MonitorStatus(m.MonitorId, message, displayName,
                    state is null || state.NextChange == DateTimeOffset.MaxValue ? null : state.NextChange,
                    state is { } s && _monitorLocks.TryGetValue(m.MonitorId, out var gate) && gate.CurrentCount == 0,
                    state?.LastSourcePath);
            }).ToList();
    }

    public void Start() => _loop ??= Task.Run(RunLoopAsync);

    public async Task ApplyConfigAsync(AppConfig config)
    {
        var cacheRoot = _store.SetCacheDirectory(config.CacheDirectory);
        if (!string.Equals(cacheRoot, _cacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            _cache = CreateCache(cacheRoot);
            _cacheRoot = _cache.Root;
            lock (_sync)
                foreach (var state in _states.Values)
                {
                    state.Files = null;
                    state.Recent.Clear();
                    state.OrientationCache.Clear();
                    state.LastScan = DateTimeOffset.MinValue;
                }
        }

        lock (_sync)
        {
            var previous = _config;
            _config = config.Clone();
            EnsureMonitors();
            foreach (var monitor in _config.Monitors)
            {
                var old = previous.Monitors.FirstOrDefault(p => p.MonitorId == monitor.MonitorId);
                var state = _states[monitor.MonitorId];
                if (old is null || !SourceEquals(old.Source, monitor.Source) || old.IncludePortrait != monitor.IncludePortrait)
                {
                    state.Files = null;
                    state.Recent.Clear();
                    state.OrientationCache.Clear();
                    state.LastScan = DateTimeOffset.MinValue;
                }
                if (old?.IntervalMinutes != monitor.IntervalMinutes || old?.Playback != monitor.Playback)
                    state.SequentialIndex = 0;
                state.NextChange = DateTimeOffset.UtcNow;
                state.Failures = 0;
            }
        }
        await RefreshDisplaysAsync();
        if (!config.Paused) await NextAsync();
        else Changed?.Invoke();
    }

    public Task TogglePauseAsync()
    {
        lock (_sync)
        {
            _config.Paused = !_config.Paused;
            foreach (var state in _states.Values)
                if (!_config.Paused) state.NextChange = DateTimeOffset.UtcNow;
        }
        SaveState();
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async Task NextAsync(string? monitorId = null)
    {
        MonitorConfig[] profiles;
        lock (_sync)
            profiles = _config.Monitors.Where(p => p.Enabled &&
                (monitorId is null || p.MonitorId == monitorId)).Select(p => p.CloneMonitor()).ToArray();
        await Task.WhenAll(profiles.Select(AdvanceSafelyAsync));
        Changed?.Invoke();
    }

    /// <summary>
    /// 删除 NAS 上的源图片（本地文件夹则删本地文件），而不是删缓存副本。
    /// 只允许删除当前显示器配置目录下的图片文件。
    /// </summary>
    public async Task<bool> DeleteImageAsync(string monitorId, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return false;
        if (!IImageSource.IsImagePath(sourcePath)) return false;

        MonitorConfig? monitor;
        lock (_sync) monitor = _config.Monitors.FirstOrDefault(m => m.MonitorId == monitorId);
        if (monitor is null || !IsInSource(monitor.Source, sourcePath)) return false;

        try
        {
            using var lease = new SourceLease(monitor.Source);
            await lease.Value.DeleteAsync(sourcePath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            HttpRequestException or NotSupportedException or ArgumentException)
        {
            return false;
        }

        lock (_sync)
        {
            if (_states.TryGetValue(monitorId, out var state))
            {
                state.Files?.RemoveAll(f => string.Equals(f.Location, sourcePath, StringComparison.OrdinalIgnoreCase));
                if (state.Files is { Count: 0 }) state.Files = null;
                state.Message = "已从 NAS 删除这张图片";
                state.LastScan = DateTimeOffset.MinValue;
                if (string.Equals(state.LastSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    state.LastSourcePath = null;
                    state.CurrentImage = null;
                    state.CurrentName = null;
                    state.FallbackPath = null;
                }
            }
            monitor.LastGoodPath = null;
            monitor.LastWallpaperPath = null;
        }
        SaveState();
        Changed?.Invoke();
        return true;
    }

    /// <summary>确认要删的文件确实位于该显示器的图片源目录内。</summary>
    private static bool IsInSource(SourceConfig source, string path)
    {
        if (source.Kind == SourceKind.WebDav)
        {
            if (!Uri.TryCreate(source.Address.Trim(), UriKind.Absolute, out var root) ||
                !Uri.TryCreate(path, UriKind.Absolute, out var target)) return false;
            if (!string.Equals(root.Host, target.Host, StringComparison.OrdinalIgnoreCase) ||
                root.Port != target.Port) return false;
            var rootPath = root.AbsolutePath.EndsWith('/') ? root.AbsolutePath : root.AbsolutePath + "/";
            return target.AbsolutePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var root = Path.GetFullPath(source.Address.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (!_exit.IsCancellationRequested)
            {
                MonitorConfig[] due;
                lock (_sync)
                    due = _config.Paused ? [] : _config.Monitors.Where(p => p.Enabled &&
                        _states[p.MonitorId].NextChange <= DateTimeOffset.UtcNow)
                        .Select(p => p.CloneMonitor()).ToArray();
                await Task.WhenAll(due.Select(AdvanceSafelyAsync));
                if (DateTimeOffset.UtcNow - _lastDisplayCheck > TimeSpan.FromSeconds(15))
                {
                    _lastDisplayCheck = DateTimeOffset.UtcNow;
                    try { RefreshDisplaysNow(); }
                    catch (System.Runtime.InteropServices.COMException) { }
                }
                if (!_config.Paused && DateTimeOffset.UtcNow - _lastCleanup > TimeSpan.FromHours(1))
                {
                    await CleanupAsync();
                    _lastCleanup = DateTimeOffset.UtcNow;
                }
                await Task.Delay(TimeSpan.FromSeconds(5), _exit.Token);
            }
        }
        catch (OperationCanceledException) when (_exit.IsCancellationRequested) { }
    }

    private async Task AdvanceSafelyAsync(MonitorConfig profile)
    {
        var gate = _monitorLocks.GetOrAdd(profile.MonitorId, _ => new(1, 1));
        if (!await gate.WaitAsync(0)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_exit.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await AdvanceAsync(profile, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_exit.IsCancellationRequested)
        {
            await UseFallbackAsync(profile, ex);
        }
        finally { gate.Release(); }
    }

    private async Task AdvanceAsync(MonitorConfig profile, CancellationToken ct)
    {
        MonitorRuntime state;
        lock (_sync) state = _states[profile.MonitorId];
        if (string.IsNullOrWhiteSpace(profile.Source.Address))
        {
            state.NextChange = DateTimeOffset.MaxValue;
            state.Message = "请在设置中配置图片目录";
            return;
        }

        if (state.Files is null || DateTimeOffset.UtcNow - state.LastScan > TimeSpan.FromMinutes(_config.ScanIntervalMinutes))
        {
            await _scanGate.WaitAsync(ct);
            try
            {
                using var source = new SourceLease(profile.Source);
                var files = new List<SourceFile>();
                await _network.WaitAsync(ct);
                try { await foreach (var file in source.Value.ScanAsync(ct)) files.Add(file); }
                finally { _network.Release(); }
                if (files.Count == 0) throw new IOException("图片源为空");

                var previous = state.LastSourcePath;
                state.Files = files;
                state.LastScan = DateTimeOffset.UtcNow;
                // 重新扫描后从当前这张的下一张继续，避免顺序播放每次刷新都从头开始。
                var index = string.IsNullOrEmpty(previous) ? -1 : files.FindIndex(
                    f => string.Equals(f.Location, previous, StringComparison.OrdinalIgnoreCase));
                state.SequentialIndex = index >= 0 ? (index + 1) % files.Count : 0;
            }
            finally { _scanGate.Release(); }
        }

        using var media = new SourceLease(profile.Source);
        var chosen = await PickNextAsync(profile, state, media.Value, ct);
        await ApplyChosenAsync(profile, state, media.Value, chosen, trackHistory: true, ct);
    }

    /// <summary>把选中的图片应用到桌面（NAS 原图直连，WebDAV 下载后应用）。</summary>
    private async Task ApplyChosenAsync(
        MonitorConfig profile, MonitorRuntime state, IImageSource source,
        SourceFile chosen, bool trackHistory, CancellationToken ct)
    {
        var previousLocation = state.LastSourcePath;
        string appliedPath;
        string message;
        if (IsDirectSource(profile.Source.Kind))
        {
            // SMB / 本地：直接把 NAS 上的原图交给系统当壁纸，缓存只作断网备份。
            appliedPath = chosen.Location;
            try
            {
                await Task.Run(() => _wallpaper.SetWallpaper(profile.MonitorId, appliedPath, Snapshot.Style), ct);
                message = "来自 NAS";
                QueueFallbackCopy(profile, chosen);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or
                UnauthorizedAccessException or NotSupportedException)
            {
                // 个别环境不接受 UNC 路径，退回“下载到缓存再设置”。
                var cached = await _cache.EnsureAsync(source, chosen, profile.Orientation, ct)
                    ?? throw new IOException("系统无法直接使用该网络路径，缓存副本也失败了。", ex);
                appliedPath = cached.Path;
                state.FallbackPath = cached.Path;
                await Task.Run(() => _wallpaper.SetWallpaper(profile.MonitorId, appliedPath, Snapshot.Style), ct);
                message = "来自 NAS · 已缓存";
            }
        }
        else
        {
            // WebDAV 是 HTTP 资源，系统无法直接当壁纸，只能先下载。
            var cached = await _cache.EnsureAsync(source, chosen, profile.Orientation, ct)
                ?? throw new IOException("图片下载失败");
            appliedPath = cached.Path;
            state.FallbackPath = cached.Path;
            await Task.Run(() => _wallpaper.SetWallpaper(profile.MonitorId, appliedPath, Snapshot.Style), ct);
            message = "来自 WebDAV · 已缓存";
        }

        if (trackHistory) state.PreviousPath = previousLocation;
        state.CurrentImage = appliedPath;
        state.LastSourcePath = chosen.Location;
        state.CurrentName = Path.GetFileName(chosen.Location);
        state.Failures = 0;
        state.Message = message;
        state.NextChange = DateTimeOffset.UtcNow.AddMinutes(profile.IntervalMinutes);
        lock (_sync)
        {
            var current = _config.Monitors.FirstOrDefault(p => p.MonitorId == profile.MonitorId);
            if (current is not null) current.LastWallpaperPath = appliedPath;
        }
        SaveState();
    }

    /// <summary>回到上一张：直接应用“上一张”记录的那张图（本地/SMB 用原图，WebDAV 用缓存副本）。</summary>
    public async Task PreviousAsync(string? monitorId = null)
    {
        MonitorConfig[] profiles;
        lock (_sync)
            profiles = _config.Monitors.Where(p => p.Enabled &&
                (monitorId is null || p.MonitorId == monitorId)).Select(p => p.CloneMonitor()).ToArray();

        foreach (var profile in profiles)
        {
            var gate = _monitorLocks.GetOrAdd(profile.MonitorId, _ => new(1, 1));
            if (!await gate.WaitAsync(0)) continue;
            try
            {
                var state = _states[profile.MonitorId];
                var previous = state.PreviousPath;
                var file = string.IsNullOrWhiteSpace(previous) || state.Files is null
                    ? null
                    : state.Files.FirstOrDefault(f =>
                        string.Equals(f.Location, previous, StringComparison.OrdinalIgnoreCase));
                if (file is null)
                {
                    state.Message = "没有上一张";
                    continue;
                }

                using var media = new SourceLease(profile.Source);
                await ApplyChosenAsync(profile, state, media.Value, file, trackHistory: false, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Runtime.InteropServices.COMException or NotSupportedException or HttpRequestException)
            {
                if (_states.TryGetValue(profile.MonitorId, out var failed))
                    failed.Message = "无法显示上一张：" + DescribeFailure(ex);
            }
            finally { gate.Release(); }
        }
        Changed?.Invoke();
    }

    private static bool IsDirectSource(SourceKind kind) =>
        kind is SourceKind.Smb or SourceKind.Local;

    /// <summary>直接使用 NAS 原图时，顺手在后台缓存一份，断网时才有东西可退。</summary>
    private void QueueFallbackCopy(MonitorConfig profile, SourceFile file)
    {
        var copy = Task.Run(async () =>
        {
            try
            {
                using var lease = new SourceLease(profile.Source);
                var cached = await _cache.EnsureAsync(
                    lease.Value, file, WallpaperOrientation.Any, CancellationToken.None);
                if (cached is null) return;

                if (_states.TryGetValue(profile.MonitorId, out var state))
                    state.FallbackPath = cached.Path;
                lock (_sync)
                {
                    var current = _config.Monitors.FirstOrDefault(p => p.MonitorId == profile.MonitorId);
                    if (current is not null) current.LastGoodPath = cached.Path;
                }
                SaveState();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                HttpRequestException or NotSupportedException or OperationCanceledException)
            {
            }
        });
        lock (_backgroundCopies)
        {
            _backgroundCopies.RemoveAll(t => t.IsCompleted);
            _backgroundCopies.Add(copy);
        }
    }

    /// <summary>
    /// 随机模式：在整个目录清单里随机取一张，并避开最近出现过的若干张，
    /// 不再预生成固定的小列表，避免“总是那 16 张按同样顺序循环”的规律感。
    /// </summary>
    private async Task<SourceFile> PickNextAsync(
        MonitorConfig profile, MonitorRuntime state, IImageSource source, CancellationToken ct)
    {
        var files = state.Files!;
        if (files.Count == 0) throw new IOException("图片源为空");

        if (profile.Playback == PlaybackMode.Sequential)
        {
            for (var attempt = 0; attempt < files.Count; attempt++)
            {
                var index = state.SequentialIndex % files.Count;
                state.SequentialIndex = (index + 1) % files.Count;
                var candidate = files[index];
                if (await MatchesAsync(state, source, candidate, profile, ct)) return candidate;
            }
            throw new IOException("没有符合方向的图片");
        }

        var window = Math.Min(RecentWindow, Math.Max(0, files.Count - 1));
        var start = Random.Shared.Next(files.Count);
        for (var attempt = 0; attempt < files.Count; attempt++)
        {
            var candidate = files[(start + attempt) % files.Count];
            if (string.Equals(candidate.Location, state.LastSourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (state.Recent.Contains(candidate.Location)) continue;
            if (!await MatchesAsync(state, source, candidate, profile, ct)) continue;
            Remember(state, candidate.Location, window);
            return candidate;
        }

        // 全被“最近用过”挡住了，清空历史后再随机一轮。
        state.Recent.Clear();
        for (var attempt = 0; attempt < files.Count; attempt++)
        {
            var candidate = files[(start + attempt) % files.Count];
            if (string.Equals(candidate.Location, state.LastSourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!await MatchesAsync(state, source, candidate, profile, ct)) continue;
            Remember(state, candidate.Location, window);
            return candidate;
        }
        throw new IOException("没有符合方向的图片");
    }

    private async Task<bool> MatchesAsync(
        MonitorRuntime state, IImageSource source, SourceFile file, MonitorConfig profile, CancellationToken ct)
    {
        var key = file.Location + "|" + file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (state.OrientationCache.TryGetValue(key, out var cached)) return cached;

        await _network.WaitAsync(ct);
        bool matches;
        try { matches = await ImageCache.MatchesOrientationAsync(source, file, profile.Orientation, ct); }
        finally { _network.Release(); }
        state.OrientationCache[key] = matches;
        return matches;
    }

    private static void Remember(MonitorRuntime state, string location, int window)
    {
        state.Recent.Enqueue(location);
        while (state.Recent.Count > window) state.Recent.Dequeue();
    }

    private async Task UseFallbackAsync(MonitorConfig profile, Exception? error = null)
    {
        var state = _states[profile.MonitorId];
        var path = new[] { state.FallbackPath, profile.LastGoodPath, profile.LocalDefaultPath }
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        if (path is null) path = EnsureDefaultImage();
        if (path is not null && state.CurrentImage != path)
            await Task.Run(() => _wallpaper.SetWallpaper(profile.MonitorId, path, Snapshot.Style));
        state.CurrentImage = path ?? state.CurrentImage;
        state.CurrentName = path is null ? state.CurrentName : Path.GetFileName(path);
        // 离线兜底时显示的是缓存副本，NAS 上的原图不再对应，删除按钮先收起来。
        state.LastSourcePath = null;
        state.Failures++;
        state.Message = DescribeFailure(error);
        state.NextChange = DateTimeOffset.UtcNow.AddMinutes(Math.Min(15, 1 << Math.Min(state.Failures, 4)));
        state.LastScan = DateTimeOffset.MinValue;
    }

    private static string DescribeFailure(Exception? error)
    {
        var message = error?.GetBaseException().Message.Trim();
        if (string.IsNullOrWhiteSpace(message)) return "NAS 暂不可用，保留当前壁纸";
        return message.Length <= 64 ? message : message[..64] + "…";
    }

    private Task RefreshDisplaysAsync()
    {
        RefreshDisplaysNow();
        return Task.CompletedTask;
    }

    /// <summary>按当前真实连接的显示器同步配置，返回是否有增删（界面可据此立即刷新列表）。</summary>
    public bool RefreshDisplaysNow()
    {
        bool changed;
        lock (_sync)
        {
            var before = _config.Monitors.Select(p => p.MonitorId).ToList();
            var connected = _wallpaper.GetDisplays();
            _config.Monitors.RemoveAll(p => connected.All(d => d.Id != p.MonitorId));
            foreach (var display in connected) EnsureMonitor(display);
            changed = !before.SequenceEqual(_config.Monitors.Select(p => p.MonitorId));
        }
        if (!changed) return false;
        SaveState();
        Changed?.Invoke();
        return true;
    }

    private void EnsureMonitors()
    {
        foreach (var display in _wallpaper.GetDisplays()) EnsureMonitor(display);
    }

    private void EnsureMonitor(DisplayInfo display)
    {
        var monitor = _config.Monitors.FirstOrDefault(p => p.MonitorId == display.Id);
        if (monitor is null)
        {
            monitor = new() { MonitorId = display.Id, Name = display.Name };
            _config.Monitors.Add(monitor);
        }
        else monitor.Name = display.Name;
        if (!_states.ContainsKey(display.Id)) _states[display.Id] = new MonitorRuntime();
    }

    private async Task CleanupAsync()
    {
        var config = Snapshot;
        var paths = config.Monitors.SelectMany(p => new[] { p.LastGoodPath, p.LastWallpaperPath, p.LocalDefaultPath })
            .Where(p => p is not null).Cast<string>();
        try
        {
            await Task.Run(() => ImageCache.Cleanup(_cache.Root,
                TimeSpan.FromDays(config.CacheRetentionDays), config.CacheLimitMb * 1024L * 1024L, paths), _exit.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void SaveState()
    {
        try { _store.Save(Snapshot); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>自定义缓存目录不可用时（例如移动硬盘没插）回退到默认目录，避免整个应用起不来。</summary>
    private ImageCache CreateCache(string root)
    {
        try
        {
            return new ImageCache(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ImageCache(_store.DefaultCacheDirectory);
        }
    }

    private string EnsureDefaultImage()
    {
        var path = Path.Combine(_cache.Root, "default.png");
        if (File.Exists(path)) return path;
        var pixels = new byte[1920 * 1080 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 130;
            pixels[i + 1] = 80;
            pixels[i + 2] = 40;
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(1920, 1080, 96, 96, PixelFormats.Bgra32, null, pixels, 1920 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static bool SourceEquals(SourceConfig a, SourceConfig b) =>
        a.Kind == b.Kind && a.Address == b.Address && a.Username == b.Username &&
        a.EncryptedPassword == b.EncryptedPassword && a.Recursive == b.Recursive &&
        a.AllowInvalidCertificate == b.AllowInvalidCertificate;

    public async ValueTask DisposeAsync()
    {
        await _exit.CancelAsync();
        Task[] copies;
        lock (_backgroundCopies) copies = _backgroundCopies.ToArray();
        if (copies.Length > 0)
        {
            try { await Task.WhenAll(copies).WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        }
        if (_loop is not null)
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        _exit.Dispose();
    }

    private sealed class MonitorRuntime
    {
        public List<SourceFile>? Files;
        public int SequentialIndex;
        public readonly Queue<string> Recent = new();
        public readonly Dictionary<string, bool> OrientationCache = new(StringComparer.OrdinalIgnoreCase);
        public int Failures;
        public string? CurrentImage;
        public string? CurrentName;
        public string? LastSourcePath;
        public string? PreviousPath;
        public string? FallbackPath;
        public string Message = "等待配置";
        public DateTimeOffset LastScan = DateTimeOffset.MinValue;
        public DateTimeOffset NextChange = DateTimeOffset.UtcNow;
    }

    private sealed class SourceLease(SourceConfig config) : IDisposable
    {
        public IImageSource Value { get; } = ImageSourceFactory.Create(config);
        public void Dispose() => (Value as IDisposable)?.Dispose();
    }
}

internal static class MonitorConfigClone
{
    public static MonitorConfig CloneMonitor(this MonitorConfig value) => JsonConfig.Clone(value);
}
