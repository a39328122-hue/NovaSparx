using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaSparx.Backend;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy =
        JsonNamingPolicy.CamelCase;

    options.SerializerOptions.DictionaryKeyPolicy =
        null;
});

builder.Services.AddHttpClient<PublicFortniteSources>(client =>
{
    client.Timeout =
        TimeSpan.FromMinutes(3);

    client.DefaultRequestHeaders
        .UserAgent
        .ParseAdd("NovaSparx/1.0 (+FNAA)");
});

builder.Services.AddSingleton<LiveProviderService>();
builder.Services.AddSingleton<TextureService>();

builder.WebHost.ConfigureKestrel(options =>
{
    // NovaSparx API is path/query driven. Large uploads are not part of the
    // public contract.
    options.Limits.MaxRequestBodySize =
        64 * 1024;

    options.Limits.KeepAliveTimeout =
        TimeSpan.FromMinutes(3);

    options.Limits.RequestHeadersTimeout =
        TimeSpan.FromSeconds(30);
});

var app = builder.Build();

static bool Authorized(HttpRequest request)
{
    var expected =
        Environment.GetEnvironmentVariable(
            "NOVASPARX_BACKEND_TOKEN");

    // Direct backend authentication is optional because the stable FNAA
    // AutoLink layer can be the authenticated edge. When this variable is set,
    // every /v1 operation requires the exact bearer token.
    if (string.IsNullOrWhiteSpace(expected))
        return true;

    var authorization =
        request.Headers.Authorization.ToString();

    const string prefix = "Bearer ";

    if (!authorization.StartsWith(
            prefix,
            StringComparison.Ordinal))
    {
        return false;
    }

    var supplied =
        authorization[prefix.Length..];

    var expectedBytes =
        Encoding.UTF8.GetBytes(expected);

    var suppliedBytes =
        Encoding.UTF8.GetBytes(supplied);

    return expectedBytes.Length ==
           suppliedBytes.Length &&
           CryptographicOperations.FixedTimeEquals(
               expectedBytes,
               suppliedBytes);
}

static IResult Unauthorized()
{
    return Results.Json(
        new
        {
            state = "error",
            error = "Unauthorized."
        },
        statusCode: 401);
}

static bool TryGetAssetPath(
    HttpRequest request,
    out string rawPath,
    out IResult? error)
{
    rawPath =
        request.Query["path"]
            .ToString()
            .Trim();

    if (string.IsNullOrWhiteSpace(rawPath))
    {
        error =
            Results.Json(
                new
                {
                    state = "invalid",
                    error = "Asset path is required."
                },
                statusCode: 400);

        return false;
    }

    if (rawPath.Length > 2400)
    {
        error =
            Results.Json(
                new
                {
                    state = "invalid",
                    error = "Asset path is too long."
                },
                statusCode: 400);

        return false;
    }

    error = null;
    return true;
}

static CancellationTokenSource RequestTimeout(
    CancellationToken requestAborted,
    TimeSpan timeout)
{
    var source =
        CancellationTokenSource
            .CreateLinkedTokenSource(
                requestAborted);

    source.CancelAfter(timeout);

    return source;
}

app.MapGet("/", () =>
{
    return Results.Json(
        new
        {
            ok = true,
            service = "NovaSparx.Backend",
            version = LiveProviderService.BackendVersion,
            schema = "novasparx.preview.v1",
            endpoints = new[]
            {
                "/health",
                "/v1/warmup",
                "/v1/refresh",
                "/v1/resolve?path=...",
                "/v1/inspect?path=...",
                "/v1/references?path=...",
                "/v1/texture?path=..."
            }
        });
});

app.MapGet(
    "/health",
    (
        LiveProviderService provider,
        TextureService textures) =>
    {
        var health =
            provider.Health();

        return Results.Json(
            new
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
                health.PreviewCacheEntries,
                textureCacheEntries =
                    textures.CacheEntries
            },
            statusCode: 200);
    });

app.MapPost(
    "/v1/warmup",
    async (
        HttpRequest request,
        LiveProviderService provider,
        CancellationToken cancellationToken) =>
    {
        if (!Authorized(request))
            return Unauthorized();

        using var timeout =
            RequestTimeout(
                cancellationToken,
                TimeSpan.FromMinutes(3));

        try
        {
            await provider.EnsureReadyAsync(
                timeout.Token);

            return Results.Json(
                provider.Health());
        }
        catch (OperationCanceledException)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error =
                        "NovaSparx warmup timed out.",
                    health =
                        provider.Health()
                },
                statusCode: 504);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error = ex.Message,
                    health =
                        provider.Health()
                },
                statusCode: 503);
        }
    });

app.MapPost(
    "/v1/refresh",
    async (
        HttpRequest request,
        LiveProviderService provider,
        TextureService textures,
        CancellationToken cancellationToken) =>
    {
        if (!Authorized(request))
            return Unauthorized();

        using var timeout =
            RequestTimeout(
                cancellationToken,
                TimeSpan.FromMinutes(3));

        try
        {
            textures.ClearCache();

            await provider.RefreshAsync(
                timeout.Token);

            return Results.Json(
                provider.Health());
        }
        catch (OperationCanceledException)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error =
                        "NovaSparx refresh timed out.",
                    health =
                        provider.Health()
                },
                statusCode: 504);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error = ex.Message,
                    health =
                        provider.Health()
                },
                statusCode: 503);
        }
    });

app.MapGet(
    "/v1/resolve",
    async (
        HttpRequest request,
        LiveProviderService provider,
        CancellationToken cancellationToken) =>
    {
        if (!Authorized(request))
            return Unauthorized();

        if (!TryGetAssetPath(
                request,
                out var rawPath,
                out var pathError))
        {
            return pathError!;
        }

        using var timeout =
            RequestTimeout(
                cancellationToken,
                TimeSpan.FromMinutes(2));

        try
        {
            var resolved =
                await provider.ResolveAsync(
                    rawPath,
                    timeout.Token);

            if (resolved is null)
            {
                return Results.Json(
                    new
                    {
                        state = "missing",
                        error =
                            "NovaSparx could not resolve this path as a renderable StaticMesh.",
                        path =
                            AssetPathResolver.Canonicalize(
                                rawPath)
                    },
                    statusCode: 404);
            }

            return Results.Json(resolved);
        }
        catch (OperationCanceledException)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error =
                        "NovaSparx timed out while resolving this mesh."
                },
                statusCode: 504);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error = ex.Message,
                    health =
                        provider.Health()
                },
                statusCode: 500);
        }
    });

app.MapGet(
    "/v1/inspect",
    async (
        HttpRequest request,
        LiveProviderService provider,
        CancellationToken cancellationToken) =>
    {
        if (!Authorized(request))
            return Unauthorized();

        if (!TryGetAssetPath(
                request,
                out var rawPath,
                out var pathError))
        {
            return pathError!;
        }

        using var timeout =
            RequestTimeout(
                cancellationToken,
                TimeSpan.FromSeconds(90));

        try
        {
            var inspection =
                await provider.InspectAsync(
                    rawPath,
                    timeout.Token);

            if (inspection is null)
            {
                return Results.Json(
                    new
                    {
                        state = "missing",
                        error =
                            "NovaSparx could not load this asset.",
                        path =
                            AssetPathResolver.Canonicalize(
                                rawPath)
                    },
                    statusCode: 404);
            }

            return Results.Json(inspection);
        }
        catch (OperationCanceledException)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error =
                        "NovaSparx timed out while inspecting this asset."
                },
                statusCode: 504);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error = ex.Message
                },
                statusCode: 500);
        }
    });

app.MapGet(
    "/v1/references",
    async (
        HttpRequest request,
        LiveProviderService provider,
        CancellationToken cancellationToken) =>
    {
        if (!Authorized(request))
            return Unauthorized();

        if (!TryGetAssetPath(
                request,
                out var rawPath,
                out var pathError))
        {
            return pathError!;
        }

        using var timeout =
            RequestTimeout(
                cancellationToken,
                TimeSpan.FromSeconds(90));

        try
        {
            var inspection =
                await provider.InspectAsync(
                    rawPath,
                    timeout.Token);

            if (inspection is null)
            {
                return Results.Json(
                    new
                    {
                        state = "missing",
                        error =
                            "NovaSparx could not load this asset.",
                        path =
                            AssetPathResolver.Canonicalize(
                                rawPath)
                    },
                    statusCode: 404);
            }

            return Results.Json(
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
                        inspection.References
                });
        }
        catch (OperationCanceledException)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error =
                        "NovaSparx timed out while resolving asset references."
                },
                statusCode: 504);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error = ex.Message
                },
                statusCode: 500);
        }
    });

app.MapGet(
    "/v1/texture",
    async (
        HttpRequest request,
        HttpResponse response,
        TextureService textures,
        CancellationToken cancellationToken) =>
    {
        if (!Authorized(request))
            return Unauthorized();

        if (!TryGetAssetPath(
                request,
                out var rawPath,
                out var pathError))
        {
            return pathError!;
        }

        using var timeout =
            RequestTimeout(
                cancellation,
                TimeSpan.FromSeconds(90));

        try
        {
            var texture =
                await textures.DecodePngAsync(
                    rawPath,
                    timeout.Token);

            if (texture is null)
            {
                return Results.Json(
                    new
                    {
                        state = "missing",
                        error =
                            "NovaSparx could not resolve this path as a decodable texture.",
                        path =
                            AssetPathResolver.Canonicalize(
                                rawPath)
                    },
                    statusCode: 404);
            }

            response.Headers.CacheControl =
                "public, max-age=1800";

            response.Headers.ETag =
                $"\"{Convert.ToHexString(
                    SHA256.HashData(
                        texture.Bytes))
                    .ToLowerInvariant()}\"";

            response.Headers["X-NovaSparx-Path"] =
                Uri.EscapeDataString(
                    texture.Path);

            response.Headers["X-NovaSparx-Resolved-Path"] =
                Uri.EscapeDataString(
                    texture.ResolvedPath);

            response.Headers["X-NovaSparx-Size"] =
                $"{texture.Width}x{texture.Height}";

            return Results.File(
                texture.Bytes,
                texture.ContentType);
        }
        catch (OperationCanceledException)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error =
                        "NovaSparx timed out while decoding this texture."
                },
                statusCode: 504);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new
                {
                    state = "error",
                    error = ex.Message
                },
                statusCode: 500);
        }
    });

app.Run();
