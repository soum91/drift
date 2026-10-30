using System.Net;

namespace Drift.Windows;

/// <summary>Windows sender using the same /pair, /offer, /upload wire protocol as Drift TV.</summary>
internal sealed class DriftSender : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private Uri? _server;
    private string _token = "";

    public async Task PairAsync(string ipv4, string pairingCode, CancellationToken cancellation = default)
    {
        if (!IPAddress.TryParse(ipv4.Trim(), out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Enter the receiver's IPv4 address");
        if (pairingCode.Length != 6 || !pairingCode.All(char.IsAsciiDigit))
            throw new ArgumentException("Enter the six-digit code shown on the receiver");

        var server = new Uri($"http://{address}:{DriftService.Port}/");
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "pair"));
        req.Headers.Add("X-Drift-Code", pairingCode);
        req.Content = new ByteArrayContent([]);
        using var response = await _http.SendAsync(req, cancellation);
        var body = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode) throw new IOException($"Pairing failed: {(int)response.StatusCode} {body}");
        var token = body.Trim();
        if (token.Length < 24 || token.Length > 200) throw new IOException("Invalid Drift pairing token");
        _server = server;
        _token = token;
    }

    public async Task SendFileAsync(string filePath, IProgress<double>? progress = null,
                                    CancellationToken cancellation = default)
    {
        if (_server is null) throw new InvalidOperationException("Pair with a Drift receiver first");
        var file = new FileInfo(filePath);
        if (!file.Exists || file.Length > DriftService.MaxFileBytes)
            throw new IOException("File is missing or exceeds the 8 GiB limit");

        string offer = "offer?name=" + Uri.EscapeDataString(file.Name) + "&size=" + file.Length;
        using var offerRequest = Request(HttpMethod.Post, offer);
        offerRequest.Content = new ByteArrayContent([]);
        using var offerResponse = await _http.SendAsync(offerRequest, cancellation);
        var ticket = (await offerResponse.Content.ReadAsStringAsync(cancellation)).Trim();
        if (!offerResponse.IsSuccessStatusCode)
            throw new IOException($"Receiver rejected file: {(int)offerResponse.StatusCode} {ticket}");
        if (ticket.Length < 16 || ticket.Length > 200) throw new IOException("Invalid approval ticket");

        using var stream = new ProgressFileStream(file.FullName, progress);
        using var uploadRequest = Request(HttpMethod.Post, "upload?ticket=" + Uri.EscapeDataString(ticket));
        uploadRequest.Content = new StreamContent(stream, 128 * 1024);
        uploadRequest.Content.Headers.ContentLength = file.Length;
        using var uploadResponse = await _http.SendAsync(uploadRequest, cancellation);
        var result = await uploadResponse.Content.ReadAsStringAsync(cancellation);
        if (!uploadResponse.IsSuccessStatusCode)
            throw new IOException($"Upload failed: {(int)uploadResponse.StatusCode} {result}");
        progress?.Report(1.0);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_server!, path));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    public void Dispose() => _http.Dispose();

    private sealed class ProgressFileStream : Stream
    {
        private readonly FileStream _file;
        private readonly IProgress<double>? _progress;
        public ProgressFileStream(string path, IProgress<double>? progress)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                   128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            _progress = progress;
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _file.Length;
        public override long Position { get => _file.Position; set => _file.Position = value; }
        private void Progress() => _progress?.Report(Length == 0 ? 1 : (double)Position / Length);
        public override int Read(byte[] buffer, int offset, int count)
        { var read = _file.Read(buffer, offset, count); Progress(); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var read = await _file.ReadAsync(buffer, cancellationToken); Progress(); return read; }
        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        { if (disposing) _file.Dispose(); base.Dispose(disposing); }
    }
}
