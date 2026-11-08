using System.Net.WebSockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Seismic.Realtime.Server.Api.Monitoring.Contract;
using Seismic.Realtime.Server.Api.Monitoring.Services;
using Seismic.Realtime.Server.Common;

namespace Seismic.Realtime.Server.Api.Monitoring.Controllers;

[ApiController]
[Route("monitoring")]
public class MonitoringController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerOptions.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly MonitoringHub _hub;
    private readonly ILogger<MonitoringController> _logger;

    public MonitoringController(MonitoringHub hub, ILogger<MonitoringController> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    // HTTP: актуальное состояние для первичной загрузки и для восстановления после реконнекта
    [HttpGet("snapshot")]
    public ActionResult<SnapshotResponse> GetSnapshot()
    {
        return Ok(_hub.BuildSnapshot());
    }

    // WebSocket для станций
    [HttpGet("station-ws")]
    public async Task StationConnect()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        _logger.LogInformation("Станция подключилась");

        var buffer = new byte[8 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    _logger.LogWarning(ex, "Станция оборвала соединение");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }

                StationTick? tick;
                try
                {
                    tick = JsonSerializer.Deserialize<StationTick>(
                        buffer.AsSpan(0, result.Count), JsonOptions);
                }
                catch (JsonException ex)
                {
                    await SendStationErrorAsync(socket, $"Невалидный JSON: {ex.Message}");
                    continue;
                }

                var error = ValidateTick(tick);
                if (error is not null)
                {
                    await SendStationErrorAsync(socket, error);
                    continue;
                }

                try
                {
                    await _hub.HandleTickAsync(tick!);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка обработки тика");
                    await SendStationErrorAsync(socket, "Внутренняя ошибка обработки тика");
                }
            }
        }
        finally
        {
            _logger.LogInformation("Станция отключилась");
        }
    }

    // WebSocket для дашбордов
    [HttpGet("dashboard-ws")]
    public async Task DashboardConnect()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var dashboard = _hub.RegisterDashboard(socket);

        try
        {
            var snapshot = _hub.BuildSnapshot();
            await _hub.SendAsync(dashboard, new MonitoringEvent
            {
                Type = "welcome",
                ObserverId = dashboard.Id.ToString(),
                ProcessedBy = MeInfo.Line,
                Stations = snapshot.Stations,
                Alerts = snapshot.Alerts,
                Threshold = snapshot.Threshold
            });

            var buffer = new byte[8 * 1024];
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    _logger.LogWarning(ex, "Dashboard {Id} оборвал соединение", dashboard.Id);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }

                DashboardCommand? command;
                try
                {
                    command = JsonSerializer.Deserialize<DashboardCommand>(
                        buffer.AsSpan(0, result.Count), JsonOptions);
                }
                catch (JsonException ex)
                {
                    await _hub.SendAsync(dashboard, new MonitoringEvent
                    {
                        Type = "error",
                        Error = $"Невалидный JSON: {ex.Message}"
                    });
                    continue;
                }

                if (command is null || string.IsNullOrWhiteSpace(command.Type))
                {
                    await _hub.SendAsync(dashboard, new MonitoringEvent
                    {
                        Type = "error",
                        Error = "Пустая команда или не указан type"
                    });
                    continue;
                }

                try
                {
                    await _hub.ApplyCommandAsync(dashboard, command);
                }
                catch (ArgumentException ex)
                {
                    await _hub.SendAsync(dashboard, new MonitoringEvent
                    {
                        Type = "error",
                        Error = ex.Message
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при обработке команды дашборда");
                    await _hub.SendAsync(dashboard, new MonitoringEvent
                    {
                        Type = "error",
                        Error = "Внутренняя ошибка"
                    });
                }
            }
        }
        finally
        {
            _hub.UnregisterDashboard(dashboard.Id);
        }
    }

    // --- валидация тика ---

    private static string? ValidateTick(StationTick? tick)
    {
        if (tick is null) return "Пустое сообщение";
        if (string.IsNullOrWhiteSpace(tick.StationId)) return "stationId обязателен";
        if (string.IsNullOrWhiteSpace(tick.Region)) return "region обязателен";
        if (tick.Amplitude is null) return "amplitude обязателен";
        if (double.IsNaN(tick.Amplitude.Value) || double.IsInfinity(tick.Amplitude.Value))
            return "amplitude должен быть конечным числом";
        if (tick.Amplitude < 0) return "amplitude не может быть отрицательным";
        if (tick.NoiseLevel is not null && tick.NoiseLevel < 0)
            return "noiseLevel не может быть отрицательным";
        if (!string.IsNullOrEmpty(tick.Status) && tick.Status != "online" && tick.Status != "degraded")
            return "status должен быть 'online' или 'degraded'";
        return null;
    }

    private static async Task SendStationErrorAsync(WebSocket socket, string message)
    {
        var evt = new MonitoringEvent { Type = "error", Error = message };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evt, JsonOptions);
        if (socket.State == WebSocketState.Open)
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
