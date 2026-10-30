using System.Net;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Makaretu.Dns;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Drift.Windows;

internal interface IDriftUi
{
    Task<bool> ApproveFileAsync(string name, long bytes);
    Task SetClipboardAsync(string text);
    Task<string> GetClipboardAsync();
    bool ClipboardSharingEnabled { get; }
    bool ClipboardReceivingEnabled { get; }
    void Report(string message);
}

internal sealed class DriftService(IDriftUi ui) : IAsyncDisposable
{
    public const int Port = 45788;
    public const long MaxFileBytes = 8L * 1024 * 1024 * 1024;
    public const int MaxClipboardBytes = 64 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _offeredFiles = new();
    private readonly SemaphoreSlim _offerGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();
    private TrustData _trust = LoadOrCreateTrust();
    private ServiceDiscovery? _discovery;
    private WebApplication? _app;
    private string _code = "";
    private DateTimeOffset _expiry;
    private int _failures;
    private string? _challenge;
    private DateTimeOffset _challengeExpiry;
    private int _challengeFailures;
    private Ticket? _ticket;

    private sealed record TrustData(string Id, string Secret);

    private sealed record Ticket(string Id, string Name, long Size, DateTimeOffset Expiry);
    public bool Running => _app is not null;
    public string PairingCode => _code;
    public bool RememberPairedDevices { get; set; } = true;
    public string DeviceId => _trust.Id;

    public static IEnumerable<(IPAddress Address, string Name)> LocalInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => new { a.Address, Network = n }))
            .Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && IsPrivateIpv4(x.Address))
            .OrderByDescending(x => (x.Network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 100 :
                                      x.Network.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 70 : 0) +
                                    (x.Network.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily ==
                                        System.Net.Sockets.AddressFamily.InterNetwork) ? 40 : 0) -
                                    (new[] { "virtual", "vethernet", "hyper-v", "wsl", "vmware", "bluetooth", "loopback" }
                                        .Any(s => (x.Network.Name + " " + x.Network.Description).Contains(s, StringComparison.OrdinalIgnoreCase)) ? 200 : 0))
            .Select(x => (x.Address, x.Network.Name));

    public static IEnumerable<IPAddress> LocalAddresses()
    {
        return LocalInterfaces().Select(x => x.Address).Distinct();
    }

    public static bool IsPrivateIpv4(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                                  (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }

    public async Task StartAsync(IPAddress bindAddress)
    {
        if (_app is not null) throw new InvalidOperationException("Drift is already listening");
        if (!IsPrivateIpv4(bindAddress)) throw new InvalidOperationException("Choose a private LAN address");
        _code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        _sessions.Clear();
        _expiry = DateTimeOffset.UtcNow.AddMinutes(15);
        _failures = 0;
        lock (_gate) _ticket = null;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(bindAddress, Port);
            server.Limits.MaxRequestBodySize = null; // Exact length checked after one-use offer.
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        });
        var app = builder.Build();
        app.MapPost("/pair", PairAsync);
        app.MapGet("/health", HealthAsync);
        app.MapGet("/challenge", ChallengeAsync);
        app.MapPost("/reconnect", ReconnectAsync);
        app.MapPost("/offer", OfferAsync);
        app.MapPost("/upload", UploadAsync);
        app.MapPost("/clipboard", PushClipboardAsync);
        app.MapGet("/clipboard", PullClipboardAsync);
        app.MapGet("/inbox", ListInboxAsync);
        app.MapGet("/download", DownloadAsync);
        await app.StartAsync();
        _app = app;
        try
        {
            var discovery = new ServiceDiscovery();
            var profile = new ServiceProfile("Drift-" + Environment.MachineName.Replace(' ', '-'),
                                             "_drift._tcp", Port, [bindAddress]);
            profile.AddProperty("id", _trust.Id);
            discovery.Advertise(profile);
            _discovery = discovery;
            ui.Report("Bonjour discovery active: " + profile.FullyQualifiedName);
        }
        catch (Exception error) { ui.Report("Bonjour unavailable: " + error.Message + " (manual IP still works)"); }
        ui.Report($"Listening on {bindAddress}:{Port}, code {_code} (15 minutes)");
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        _sessions.Clear();
        _discovery?.Dispose();
        _discovery = null;
        lock (_gate) _ticket = null;
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
            ui.Report("Receiving stopped");
        }
    }

    public string QueueForIPhone(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Selected file not found", file);
        var info = new FileInfo(file);
        if (info.Length > MaxFileBytes) throw new InvalidOperationException("8 GiB limit");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        lock (_gate)
        {
            if (_offeredFiles.Count >= 50) _offeredFiles.Remove(_offeredFiles.Keys.First());
            _offeredFiles[id] = file;
        }
        ui.Report($"Available for iPhone download: {Path.GetFileName(file)}");
        return id;
    }

    private bool Authorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (_app is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var bearer = header[7..];
        if (bearer.Length != 48 || !_sessions.TryGetValue(bearer, out var expiry) || DateTimeOffset.UtcNow > expiry) return false;
        _sessions[bearer] = DateTimeOffset.UtcNow.AddMinutes(30); // Sliding short-lived session.
        return true;
    }

    private string NewSession()
    {
        var bearer = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _sessions[bearer] = DateTimeOffset.UtcNow.AddMinutes(30);
        return bearer;
    }

    private Task HealthAsync(HttpContext context)
    {
        if (!Authorized(context)) context.Response.StatusCode = 403;
        else context.Response.Headers["X-Drift-Device-ID"] = _trust.Id;
        return Task.CompletedTask;
    }

    private async Task ChallengeAsync(HttpContext context)
    {
        if (!RememberPairedDevices) { context.Response.StatusCode = 404; return; }
        string challenge;
        lock (_gate)
        {
            challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _challenge = challenge;
            _challengeExpiry = DateTimeOffset.UtcNow.AddMinutes(1);
            _challengeFailures = 0;
        }
        context.Response.Headers["X-Drift-Device-ID"] = _trust.Id;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(challenge);
    }

    private async Task ReconnectAsync(HttpContext context)
    {
        if (!RememberPairedDevices) { context.Response.StatusCode = 403; return; }
        var proof = context.Request.Headers["X-Drift-Proof"].ToString();
        bool valid;
        lock (_gate)
        {
            var expected = _challenge is null ? "" : Convert.ToHexString(HMACSHA256.HashData(
                Convert.FromHexString(_trust.Secret), Encoding.UTF8.GetBytes(_challenge)));
            valid = _challenge is not null && DateTimeOffset.UtcNow <= _challengeExpiry &&
                proof.Length == expected.Length && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(proof), Encoding.ASCII.GetBytes(expected));
            if (valid || ++_challengeFailures > 10) _challenge = null;
        }
        if (!valid) { context.Response.StatusCode = 403; return; }
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(NewSession());
        ui.Report("Trusted iPhone automatically reconnected");
    }

    private async Task PairAsync(HttpContext context)
    {
        if (DateTimeOffset.UtcNow > _expiry || _failures >= 10)
        {
            context.Response.StatusCode = 403;
            return;
        }
        var supplied = Encoding.ASCII.GetBytes(context.Request.Headers["X-Drift-Code"].ToString());
        var expected = Encoding.ASCII.GetBytes(_code);
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            Interlocked.Increment(ref _failures);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Invalid code or expired session");
            return;
        }
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        if (RememberPairedDevices)
        {
            context.Response.Headers["X-Drift-Trust-Key"] = _trust.Secret;
            context.Response.Headers["X-Drift-Device-ID"] = _trust.Id;
        }
        await context.Response.WriteAsync(NewSession());
        ui.Report("iPhone or another Drift companion paired");
    }

    public void RevokeTrustedDevices()
    {
        _trust = SaveNewTrust();
        _sessions.Clear();
        lock (_gate) _challenge = null;
        ui.Report("Previously trusted devices revoked. Restart receiving and pair again.");
    }

    private static string TrustPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "Drift", "trusted-device.dat");

    private static TrustData SaveNewTrust()
    {
        var data = new TrustData(Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Directory.CreateDirectory(Path.GetDirectoryName(TrustPath)!);
        File.WriteAllBytes(TrustPath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(data),
                                                        null, DataProtectionScope.CurrentUser));
        return data;
    }

    private static TrustData LoadOrCreateTrust()
    {
        try
        {
            var decoded = ProtectedData.Unprotect(File.ReadAllBytes(TrustPath), null, DataProtectionScope.CurrentUser);
            var data = JsonSerializer.Deserialize<TrustData>(decoded);
            if (data is not null && data.Id.Length == 32 && data.Secret.Length == 64) return data;
        }
        catch (Exception) { /* First launch, or the current Windows account changed. */ }
        return SaveNewTrust();
    }

    private async Task OfferAsync(HttpContext context)
    {
        if (!Authorized(context)) { context.Response.StatusCode = 403; return; }
        var name = context.Request.Query["name"].ToString();
        if (!SafeName(name) || !long.TryParse(context.Request.Query["size"], out var size) ||
            size < 0 || size > MaxFileBytes)
        {
            context.Response.StatusCode = 400; return;
        }
        await _offerGate.WaitAsync(context.RequestAborted);
        try
        {
            lock (_gate)
            {
                if (_ticket is not null && _ticket.Expiry > DateTimeOffset.UtcNow)
                {
                    context.Response.StatusCode = 409;
                    return;
                }
            }
            var approved = await ui.ApproveFileAsync(name, size)
                .WaitAsync(TimeSpan.FromSeconds(90), context.RequestAborted);
            if (!approved) { context.Response.StatusCode = 403; return; }
            var ticket = new Ticket(Convert.ToHexString(RandomNumberGenerator.GetBytes(18)),
                                    name, size, DateTimeOffset.UtcNow.AddMinutes(2));
            lock (_gate) _ticket = ticket;
            await context.Response.WriteAsync(ticket.Id);
        }
        catch (TimeoutException) { context.Response.StatusCode = 408; }
        finally { _offerGate.Release(); }
    }

    private async Task UploadAsync(HttpContext context)
    {
        if (!Authorized(context)) { context.Response.StatusCode = 403; return; }
        var id = context.Request.Query["ticket"].ToString();
        Ticket? ticket;
        lock (_gate)
        {
            ticket = _ticket;
            _ticket = null; // One use, even if upload later fails.
        }
        if (ticket is null || ticket.Id != id || ticket.Expiry < DateTimeOffset.UtcNow ||
            context.Request.ContentLength != ticket.Size)
        {
            context.Response.StatusCode = 403;
            return;
        }

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                  "Downloads", "Drift");
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, ".drift-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var checksum = SHA256.Create();
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                                    FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[128 * 1024];
                long remaining = ticket.Size;
                while (remaining > 0)
                {
                    int read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), context.RequestAborted);
                    if (read == 0) throw new EndOfStreamException("Upload was truncated");
                    await target.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                    checksum.TransformBlock(buffer, 0, read, null, 0);
                    remaining -= read;
                }
                checksum.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                await target.FlushAsync(context.RequestAborted);
                target.Flush(flushToDisk: true);
            }
            var dest = UniqueFile(folder, ticket.Name);
            File.Move(temp, dest);
            ui.Report($"Received {ticket.Name} (SHA-256 {Convert.ToHexString(checksum.Hash!)})");
            await context.Response.WriteAsync($"Saved: {Path.GetFileName(dest)}\nSHA-256: {Convert.ToHexString(checksum.Hash!)}");
        }
        catch (IOException ex)
        {
            ui.Report("Upload failed: " + ex.Message);
            context.Response.StatusCode = 400;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task PushClipboardAsync(HttpContext context)
    {
        if (!Authorized(context) || !ui.ClipboardReceivingEnabled)
        {
            context.Response.StatusCode = 403; return;
        }
        var data = await ReadBoundedAsync(context.Request.Body, MaxClipboardBytes, context.RequestAborted);
        if (data is null) { context.Response.StatusCode = 413; return; }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { context.Response.StatusCode = 400; return; }
        await ui.SetClipboardAsync(text);
        ui.Report("iPhone clipboard received on Windows");
        await context.Response.WriteAsync("OK");
    }

    private async Task PullClipboardAsync(HttpContext context)
    {
        if (!Authorized(context) || !ui.ClipboardSharingEnabled)
        {
            context.Response.StatusCode = 403; return;
        }
        var text = await ui.GetClipboardAsync();
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxClipboardBytes) { context.Response.StatusCode = 413; return; }
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private async Task ListInboxAsync(HttpContext context)
    {
        if (!Authorized(context)) { context.Response.StatusCode = 403; return; }
        object[] entries;
        lock (_gate)
        {
            entries = _offeredFiles.Select(p => new { id = p.Key,
                name = Path.GetFileName(p.Value),
                size = File.Exists(p.Value) ? new FileInfo(p.Value).Length : 0L }).ToArray();
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(entries));
    }

    private async Task DownloadAsync(HttpContext context)
    {
        if (!Authorized(context)) { context.Response.StatusCode = 403; return; }
        string? path;
        lock (_gate) _offeredFiles.TryGetValue(context.Request.Query["id"].ToString(), out path);
        if (path is null || !File.Exists(path)) { context.Response.StatusCode = 404; return; }
        var name = Path.GetFileName(path);
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(name);
        await context.Response.SendFileAsync(path, 0, null, context.RequestAborted);
        ui.Report("iPhone downloaded " + name);
    }

    private static bool SafeName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 180 && value != "." && value != ".." &&
        !value.StartsWith('.') && value.IndexOfAny(['/', '\\', '\0', '\r', '\n']) == -1 &&
        !value.Any(char.IsControl);

    private static string UniqueFile(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var index = 1; File.Exists(path) && index <= 9999; index++)
            path = Path.Combine(folder, $"{stem} ({index}){ext}");
        if (File.Exists(path)) throw new IOException("Too many copies of this file");
        return path;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream input, int max, CancellationToken cancel)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancel);
            if (read == 0) return stream.ToArray();
            if (stream.Length + read > max) return null;
            stream.Write(buffer, 0, read);
        }
    }

    public async ValueTask DisposeAsync() { await StopAsync(); _offerGate.Dispose(); }
}

/// <summary>
/// Native Windows BLE peripheral receiver. Uses the same encrypted GATT protocol and
/// UUIDs as the Android TV Drift receiver; requires Windows Bluetooth radio support
/// for peripheral advertising. Incoming files still require local user approval.
/// </summary>
internal sealed class DriftBluetoothReceiver(IDriftUi ui)
{
    private static readonly Guid ServiceUuid = Guid.Parse("d25e69c1-7c6d-46e3-a7c9-a7cfe89a0020");
    private static readonly Guid ControlUuid = Guid.Parse("d25e69c1-7c6d-46e3-a7c9-a7cfe89a0021");
    private static readonly Guid DataUuid = Guid.Parse("d25e69c1-7c6d-46e3-a7c9-a7cfe89a0022");
    private static readonly Guid StatusUuid = Guid.Parse("d25e69c1-7c6d-46e3-a7c9-a7cfe89a0023");
    private const long MaxBleBytes = 8L * 1024 * 1024;
    private readonly object _lock = new();
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _control;
    private GattLocalCharacteristic? _data;
    private GattLocalCharacteristic? _status;
    private FileStream? _stream;
    private IncrementalHash? _hash;
    private string? _tempPath;
    private string? _name;
    private long _expected;
    private long _received;
    private bool _approving;
    private bool _active;
    private string _lastStatus = "WAITING";
    public bool Running => _active;

    public async Task StartAsync()
    {
        if (_active) return;
        var result = await GattServiceProvider.CreateAsync(ServiceUuid);
        if (result.Error != BluetoothError.Success || result.ServiceProvider is null)
            throw new InvalidOperationException($"Bluetooth LE peripheral unavailable: {result.Error}. Check Windows Bluetooth adapter and drivers.");
        var provider = result.ServiceProvider;
        try
        {
            var writeParameters = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write,
                WriteProtectionLevel = GattProtectionLevel.EncryptionRequired
            };
            var control = await provider.Service.CreateCharacteristicAsync(ControlUuid, writeParameters);
            var data = await provider.Service.CreateCharacteristicAsync(DataUuid, writeParameters);
            var status = await provider.Service.CreateCharacteristicAsync(StatusUuid, new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired
            });
            if (control.Error != BluetoothError.Success || data.Error != BluetoothError.Success ||
                status.Error != BluetoothError.Success || control.Characteristic is null ||
                data.Characteristic is null || status.Characteristic is null)
                throw new InvalidOperationException("Bluetooth radio rejected Drift encrypted GATT characteristics.");
            _control = control.Characteristic;
            _data = data.Characteristic;
            _status = status.Characteristic;
            _control.WriteRequested += ControlWriteRequested;
            _data.WriteRequested += DataWriteRequested;
            _status.ReadRequested += StatusReadRequested;
            _provider = provider;
            _active = true;
            provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true
            });
            ui.Report("Bluetooth LE: Drift receiver advertising. Scan from the iPhone Bluetooth tab.");
        }
        catch
        {
            _active = false;
            provider.StopAdvertising();
            _provider = null;
            throw;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _active = false;
            _approving = false;
            Discard();
            _provider?.StopAdvertising();
            if (_control is not null) _control.WriteRequested -= ControlWriteRequested;
            if (_data is not null) _data.WriteRequested -= DataWriteRequested;
            if (_status is not null) _status.ReadRequested -= StatusReadRequested;
            _provider = null;
            _control = null;
            _data = null;
            _status = null;
        }
        ui.Report("Bluetooth LE receiver stopped");
    }

    private async void StatusReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;
            string value;
            lock (_lock) value = _lastStatus;
            var bytes = Encoding.UTF8.GetBytes(value);
            if (request.Offset > (uint)bytes.Length) request.RespondWithProtocolError(0x07);
            else
            {
                using var writer = new DataWriter();
                writer.WriteBytes(bytes.AsSpan((int)request.Offset).ToArray());
                request.RespondWithValue(writer.DetachBuffer());
            }
        }
        catch (Exception error) { ui.Report("BLE read error: " + error.Message); }
        finally { deferral.Complete(); }
    }

    private async void ControlWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;
            var reader = DataReader.FromBuffer(request.Value);
            var bytes = new byte[(int)reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            bool accepted = request.Offset == 0 && HandleControl(Encoding.UTF8.GetString(bytes));
            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                if (accepted) request.Respond();
                else request.RespondWithProtocolError(0x0E);
            }
        }
        catch (Exception error) { ui.Report("BLE command failed: " + error.Message); }
        finally { deferral.Complete(); }
    }

    private bool HandleControl(string command)
    {
        if (command == "CANCEL")
        {
            lock (_lock) { Discard(); _approving = false; }
            Publish("CANCELLED");
            return true;
        }
        if (command == "FINISH")
        {
            string? saved = null;
            string? checksum = null;
            lock (_lock)
            {
                if (_stream is null || _name is null || _tempPath is null || _hash is null || _received != _expected)
                {
                    Discard();
                }
                else
                {
                    try
                    {
                        _stream.Flush(flushToDisk: true);
                        _stream.Dispose(); _stream = null;
                        checksum = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
                        var directory = Path.GetDirectoryName(_tempPath)!;
                        var destination = UniqueDestination(directory, _name);
                        File.Move(_tempPath, destination);
                        saved = destination;
                        _tempPath = null;
                    }
                    catch (IOException error) { ui.Report("Bluetooth save failed: " + error.Message); }
                    finally { Discard(); }
                }
            }
            if (saved is null || checksum is null) { Publish("ERROR:Incomplete file or save failed"); return false; }
            ui.Report($"Bluetooth file received: {Path.GetFileName(saved)} (SHA-256 {checksum})");
            Publish("DONE:" + checksum);
            return true;
        }
        if (!command.StartsWith("START:", StringComparison.Ordinal)) return false;
        var separator = command.LastIndexOf(':');
        if (separator <= 6 || !long.TryParse(command[(separator + 1)..], out var size)) return false;
        var requested = command[6..separator];
        if (string.IsNullOrWhiteSpace(requested) || requested != Path.GetFileName(requested) ||
            requested.Length > 180 || requested.StartsWith('.') || requested.Any(char.IsControl) ||
            size < 0 || size > MaxBleBytes) return false;
        lock (_lock)
        {
            if (!_active || _approving || _stream is not null) return false;
            _approving = true;
        }
        Publish("APPROVAL");
        _ = ApproveBluetoothFileAsync(requested, size);
        return true;
    }

    private async Task ApproveBluetoothFileAsync(string requested, long size)
    {
        bool allowed;
        try { allowed = await ui.ApproveFileAsync(requested, size).WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (Exception error) { ui.Report("BLE approval failed: " + error.Message); allowed = false; }
        bool ready = false;
        lock (_lock)
        {
            if (!_active || !_approving) return;
            _approving = false;
            if (allowed)
            {
                try
                {
                    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Drift");
                    Directory.CreateDirectory(folder);
                    _tempPath = Path.Combine(folder, ".drift-" + Guid.NewGuid().ToString("N") + ".part");
                    _stream = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    _name = requested;
                    _expected = size;
                    _received = 0;
                    ready = true;
                }
                catch (IOException error) { ui.Report("BLE could not open file: " + error.Message); Discard(); }
            }
        }
        Publish(ready ? "READY" : "DECLINED");
    }

    private async void DataWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;
            var reader = DataReader.FromBuffer(request.Value);
            var bytes = new byte[(int)reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            bool accepted;
            lock (_lock)
            {
                accepted = request.Offset == 0 && _active && _stream is not null &&
                           bytes.Length <= _expected - _received;
                if (accepted)
                {
                    try
                    {
                        _stream!.Write(bytes);
                        _hash!.AppendData(bytes);
                        _received += bytes.Length;
                    }
                    catch (IOException error) { ui.Report("BLE file write failed: " + error.Message); Discard(); accepted = false; }
                }
            }
            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                if (accepted) request.Respond();
                else request.RespondWithProtocolError(0x0E);
            }
        }
        catch (Exception error) { ui.Report("BLE data error: " + error.Message); }
        finally { deferral.Complete(); }
    }

    private async void Publish(string value)
    {
        GattLocalCharacteristic? status;
        lock (_lock) { _lastStatus = value; status = _status; }
        if (status is null) return;
        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(Encoding.UTF8.GetBytes(value));
            await status.NotifyValueAsync(writer.DetachBuffer());
        }
        catch (Exception error) { ui.Report("BLE notification failed: " + error.Message); }
    }

    private static string UniqueDestination(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (int n = 1; File.Exists(path) && n <= 9999; n++)
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        if (File.Exists(path)) throw new IOException("Too many duplicate files");
        return path;
    }

    private void Discard()
    {
        _stream?.Dispose(); _stream = null;
        _hash?.Dispose(); _hash = null;
        if (_tempPath is not null && File.Exists(_tempPath)) File.Delete(_tempPath);
        _tempPath = null;
        _name = null;
        _expected = _received = 0;
    }
}
