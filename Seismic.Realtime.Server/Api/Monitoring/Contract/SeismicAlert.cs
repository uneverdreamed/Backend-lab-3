namespace Seismic.Realtime.Server.Api.Monitoring.Contract;
public class SeismicAlert
{
    public Guid Id { get; set; }
    public string StationId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public double Amplitude { get; set; }
    public double Threshold { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
    