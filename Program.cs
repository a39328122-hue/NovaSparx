using System.Text.Json;
using NovaSparx.Backend;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient<PublicFortniteSources>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(3);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("NovaSparx/0.2 (+FNAA)");
});

builder.Services.AddSingleton<LiveProviderService>();

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(3);
});

var app = builder.Build();

static bool Authorized(HttpRequest request)
{
    var expected = Environment.GetEnvironmentVariable("NOVASPARX_BACKEND_TOKEN");
    if (string.IsNullOrWhiteSpace(expected)) return true;

    var auth = request.Headers.Authorization.ToString();
    return string.Equals(auth, $"Bearer {expected}", StringComparison.Ordinal);
}

app.MapGet("/", () => Results.Json(new
{
    ok = true,
    service = "NovaSparx.Backend",
    version = LiveProviderService.BackendVersion
}));

app.MapGet("/health", (LiveProviderService provider) =>
{
    var health = provider.Health();
    return Results.Json(health, statusCode: 200);
});

app.MapPost("/v1/warmup", async (
    HttpRequest request,
    LiveProviderService provider,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(request))
        return Results.Json(new { state = "error", error = "Unauthorized." }, statusCode: 401);

    try
    {
        await provider.EnsureReadyAsync(cancellationToken);
        return Results.Json(provider.Health());
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            state = "error",
            error = ex.Message,
            health = provider.Health()
        }, statusCode: 503);
    }
});

app.MapPost("/v1/refresh", async (
    HttpRequest request,
    LiveProviderService provider,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(request))
        return Results.Json(new { state = "error", error = "Unauthorized." }, statusCode: 401);

    try
    {
        await provider.RefreshAsync(cancellationToken);
        return Results.Json(provider.Health());
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            state = "error",
            error = ex.Message,
            health = provider.Health()
        }, statusCode: 503);
    }
});

app.MapGet("/v1/resolve", async (
    HttpRequest request,
    LiveProviderService provider,
    CancellationToken cancellationToken) =>
{
    if (!Authorized(request))
        return Results.Json(new { state = "error", error = "Unauthorized." }, statusCode: 401);

    var rawPath = request.Query["path"].ToString();

    if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > 2400)
        return Results.Json(new { state = "invalid", error = "Invalid asset path." }, statusCode: 400);

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromMinutes(2));

    try
    {
        var resolved = await provider.ResolveAsync(rawPath, timeout.Token);

        if (resolved is null)
        {
            return Results.Json(new
            {
                state = "missing",
                error = "NovaSparx Live could not resolve this path as a StaticMesh.",
                path = AssetPathResolver.Canonicalize(rawPath)
            }, statusCode: 404);
        }

        return Results.Json(resolved, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new
        {
            state = "error",
            error = "NovaSparx Live timed out while initializing or reading this asset."
        }, statusCode: 504);
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            state = "error",
            error = ex.Message,
            health = provider.Health()
        }, statusCode: 500);
    }
});

app.Run();
