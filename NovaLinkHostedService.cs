using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NovaSparx.Backend;

/// <summary>
/// Persistent reverse WebSocket client used by NovaSparx AutoLink.
///
/// NovaSparx connects OUT to the stable Cloudflare endpoint, so Back4App does
/// not need a stable inbound hostname for FNAA to reach heavy asset parsing.
///
/// Environment:
///   NOVASPARX_LINK_URL   = wss://.../connect
///   NOVASPARX_LINK_TOKEN = shared secret used only in request headers
///
/// Protocol:
///   Worker -> Nova:
///     {"type":"request","id":"...","method":"GET",
///      "path":"/v1/resolve","query":{"path":"/Game/..."}}
///
///   Nova -> Worker:
///     {"type":"response","id":"...","status":200,
///      "contentType":"application/json; charset=utf-8",
///      "length":1234,"chunks":1}
///     <binary body chunk(s), max 512 KiB each>
///     {"type":"response_end","id":"..."}
///
/// Requests are intentionally processed sequentially on one socket. That keeps
/// binary response chunks deterministic and removes the need to multiplex a
/// request id into every binary frame.
/// </summary>
public sealed class NovaLinkHostedService : BackgroundService
{
    private const int ChunkSize =
        512 * 1024;

    private const int MaxResponseBytes =
        64 * 1024 * 1024;

    private const int MaxControlMessageBytes =
        256 * 1024;

    private readonly NovaRequestDispatcher _dispatcher;
    private readonly ILogger<NovaLinkHostedService> _log;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase
        };

    public NovaLinkHostedService(
        NovaRequestDispatcher dispatcher,
        ILogger<NovaLinkHostedService> log)
    {
        _dispatcher = dispatcher;
        _log = log;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var rawUrl =
            Environment.GetEnvironmentVariable(
                "NOVASPARX_LINK_URL");

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            _log.LogInformation(
                "NovaLink disabled: NOVASPARX_LINK_URL is not configured.");

            return;
        }

        if (!Uri.TryCreate(
                rawUrl,
                UriKind.Absolute,
                out var linkUri) ||
            linkUri.Scheme is not ("ws" or "wss"))
        {
            _log.LogError(
                "NovaLink disabled: NOVASPARX_LINK_URL must be an absolute ws:// or wss:// URL.");

            return;
        }

        var token =
            Environment.GetEnvironmentVariable(
                "NOVASPARX_LINK_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogError(
                "NovaLink disabled: NOVASPARX_LINK_TOKEN is not configured.");

            return;
        }

        var reconnectAttempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var socket =
                    CreateSocket(token);

                _log.LogInformation(
                    "NovaLink connecting to {Host}.",
                    linkUri.Host);

                await socket.ConnectAsync(
                    linkUri,
                    stoppingToken);

                reconnectAttempt = 0;

                _log.LogInformation(
                    "NovaLink connected.");

                await RunConnectedAsync(
                    socket,
                    stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                {
                    _log.LogWarning(
                        "NovaLink connection closed; reconnecting.");
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "NovaLink connection failed.");
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            reconnectAttempt++;

            var delay =
                ReconnectDelay(
                    reconnectAttempt);

            try
            {
                await Task.Delay(
                    delay,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static ClientWebSocket CreateSocket(
        string token)
    {
        var socket =
            new ClientWebSocket();

        socket.Options.KeepAliveInterval =
            TimeSpan.FromSeconds(20);

        socket.Options.SetRequestHeader(
            "Authorization",
            $"Bearer {token}");

        socket.Options.SetRequestHeader(
            "X-NovaSparx-Link-Token",
            token);

        socket.Options.SetRequestHeader(
            "X-NovaSparx-Version",
            LiveProviderService.BackendVersion);

        return socket;
    }

    private async Task RunConnectedAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        while (
            socket.State == WebSocketState.Open &&
            !cancellationToken.IsCancellationRequested)
        {
            var text =
                await ReceiveTextMessageAsync(
                    socket,
                    cancellationToken);

            if (text is null)
                break;

            LinkRequest? request;

            try
            {
                request =
                    JsonSerializer.Deserialize<LinkRequest>(
                        text,
                        JsonOptions);
            }
            catch (JsonException ex)
            {
                _log.LogDebug(
                    ex,
                    "NovaLink received invalid JSON.");

                await SendControlAsync(
                    socket,
                    new
                    {
                        type = "protocol_error",
                        error = "Invalid JSON control message."
                    },
                    cancellationToken);

                continue;
            }

            if (request is null)
                continue;

            var type =
                request.Type?
                    .Trim()
                    .ToLowerInvariant();

            if (type == "hello")
            {
                // AutoLink sends one negotiated hello immediately after the
                // WebSocket upgrade. It is informational, not an RPC request.
                continue;
            }

            if (type == "ping")
            {
                await SendControlAsync(
                    socket,
                    new
                    {
                        type = "pong",
                        time =
                            DateTimeOffset.UtcNow
                    },
                    cancellationToken);

                continue;
            }

            if (type != "request")
            {
                await SendControlAsync(
                    socket,
                    new
                    {
                        type = "protocol_error",
                        error =
                            "Unsupported NovaLink control message."
                    },
                    cancellationToken);

                continue;
            }

            if (string.IsNullOrWhiteSpace(
                    request.Id))
            {
                await SendControlAsync(
                    socket,
                    new
                    {
                        type = "protocol_error",
                        error =
                            "Request id is required."
                    },
                    cancellationToken);

                continue;
            }

            await HandleRequestAsync(
                socket,
                request,
                cancellationToken);
        }

        if (socket.State is
            WebSocketState.Open or
            WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "NovaLink reconnect",
                    cancellationToken);
            }
            catch
            {
                // Socket is already unusable. The outer reconnect loop handles it.
            }
        }
    }

    private async Task HandleRequestAsync(
        ClientWebSocket socket,
        LinkRequest request,
        CancellationToken cancellationToken)
    {
        var query =
            MergeQuery(
                request.Path,
                request.Query);

        var route =
            StripQuery(
                request.Path);

        var response =
            await _dispatcher.DispatchAsync(
                request.Method ?? "GET",
                route,
                query,
                cancellationToken);

        if (response.Body.Length > MaxResponseBytes)
        {
            response =
                JsonError(
                    502,
                    "NovaSparx response exceeded the 64 MiB AutoLink limit.");
        }

        var chunks =
            response.Body.Length == 0
                ? 0
                : (response.Body.Length +
                   ChunkSize - 1) /
                  ChunkSize;

        await SendControlAsync(
            socket,
            new
            {
                type = "response",
                id = request.Id,
                status = response.Status,
                contentType =
                    response.ContentType,
                length =
                    response.Body.Length,
                chunks
            },
            cancellationToken);

        var offset = 0;

        while (offset < response.Body.Length)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var count =
                Math.Min(
                    ChunkSize,
                    response.Body.Length - offset);

            await socket.SendAsync(
                response.Body.AsMemory(
                    offset,
                    count),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);

            offset += count;
        }

        await SendControlAsync(
            socket,
            new
            {
                type = "response_end",
                id = request.Id
            },
            cancellationToken);
    }

    private static async Task<string?>
        ReceiveTextMessageAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
    {
        using var stream =
            new MemoryStream();

        var buffer =
            new byte[16 * 1024];

        while (true)
        {
            var result =
                await socket.ReceiveAsync(
                    buffer,
                    cancellationToken);

            if (result.MessageType ==
                WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType !=
                WebSocketMessageType.Text)
            {
                throw new InvalidDataException(
                    "NovaLink accepts only text control messages from the worker.");
            }

            if (result.Count > 0)
            {
                stream.Write(
                    buffer,
                    0,
                    result.Count);
            }

            if (stream.Length >
                MaxControlMessageBytes)
            {
                throw new InvalidDataException(
                    "NovaLink control message exceeded 256 KiB.");
            }

            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(
            stream.GetBuffer(),
            0,
            checked((int)stream.Length));
    }

    private static async Task SendControlAsync(
        ClientWebSocket socket,
        object value,
        CancellationToken cancellationToken)
    {
        var bytes =
            JsonSerializer.SerializeToUtf8Bytes(
                value,
                JsonOptions);

        await socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static Dictionary<string, string>
        MergeQuery(
            string? rawPath,
            Dictionary<string, string>? supplied)
    {
        var result =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        if (supplied is not null)
        {
            foreach (var pair in supplied)
            {
                result[pair.Key] =
                    pair.Value;
            }
        }

        if (string.IsNullOrWhiteSpace(rawPath))
            return result;

        var question =
            rawPath.IndexOf('?');

        if (question < 0 ||
            question + 1 >= rawPath.Length)
        {
            return result;
        }

        var query =
            rawPath[(question + 1)..];

        foreach (var piece in
                 query.Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var equals =
                piece.IndexOf('=');

            var key =
                equals < 0
                    ? piece
                    : piece[..equals];

            var value =
                equals < 0
                    ? string.Empty
                    : piece[(equals + 1)..];

            key =
                Uri.UnescapeDataString(
                    key.Replace('+', ' '));

            value =
                Uri.UnescapeDataString(
                    value.Replace('+', ' '));

            if (key.Length > 0 &&
                !result.ContainsKey(key))
            {
                result[key] =
                    value;
            }
        }

        return result;
    }

    private static string StripQuery(
        string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return "/";

        var value =
            rawPath.Trim();

        var question =
            value.IndexOf('?');

        if (question >= 0)
            value = value[..question];

        return value.Length == 0
            ? "/"
            : value;
    }

    private static TimeSpan ReconnectDelay(
        int attempt)
    {
        var exponent =
            Math.Clamp(
                attempt - 1,
                0,
                5);

        var seconds =
            Math.Min(
                30,
                1 << exponent);

        // Small jitter prevents a fleet of restarted containers reconnecting
        // at exactly the same instant.
        var jitterMilliseconds =
            Random.Shared.Next(
                0,
                750);

        return TimeSpan.FromMilliseconds(
            seconds * 1000 +
            jitterMilliseconds);
    }

    private static DispatchResponse JsonError(
        int status,
        string error)
    {
        var body =
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    state = "error",
                    error
                },
                JsonOptions);

        return new DispatchResponse(
            Status:
                status,

            ContentType:
                "application/json; charset=utf-8",

            Body:
                body);
    }

    private sealed class LinkRequest
    {
        public string? Type { get; init; }
        public string? Id { get; init; }
        public string? Method { get; init; }
        public string? Path { get; init; }
        public Dictionary<string, string>? Query { get; init; }
    }
}
