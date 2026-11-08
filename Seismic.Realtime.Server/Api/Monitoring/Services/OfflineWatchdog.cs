namespace Seismic.Realtime.Server.Api.Monitoring.Services;
public class OfflineWatchdog : BackgroundService
{
    private readonly MonitoringHub _hub;
    private readonly ILogger<OfflineWatchdog> _logger;

    public OfflineWatchdog(MonitoringHub hub, ILogger<OfflineWatchdog> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OfflineWatchdog запущен");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _hub.TickOfflineWatchdogAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой в OfflineWatchdog");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}
