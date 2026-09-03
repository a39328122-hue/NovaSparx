using System.Text.Json;

namespace NovaSparx.Backend;

/// <summary>
/// One execution path for both direct HTTP requests and NovaLink requests.
/// </summary>
public sealed class NovaRequestDispatcher
{
    private readonly LiveProviderService _provider;
    private readonly MeshResolverService _meshes;
    private readonly AssetInspectorService _inspector;
    private readonly TextureService _textures;
    private readonly PreviewResolverService _previews;
    private readonly ILogger<NovaRequestDispatcher> _log;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase
        };

    public NovaRequestDispatcher(
        LiveProviderService provider,
        MeshResolverService meshes,
        AssetInspectorService inspector,
        TextureService textures,
        PreviewResolverService previews,
        ILogger<NovaRequestDispatcher> log)
    {
        _provider = provider;
        _meshes = meshes;
        _inspector = inspector;
        _textures = textures;
        _previews = previews;
        _log = log;
    }

    public async Task<DispatchResponse> DispatchAsync(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken cancellationToken)
    {
        method =
            string.IsNullOrWhiteSpace(method)
                ? "GET"
                : method.Trim().ToUpperInvariant();

        path =
            NormalizeRoute(path);

        try
        {
            return (method, path) switch
            {
                ("GET", "/health") or
                ("GET", "/v1/health") =>
                    Json(
                        200,
                        BuildHealth()),

                ("POST", "/v1/warmup") =>
                    await WarmupAsync(
                        cancellationToken),

                ("POST", "/v1/refresh") =>
                    await RefreshAsync(
                        cancellationToken),

                ("GET", "/v1/resolve") =>
                    await ResolveAsync(
                        GetAssetPath(query),
                        cancellationToken),

                ("GET", "/v1/preview") =>
                    await PreviewAsync(
                        GetAssetPath(query),
                        cancellationToken),

                ("GET", "/v1/inspect") =>
                    await InspectAsync(
                        GetAssetPath(query),
                        cancellationToken),

                ("GET", "/v1/references") =>
                    await ReferencesAsync(
                        GetAssetPath(query),
                        cancellationToken),

                ("GET", "/v1/texture") =>
                    await TextureAsync(
                        GetAssetPath(query),
                        cancellationToken),

                _ =>
                    Json(
                        404,
                        new
                        {
                            state = "missing",
                            error =
                                "Unknown NovaSparx operation.",
                            method,
                            path
                        })
            };
        }
        catch (ArgumentException ex)
        {
            return Json(
                400,
                new
                {
                    state = "invalid",
                    error = ex.Message
                });
        }
        catch (OperationCanceledException)
        {
            return Json(
                504,
                new
                {
                    state = "error",
                    error =
                        "NovaSparx operation timed out."
                });
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Nova request dispatch failed: {Method} {Path}",
                method,
                path);

            return Json(
                500,
                new
                {
                    state = "error",
                    error = ex.Message
                });
        }
    }

    private object BuildHealth()
    {
        var health =
            _provider.Health();

        return new
        {
            health.Ok,
            health.Service,
            health.Version,
            health.ProviderReady,
            health.Mode,
            health.ManifestVersion,
            health.RegisteredArchives,
            health.MountedArchives,
            health.IndexedFiles,
            health.RequiredKeys,
            health.LoadedKeys,
            health.LastError,
            health.TextureStreamingReady,
            providerPreviewCacheEntries =
                health.PreviewCacheEntries,
            meshCacheEntries =
                _meshes.CacheEntries,
            textureCacheEntries =
                _textures.CacheEntries,
            universalMeshPreview = true,
            universalPreviewPlan = true,
            staticMesh = true,
            skeletalMesh = true,
            inspector = "universal-uobject-metadata-v1"
        };
    }

    private async Task<DispatchResponse> WarmupAsync(
        CancellationToken cancellationToken)
    {
        using var timeout =
            CreateTimeout(
                cancellationToken,
                TimeSpan.FromMinutes(3));

        await _provider.EnsureReadyAsync(
            timeout.Token);

        return Json(
            200,
            BuildHealth());
    }

    private async Task<DispatchResponse> RefreshAsync(
        CancellationToken cancellationToken)
    {
        using var timeout =
            CreateTimeout(
                cancellationToken,
                TimeSpan.FromMinutes(3));

        _textures.ClearCache();
        _meshes.ClearCache();

        await _provider.RefreshAsync(
            timeout.Token);

        return Json(
            200,
            BuildHealth());
    }

    private async Task<DispatchResponse> ResolveAsync(
        string rawPath,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CreateTimeout(
                cancellationToken,
                TimeSpan.FromMinutes(2));

        var resolved =
            await _meshes.ResolveAsync(
                rawPath,
                timeout.Token);

        if (resolved is null)
        {
            return Json(
                404,
                new
                {
                    state = "missing",
                    error =
                        "NovaSparx could not resolve this path as a renderable StaticMesh or SkeletalMesh.",
                    path =
                        AssetPathResolver.Canonicalize(
                            rawPath)
                });
        }

        return Json(
            200,
            resolved);
    }

    private async Task<DispatchResponse> InspectAsync(
        string rawPath,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CreateTimeout(
                cancellationToken,
                TimeSpan.FromSeconds(90));

        var inspection =
            await _inspector.InspectAsync(
                rawPath,
                timeout.Token);

        if (inspection is null)
        {
            return Json(
                404,
                new
                {
                    state = "missing",
                    error =
                        "NovaSparx could not load this asset.",
                    path =
                        AssetPathResolver.Canonicalize(
                            rawPath)
                });
        }

        return Json(
            200,
            inspection);
    }

    private async Task<DispatchResponse> PreviewAsync(
        string rawPath,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CreateTimeout(
                cancellationToken,
                TimeSpan.FromMinutes(2));

        var preview =
            await _previews.ResolveAsync(
                rawPath,
                timeout.Token);

        return Json(
            200,
            preview);
    }

    private async Task<DispatchResponse> ReferencesAsync(
        string rawPath,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CreateTimeout(
                cancellationToken,
                TimeSpan.FromSeconds(90));

        var inspection =
            await _inspector.InspectAsync(
                rawPath,
                timeout.Token);

        if (inspection is null)
        {
            return Json(
                404,
                new
                {
                    state = "missing",
                    error =
                        "NovaSparx could not load this asset.",
                    path =
                        AssetPathResolver.Canonicalize(
                            rawPath)
                });
        }

        return Json(
            200,
            new
            {
                state = "ready",
                path =
                    inspection.Path,
                resolvedPath =
                    inspection.ResolvedPath,
                assetType =
                    inspection.AssetType,
                references =
                    inspection.References,
                count =
                    inspection.References.Length
            });
    }

    private async Task<DispatchResponse> TextureAsync(
        string rawPath,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CreateTimeout(
                cancellationToken,
                TimeSpan.FromSeconds(90));

        var texture =
            await _textures.DecodePngAsync(
                rawPath,
                timeout.Token);

        if (texture is null)
        {
            return Json(
                404,
                new
                {
                    state = "missing",
                    error =
                        "NovaSparx could not resolve this path as a decodable texture.",
                    path =
                        AssetPathResolver.Canonicalize(
                            rawPath)
                });
        }

        return new DispatchResponse(
            Status:
                200,

            ContentType:
                texture.ContentType,

            Body:
                texture.Bytes);
    }

    private static string GetAssetPath(
        IReadOnlyDictionary<string, string>? query)
    {
        if (query is null ||
            !query.TryGetValue(
                "path",
                out var rawPath))
        {
            throw new ArgumentException(
                "Asset path is required.");
        }

        rawPath =
            rawPath.Trim();

        if (rawPath.Length == 0)
        {
            throw new ArgumentException(
                "Asset path is required.");
        }

        if (rawPath.Length > 2400)
        {
            throw new ArgumentException(
                "Asset path is too long.");
        }

        return rawPath;
    }

    private static string NormalizeRoute(
        string value)
    {
        value =
            string.IsNullOrWhiteSpace(value)
                ? "/"
                : value.Trim();

        var queryIndex =
            value.IndexOf('?');

        if (queryIndex >= 0)
            value = value[..queryIndex];

        if (!value.StartsWith('/'))
            value = "/" + value;

        while (value.Contains(
                   "//",
                   StringComparison.Ordinal))
        {
            value =
                value.Replace(
                    "//",
                    "/",
                    StringComparison.Ordinal);
        }

        if (value.Length > 1)
            value = value.TrimEnd('/');

        return value;
    }

    private static CancellationTokenSource CreateTimeout(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var source =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);

        source.CancelAfter(timeout);

        return source;
    }

    private static DispatchResponse Json(
        int status,
        object value)
    {
        return new DispatchResponse(
            Status:
                status,

            ContentType:
                "application/json; charset=utf-8",

            Body:
                JsonSerializer.SerializeToUtf8Bytes(
                    value,
                    JsonOptions));
    }
}

