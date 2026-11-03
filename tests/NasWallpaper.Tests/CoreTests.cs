using NasWallpaper.Models;
using NasWallpaper.Services.Cache;
using NasWallpaper.Services.Configuration;
using NasWallpaper.Services.Editing;
using NasWallpaper.Services.Preview;
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
        // 设计是“最近 10 张不重复”，因此任意连续 10 次内部不能出现重复；
        // 第 11 次允许和第 1 次相同，所以不能要求 12 次全部不同。
        for (var i = 0; i + 10 <= picked.Count; i++)
        {
            var window = picked.Skip(i).Take(10).ToList();
            Assert.Equal(10, window.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
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

    [Fact]
    public async Task UploadingImageRenamesInsteadOfOverwriting()
    {
        var source = Path.Combine(_root, "pictures");
        Directory.CreateDirectory(source);
        var existing = Path.Combine(source, "photo.jpg");
        File.WriteAllText(existing, "keep");
        var local = Path.Combine(_root, "photo.jpg");
        File.WriteAllText(local, "new");

        var input = (IImageUploader)ImageSourceFactory.Create(
            new SourceConfig { Kind = SourceKind.Local, Address = source });
        var uploaded = await input.UploadAsync(local, default);

        Assert.Equal("photo (1).jpg", Path.GetFileName(uploaded));
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.Equal("new", File.ReadAllText(uploaded));
    }

    [Fact]
    public async Task ImportedImageIsCopiedToSourceAndApplied()
    {
        var source = Path.Combine(_root, "pictures");
        Directory.CreateDirectory(source);
        var local = Path.Combine(_root, "dropped.jpg");
        CreateImage(local, 1920, 1080);

        var store = new ConfigStore(Path.Combine(_root, "data"));
        var wallpaper = new FakeWallpaper();
        var config = new AppConfig();
        config.Monitors.Add(new MonitorConfig
        {
            MonitorId = "display-1",
            Source = new() { Kind = SourceKind.Local, Address = source }
        });

        await using var engine = new RotationEngine(store, config, wallpaper);
        var result = await engine.ImportImageAsync("display-1", local);

        Assert.True(result.Success, result.Message);
        var copied = Path.Combine(source, "dropped.jpg");
        Assert.True(File.Exists(copied));
        var applied = Assert.Single(wallpaper.Applied);
        Assert.Equal(copied, applied.Path);
    }

    [Fact]
    public async Task GalleryListsPortraitImagesEvenWhenRotationHidesThem()
    {
        var source = Path.Combine(_root, "pictures");
        CreateImage(Path.Combine(source, "landscape.jpg"), 1920, 1080);
        CreateImage(Path.Combine(source, "portrait.jpg"), 1080, 1920);

        var store = new ConfigStore(Path.Combine(_root, "data"));
        var config = new AppConfig();
        config.Monitors.Add(new MonitorConfig
        {
            MonitorId = "display-1",
            IncludePortrait = false,
            Source = new() { Kind = SourceKind.Local, Address = source }
        });

        await using var engine = new RotationEngine(store, config, new FakeWallpaper());
        var files = await engine.GetImagesAsync("display-1", refresh: true, default);

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public async Task ThumbnailLoaderDecodesLocalImageAndWritesCache()
    {
        var source = Path.Combine(_root, "pictures");
        var path = Path.Combine(source, "wallpaper.jpg");
        CreateImage(path, 1920, 1080);
        var info = new FileInfo(path);
        var file = new SourceFile(info.FullName, info.Length, info.LastWriteTimeUtc);

        var cache = new ImageCache(Path.Combine(_root, "cache"));
        using var loader = new GalleryThumbnailLoader(
            new SourceConfig { Kind = SourceKind.Local, Address = source }, cache);

        var preview = await loader.LoadAsync(file, 240, default);

        Assert.NotNull(preview);
        Assert.Equal(1920, preview!.Width);
        Assert.Equal(1080, preview.Height);
        Assert.True(preview.Image.IsFrozen);
        Assert.InRange(preview.Image.PixelWidth, 1, 240);
        Assert.NotEmpty(Directory.GetFiles(cache.Root, "thumb-*.cache"));

        // 第二次命中缩略图缓存，尺寸仍来自文件名而不是缩略图本身。
        var cached = await loader.LoadAsync(file, 240, default);
        Assert.NotNull(cached);
        Assert.Equal(1920, cached!.Width);
    }

    [Fact]
    public void RotatingImageSwapsDimensions()
    {
        var path = Path.Combine(_root, "rotate.jpg");
        CreateImage(path, 320, 200);

        var clockwise = ImageRotator.Rotate(File.ReadAllBytes(path), ".jpg", RotateDirection.Clockwise);
        var frame = BitmapDecoder.Create(new MemoryStream(clockwise),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(200, frame.PixelWidth);
        Assert.Equal(320, frame.PixelHeight);

        var back = ImageRotator.Rotate(clockwise, ".jpg", RotateDirection.CounterClockwise);
        var backFrame = BitmapDecoder.Create(new MemoryStream(back),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(320, backFrame.PixelWidth);
        Assert.Equal(200, backFrame.PixelHeight);
    }

    [Fact]
    public async Task RotatingImageWritesBackToSource()
    {
        var source = Path.Combine(_root, "pictures");
        var path = Path.Combine(source, "wallpaper.jpg");
        CreateImage(path, 320, 200);
        var info = new FileInfo(path);
        var file = new SourceFile(info.FullName, info.Length, info.LastWriteTimeUtc);

        var store = new ConfigStore(Path.Combine(_root, "data"));
        var config = new AppConfig();
        config.Monitors.Add(new MonitorConfig
        {
            MonitorId = "display-1",
            Source = new() { Kind = SourceKind.Local, Address = source }
        });
        await using var engine = new RotationEngine(store, config, new FakeWallpaper());

        var result = await engine.RotateImageAsync(
            "display-1", file, RotateDirection.Clockwise, default);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Length > 0);
        var frame = BitmapDecoder.Create(
            new MemoryStream(File.ReadAllBytes(path)),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(200, frame.PixelWidth);
        Assert.Equal(320, frame.PixelHeight);
    }

    [Fact]
    public async Task MissingDisplayKeepsBoundSource()
    {
        var dir = Path.Combine(_root, "data");
        var source = Path.Combine(_root, "pictures");
        CreateImage(Path.Combine(source, "a.jpg"), 320, 200);
        var store = new ConfigStore(dir);
        var config = new AppConfig();
        config.Monitors.Add(new MonitorConfig
        {
            MonitorId = "display-1",
            Source = new() { Kind = SourceKind.Local, Address = source }
        });

        // 模拟锁屏/休眠/切换远程会话时系统暂时枚举不到显示器。
        await using var engine = new RotationEngine(store, config, new NoDisplaysWallpaper());
        engine.RefreshDisplaysNow();

        var monitor = Assert.Single(engine.Snapshot.Monitors);
        Assert.Equal(source, monitor.Source.Address);

        var reopened = new ConfigStore(dir);
        Assert.Equal(source, Assert.Single(reopened.Load().Monitors).Source.Address);
    }

    [Fact]
    public void CorruptSettingsRecoversBoundSourceFromLastGood()
    {
        var dir = Path.Combine(_root, "data");
        var store = new ConfigStore(dir);
        var config = new AppConfig();
        config.Monitors.Add(new MonitorConfig
        {
            MonitorId = "display-1",
            Source = new() { Kind = SourceKind.Smb, Address = @"\\nas\photos", Username = "user" }
        });
        store.Save(config);
        Assert.True(File.Exists(Path.Combine(dir, "settings.lastgood.json")));

        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ broken");
        var reopened = new ConfigStore(dir);
        var loaded = reopened.Load();

        Assert.True(reopened.RecoveredFromBackup);
        Assert.Equal(@"\\nas\photos", Assert.Single(loaded.Monitors).Source.Address);
    }

    [Fact]
    public void BrokenSettingsIsNotOverwrittenByAutomaticSave()
    {
        var dir = Path.Combine(_root, "data");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "not json");

        var store = new ConfigStore(dir);
        Assert.NotNull(store.Load());
        Assert.True(store.LoadFailed);

        // 后台轮播的自动保存不能拿空配置覆盖现场。
        store.Save(new AppConfig());
        Assert.Equal("not json", File.ReadAllText(path));
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

    /// <summary>模拟系统暂时枚举不到任何显示器的时刻。</summary>
    private sealed class NoDisplaysWallpaper : IWallpaperService
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => [];
        public void SetWallpaper(string monitorId, string localPath, WallpaperStyle style) { }
    }
}
