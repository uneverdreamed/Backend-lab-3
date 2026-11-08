using Seismic.Realtime.Server.Api.Monitoring.Services;
using Seismic.Realtime.Server.Common;
using Seismic.Realtime.Server.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(config => config.ReadFrom.Configuration(builder.Configuration));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection("Monitoring"));

builder.Services.AddSingleton<MonitoringHub>();

builder.Services.AddHostedService<OfflineWatchdog>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseWebSockets();

app.MapControllers();

Console.WriteLine();
Console.WriteLine("====================================================");
Console.WriteLine("  Seismic.Realtime.Server — лаба №3 (WebSocket)");
Console.WriteLine($"  {MeInfo.Line}");
Console.WriteLine("====================================================");
Console.WriteLine();

app.Run();
