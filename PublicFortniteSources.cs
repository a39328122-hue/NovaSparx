using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.MappingsProvider.Usmap;
using EpicManifestParser;
using EpicManifestParser.UE;

namespace NovaSparx.Backend;

public sealed partial class PublicFortniteSources
{
    private readonly HttpClient _http;
    private readonly ILogger<PublicFortniteSources> _log;
    private readonly string _cacheRoot;

    public PublicFortniteSources(HttpClient http, ILogger<PublicFortniteSources> log)
    {
        _http = http;
        _log = log;
        _cacheRoot = Environment.GetEnvironmentVariable("NOVASPARX_CACHE_DIR")
            ?? Path.Combine(Path.GetTempPath(), "novasparx-cache");

        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(Path.Combine(_cacheRoot, "manifests"));
        Directory.CreateDirectory(Path.Combine(_cacheRoot, "chunks"));
        Directory.CreateDirectory(Path.Combine(_cacheRoot, "mappings"));
    }

    public string CacheRoot => _cacheRoot;
    public string ChunkCache => Path.Combine(_cacheRoot, "chunks");
    public string ManifestCache => Path.Combine(_cacheRoot, "manifests");

    public ManifestParseOptions CreateManifestOptions()
    {
        return new ManifestParseOptions
        {
            ChunkCacheDirectory = ChunkCache,
            ManifestCacheDirectory = ManifestCache,
            ChunkBaseUrl = Environment.GetEnvironmentVariable("NOVASPARX_CHUNK_BASE_URL")
                ?? "https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir/",
            Client = _http,
            CacheChunksAsIs = false,
            Decompressor = DecompressorBuilder.Default.Build()
        };
    }

    public async Task<(FBuildPatchAppManifest Manifest, string Version)> GetLiveManifestAsync(
        CancellationToken cancellationToken)
    {
        var direct = Environment.GetEnvironmentVariable("NOVASPARX_MANIFEST_URL");

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return await DownloadManifestFromAnyEndpoint(direct, cancellationToken);
        }

        var api = Environment.GetEnvironmentVariable("NOVASPARX_MANIFEST_API")
            ?? "https://api.fortniteapi.com/v1/manifests";

        using var response = await _http.GetAsync(api, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (LooksLikeManifest(body))
        {
            var manifest = FBuildPatchAppManifest.Deserialize(body, CreateManifestOptions());
            return (manifest, ReadVersion(manifest));
        }

        using var doc = JsonDocument.Parse(body);
        var candidates = FindManifestCandidates(doc.RootElement);

        foreach (var candidate in candidates.OrderByDescending(x => x.Score))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (candidate.Url is not null)
            {
                try
                {
                    return await DownloadManifestFromAnyEndpoint(candidate.Url, cancellationToken);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Manifest candidate failed: {Url}", candidate.Url);
                }
            }

            if (!string.IsNullOrWhiteSpace(candidate.Id))
            {
                var detailUrl = api.TrimEnd('/') + "/" + Uri.EscapeDataString(candidate.Id);
                try
                {
                    return await DownloadManifestFromAnyEndpoint(detailUrl, cancellationToken);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Manifest detail candidate failed: {Url}", detailUrl);
                }
            }
        }

        throw new InvalidOperationException(
            "The public manifest API answered, but NovaSparx could not locate a Windows Fortnite manifest URL. " +
            "Set NOVASPARX_MANIFEST_URL to a direct current .manifest URL if the API schema changed.");
    }

    private async Task<(FBuildPatchAppManifest Manifest, string Version)> DownloadManifestFromAnyEndpoint(
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (LooksLikeManifest(bytes))
        {
            var manifest = FBuildPatchAppManifest.Deserialize(bytes, CreateManifestOptions());
            return (manifest, ReadVersion(manifest));
        }

        using var doc = JsonDocument.Parse(bytes);
        var candidates = FindManifestCandidates(doc.RootElement);

        foreach (var candidate in candidates.OrderByDescending(x => x.Score))
        {
            if (candidate.Url is null || candidate.Url.Equals(url, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var nested = await _http.GetAsync(candidate.Url, cancellationToken);
                nested.EnsureSuccessStatusCode();
                var nestedBytes = await nested.Content.ReadAsByteArrayAsync(cancellationToken);

                if (!LooksLikeManifest(nestedBytes)) continue;

                var manifest = FBuildPatchAppManifest.Deserialize(nestedBytes, CreateManifestOptions());
                return (manifest, ReadVersion(manifest));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Nested manifest URL failed: {Url}", candidate.Url);
            }
        }

        throw new InvalidOperationException("The manifest endpoint did not contain raw manifest bytes or a direct .manifest URL.");
    }

    private static bool LooksLikeManifest(byte[] bytes)
    {
        if (bytes.Length < 16) return false;
        var first = bytes.SkipWhile(b => b is 9 or 10 or 13 or 32).FirstOrDefault();
        return first is not (byte)'{' and not (byte)'[';
    }

    private static string ReadVersion(FBuildPatchAppManifest manifest)
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

    private sealed record ManifestCandidate(string? Url, string? Id, int Score);

    private static List<ManifestCandidate> FindManifestCandidates(JsonElement root)
    {
        var list = new List<ManifestCandidate>();

        Walk(root, element =>
        {
            if (element.ValueKind != JsonValueKind.Object) return;

            string? url = null;
            string? id = null;
            var score = 0;
            var text = element.GetRawText().ToLowerInvariant();

            if (text.Contains("windows")) score += 30;
            if (text.Contains("fortnite")) score += 20;
            if (text.Contains("live")) score += 10;
            if (text.Contains("latest")) score += 10;
            if (text.Contains("android") || text.Contains("ios") || text.Contains("mac")) score -= 20;
            if (text.Contains("studio") || text.Contains("uefn")) score -= 5;

            foreach (var property in element.EnumerateObject())
            {
                var key = property.Name.ToLowerInvariant();

                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                        uri.Scheme is "http" or "https")
                    {
                        var low = value.ToLowerInvariant();
                        if (low.Contains(".manifest") || key.Contains("download") || key.Contains("manifest"))
                        {
                            url ??= value;
                            if (low.Contains(".manifest")) score += 50;
                        }
                    }

                    if (key is "id" or "manifestid" or "manifest_id")
                        id ??= value;
                }
                else if (property.Value.ValueKind == JsonValueKind.Number &&
                         key is "id" or "manifestid" or "manifest_id")
                {
                    id ??= property.Value.GetRawText();
                }
            }

            if (url is not null || id is not null)
                list.Add(new ManifestCandidate(url, id, score));
        });

        return list;
    }

    public async Task<FileUsmapTypeMappingsProvider?> GetMappingsAsync(CancellationToken cancellationToken)
    {
        var direct = Environment.GetEnvironmentVariable("NOVASPARX_MAPPINGS_URL");
        if (!string.IsNullOrWhiteSpace(direct))
            return await DownloadMappings(direct, cancellationToken);

        foreach (var endpoint in new[]
        {
            Environment.GetEnvironmentVariable("NOVASPARX_MAPPINGS_API")
                ?? "https://api.fortniteapi.com/v1/mappings",
            "https://api.fortniteapi.com/v1/mappings/legacy"
        })
        {
            try
            {
                using var response = await _http.GetAsync(endpoint, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));

                var urls = FindUrls(doc.RootElement)
                    .Where(x => x.Contains("usmap", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(ScoreMappingUrl)
                    .ToArray();

                foreach (var url in urls)
                {
                    try
                    {
                        var provider = await DownloadMappings(url, cancellationToken);
                        if (provider is not null) return provider;
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Mapping candidate failed: {Url}", url);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Mappings endpoint failed: {Url}", endpoint);
            }
        }

        return null;
    }

    private static int ScoreMappingUrl(string url)
    {
        var score = 0;
        var low = url.ToLowerInvariant();
        if (low.EndsWith(".usmap")) score += 40;
        if (low.Contains("zstandard") || low.Contains("zstd") || low.EndsWith(".zst")) score -= 10;
        if (low.Contains("latest")) score += 10;
        return score;
    }

    private async Task<FileUsmapTypeMappingsProvider?> DownloadMappings(
        string url,
        CancellationToken cancellationToken)
    {
        var bytes = await _http.GetByteArrayAsync(url, cancellationToken);
        if (bytes.Length < 32) return null;

        if (IsGzip(bytes))
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            await gzip.CopyToAsync(output, cancellationToken);
            bytes = output.ToArray();
        }

        // Zstandard magic. Do not write a corrupt mapping file.
        if (bytes.Length >= 4 &&
            bytes[0] == 0x28 && bytes[1] == 0xB5 && bytes[2] == 0x2F && bytes[3] == 0xFD)
        {
            _log.LogWarning("Skipping Zstandard-compressed usmap candidate because this alpha uses an uncompressed/GZip mapping path.");
            return null;
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
        var path = Path.Combine(_cacheRoot, "mappings", $"{hash}.usmap");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return new FileUsmapTypeMappingsProvider(path);
    }

    private static bool IsGzip(byte[] bytes) =>
        bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    public async Task<IReadOnlyList<KeyValuePair<FGuid, FAesKey>>> GetAesKeysAsync(
        CancellationToken cancellationToken)
    {
        var endpoint = Environment.GetEnvironmentVariable("NOVASPARX_AES_API")
            ?? "https://api.fortniteapi.com/v1/aes";

        using var response = await _http.GetAsync(endpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));

        var result = new Dictionary<FGuid, FAesKey>();
        var mainKey = FindMainAesKey(doc.RootElement);

        if (mainKey is not null)
            result[new FGuid()] = new FAesKey(mainKey);

        Walk(doc.RootElement, element =>
        {
            if (element.ValueKind != JsonValueKind.Object) return;

            string? key = null;
            string? guid = null;

            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name.ToLowerInvariant();
                if (property.Value.ValueKind != JsonValueKind.String) continue;

                var value = property.Value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(value)) continue;

                if ((name.Contains("key") || name.Contains("aes")) && AesRegex().IsMatch(value))
                    key ??= NormalizeAes(value);

                if (name.Contains("guid") && GuidRegex().IsMatch(value))
                    guid ??= NormalizeGuid(value);
            }

            if (key is null || guid is null) return;

            try
            {
                result[new FGuid(guid)] = new FAesKey(key);
            }
            catch
            {
                // Ignore malformed third-party response entries.
            }
        });

        return result.ToArray();
    }

    private static string? FindMainAesKey(JsonElement root)
    {
        string? found = null;

        Walk(root, element =>
        {
            if (found is not null || element.ValueKind != JsonValueKind.Object) return;

            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name.ToLowerInvariant();
                if (property.Value.ValueKind != JsonValueKind.String) continue;

                var value = property.Value.GetString()?.Trim() ?? "";
                if (!AesRegex().IsMatch(value)) continue;

                if (name is "mainkey" or "main_key" or "mainaes" or "mainaeskey" ||
                    (name.Contains("main") && name.Contains("key")))
                {
                    found = NormalizeAes(value);
                    return;
                }
            }
        });

        return found;
    }

    private static string NormalizeAes(string value)
    {
        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? "0x" + value[2..].ToUpperInvariant()
            : value.ToUpperInvariant();
    }

    private static string NormalizeGuid(string value) =>
        value.Replace("-", "", StringComparison.Ordinal)
             .Replace("{", "", StringComparison.Ordinal)
             .Replace("}", "", StringComparison.Ordinal)
             .Trim()
             .ToUpperInvariant();

    private static IEnumerable<string> FindUrls(JsonElement root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Walk(root, element =>
        {
            if (element.ValueKind != JsonValueKind.String) return;
            var value = element.GetString();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https")
                set.Add(value!);
        });

        return set;
    }

    private static void Walk(JsonElement root, Action<JsonElement> visitor)
    {
        visitor(root);

        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in root.EnumerateArray()) Walk(item, visitor);
                break;

            case JsonValueKind.Object:
                foreach (var property in root.EnumerateObject()) Walk(property.Value, visitor);
                break;
        }
    }

    [GeneratedRegex(@"^(?:0x)?[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AesRegex();

    [GeneratedRegex(@"^[{]?[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}[}]?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();
}
