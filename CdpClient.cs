using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ApiForge;

/// <summary>
/// Lightweight Chrome DevTools Protocol client over WebSocket.
/// Connects to a single CDP target and supports sending commands + receiving events.
/// </summary>
internal class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private int _nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private CancellationTokenSource _cts = new();
    private Task? _receiveTask;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsConnected => _ws.State == WebSocketState.Open;

    /// <summary>
    /// Fired when a CDP event is received. Parameters: method name, event params.
    /// </summary>
    public event Action<string, JsonElement>? EventReceived;

    public async Task ConnectAsync(string wsUrl)
    {
        await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public async Task<JsonElement?> SendAsync(string method, object? args = null)
    {
        if (!IsConnected) return null;

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>();
        _pending[id] = tcs;

        var message = args != null
            ? JsonSerializer.Serialize(new { id, method, @params = args })
            : JsonSerializer.Serialize(new { id, method });

        var bytes = Encoding.UTF8.GetBytes(message);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        timeout.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var t))
                t.TrySetResult(null);
        });

        return await tcs.Task;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1024 * 256];
        var messageBuffer = new MemoryStream();

        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                messageBuffer.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    messageBuffer.SetLength(0);
                    ProcessMessage(json);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetInt32();
                if (_pending.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("result", out var result))
                        tcs.TrySetResult(result.Clone());
                    else
                        tcs.TrySetResult(null);
                }
            }
            else if (root.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString()!;
                var paramsElement = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                EventReceived?.Invoke(method, paramsElement);
            }
        }
        catch (JsonException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { }
        }
        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
        }
        _ws.Dispose();
        _cts.Dispose();
    }
}
