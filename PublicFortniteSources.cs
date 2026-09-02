using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using EpicManifestParser;
using EpicManifestParser.UE;

namespace NovaSparx.Backend;

/// <summary>
/// Public data adapters used by NovaSparx.
/// Every endpoint can be overridden with environment variables so a schema/provider
/// change does not require redesigning the Live provider.
/// </summary>
public sealed partial class PublicFortniteSources
{
    private readonly HttpClient _http;
    private readonly ILogger<PublicFortniteSources> _log;
    private readonly string _cacheRoot;

    public PublicFortniteSources(
        HttpClient http,
        ILogger<PublicFortniteSources> log)
    {
        _http = http;
        _log = log;

        _cacheRoot =
            Environment.GetEnvironmentVariable("NOVASPARX_CACHE_DIR") ??
            Path.Combine(Path.GetTempPath(), "novasparx-cache");

        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(ManifestCache);
        Directory.CreateDirectory(ChunkCache);
        Directory.CreateDirectory(MappingsCache);
        Directory.CreateDirectory(TocCache);
    }

    public string CacheRoot => _cacheRoot;
    public string ManifestCache => Path.Combine(_cacheRoot, "manifests");
    public string ChunkCache => Path.Combine(_cacheRoot, "chunks");
    public string MappingsCache => Path.Combine(_cacheRoot, "mappings");
    public string TocCache => Path.Combine(_cacheRoot, "uondemandtoc");

    public ManifestParseOptions CreateManifestOptions()
    {
        return new ManifestParseOptions
        {
            ChunkCacheDirectory = ChunkCache,
            ManifestCacheDirectory = ManifestCache,
            ChunkBaseUrl =
                Environment.GetEnvironmentVariable("NOVASPARX_CHUNK_BASE_URL") ??
                "https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir/",
            Client = _http,

            // Match the current Fortnite tooling pattern: BuildPatch chunks stay cached
            // in their transport form and CUE4Parse/EpicManifestParser handle decoding.
            CacheChunksAsIs = true,
            Decompressor = CUE4Parse.Compression.Compression.Decompressor
        };
    }

    public async Task<(FBuildPatchAppManifest Manifest, string Version)>
        GetLiveManifestAsync(CancellationToken cancellationToken)
    {
        var direct =
            Environment.GetEnvironmentVariable("NOVASPARX_MANIFEST_URL");

        if (!string.IsNullOrWhiteSpace(direct))
            return await DownloadManifestFromAnyEndpoint(
                direct,
                cancellationToken);

        var api =
            Environment.GetEnvironmentVariable("NOVASPARX_MANIFEST_API") ??
            "https://api.fortniteapi.com/v1/manifests";

        return await DownloadManifestFromAnyEndpoint(
            api,
            cancellationToken);
    }

    /// <summary>
    /// Optional Fortnite_Studio manifest. It gives NovaSparx another archive source
    /// for UEFN/plugin assets that may not exist in the main Fortnite manifest.
    /// Dilly exposes this same manifest family publicly.
    /// </summary>
    public async Task<(FBuildPatchAppManifest Manifest, string Version)?>
        GetStudioManifestAsync(CancellationToken cancellationToken)
    {
        var endpoint =
            Environment.GetEnvironmentVariable("NOVASPARX_STUDIO_MANIFEST_API") ??
            "https://export-service-new.dillyapis.com/v1/manifests";

        try
        {
            using var response =
                await _http.GetAsync(endpoint, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var bytes =
                await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (LooksLikeManifest(bytes))
            {
                var direct = FBuildPatchAppManifest.Deserialize(
                    bytes,
                    CreateManifestOptions());
                return (direct, ReadVersion(direct));
            }

            using var doc = JsonDocument.Parse(bytes);

            string? downloadUrl = null;

            Walk(doc.RootElement, element =>
            {
                if (downloadUrl is not null ||
                    element.ValueKind != JsonValueKind.Object)
                    return;

                string? appName = null;
                string? candidateUrl = null;

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                        continue;

                    var value = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    var key = property.Name.ToLowerInvariant();

                    if (key is "appname" or "app_name" or "app")
                        appName = value;

                    if (key.Contains("download") || key.Contains("manifest"))
                    {
                        if (Uri.TryCreate(
                            value,
                            UriKind.Absolute,
                            out var uri) &&
                            uri.Scheme is "http" or "https")
                        {
                            candidateUrl = value;
                        }
                    }
                }

                if (appName?.Equals(
                    "Fortnite_Studio",
                    StringComparison.OrdinalIgnoreCase) == true)
                {
                    downloadUrl = candidateUrl;
                }
            });

            if (string.IsNullOrWhiteSpace(downloadUrl))
                return null;

            return await DownloadManifestFromAnyEndpoint(
                downloadUrl,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Fortnite_Studio manifest source failed.");
            return null;
        }
    }

    public async Task<ExternalTocResult?>
        GetTextureStreamingTocAsync(
            FBuildPatchAppManifest liveManifest,
            CancellationToken cancellationToken)
    {
        try
        {
            var ini = liveManifest.Files.FirstOrDefault(
                file => file.FileName.Equals(
                    "Cloud/IoStoreOnDemand.ini",
                    StringComparison.OrdinalIgnoreCase));

            if (ini is null)
                return null;

            string text;
            await using (var stream = ini.GetStream())
            using (var reader = new StreamReader(stream))
            {
                text = await reader.ReadToEndAsync(cancellationToken);
            }

            var match = TocPathRegex().Match(text);
            if (!match.Success)
                return null;

            var tocPath = match.Groups[1].Value
                .Trim()
                .Trim('"')
                .Replace("\\\"", "", StringComparison.Ordinal)
                .Replace('\\', '/');

            if (string.IsNullOrWhiteSpace(tocPath))
                return null;

            var url = Uri.TryCreate(
                tocPath,
                UriKind.Absolute,
                out var absolute)
                ? absolute.ToString()
                : "https://download.epicgames.com/" +
                  tocPath.TrimStart('/');

            var fileName = Path.GetFileName(
                new Uri(url).AbsolutePath);

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "IoStoreOnDemand.uondemandtoc";

            var cachePath =
                Path.Combine(TocCache, fileName);

            byte[] bytes;

            if (File.Exists(cachePath) &&
                new FileInfo(cachePath).Length > 32)
            {
                bytes =
                    await File.ReadAllBytesAsync(
                        cachePath,
                        cancellationToken);
            }
            else
            {
                bytes =
                    await _http.GetByteArrayAsync(
                        url,
                        cancellationToken);

                if (bytes.Length < 32)
                    return null;

                await File.WriteAllBytesAsync(
                    cachePath,
                    bytes,
                    cancellationToken);
            }

            return new ExternalTocResult(
                fileName,
                url,
                bytes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Texture-streaming IoStore TOC could not be loaded.");
            return null;
        }
    }

    private async Task<(FBuildPatchAppManifest Manifest, string Version)>
        DownloadManifestFromAnyEndpoint(
            string url,
            CancellationToken cancellationToken)
    {
        using var response =
            await _http.GetAsync(url, cancellationToken);

        response.EnsureSuccessStatusCode();

        var bytes =
            await response.Content.ReadAsByteArrayAsync(
                cancellationToken);

        if (LooksLikeManifest(bytes))
        {
            var manifest =
                FBuildPatchAppManifest.Deserialize(
                    bytes,
                    CreateManifestOptions());

            return (manifest, ReadVersion(manifest));
        }

        using var doc = JsonDocument.Parse(bytes);

        var candidates =
            FindManifestCandidates(doc.RootElement);

        foreach (var candidate in
                 candidates.OrderByDescending(x => x.Score))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(candidate.Url) &&
                !candidate.Url.Equals(
                    url,
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var nested =
                        await _http.GetAsync(
                            candidate.Url,
                            cancellationToken);

                    if (!nested.IsSuccessStatusCode)
                        continue;

                    var nestedBytes =
                        await nested.Content.ReadAsByteArrayAsync(
                            cancellationToken);

                    if (!LooksLikeManifest(nestedBytes))
                        continue;

                    var manifest =
                        FBuildPatchAppManifest.Deserialize(
                            nestedBytes,
                            CreateManifestOptions());

                    return (manifest, ReadVersion(manifest));
                }
                catch (Exception ex)
                {
                    _log.LogDebug(
                        ex,
                        "Manifest candidate failed: {Url}",
                        candidate.Url);
                }
            }

            if (!string.IsNullOrWhiteSpace(candidate.Id))
            {
                var detail =
                    url.TrimEnd('/') + "/" +
                    Uri.EscapeDataString(candidate.Id);

                try
                {
                    return await DownloadManifestFromAnyEndpoint(
                        detail,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(
                        ex,
                        "Manifest detail candidate failed: {Url}",
                        detail);
                }
            }
        }

        throw new InvalidOperationException(
            "NovaSparx received manifest metadata but could not find raw " +
            "Fortnite manifest bytes or a usable .manifest download URL. " +
            "If the public API changed, set NOVASPARX_MANIFEST_URL.");
    }

    private static bool LooksLikeManifest(byte[] bytes)
    {
        if (bytes.Length < 16)
            return false;

        var first =
            bytes.SkipWhile(b => b is 9 or 10 or 13 or 32)
                 .FirstOrDefault();

        return first is not (byte)'{' and not (byte)'[';
    }

    private static string ReadVersion(
        FBuildPatchAppManifest manifest)
    {
        try
        {
            return manifest.Meta?.BuildVersion ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private sealed record ManifestCandidate(
        string? Url,
        string? Id,
        int Score);

    private static List<ManifestCandidate>
        FindManifestCandidates(JsonElement root)
    {
        var list = new List<ManifestCandidate>();

        Walk(root, element =>
        {
            if (element.ValueKind != JsonValueKind.Object)
                return;

            string? url = null;
            string? id = null;
            var score = 0;
            var text =
                element.GetRawText().ToLowerInvariant();

            if (text.Contains("windows"))
                score += 40;

            if (text.Contains("fortnite"))
                score += 25;

            if (text.Contains("live") ||
                text.Contains("latest"))
                score += 10;

            if (text.Contains("android") ||
                text.Contains("ios") ||
                text.Contains("mac"))
                score -= 40;

            if (text.Contains("studio") ||
                text.Contains("uefn"))
                score -= 15;

            foreach (var property in
                     element.EnumerateObject())
            {
                var key =
                    property.Name.ToLowerInvariant();

                if (property.Value.ValueKind ==
                    JsonValueKind.String)
                {
                    var value =
                        property.Value.GetString();

                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    if (Uri.TryCreate(
                        value,
                        UriKind.Absolute,
                        out var uri) &&
                        uri.Scheme is "http" or "https")
                    {
                        var low =
                            value.ToLowerInvariant();

                        if (low.Contains(".manifest") ||
                            key.Contains("download") ||
                            key.Contains("manifest"))
                        {
                            url ??= value;

                            if (low.Contains(".manifest"))
                                score += 60;
                        }
                    }

                    if (key is "manifestid" or "manifest_id")
                    {
                        id = value;
                    }
                    else if (
                        key == "id" &&
                        string.IsNullOrWhiteSpace(id))
                    {
                        id = value;
                    }

                }
                else if (
                    property.Value.ValueKind ==
                    JsonValueKind.Number &&
                    key is "id" or "manifestid" or "manifest_id")
                {
                    id ??=
                        property.Value.GetRawText();
                }
            }

            if (url is not null || id is not null)
                list.Add(
                    new ManifestCandidate(
                        url,
                        id,
                        score));
        });

        return list;
    }

    public async Task<FileUsmapTypeMappingsProvider?>
        GetMappingsAsync(
            CancellationToken cancellationToken)
    {
        var direct =
            Environment.GetEnvironmentVariable(
                "NOVASPARX_MAPPINGS_URL");

        if (!string.IsNullOrWhiteSpace(direct))
            return await DownloadMappings(
                direct,
                cancellationToken);

        var endpoints = new[]
        {
            Environment.GetEnvironmentVariable(
                "NOVASPARX_MAPPINGS_API") ??
            "https://api.fortniteapi.com/v1/mappings",

            "https://api.fortniteapi.com/v1/mappings/legacy"
        };

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var response =
                    await _http.GetAsync(
                        endpoint,
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                    continue;

                using var doc =
                    JsonDocument.Parse(
                        await response.Content
                            .ReadAsByteArrayAsync(
                                cancellationToken));

                var urls =
                    FindUrls(doc.RootElement)
                        .Where(url =>
                            url.Contains(
                                "usmap",
                                StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(ScoreMappingUrl)
                        .ToArray();

                foreach (var url in urls)
                {
                    var provider =
                        await DownloadMappings(
                            url,
                            cancellationToken);

                    if (provider is not null)
                        return provider;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Mappings source failed: {Endpoint}",
                    endpoint);
            }
        }

        return null;
    }

    private static int ScoreMappingUrl(string url)
    {
        var score = 0;
        var low = url.ToLowerInvariant();

        if (low.EndsWith(".usmap"))
            score += 50;

        if (low.Contains("latest"))
            score += 10;

        if (low.Contains("zstandard") ||
            low.Contains("zstd") ||
            low.EndsWith(".zst"))
            score -= 20;

        return score;
    }

    private async Task<FileUsmapTypeMappingsProvider?>
        DownloadMappings(
            string url,
            CancellationToken cancellationToken)
    {
        var bytes =
            await _http.GetByteArrayAsync(
                url,
                cancellationToken);

        if (bytes.Length < 32)
            return null;

        if (IsGzip(bytes))
        {
            using var input =
                new MemoryStream(bytes);

            using var gzip =
                new GZipStream(
                    input,
                    CompressionMode.Decompress);

            using var output =
                new MemoryStream();

            await gzip.CopyToAsync(
                output,
                cancellationToken);

            bytes = output.ToArray();
        }

        // Keep the adapter deterministic: do not save a compressed .zst blob as .usmap.
        if (IsZstd(bytes))
        {
            _log.LogWarning(
                "Mappings candidate is Zstandard-compressed; " +
                "set NOVASPARX_MAPPINGS_URL to a raw/GZip .usmap source.");
            return null;
        }

        var hash =
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
                [..16];

        var path =
            Path.Combine(
                MappingsCache,
                $"{hash}.usmap");

        await File.WriteAllBytesAsync(
            path,
            bytes,
            cancellationToken);

        return new FileUsmapTypeMappingsProvider(
            path,
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<KeyValuePair<FGuid, FAesKey>>>
        GetAesKeysAsync(
            CancellationToken cancellationToken)
    {
        var endpoint =
            Environment.GetEnvironmentVariable(
                "NOVASPARX_AES_API") ??
            "https://api.fortniteapi.com/v1/aes";

        using var response =
            await _http.GetAsync(
                endpoint,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        using var doc =
            JsonDocument.Parse(
                await response.Content
                    .ReadAsByteArrayAsync(
                        cancellationToken));

        var result =
            new Dictionary<FGuid, FAesKey>();

        var main =
            FindMainAesKey(doc.RootElement);

        if (main is not null)
            result[new FGuid()] =
                new FAesKey(main);

        Walk(doc.RootElement, element =>
        {
            if (element.ValueKind !=
                JsonValueKind.Object)
                return;

            string? key = null;
            string? guid = null;

            foreach (var property in
                     element.EnumerateObject())
            {
                if (property.Value.ValueKind !=
                    JsonValueKind.String)
                    continue;

                var name =
                    property.Name.ToLowerInvariant();

                var value =
                    property.Value.GetString()?.Trim();

                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if ((name.Contains("key") ||
                     name.Contains("aes")) &&
                    AesRegex().IsMatch(value))
                {
                    key ??= NormalizeAes(value);
                }

                if (name.Contains("guid") &&
                    GuidRegex().IsMatch(value))
                {
                    guid ??= NormalizeGuid(value);
                }
            }

            if (key is null || guid is null)
                return;

            try
            {
                result[new FGuid(guid)] =
                    new FAesKey(key);
            }
            catch
            {
                // Ignore malformed third-party entries.
            }
        });

        return result.ToArray();
    }

    private static string? FindMainAesKey(
        JsonElement root)
    {
        string? found = null;

        Walk(root, element =>
        {
            if (found is not null ||
                element.ValueKind !=
                JsonValueKind.Object)
                return;

            foreach (var property in
                     element.EnumerateObject())
            {
                if (property.Value.ValueKind !=
                    JsonValueKind.String)
                    continue;

                var name =
                    property.Name.ToLowerInvariant();

                var value =
                    property.Value.GetString()?.Trim() ?? "";

                if (!AesRegex().IsMatch(value))
                    continue;

                if (name is "mainkey" or
                    "main_key" or
                    "mainaes" or
                    "mainaeskey" ||
                    (name.Contains("main") &&
                     name.Contains("key")))
                {
                    found = NormalizeAes(value);
                    return;
                }
            }
        });

        return found;
    }

    private static string NormalizeAes(
        string value)
    {
        value = value.Trim();

        return value.StartsWith(
            "0x",
            StringComparison.OrdinalIgnoreCase)
            ? "0x" + value[2..].ToUpperInvariant()
            : value.ToUpperInvariant();
    }

    private static string NormalizeGuid(
        string value)
    {
        return value
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("{", "", StringComparison.Ordinal)
            .Replace("}", "", StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
    }

    private static IEnumerable<string>
        FindUrls(JsonElement root)
    {
        var set =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        Walk(root, element =>
        {
            if (element.ValueKind !=
                JsonValueKind.String)
                return;

            var value =
                element.GetString();

            if (Uri.TryCreate(
                value,
                UriKind.Absolute,
                out var uri) &&
                uri.Scheme is "http" or "https")
            {
                set.Add(value!);
            }
        });

        return set;
    }

    private static void Walk(
        JsonElement root,
        Action<JsonElement> visitor)
    {
        visitor(root);

        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in
                         root.EnumerateArray())
                    Walk(item, visitor);
                break;

            case JsonValueKind.Object:
                foreach (var property in
                         root.EnumerateObject())
                    Walk(property.Value, visitor);
                break;
        }
    }

    private static bool IsGzip(byte[] bytes) =>
        bytes.Length >= 2 &&
        bytes[0] == 0x1F &&
        bytes[1] == 0x8B;

    private static bool IsZstd(byte[] bytes) =>
        bytes.Length >= 4 &&
        bytes[0] == 0x28 &&
        bytes[1] == 0xB5 &&
        bytes[2] == 0x2F &&
        bytes[3] == 0xFD;

    [GeneratedRegex(
        @"^\s*TocPath\s*=\s*""?([^""\r\n]+)""?\s*$",
        RegexOptions.IgnoreCase |
        RegexOptions.Multiline |
        RegexOptions.CultureInvariant)]
    private static partial Regex TocPathRegex();

    [GeneratedRegex(
        @"^(?:0x)?[0-9a-fA-F]{64}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AesRegex();

    [GeneratedRegex(
        @"^[{]?[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}[}]?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();
}

public sealed record ExternalTocResult(
    string Name,
    string Url,
    byte[] Bytes);
