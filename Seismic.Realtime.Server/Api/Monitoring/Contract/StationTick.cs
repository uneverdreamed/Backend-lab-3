namespace Seismic.Realtime.Server.Api.Monitoring.Contract;
public class StationTick
{
    public string? StationId { get; set; }
    public string? Region { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public double? Amplitude { get; set; }
    public double? NoiseLevel { get; set; }
    public string? Status { get; set; }
}
