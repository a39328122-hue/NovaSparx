using System.Collections.Concurrent;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;

namespace NovaSparx.Backend;

/// <summary>
/// Decodes Fortnite UTexture assets into browser-ready PNGs.
///
/// CUE4Parse does the Unreal texture decode.
/// CUE4Parse-Conversion does the PNG encoding.
/// NovaSparx only controls limits, caching and API-safe output.
/// </summary>
public sealed class TextureService
{
    private readonly LiveProviderService _provider;
    private readonly ILogger<TextureService> _log;

    private readonly SemaphoreSlim _decodeGate;

    private readonly ConcurrentDictionary<string, CacheEntry>
        _cache =
            new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(
        DateTimeOffset CreatedAt,
        TexturePayload Value);

    private static readonly TimeSpan CacheTtl =
        TimeSpan.FromMinutes(
            int.TryParse(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_TEXTURE_CACHE_MINUTES"),
                out var minutes)
                ? Math.Clamp(minutes, 1, 180)
                : 30);

    private static readonly int MaxMipSize =
        int.TryParse(
            Environment.GetEnvironmentVariable(
                "NOVASPARX_TEXTURE_MAX_SIZE"),
            out var maxSize)
            ? Math.Clamp(maxSize, 64, 4096)
            : 2048;

    private static readonly int MaxEncodedBytes =
        int.TryParse(
            Environment.GetEnvironmentVariable(
                "NOVASPARX_TEXTURE_MAX_BYTES"),
            out var maxBytes)
            ? Math.Clamp(
                maxBytes,
                256 * 1024,
                32 * 1024 * 1024)
            : 12 * 1024 * 1024;

    public TextureService(
        LiveProviderService provider,
        ILogger<TextureService> log)
    {
        _provider = provider;
        _log = log;

        var concurrency =
            int.TryParse(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_TEXTURE_CONCURRENCY"),
                out var parsed)
                ? Math.Clamp(parsed, 1, 2)
                : 1;

        _decodeGate =
            new SemaphoreSlim(
                concurrency,
                concurrency);
    }

    public int CacheEntries =>
        _cache.Count;

    public async Task<TexturePayload?>
        DecodePngAsync(
            string rawPath,
            CancellationToken cancellationToken)
    {
        var canonical =
            AssetPathResolver.Canonicalize(
                rawPath);

        if (canonical.Length == 0)
            return null;

        if (_cache.TryGetValue(
                canonical,
                out var cached) &&
            DateTimeOffset.UtcNow -
            cached.CreatedAt < CacheTtl)
        {
            return cached.Value;
        }

        var loaded =
            await _provider.LoadObjectAsync(
                rawPath,
                cancellationToken);

        if (loaded is null)
            return null;

        if (loaded.Value.Object is not UTexture texture)
            return null;

        await _decodeGate.WaitAsync(
            cancellationToken);

        try
        {
            if (_cache.TryGetValue(
                    canonical,
                    out cached) &&
                DateTimeOffset.UtcNow -
                cached.CreatedAt < CacheTtl)
            {
                return cached.Value;
            }

            cancellationToken
                .ThrowIfCancellationRequested();

            CTexture? decoded =
                null;

            try
            {
                // Decode the largest mip that is at or below the HTTP/browser
                // preview budget. This avoids decoding an 8K/16K source just to
                // display a small preview on a phone.
                decoded =
                    texture.Decode(
                        MaxMipSize,
                        ETexturePlatform.DesktopMobile);

                if (decoded is null)
                {
                    // Some assets have unusual mip metadata. Fall back to the
                    // first decodable mip rather than pretending the texture
                    // does not exist.
                    decoded =
                        texture.Decode(
                            ETexturePlatform.DesktopMobile);
                }

                if (decoded is null)
                    return null;

                cancellationToken
                    .ThrowIfCancellationRequested();

                var png =
                    decoded.Encode(
                        ETextureFormat.Png,
                        false,
                        out var extension,
                        100);

                if (!extension.Equals(
                        "png",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Texture encoder returned unexpected format: {extension}");
                }

                if (png.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Texture encoder returned an empty PNG.");
                }

                if (png.Length > MaxEncodedBytes)
                {
                    throw new InvalidOperationException(
                        "Decoded texture exceeds the NovaSparx HTTP preview budget " +
                        $"({png.Length:N0} bytes > {MaxEncodedBytes:N0} bytes).");
                }

                var payload =
                    new TexturePayload(
                        Path:
                            canonical,

                        ResolvedPath:
                            loaded.Value.ResolvedPath,

                        ContentType:
                            "image/png",

                        Bytes:
                            png,

                        Width:
                            decoded.Width,

                        Height:
                            decoded.Height);

                TrimCacheIfNeeded();

                _cache[canonical] =
                    new CacheEntry(
                        DateTimeOffset.UtcNow,
                        payload);

                return payload;
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    "Texture decode failed for {Path}.",
                    canonical);

                throw;
            }
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    public void ClearCache()
    {
        _cache.Clear();
    }

    private void TrimCacheIfNeeded()
    {
        if (_cache.Count <= 220)
            return;

        var oldest =
            _cache
                .OrderBy(
                    pair =>
                        pair.Value.CreatedAt)
                .Take(60)
                .Select(
                    pair =>
                        pair.Key)
                .ToArray();

        foreach (var key in oldest)
        {
            _cache.TryRemove(
                key,
                out _);
        }
    }
}
