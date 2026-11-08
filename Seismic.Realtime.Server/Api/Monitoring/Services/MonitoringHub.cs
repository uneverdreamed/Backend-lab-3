using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Seismic.Realtime.Server.Api.Monitoring.Contract;
using Seismic.Realtime.Server.Common;
using Seismic.Realtime.Server.Options;

namespace Seismic.Realtime.Server.Api.Monitoring.Services;

// In-memory хранилище состояния и диспетчер WebSocket-подписчиков
public class MonitoringHub
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerOptions.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IOptionsMonitor<MonitoringOptions> _options;
    private readonly ILogger<MonitoringHub> _logger;

    // все дашборды и их подписки (пустой Region = подписка на все).
    private readonly ConcurrentDictionary<Guid, Dashboard> _dashboards = new();

    // актуальное состояние каждой станции
    private readonly ConcurrentDictionary<string, StationState> _stations = new();

    // последние N алертов (ограничение в HistorySize).
    private readonly List<SeismicAlert> _alerts = new();

    // текущий порог
    private double _threshold;

    public MonitoringHub(IOptionsMonitor<MonitoringOptions> options, ILogger<MonitoringHub> logger)
    {
        _options = options;
        _logger = logger;
        _threshold = options.CurrentValue.AmplitudeThreshold;
    }

    public double Threshold => _threshold;

    // --- снапшот состояния (для HTTP /monitoring/snapshot и для welcome-сообщения) ---

    public SnapshotResponse BuildSnapshot()
    {
        return new SnapshotResponse
        {
            ProcessedBy = MeInfo.Line,
            Threshold = _threshold,
            Stations = _stations.Values
                .OrderBy(s => s.StationId)
                .ToList(),
            Alerts = CloneAlerts()
        };
    }

    private List<SeismicAlert> CloneAlerts()
    {
        lock (_alerts)
        {
            return _alerts.ToList();
        }
    }

    // --- регистрация/удаление дашборда ---

    public Dashboard RegisterDashboard(WebSocket socket)
    {
        var dashboard = new Dashboard
        {
            Id = Guid.NewGuid(),
            Socket = socket
        };
        _dashboards[dashboard.Id] = dashboard;
        _logger.LogInformation("Dashboard {Id} подключен. Всего: {Count}", dashboard.Id, _dashboards.Count);
        return dashboard;
    }

    public void UnregisterDashboard(Guid id)
    {
        if (_dashboards.TryRemove(id, out _))
        {
            _logger.LogInformation("Dashboard {Id} отключён. Осталось: {Count}", id, _dashboards.Count);
        }
    }

    // --- приход тика от станции ---

    public async Task HandleTickAsync(StationTick tick)
    {
        var state = new StationState
        {
            StationId = tick.StationId!,
            Region = tick.Region ?? "unknown",
            Amplitude = tick.Amplitude!.Value,
            NoiseLevel = tick.NoiseLevel ?? 0.0,
            Status = string.IsNullOrWhiteSpace(tick.Status) ? "online" : tick.Status!,
            LastTickAt = tick.CreatedAt ?? DateTimeOffset.UtcNow
        };

        var previousStatus = _stations.TryGetValue(state.StationId, out var prev) ? prev.Status : null;
        _stations[state.StationId] = state;

        await BroadcastAsync(state.Region, new MonitoringEvent
        {
            Type = "station-update",
            Station = state
        });

        if (previousStatus == "offline" && state.Status != "offline")
        {
            await BroadcastAsync(state.Region, new MonitoringEvent
            {
                Type = "station-online",
                Station = state
            });
        }

        if (state.Amplitude >= _threshold)
        {
            var alert = new SeismicAlert
            {
                Id = Guid.NewGuid(),
                StationId = state.StationId,
                Region = state.Region,
                Amplitude = state.Amplitude,
                Threshold = _threshold,
                CreatedAt = DateTimeOffset.UtcNow
            };

            lock (_alerts)
            {
                _alerts.Add(alert);
                var max = _options.CurrentValue.HistorySize;
                while (_alerts.Count > max)
                {
                    _alerts.RemoveAt(0);
                }
            }

            await BroadcastAsync(state.Region, new MonitoringEvent
            {
                Type = "alert",
                Alert = alert
            });
        }
    }

    // --- фоновая задача ищет пропавшие станции и шлет station-offline ---

    public async Task TickOfflineWatchdogAsync(CancellationToken ct)
    {
        var deadline = TimeSpan.FromSeconds(_options.CurrentValue.StationOfflineAfterSeconds);
        var now = DateTimeOffset.UtcNow;

        foreach (var state in _stations.Values)
        {
            if (state.Status == "offline") continue;
            if (now - state.LastTickAt <= deadline) continue;

            state.Status = "offline";
            _logger.LogWarning("Станция {StationId} ушла в offline (нет тиков > {Seconds} сек)",
                state.StationId, deadline.TotalSeconds);

            await BroadcastAsync(state.Region, new MonitoringEvent
            {
                Type = "station-offline",
                Station = state
            });
        }

        await Task.CompletedTask;
    }

    // --- команды дашборда ---

    public async Task ApplyCommandAsync(Dashboard dashboard, DashboardCommand command)
    {
        switch (command.Type)
        {
            case "subscribe":
                dashboard.Region = string.IsNullOrWhiteSpace(command.Region) ? null : command.Region;
                await SendAsync(dashboard, new MonitoringEvent
                {
                    Type = "subscription-changed",
                    Region = dashboard.Region,
                    Subscribed = true
                });
                break;

            case "unsubscribe":
                dashboard.Region = null;
                await SendAsync(dashboard, new MonitoringEvent
                {
                    Type = "subscription-changed",
                    Region = null,
                    Subscribed = false
                });
                break;

            case "set-threshold":
                if (command.Threshold is null || command.Threshold < 0)
                {
                    throw new ArgumentException("threshold должен быть неотрицательным числом");
                }
                _threshold = command.Threshold.Value;
                await BroadcastAsync(region: null, new MonitoringEvent
                {
                    Type = "threshold-changed",
                    Threshold = _threshold
                });
                break;

            case "ping":
                await SendAsync(dashboard, new MonitoringEvent { Type = "pong" });
                break;

            default:
                throw new ArgumentException($"Неизвестный тип команды: '{command.Type}'");
        }
    }

    // --- Отправка ---

    private async Task BroadcastAsync(string? region, MonitoringEvent evt)
    {
        foreach (var dashboard in _dashboards.Values)
        {
            if (dashboard.Socket.State != WebSocketState.Open) continue;
            if (region is not null && dashboard.Region is not null && dashboard.Region != region) continue;
            await SendAsync(dashboard, evt);
        }
    }

    public async Task SendAsync(Dashboard dashboard, MonitoringEvent evt)
    {
        await dashboard.SendLock.WaitAsync();
        try
        {
            if (dashboard.Socket.State != WebSocketState.Open) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(evt, JsonOptions);
            await dashboard.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка отправки дашборду {Id}", dashboard.Id);
        }
        finally
        {
            dashboard.SendLock.Release();
        }
    }
}

// состояние одного подключенного дашборда.
public class Dashboard
{
    public Guid Id { get; set; }
    public WebSocket Socket { get; set; } = default!;
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public string? Region { get; set; }
}
