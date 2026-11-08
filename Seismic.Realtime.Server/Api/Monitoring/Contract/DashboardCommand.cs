namespace Seismic.Realtime.Server.Api.Monitoring.Contract;
public class DashboardCommand
{
    public string? Type { get; set; }
    public string? Region { get; set; }
    public double? Threshold { get; set; }
}
