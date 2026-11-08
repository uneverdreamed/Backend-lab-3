namespace Seismic.Realtime.Server.Api.Monitoring.Contract;
public class MonitoringEvent
{
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // welcome: кто обслуживает + текущее состояние (все станции, порог, последние алерты)
    public string? ObserverId { get; set; }
    public string? ProcessedBy { get; set; }
    public List<StationState>? Stations { get; set; }
    public List<SeismicAlert>? Alerts { get; set; }

    // station-update/station-offline/station-online
    public StationState? Station { get; set; }

    // alert
    public SeismicAlert? Alert { get; set; }

    // threshold-changed
    public double? Threshold { get; set; }

    // subscription-changed
    public string? Region { get; set; }
    public bool? Subscribed { get; set; }

    // error
    public string? Error { get; set; }
}
