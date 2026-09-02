namespace NovaSparx.Backend;

public sealed class NovaWarmupHostedService : BackgroundService
{
    private readonly LiveProviderService _provider;
    private readonly ILogger<NovaWarmupHostedService> _log;

    public NovaWarmupHostedService(
        LiveProviderService provider,
        ILogger<NovaWarmupHostedService> log)
    {
        _provider = provider;
        _log = log;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        try
        {
            // Let Kestrel start listening first so Back4App health checks
            // do not wait for the Fortnite provider initialization.
            await Task.Delay(
                TimeSpan.FromSeconds(2),
                stoppingToken);

            _log.LogInformation(
                "NovaSparx provider warmup starting.");

            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken);

            timeout.CancelAfter(
                TimeSpan.FromMinutes(3));

            await _provider.EnsureReadyAsync(
                timeout.Token);

            var health =
                _provider.Health();

            _log.LogInformation(
                "NovaSparx provider warmup finished. Ready={Ready}, RegisteredArchives={RegisteredArchives}, MountedArchives={MountedArchives}, IndexedFiles={IndexedFiles}, LoadedKeys={LoadedKeys}.",
                health.ProviderReady,
                health.RegisteredArchives,
                health.MountedArchives,
                health.IndexedFiles,
                health.LoadedKeys);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "NovaSparx provider warmup timed out. The backend will stay online and can retry on the first asset request.");
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "NovaSparx provider warmup failed. The backend will stay online and can retry on the first asset request.");
        }
    }
}
