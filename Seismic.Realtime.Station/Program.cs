using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Seismic.Realtime.Station.Common;

// Эмулятор сейсмостанции
//
// Аргументы командной строки:
//   --id KDTR            — идентификатор станции (по умолчанию случайный)
//   --region kamchatka   — регион
//   --server ws://host:port — адрес сервера, по умолчанию ws://localhost:5090
//   --interval 500       — пауза между тиками, мс
//   --quiet              — тихий режим без --bad
//   --bad                — каждые 5 тиков слать «мусор» для демонстрации валидации

var opts = ParseArgs(args);

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine();
Console.WriteLine("====================================================");
Console.WriteLine("  Seismic.Realtime.Station — эмулятор сейсмостанции");
Console.WriteLine($"  {MeInfo.Line}");
Console.WriteLine($"  station: {opts.StationId}   region: {opts.Region}");
Console.WriteLine($"  server:  {opts.Server}/monitoring/station-ws");
Console.WriteLine("====================================================");
Console.WriteLine();

var jsonOptions = new JsonSerializerOptions(JsonSerializerOptions.Web)
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var random = new Random();
var tickCounter = 0;

// главный цикл с автореконнектом
while (!cts.IsCancellationRequested)
{
    using var socket = new ClientWebSocket();
    var uri = new Uri($"{opts.Server}/monitoring/station-ws");

    try
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Подключаюсь к {uri}");
        await socket.ConnectAsync(uri, cts.Token);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Соединение установлено");

        // отдельная задача читает ответы сервера (в основном error-сообщения)
        var readerTask = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (!cts.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                try
                {
                    var r = await socket.ReceiveAsync(buffer, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    var text = Encoding.UTF8.GetString(buffer, 0, r.Count);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ← сервер: {text}");
                }
                catch (OperationCanceledException) { return; }
                catch (WebSocketException) { return; }
            }
        }, cts.Token);

        while (!cts.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            tickCounter++;
            object payload;

            if (opts.SendBad && tickCounter % 5 == 0)
            {
                payload = new { stationId = opts.StationId, amplitude = "это не число" };
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] → тик #{tickCounter}: СПЕЦИАЛЬНО БИТЫЙ (для демо валидации)");
            }
            else
            {
                var isBurst = random.NextDouble() < 0.05;
                var amplitude = isBurst
                    ? Math.Round(5.5 + random.NextDouble() * 3.0, 2)
                    : Math.Round(0.5 + random.NextDouble() * 3.5, 2);

                payload = new
                {
                    stationId = opts.StationId,
                    region = opts.Region,
                    createdAt = DateTimeOffset.UtcNow,
                    amplitude,
                    noiseLevel = Math.Round(0.1 + random.NextDouble() * 0.6, 2),
                    status = "online"
                };

                if (!opts.Quiet)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] → тик #{tickCounter}: A = {amplitude}{(isBurst ? " (всплеск!)" : "")}");
                }
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
            await Task.Delay(opts.IntervalMs, cts.Token);
        }

        await readerTask;
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Сбой: {ex.GetType().Name}: {ex.Message}");
    }

    if (cts.IsCancellationRequested) break;

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Разрыв. Повтор через 3 сек...");
    try { await Task.Delay(TimeSpan.FromSeconds(3), cts.Token); }
    catch (OperationCanceledException) { break; }
}

Console.WriteLine("Станция остановлена.");

static StationOptions ParseArgs(string[] args)
{
    var o = new StationOptions
    {
        StationId = "KDTR-" + Guid.NewGuid().ToString("N")[..4].ToUpper(),
        Region = "kamchatka",
        Server = "ws://localhost:5090",
        IntervalMs = 1000,
        Quiet = false,
        SendBad = false
    };
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--id" when i + 1 < args.Length: o.StationId = args[++i]; break;
            case "--region" when i + 1 < args.Length: o.Region = args[++i]; break;
            case "--server" when i + 1 < args.Length: o.Server = args[++i]; break;
            case "--interval" when i + 1 < args.Length: o.IntervalMs = int.Parse(args[++i]); break;
            case "--quiet": o.Quiet = true; break;
            case "--bad": o.SendBad = true; break;
        }
    }
    return o;
}

internal class StationOptions
{
    public string StationId { get; set; } = "";
    public string Region { get; set; } = "";
    public string Server { get; set; } = "";
    public int IntervalMs { get; set; }
    public bool Quiet { get; set; }
    public bool SendBad { get; set; }
}
