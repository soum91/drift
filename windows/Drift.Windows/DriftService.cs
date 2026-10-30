using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

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
    private WebApplication? _app;
    private string _token = "";
    private string _code = "";
    private DateTimeOffset _expiry;
    private int _failures;
    private Ticket? _ticket;

    private sealed record Ticket(string Id, string Name, long Size, DateTimeOffset Expiry);
    public bool Running => _app is not null;
    public string PairingCode => _code;

    public static IEnumerable<IPAddress> LocalAddresses()
    {
        return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(a) && IsPrivateIpv4(a))
            .Distinct();
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
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
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
        app.MapPost("/offer", OfferAsync);
        app.MapPost("/upload", UploadAsync);
        app.MapPost("/clipboard", PushClipboardAsync);
        app.MapGet("/clipboard", PullClipboardAsync);
        app.MapGet("/inbox", ListInboxAsync);
        app.MapGet("/download", DownloadAsync);
        await app.StartAsync();
        _app = app;
        ui.Report($"Listening on {bindAddress}:{Port}, code {_code} (15 minutes)");
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        _token = "";
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
        byte[] incoming = Encoding.ASCII.GetBytes(header);
        byte[] expected = Encoding.ASCII.GetBytes("Bearer " + _token);
        return _app is not null && DateTimeOffset.UtcNow <= _expiry &&
               incoming.Length == expected.Length && CryptographicOperations.FixedTimeEquals(incoming, expected);
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
        await context.Response.WriteAsync(_token);
        ui.Report("iPhone or another Drift companion paired");
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
