namespace CopilotSessionInspector.Services;

public sealed class SessionCacheWarmupService : BackgroundService
{
    private readonly SessionAnalysisService _analysis;
    private readonly ILogger<SessionCacheWarmupService> _logger;

    public SessionCacheWarmupService(SessionAnalysisService analysis, ILogger<SessionCacheWarmupService> logger)
    {
        _analysis = analysis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            await Task.Run(() => _analysis.GetSessionRows(), stoppingToken);
            _logger.LogInformation("Copilot session caches warmed successfully.");
        }
        catch (OperationCanceledException)
        {
            // App is shutting down before warmup finished.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed warming Copilot session caches.");
        }
    }
}
