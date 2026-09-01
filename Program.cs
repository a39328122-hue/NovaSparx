using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaSparx.Backend;

var builder =
    WebApplication.CreateBuilder(args);

builder.Services
    .ConfigureHttpJsonOptions(
        options =>
        {
            options.SerializerOptions
                    .PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase;

            options.SerializerOptions
                    .DictionaryKeyPolicy =
                null;
        });

builder.Services
    .AddHttpClient<PublicFortniteSources>(
        client =>
        {
            client.Timeout =
                TimeSpan.FromMinutes(3);

            client.DefaultRequestHeaders
                .UserAgent
                .ParseAdd(
                    "NovaSparx/1.0 (+FNAA)");
        });

builder.Services
    .AddSingleton<LiveProviderService>();

builder.Services
    .AddSingleton<MeshResolverService>();

builder.Services
    .AddSingleton<AssetInspectorService>();

builder.Services
    .AddSingleton<TextureService>();

builder.Services
    .AddSingleton<NovaRequestDispatcher>();

builder.Services
    .AddHostedService<NovaLinkHostedService>();

builder.WebHost
    .ConfigureKestrel(
        options =>
        {
            options.Limits
                    .MaxRequestBodySize =
                64 * 1024;

            options.Limits
                    .KeepAliveTimeout =
                TimeSpan.FromMinutes(3);

            options.Limits
                    .RequestHeadersTimeout =
                TimeSpan.FromSeconds(30);
        });

var app =
    builder.Build();

static bool Authorized(
    HttpRequest request)
{
    var expected =
        Environment.GetEnvironmentVariable(
            "NOVASPARX_BACKEND_TOKEN");

    // Public /health stays unauthenticated, but every /v1 direct operation is
    // closed unless a backend token is explicitly configured. AutoLink does
    // not use these HTTP routes, so the reverse tunnel keeps working even when
    // direct fallback is disabled.
    if (string.IsNullOrWhiteSpace(
            expected))
    {
        return false;
    }

    var authorization =
        request.Headers
            .Authorization
            .ToString();

    const string prefix =
        "Bearer ";

    if (!authorization.StartsWith(
            prefix,
            StringComparison.Ordinal))
    {
        return false;
    }

    var supplied =
        authorization[
            prefix.Length..];

    var expectedBytes =
        Encoding.UTF8
            .GetBytes(expected);

    var suppliedBytes =
        Encoding.UTF8
            .GetBytes(supplied);

    return (
        expectedBytes.Length ==
        suppliedBytes.Length &&
        CryptographicOperations
            .FixedTimeEquals(
                expectedBytes,
                suppliedBytes)
    );
}

static Dictionary<string, string>
    QueryDictionary(
        HttpRequest request)
{
    var result =
        new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

    foreach (var pair in request.Query)
    {
        if (!result.ContainsKey(
                pair.Key))
        {
            result[pair.Key] =
                pair.Value.ToString();
        }
    }

    return result;
}

static async Task DispatchHttpAsync(
    HttpContext context,
    NovaRequestDispatcher dispatcher,
    string route,
    bool requireAuth,
    CancellationToken cancellationToken)
{
    if (requireAuth &&
        !Authorized(
            context.Request))
    {
        context.Response.StatusCode =
            StatusCodes.Status401Unauthorized;

        context.Response.ContentType =
            "application/json; charset=utf-8";

        await context.Response
            .WriteAsJsonAsync(
                new
                {
                    state = "error",
                    error = "Unauthorized."
                },
                cancellationToken);

        return;
    }

    var response =
        await dispatcher.DispatchAsync(
            context.Request.Method,
            route,
            QueryDictionary(
                context.Request),
            cancellationToken);

    context.Response.StatusCode =
        response.Status;

    context.Response.ContentType =
        response.ContentType;

    context.Response.Headers[
        "X-Content-Type-Options"] =
        "nosniff";

    if (response.Status == 200 &&
        response.ContentType.StartsWith(
            "image/",
            StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers
            .CacheControl =
            "public, max-age=1800";

        context.Response.Headers
            .ETag =
            $"\"{Convert.ToHexString(
                SHA256.HashData(
                    response.Body))
                .ToLowerInvariant()}\"";
    }
    else
    {
        context.Response.Headers
            .CacheControl =
            "no-store";
    }

    if (response.Body.Length > 0)
    {
        await context.Response.Body
            .WriteAsync(
                response.Body,
                cancellationToken);
    }
}

app.MapGet(
    "/",
    () =>
    {
        var linkUrl =
            Environment.GetEnvironmentVariable(
                "NOVASPARX_LINK_URL");

        return Results.Json(
            new
            {
                ok = true,
                service =
                    "NovaSparx.Backend",
                version =
                    LiveProviderService
                        .BackendVersion,
                schema =
                    "novasparx.preview.v1",
                universalAssetInspection =
                    true,
                staticMeshPreview =
                    true,
                skeletalMeshPreview =
                    true,
                autoLinkConfigured =
                    !string.IsNullOrWhiteSpace(
                        linkUrl),
                endpoints =
                    new[]
                    {
                        "/health",
                        "/v1/health",
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
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/health",
            requireAuth: false,
            cancellationToken));

app.MapGet(
    "/v1/health",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/health",
            requireAuth: true,
            cancellationToken));

app.MapPost(
    "/v1/warmup",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/warmup",
            requireAuth: true,
            cancellationToken));

app.MapPost(
    "/v1/refresh",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/refresh",
            requireAuth: true,
            cancellationToken));

app.MapGet(
    "/v1/resolve",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/resolve",
            requireAuth: true,
            cancellationToken));

app.MapGet(
    "/v1/inspect",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/inspect",
            requireAuth: true,
            cancellationToken));

app.MapGet(
    "/v1/references",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/references",
            requireAuth: true,
            cancellationToken));

app.MapGet(
    "/v1/texture",
    (
        HttpContext context,
        NovaRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        DispatchHttpAsync(
            context,
            dispatcher,
            "/v1/texture",
            requireAuth: true,
            cancellationToken));

app.Run();
