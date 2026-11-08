namespace Seismic.Realtime.Server.Api.Monitoring.Contract;
public class SnapshotResponse
{
    public string ProcessedBy { get; set; } = string.Empty;
    public double Threshold { get; set; }
    public List<StationState> Stations { get; set; } = new();
    public List<SeismicAlert> Alerts { get; set; } = new();
}
