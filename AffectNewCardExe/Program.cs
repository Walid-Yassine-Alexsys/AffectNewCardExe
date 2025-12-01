// .NET 8 Console – RFID/Moxa → RAW DATA → SignalR
// No DB, no HTTP, only read raw ASCII and publish.

using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR.Management;
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
}

public sealed class RfidOptions
{
    public string Host { get; set; } = "10.116.136.22";
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
    public string MethodName { get; set; } = "ReceiveHex"; // name unchanged
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
        if (_cachedDeviceId != null) return _cachedDeviceId;

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
        if (_cachedDeviceName != null) return _cachedDeviceName;

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

#region Tag Event

public sealed class TagEventArgs : EventArgs
{
    public required string Raw { get; init; }
    public required DateTime Timestamp { get; init; }
}

#endregion

#region RFID Reader

public sealed class RfidReaderService : IHostedService
{
    private readonly ILogger<RfidReaderService> _log;
    private readonly RfidOptions _opt;
    private readonly AppOptions _app;
    private CancellationTokenSource? _cts;

    public event EventHandler<TagEventArgs>? TagReceived;

    public RfidReaderService(
        ILogger<RfidReaderService> log,
        IOptions<RfidOptions> opt,
        IOptions<AppOptions> appOpt)
    {
        _log = log;
        _opt = opt.Value;
        _app = appOpt.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_app.TestMode)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = Task.Run(() => RunAsync(_cts.Token));
            Console.WriteLine("[MODE] REAL MODE → Connecting to RFID reader...");
        }
        else
        {
            Console.WriteLine("[MODE] TEST MODE → Manual RAW input only. No RFID connection.");
        }

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

                Console.WriteLine($"\n[RFID] Trying primary {_opt.Host}:{_opt.Port} ...");

                foreach (var host in hosts)
                {
                    if (string.IsNullOrWhiteSpace(host)) continue;

                    try
                    {
                        tcp = new TcpClient();
                        var connectTask = tcp.ConnectAsync(host, _opt.Port);
                        var completed = await Task.WhenAny(connectTask, Task.Delay(2000, ct));

                        if (completed == connectTask)
                        {
                            await connectTask;
                            Console.WriteLine($"[RFID] CONNECTED to {host}:{_opt.Port}");
                            break;
                        }
                        else
                        {
                            Console.WriteLine($"[RFID] TIMEOUT connecting to {host}:{_opt.Port}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RFID] ERROR connecting to {host}:{_opt.Port} → {ex.Message}");
                    }

                    if (host == _opt.Host && !string.IsNullOrWhiteSpace(_opt.FallbackHost))
                        Console.WriteLine($"[RFID] Trying fallback {_opt.FallbackHost}:{_opt.Port} ...");
                }

                if (tcp == null || !tcp.Connected)
                {
                    Console.WriteLine("[RFID] FAILED both primary and fallback → retrying...");
                    await Task.Delay(_opt.ReconnectDelayMs, ct);
                    continue;
                }

                using var stream = tcp.GetStream();
                var buffer = ArrayPool<byte>.Shared.Rent(_opt.ReadBufferBytes);
                var sb = new StringBuilder();

                Console.WriteLine("[RFID] Ready, waiting for tags...");

                while (!ct.IsCancellationRequested)
                {
                    if (!stream.DataAvailable)
                    {
                        await Task.Delay(10, ct);
                        continue;
                    }

                    int n = await stream.ReadAsync(buffer.AsMemory(0, _opt.ReadBufferBytes), ct);
                    if (n <= 0) throw new IOException("RFID disconnected");

                    var chunk = Encoding.ASCII.GetString(buffer, 0, n);
                    sb.Append(chunk);

                    foreach (var line in Split(sb, _opt.LineTerminatorRegex))
                    {
                        var txt = Strip(line);
                        if (string.IsNullOrWhiteSpace(txt)) continue;

                        TagReceived?.Invoke(this, new TagEventArgs
                        {
                            Raw = txt,
                            Timestamp = DateTime.Now
                        });

                        Console.WriteLine($"[TAG] RAW = {txt}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RFID] ERROR → {ex.Message}");
                await Task.Delay(_opt.ReconnectDelayMs, ct);
            }
        }
    }

    private static IEnumerable<string> Split(StringBuilder sb, string? regex)
    {
        if (string.IsNullOrEmpty(regex))
        {
            var txt = sb.ToString();
            var parts = txt.Split('\n');
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
        => new string(s.Where(c => !char.IsControl(c)).ToArray());
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
        _deviceName = DeviceIdentity.GetOrCreateDeviceName(_dev.Name);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _mgr = new ServiceManagerBuilder()
            .WithOptions(o => o.ConnectionString = _opt.ConnectionString)
            .BuildServiceManager();

        _hub = await _mgr.CreateHubContextAsync(_opt.HubName, cancellationToken);
        Console.WriteLine($"[SignalR] Connected to hub '{_opt.HubName}'");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task SendRawAsync(string hex)
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
        Console.WriteLine($"[SEND] RAW → {hex}");
    }
}

#endregion

#region Resolver (RFID → SignalR)

public sealed class RfidToSignalRService : IHostedService
{
    private readonly AppOptions _app;
    private readonly RfidReaderService _reader;
    private readonly HexPublisher _publisher;

    private CancellationTokenSource? _cts;

    public RfidToSignalRService(
        IOptions<AppOptions> app,
        RfidReaderService reader,
        HexPublisher publisher)
    {
        _app = app.Value;
        _reader = reader;
        _publisher = publisher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_app.TestMode)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = Task.Run(() => ManualInputLoopAsync(_cts.Token));
        }
        else
        {
            _reader.TagReceived += OnTag;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app.TestMode)
            _cts?.Cancel();
        else
            _reader.TagReceived -= OnTag;

        return Task.CompletedTask;
    }

    private async void OnTag(object? sender, TagEventArgs e)
    {
        await _publisher.SendRawAsync(e.Raw);
    }

    private async Task ManualInputLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Console.Write("\nRAW: ");
            var input = Console.ReadLine();

            if (!string.IsNullOrWhiteSpace(input))
                await _publisher.SendRawAsync(input.Trim());
        }
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

        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("appsettings.json"));

        if (name == null)
            throw new Exception("Embedded appsettings.json not found.");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new Exception("Embedded appsettings.json stream null.");

        cb.AddJsonStream(stream);
        return cb.Build();
    }
}

#endregion

#region Main

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
        builder.Logging.AddSimpleConsole(o =>
        {
            o.TimestampFormat = "HH:mm:ss ";
            o.SingleLine = true;
        });

        var app = builder.Build();

        Console.WriteLine("===============================================");
        Console.WriteLine("  RFID → RAW → SignalR");
        Console.WriteLine("  TestMode = " + (cfg.GetValue<bool>("App:TestMode") ? "YES" : "NO"));
        Console.WriteLine("  DeviceId  : " + DeviceIdentity.GetOrCreateDeviceId());
        Console.WriteLine("  DeviceName: " + DeviceIdentity.GetOrCreateDeviceName());
        Console.WriteLine("===============================================");

        await app.RunAsync();
    }
}

#endregion
