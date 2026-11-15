using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;

internal sealed class LocalRangeServer : IAsyncDisposable
{
    private readonly string _filePath;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly Task _serverTask;
    private int _nextConnectionId;

    private LocalRangeServer(string filePath)
    {
        _filePath = filePath;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        SourceUri = new Uri($"http://127.0.0.1:{endpoint.Port}/huitian-test-model.bin");
        _serverTask = ServeAsync(_lifetime.Token);
    }

    public Uri SourceUri { get; }

    public static LocalRangeServer Start(string filePath) => new(filePath);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener.Stop();
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
        }

        var connections = _connections.Values.ToArray();
        if (connections.Length > 0)
        {
            await Task.WhenAll(connections).ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var connectionId = Interlocked.Increment(ref _nextConnectionId);
            var connection = HandleClientAsync(client, cancellationToken);
            _connections[connectionId] = connection;
            _ = connection.ContinueWith(
                completed => _connections.TryRemove(connectionId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await RespondAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Cancellation tests intentionally close in-flight range requests.
            }
            catch (SocketException)
            {
                // A reset test connection is equivalent to the client abandoning it.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RespondAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        string? rangeHeader = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                rangeHeader = line[(line.IndexOf(':') + 1)..].Trim();
            }
        }

        var fileLength = new FileInfo(_filePath).Length;
        var (start, end) = ParseRange(rangeHeader, fileLength);
        var contentLength = end - start + 1;
        var responseHeaders =
            "HTTP/1.1 206 Partial Content\r\n" +
            $"Content-Length: {contentLength}\r\n" +
            $"Content-Range: bytes {start}-{end}/{fileLength}\r\n" +
            "Accept-Ranges: bytes\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHeaders), cancellationToken)
            .ConfigureAwait(false);

        await using var input = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        input.Position = start;
        var buffer = new byte[128 * 1024];
        var remaining = contentLength;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static (long Start, long End) ParseRange(string? value, long fileLength)
    {
        if (value is null || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return (0, fileLength - 1);
        }

        var parts = value[6..].Split('-', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var start))
        {
            throw new InvalidDataException("Invalid test range request.");
        }

        var end = long.TryParse(parts[1], out var parsedEnd)
            ? parsedEnd
            : fileLength - 1;
        if (start < 0 || end < start || end >= fileLength)
        {
            throw new InvalidDataException("Test range is outside the fixture.");
        }

        return (start, end);
    }
}
