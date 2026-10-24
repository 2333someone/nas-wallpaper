using NasWallpaper.Models;
using NasWallpaper.Services.Cache;
using NasWallpaper.Services.Configuration;
using NasWallpaper.Services.Rotation;
using NasWallpaper.Services.Sources;
using NasWallpaper.Services.Update;
using NasWallpaper.Services.Wallpaper;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace NasWallpaper.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NAS Wallpaper Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CacheFiltersLandscapeUnlessPortraitAllowed()
    {
        var landscapePath = Path.Combine(_root, "landscape.jpg");
        var portraitPath = Path.Combine(_root, "portrait.jpg");
        CreateImage(landscapePath, 1920, 1080);
        CreateImage(portraitPath, 1080, 1920);

        var input = ImageSourceFactory.Create(new SourceConfig { Kind = SourceKind.Local, Address = _root });
        var files = new List<SourceFile>();
        await foreach (var item in input.ScanAsync(default)) files.Add(item);
        var landscape = files.Single(f => f.Location.EndsWith("landscape.jpg", StringComparison.OrdinalIgnoreCase));
        var portrait = files.Single(f => f.Location.EndsWith("portrait.jpg", StringComparison.OrdinalIgnoreCase));

        var cache = new ImageCache(Path.Combine(_root, "cache"));
        var accepted = await cache.EnsureAsync(input, landscape, WallpaperOrientation.Landscape, default);
        Assert.NotNull(accepted);
        Assert.True(File.Exists(accepted.Path));

        Assert.Null(await cache.EnsureAsync(input, portrait, WallpaperOrientation.Landscape, default));
        Assert.NotNull(await cache.EnsureAsync(input, portrait, WallpaperOrientation.Any, default));
        Assert.Equal(accepted.Path,
            (await cache.EnsureAsync(input, landscape, WallpaperOrientation.Landscape, default))!.Path);
    }

    [Fact]
    public async Task CacheRejectsFilePretendingToBeImage()
    {
        var bytes = "not an image"u8.ToArray();
        var cache = new ImageCache(_root);
        var file = new SourceFile("x", bytes.Length, DateTimeOffset.UtcNow);
        Assert.Null(await cache.EnsureAsync(new MemorySource(bytes), file, WallpaperOrientation.Landscape, default));
        Assert.Empty(Directory.GetFiles(_root, "*.cache"));
    }

    [Fact]
    public void CacheKeyTracksRemoteMetadata()
    {
        var a = new SourceFile("file.jpg", 100, DateTimeOffset.UnixEpoch);
        Assert.Equal(ImageCache.CreateKey(a), ImageCache.CreateKey(a));
        Assert.NotEqual(ImageCache.CreateKey(a), ImageCache.CreateKey(a with { Length = 101 }));
    }

    [Fact]
    public void CleanupProtectsCurrentImageAndEnforcesCapacity()
    {
        Directory.CreateDirectory(_root);
        var keep = Write("keep.cache", 10);
        var old = Write("old.cache", 20);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        var removed = ImageCache.Cleanup(_root, TimeSpan.FromDays(14), 0, [keep]);
        Assert.Equal(1, removed);
        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void ConfigRoundTripsAndRecoversFromInvalidJson()
    {
        var store = new ConfigStore(_root);
        var config = new AppConfig { CacheLimitMb = 512 };
        store.Save(config);
        Assert.Equal(512, store.Load().CacheLimitMb);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "invalid");
        Assert.Equal(1024, store.Load().CacheLimitMb);
    }

    [Fact]
    public void CacheDirectoryCanBeCustomizedAndSurvivesRestart()
    {
        var custom = Path.Combine(_root, "D 盘缓存");
        var store = new ConfigStore(_root);
        Assert.True(store.IsDefaultCacheDirectory);

        store.SetCacheDirectory(custom);
        Assert.Equal(custom, store.CacheDirectory);
        store.Save(new AppConfig { CacheDirectory = custom });

        var reopened = new ConfigStore(_root);
        var loaded = reopened.Load();
        Assert.Equal(custom, reopened.CacheDirectory);
        Assert.False(reopened.IsDefaultCacheDirectory);
        Assert.Equal(custom, loaded.CacheDirectory);

        var cleared = new ConfigStore(_root);
        cleared.SetCacheDirectory("");
        Assert.True(cleared.IsDefaultCacheDirectory);
    }

    [Fact]
    public void UpdateRepositoryAcceptsOwnerSlashRepoOrGitHubUrl()
    {
        Assert.True(UpdateService.TryNormalizeRepository("acme/NAS-Wallpaper", out var repo));
        Assert.Equal("acme/NAS-Wallpaper", repo);

        Assert.True(UpdateService.TryNormalizeRepository("https://github.com/acme/NAS-Wallpaper/", out repo));
        Assert.Equal("acme/NAS-Wallpaper", repo);

        Assert.True(UpdateService.TryNormalizeRepository("https://github.com/acme/NAS-Wallpaper/releases", out repo));
        Assert.Equal("acme/NAS-Wallpaper", repo);

        Assert.False(UpdateService.TryNormalizeRepository("", out _));
        Assert.False(UpdateService.TryNormalizeRepository("only-one-part", out _));
    }

    [Fact]
    public void WebDavParsesPropertiesAndEscapesSpaces()
    {
        var xml = XDocument.Parse("<D:multistatus xmlns:D='DAV:'><D:response><D:href>/photos/a%20b.jpg</D:href><D:propstat><D:prop><D:getcontentlength>123</D:getcontentlength><D:getlastmodified>Wed, 21 Oct 2015 07:28:00 GMT</D:getlastmodified></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>");
        var entry = WebDavImageSource.ParseResponses(xml, new Uri("https://nas.test/photos/")).Single();
        Assert.False(entry.IsCollection);
        Assert.Equal(123, entry.File!.Length);
        Assert.Contains("a%20b.jpg", entry.File.Location);
    }

    [Fact]
    public async Task RotationAppliesSourceImageDirectly()
    {
        var source = Path.Combine(_root, "pictures");
        CreateImage(Path.Combine(source, "wallpaper.jpg"), 1920, 1080);
        var store = new ConfigStore(Path.Combine(_root, "data"));
        var wallpaper = new FakeWallpaper();
        var config = new AppConfig();
        config.Monitors.Add(new MonitorConfig
        {
            MonitorId = "display-1",
            Source = new() { Kind = SourceKind.Local, Address = source }
        });
        await using var engine = new RotationEngine(store, config, wallpaper);
        await engine.NextAsync();
        var applied = Assert.Single(wallpaper.Applied);
        Assert.Equal("display-1", applied.Monitor);
        Assert.Equal(Path.Combine(source, "wallpaper.jpg"), applied.Path);
        Assert.True(File.Exists(applied.Path));
    }

    [Fact]
    public async Task RandomPlaybackCoversWholeFolderWithoutRepeats()
    {
        var source = Path.Combine(_root, "pictures");
        for (var i = 0; i < 24; i++)
            CreateImage(Path.Combine(source, $"p{i:00}.jpg"), 320, 200);

        var store = new ConfigStore(Path.Combine(_root, "data"));
        var wallpaper = new FakeWallpaper();
        var config = new AppConfig();
        config.Monitors.Add(new MonitorConfig
        {
            MonitorId = "display-1",
            Playback = PlaybackMode.Random,
            Source = new() { Kind = SourceKind.Local, Address = source }
        });
        await using var engine = new RotationEngine(store, config, wallpaper);
        for (var i = 0; i < 12; i++) await engine.NextAsync();

        var picked = wallpaper.Applied.Select(a => Path.GetFileName(a.Path)).ToList();
        Assert.Equal(12, picked.Count);
        // 最近 10 张不重复，所以连续 12 次应全部不同，且不再局限于固定的小列表。
        Assert.Equal(12, picked.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task DeletingCurrentImageRemovesSourceFile()
    {
        var source = Path.Combine(_root, "pictures");
        CreateImage(Path.Combine(source, "wallpaper.jpg"), 1920, 1080);
        var store = new ConfigStore(Path.Combine(_root, "data"));
        var wallpaper = new FakeWallpaper();
        var config = new AppConfig();
        config.Monitors.Add(new MonitorConfig
        {
            MonitorId = "display-1",
            Source = new() { Kind = SourceKind.Local, Address = source }
        });
        await using var engine = new RotationEngine(store, config, wallpaper);
        await engine.NextAsync();

        var sourceImage = wallpaper.Applied[0].Path;
        Assert.True(File.Exists(sourceImage));
        Assert.True(await engine.DeleteImageAsync("display-1", sourceImage));
        Assert.False(File.Exists(sourceImage));
        Assert.Null(engine.Snapshot.Monitors[0].LastGoodPath);
    }

    [Fact]
    public async Task DeletingFileOutsideSourceFolderIsRefused()
    {
        var source = Path.Combine(_root, "pictures");
        CreateImage(Path.Combine(source, "wallpaper.jpg"), 1920, 1080);
        var outside = Path.Combine(_root, "outside.jpg");
        CreateImage(outside, 800, 600);

        var store = new ConfigStore(Path.Combine(_root, "data"));
        var config = new AppConfig();
        config.Monitors.Add(new MonitorConfig
        {
            MonitorId = "display-1",
            Source = new() { Kind = SourceKind.Local, Address = source }
        });
        await using var engine = new RotationEngine(store, config, new FakeWallpaper());

        Assert.False(await engine.DeleteImageAsync("display-1", outside));
        Assert.True(File.Exists(outside));
        Assert.False(await engine.DeleteImageAsync("display-1",
            Path.Combine(store.CacheDirectory, "default.png")));
    }

    [Fact]
    public async Task MissingSourceKeepsLastGoodWithoutThrowing()
    {
        var store = new ConfigStore(_root);
        var wallpaper = new FakeWallpaper();
        var config = new AppConfig();
        config.Monitors.Add(new MonitorConfig
        {
            MonitorId = "display-1", LastGoodPath = "good.jpg",
            Source = new() { Kind = SourceKind.Local, Address = Path.Combine(_root, "offline") }
        });
        await using var engine = new RotationEngine(store, config, wallpaper);
        await engine.NextAsync();
        var fallback = Assert.Single(wallpaper.Applied);
        Assert.EndsWith("default.png", fallback.Path);
        Assert.True(File.Exists(fallback.Path));
    }

    private string Write(string name, int length)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private static void CreateImage(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 160; pixels[i + 1] = 80; pixels[i + 3] = 255; }
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class MemorySource(byte[] bytes) : IImageSource
    {
        public IAsyncEnumerable<SourceFile> ScanAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Stream> OpenReadAsync(SourceFile file, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(bytes));
        public Task DeleteAsync(string location, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeWallpaper : IWallpaperService
    {
        public List<(string Monitor, string Path)> Applied { get; } = [];
        public IReadOnlyList<DisplayInfo> GetDisplays() => [new("display-1", "主显示器", 1920, 1080, true)];
        public void SetWallpaper(string monitorId, string localPath, WallpaperStyle style) => Applied.Add((monitorId, localPath));
    }
}
