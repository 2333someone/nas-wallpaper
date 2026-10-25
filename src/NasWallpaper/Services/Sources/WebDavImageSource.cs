using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NasWallpaper.Models;
using NasWallpaper.Services.Configuration;

namespace NasWallpaper.Services.Sources;

internal sealed class WebDavImageSource : IImageSource, IImageUploader, IDisposable
{
    internal const int ScanLimit = 10_000;
    private static readonly XNamespace Dav = "DAV:";
    private readonly HttpClient _client;
    private readonly Uri _root;
    private readonly bool _recursive;

    public WebDavImageSource(SourceConfig config)
        : this(config, CreateClient(config))
    {
    }

    internal WebDavImageSource(SourceConfig config, HttpClient client)
    {
        _client = client;
        _root = NormalizeRoot(new Uri(config.Address.Trim(), UriKind.Absolute));
        _recursive = config.Recursive;
        if (_root.Scheme != Uri.UriSchemeHttp && _root.Scheme != Uri.UriSchemeHttps)
            throw new NotSupportedException("WebDAV 地址必须使用 HTTP 或 HTTPS。");
    }

    public async IAsyncEnumerable<SourceFile> ScanAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var pending = new Queue<Uri>();
        var visited = new HashSet<Uri>(UriComparer.Instance);
        pending.Enqueue(_root);
        visited.Add(_root);
        var emitted = 0;

        while (pending.Count > 0 && emitted < ScanLimit)
        {
            var directory = pending.Dequeue();
            using var request = new HttpRequestMessage(
                new HttpMethod("PROPFIND"), directory);
            request.Headers.TryAddWithoutValidation("Depth", "1");
            request.Content = new StringContent(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<propfind xmlns=\"DAV:\"><prop>" +
                "<resourcetype/><getcontentlength/><getlastmodified/>" +
                "</prop></propfind>",
                new UTF8Encoding(false), "application/xml");

            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"WebDAV PROPFIND 失败：{(int)response.StatusCode} {response.ReasonPhrase}",
                    null, response.StatusCode);

            await using var responseStream = await response.Content
                .ReadAsStreamAsync(ct).ConfigureAwait(false);
            var document = await ReadXmlAsync(responseStream, ct).ConfigureAwait(false);
            foreach (var item in ParseResponses(document, directory))
            {
                ct.ThrowIfCancellationRequested();
                if (item.IsCollection)
                {
                    if (_recursive && visited.Add(item.Location)) pending.Enqueue(item.Location);
                    continue;
                }

                if (item.File is null) continue;
                yield return item.File;
                emitted++;
                if (emitted >= ScanLimit) break;
            }
        }
    }

    public async Task<Stream> OpenReadAsync(SourceFile file, CancellationToken ct)
    {
        var location = new Uri(file.Location, UriKind.Absolute);
        EnsureInRoot(location, _root);
        var response = await _client.GetAsync(
            location, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"WebDAV 下载失败：{(int)response.StatusCode} {response.ReasonPhrase}",
                    null, response.StatusCode);
            return new ResponseStream(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>删除 WebDAV 上的原始图片。</summary>
    public async Task DeleteAsync(string location, CancellationToken ct)
    {
        var target = new Uri(location, UriKind.Absolute);
        EnsureInRoot(target, _root);
        using var request = new HttpRequestMessage(HttpMethod.Delete, target);
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"WebDAV 删除失败：{(int)response.StatusCode} {response.ReasonPhrase}",
                null, response.StatusCode);
    }

    /// <summary>PUT 到图片源根目录；同名文件不覆盖，自动换下一个序号。</summary>
    public async Task<string> UploadAsync(string localPath, CancellationToken ct)
    {
        var name = Path.GetFileName(localPath);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = attempt == 0 ? name : $"{stem} ({attempt}){extension}";
            var target = new Uri(_root, Uri.EscapeDataString(candidate));
            if (await ExistsAsync(target, ct).ConfigureAwait(false)) continue;

            await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeFor(extension));
            using var request = new HttpRequestMessage(HttpMethod.Put, target) { Content = content };
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return target.AbsoluteUri;
            if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
                continue;
            throw new HttpRequestException(
                $"WebDAV 上传失败：{(int)response.StatusCode} {response.ReasonPhrase}",
                null, response.StatusCode);
        }

        throw new HttpRequestException("WebDAV 上传失败：同名文件过多，请先整理图片目录。");
    }

    private async Task<bool> ExistsAsync(Uri target, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), target);
        request.Headers.TryAddWithoutValidation("Depth", "0");
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        return response.IsSuccessStatusCode;
    }

    private static string MediaTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        _ => "image/jpeg"
    };

    public void Dispose() => _client.Dispose();

    internal static IEnumerable<WebDavEntry> ParseResponses(XDocument document, Uri root)
    {
        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href) ||
                !TryResolve(root, href.Trim(), out var location))
                continue;

            var successfulProperties = response.Elements(Dav + "propstat")
                .Where(p => !TryGetStatusCode(p.Element(Dav + "status")?.Value, out var status) ||
                    status is >= 200 and < 300)
                .Select(p => p.Element(Dav + "prop"))
                .Where(p => p is not null)
                .ToList();
            var properties = successfulProperties.Count > 0
                ? successfulProperties
                : response.Elements(Dav + "prop").ToList<XElement?>();
            var isCollection = properties.Any(p => p!
                .Descendants(Dav + "collection").Any());
            var lengthText = properties
                .Select(p => p!.Element(Dav + "getcontentlength")?.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var modifiedText = properties
                .Select(p => p!.Element(Dav + "getlastmodified")?.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            long.TryParse(lengthText, out var length);
            DateTimeOffset.TryParse(modifiedText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces |
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var modified);
            yield return new WebDavEntry(location, isCollection,
                isCollection || length < 0
                    ? null
                    : new SourceFile(location.AbsoluteUri, length, modified));
        }
    }

    private static bool TryResolve(Uri root, string href, out Uri location)
    {
        Uri? resolved = null;
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            Uri.TryCreate(root.Scheme + ":" + href, UriKind.Absolute, out resolved);
        }
        else if (!Uri.TryCreate(href, UriKind.Absolute, out resolved))
        {
            Uri.TryCreate(root, href, out resolved);
        }

        location = resolved!;
        return resolved is not null && IsInRoot(root, resolved);
    }

    private static void EnsureInRoot(Uri location, Uri root)
    {
        if (!IsInRoot(root, location))
            throw new UnauthorizedAccessException("WebDAV 文件不在配置的地址范围内。");
    }

    private static bool IsInRoot(Uri root, Uri location)
    {
        if ((location.Scheme != Uri.UriSchemeHttp && location.Scheme != Uri.UriSchemeHttps) ||
            !string.Equals(location.Scheme, root.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(location.Host, root.Host, StringComparison.OrdinalIgnoreCase) ||
            location.Port != root.Port)
            return false;
        var rootPath = root.AbsolutePath;
        var locationPath = location.AbsolutePath;
        return locationPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) &&
            (rootPath.EndsWith('/') || locationPath.Length == rootPath.Length ||
                locationPath[rootPath.Length] == '/');
    }

    private static Uri NormalizeRoot(Uri address)
    {
        var builder = new UriBuilder(address) { Query = "", Fragment = "" };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri;
    }

    private static bool TryGetStatusCode(string? value, out int status)
    {
        status = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out status);
    }

    private static async Task<XDocument> ReadXmlAsync(Stream stream, CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024
        };
        using var reader = XmlReader.Create(stream, settings);
        return await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
    }

    private static HttpClient CreateClient(SourceConfig config)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        if (config.AllowInvalidCertificate)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var password = CredentialProtector.Unprotect(config.EncryptedPassword);
        if (!string.IsNullOrEmpty(config.Username) || !string.IsNullOrEmpty(password))
            handler.Credentials = new NetworkCredential(config.Username, password);
        handler.PreAuthenticate = true;
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(100)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("NAS-Wallpaper", "1.0"));
        return client;
    }

    private sealed class UriComparer : IEqualityComparer<Uri>
    {
        public static readonly UriComparer Instance = new();
        public bool Equals(Uri? x, Uri? y) =>
            string.Equals(x?.AbsoluteUri, y?.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(Uri obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AbsoluteUri);
    }

    private sealed class ResponseStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;

        public ResponseStream(Stream inner, HttpResponseMessage response)
        {
            _inner = inner;
            _response = response;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

internal sealed record WebDavEntry(Uri Location, bool IsCollection, SourceFile? File);
