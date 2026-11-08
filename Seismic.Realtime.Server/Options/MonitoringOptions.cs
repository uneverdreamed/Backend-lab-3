namespace Seismic.Realtime.Server.Options;
public class MonitoringOptions
{
    public double AmplitudeThreshold { get; set; } = 5.0; // порог амплитуды
    public int HistorySize { get; set; } = 50;
    public int StationOfflineAfterSeconds { get; set; } = 10;
}
