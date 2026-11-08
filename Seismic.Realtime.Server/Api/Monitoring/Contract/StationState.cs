namespace Seismic.Realtime.Server.Api.Monitoring.Contract;
public class StationState
{
    public string StationId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public double Amplitude { get; set; }
    public double NoiseLevel { get; set; }
    public string Status { get; set; } = "online"; // online/degraded/offline
    public DateTimeOffset LastTickAt { get; set; }
}
