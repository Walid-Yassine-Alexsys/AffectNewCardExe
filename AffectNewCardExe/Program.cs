// .NET 8 Console/Minimal Program – RFID/Moxa → HEX → SignalR
// No DB, no HTTP, no REST. Only read HEX and publish.
// TestMode = manual HEX typing. Normal mode = TCP RFID reading.

using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR.Management;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

#region Options

public sealed class AppOptions
{
    public bool TestMode { get; set; } = false;
    public int HttpPort { get; set; } = 5003; // unused but kept for config simplicity
}

public sealed class RfidOptions
{
    public string Host { get; set; } = "10.10.10.10";
    public string? FallbackHost { get; set; } = null;
    public int Port { get; set; } = 4001;
    public int ReconnectDelayMs { get; set; } = 2000;
    public int ReadBufferBytes { get; set; } = 4096;
    public string? LineTerminatorRegex { get; set; }
}

public sealed class SignalROptions
{
    public string ConnectionString { get; set; } = default!;
    public string HubName { get; set; } = "read_new_card_hub";
    public string MethodName { get; set; } = "ReceiveHex";
}

public sealed class DeviceOptions
{
    public string? Name { get; set; }
}

#endregion

#region Device Identity

public static class DeviceIdentity
{
    private static readonly string DeviceIdFile;
    private static readonly string DeviceNameFile;
    private static string? _cachedDeviceId;
    private static string? _cachedDeviceName;

    static DeviceIdentity()
    {
        var baseDirectory = AppContext.BaseDirectory;
        DeviceIdFile = Path.Combine(baseDirectory, "device.id.txt");
        DeviceNameFile = Path.Combine(baseDirectory, "device.name.txt");
    }

    public static string GetOrCreateDeviceId()
    {
        if (_cachedDeviceId is not null) return _cachedDeviceId;

        if (File.Exists(DeviceIdFile))
        {
            var v = File.ReadAllText(DeviceIdFile).Trim();
            if (!string.IsNullOrWhiteSpace(v)) return _cachedDeviceId = v;
        }

        var id = Guid.NewGuid().ToString();
        File.WriteAllText(DeviceIdFile, id);
        return _cachedDeviceId = id;
    }

    public static string GetOrCreateDeviceName(string? configuredName = null)
    {
        if (_cachedDeviceName is not null) return _cachedDeviceName;

        if (File.Exists(DeviceNameFile))
        {
            var v = File.ReadAllText(DeviceNameFile).Trim();
            if (!string.IsNullOrWhiteSpace(v)) return _cachedDeviceName = v;
        }

        var deviceId = GetOrCreateDeviceId();
        var last4 = deviceId[^4..];
        var def = $"kiosk--{last4}".ToLower();

        var name = string.IsNullOrWhiteSpace(configuredName) ? def : configuredName;
        File.WriteAllText(DeviceNameFile, name);

        return _cachedDeviceName = name;
    }
}

#endregion

#region Tag Args
public sealed class TagEventArgs : EventArgs
{
    public required string Hex { get; init; }
    public required DateTime Timestamp { get; init; }
}
#endregion

#region RFID Reader (no DB)

public sealed class RfidReaderService : IHostedService
{
    private readonly ILogger<RfidReaderService> _log;
    private readonly RfidOptions _opt;
    private CancellationTokenSource? _cts;

    public event EventHandler<TagEventArgs>? TagReceived;

    public RfidReaderService(ILogger<RfidReaderService> log, IOptions<RfidOptions> opt)
    {
        _log = log;
        _opt = opt.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { }
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TcpClient? tcp = null;
                string[] hosts = { _opt.Host, _opt.FallbackHost ?? "" };

                Console.WriteLine($"\n[RFID] Trying to connect to primary {_opt.Host}:{_opt.Port} ...");

                foreach (var host in hosts)
                {
                    if (string.IsNullOrWhiteSpace(host)) continue;

                    try
                    {
                        tcp = new TcpClient();

                        var connectTask = tcp.ConnectAsync(host, _opt.Port);
                        var finished = await Task.WhenAny(connectTask, Task.Delay(2000, ct));

                        if (finished == connectTask)
                        {
                            await connectTask;
                            Console.WriteLine($"[RFID] CONNECTED to {host}:{_opt.Port}");
                            _log.LogInformation("RFID Connected on {host}:{port}", host, _opt.Port);
                            break;
                        }
                        else
                        {
                            Console.WriteLine($"[RFID] Timeout connecting to {host}:{_opt.Port}");
                            _log.LogWarning("Timeout connecting to {host}", host);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RFID] ERROR connecting to {host}:{_opt.Port} -> {ex.Message}");
                        _log.LogWarning("Cannot connect to {host}", host);
                    }

                    // If failed primary and fallback exists:
                    if (host == _opt.Host && !string.IsNullOrWhiteSpace(_opt.FallbackHost))
                        Console.WriteLine($"[RFID] Trying fallback {_opt.FallbackHost}:{_opt.Port} ...");
                }

                if (tcp == null || !tcp.Connected)
                {
                    Console.WriteLine("[RFID] BOTH primary and fallback connections FAILED. Retrying...");
                    await Task.Delay(_opt.ReconnectDelayMs, ct);
                    continue;
                }


               
                using var stream = tcp.GetStream();
                var buffer = ArrayPool<byte>.Shared.Rent(_opt.ReadBufferBytes);
                var sb = new StringBuilder();

                while (!ct.IsCancellationRequested)
                {
                    if (!stream.DataAvailable)
                    {
                        await Task.Delay(5, ct);
                        continue;
                    }

                    int n = await stream.ReadAsync(buffer.AsMemory(0, _opt.ReadBufferBytes), ct);
                    if (n <= 0) throw new IOException("RFID closed");

                    var chunk = Encoding.ASCII.GetString(buffer, 0, n);
                    sb.Append(chunk);

                    foreach (var line in Split(sb, _opt.LineTerminatorRegex))
                    {
                        var txt = Strip(line);
                        if (string.IsNullOrWhiteSpace(txt)) continue;

                        var hex = ToHex(txt);

                        TagReceived?.Invoke(this, new TagEventArgs
                        {
                            Hex = hex,
                            Timestamp = DateTime.Now
                        });

                        _log.LogInformation("HEX={hex}", hex);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "RFID error");
                await Task.Delay(_opt.ReconnectDelayMs, ct);
            }
        }
    }

    private static IEnumerable<string> Split(StringBuilder sb, string? regex)
    {
        if (string.IsNullOrEmpty(regex))
        {
            var text = sb.ToString();
            var parts = text.Split('\n');
            for (int i = 0; i < parts.Length - 1; i++)
                yield return parts[i];

            sb.Clear().Append(parts[^1]);
        }
        else
        {
            var rx = new Regex(regex);
            var txt = sb.ToString();
            int last = 0;

            foreach (Match m in rx.Matches(txt))
            {
                yield return txt[last..m.Index];
                last = m.Index + m.Length;
            }

            sb.Clear().Append(txt[last..]);
        }
    }

    private static string Strip(string s)
        => new string(s.Where(ch => !char.IsControl(ch)).ToArray());

    private static string ToHex(string ascii)
    {
        var b = Encoding.ASCII.GetBytes(ascii);
        var sb = new StringBuilder();

        foreach (var x in b)
            sb.Append(x.ToString("X2"));

        return sb.ToString();
    }
}

#endregion

#region SignalR Publisher
public sealed class HexPublisher : IHostedService
{
    private readonly ILogger<HexPublisher> _log;
    private readonly SignalROptions _opt;
    private readonly DeviceOptions _dev;

    private ServiceManager? _mgr;
    private ServiceHubContext? _hub;

    private readonly string _deviceId;
    private readonly string _deviceName;

    public HexPublisher(
        ILogger<HexPublisher> log,
        IOptions<SignalROptions> opt,
        IOptions<DeviceOptions> dev)
    {
        _log = log;
        _opt = opt.Value;
        _dev = dev.Value;

        _deviceId = DeviceIdentity.GetOrCreateDeviceId();
        _deviceName = DeviceIdentity.GetOrCreateDeviceName(dev.Value.Name);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _mgr = new ServiceManagerBuilder()
            .WithOptions(o => o.ConnectionString = _opt.ConnectionString)
            .BuildServiceManager();

        _hub = await _mgr.CreateHubContextAsync(_opt.HubName, cancellationToken);
        _log.LogInformation("Connected to SignalR hub {hub}", _opt.HubName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task SendHexAsync(string hex)
    {
        if (_hub == null) return;

        var payload = new
        {
            hex,
            deviceId = _deviceId,
            deviceName = _deviceName,
            tsUtc = DateTime.UtcNow
        };

        await _hub.Clients.All.SendAsync(_opt.MethodName, payload);
        _log.LogInformation("Sent: {p}", JsonSerializer.Serialize(payload));
    }
}

#endregion

#region Resolver

public sealed class RfidToSignalRService : IHostedService
{
    private readonly ILogger<RfidToSignalRService> _log;
    private readonly AppOptions _app;
    private readonly RfidReaderService _reader;
    private readonly HexPublisher _publisher;

    private CancellationTokenSource? _cts;

    public RfidToSignalRService(
        ILogger<RfidToSignalRService> log,
        IOptions<AppOptions> app,
        RfidReaderService reader,
        HexPublisher publisher)
    {
        _log = log;
        _app = app.Value;
        _reader = reader;
        _publisher = publisher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_app.TestMode)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = Task.Run(() => ManualLoopAsync(_cts.Token));
            _log.LogWarning("TEST MODE: type HEX and press ENTER");
        }
        else
        {
            _reader.TagReceived += OnTag;
            _log.LogInformation("Listening for RFID...");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app.TestMode) _cts?.Cancel();
        else _reader.TagReceived -= OnTag;

        return Task.CompletedTask;
    }

    private async Task ManualLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Console.Write("\nHEX: ");
            var line = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            await _publisher.SendHexAsync(line.Trim());
        }
    }

    private async void OnTag(object? s, TagEventArgs e)
    {
        await _publisher.SendHexAsync(e.Hex);
    }
}

#endregion

#region Config Loader

static class ConfigLoader
{
    public static IConfiguration Load()
    {
        var baseDir = AppContext.BaseDirectory;
        var external = Path.Combine(baseDir, "appsettings.json");

        var cb = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddEnvironmentVariables();

        if (File.Exists(external))
        {
            cb.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            return cb.Build();
        }

        // EMBEDDED
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new Exception("Embedded appsettings.json not found.");

        using var stream = asm.GetManifestResourceStream(resourceName)
                      ?? throw new Exception("appsettings.json stream is null.");

        cb.AddJsonStream(stream);
        return cb.Build();
    }
}


#endregion

#region Program Main

public class Program
{
    public static async Task Main(string[] args)
    {
        var cfg = ConfigLoader.Load();

        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration.AddConfiguration(cfg);

        builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
        builder.Services.Configure<RfidOptions>(builder.Configuration.GetSection("Rfid"));
        builder.Services.Configure<SignalROptions>(builder.Configuration.GetSection("SignalR"));
        builder.Services.Configure<DeviceOptions>(builder.Configuration.GetSection("Device"));

        builder.Services.AddSingleton<RfidReaderService>();
        builder.Services.AddSingleton<HexPublisher>();
        builder.Services.AddSingleton<RfidToSignalRService>();

        builder.Services.AddHostedService(sp => sp.GetRequiredService<HexPublisher>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RfidReaderService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RfidToSignalRService>());

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss "; o.SingleLine = true; });

        var app = builder.Build();

        Console.WriteLine("===============================================");
        Console.WriteLine("  RFID → HEX → SignalR");
        Console.WriteLine("  TestMode = " + (cfg.GetValue<bool>("App:TestMode") ? "YES" : "NO"));
        Console.WriteLine("  Device Files:");
        Console.WriteLine("     " + DeviceIdentity.GetOrCreateDeviceId());
        Console.WriteLine("     " + DeviceIdentity.GetOrCreateDeviceName());
        Console.WriteLine("===============================================");

        await app.RunAsync();
    }
}

#endregion
