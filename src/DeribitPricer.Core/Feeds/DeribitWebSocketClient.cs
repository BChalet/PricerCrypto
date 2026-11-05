// La connexion WebSocket à Deribit : abonnements aux prix, heartbeat et reconnexion automatique.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Feeds;

// Connexion WebSocket JSON-RPC à Deribit : abonnements aux canaux, heartbeat,
// reconnexion automatique. Chaque notification reçue est publiée comme MarketEvent.
public sealed class DeribitWebSocketClient
{
    private readonly Uri _uri;
    private readonly TokenBucketRateLimiter _limiter;
    private readonly FeedStatus _status;
    private readonly Action<MarketEvent> _publish;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<string> _subscribed = new();
    private ClientWebSocket? _socket;
    private Func<IReadOnlyCollection<string>> _desiredChannels = () => Array.Empty<string>();
    private int _requestId;

    public DeribitWebSocketClient(Uri uri, TokenBucketRateLimiter limiter, FeedStatus status, Action<MarketEvent> publish)
    {
        _uri = uri;
        _limiter = limiter;
        _status = status;
        _publish = publish;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    // Boucle principale : connexion, abonnements, lecture ; reconnexion en cas d'erreur.
    public async Task RunAsync(Func<IReadOnlyCollection<string>> desiredChannels, CancellationToken ct)
    {
        _desiredChannels = desiredChannels;
        int retry = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(_uri, ct);
                _socket = socket;
                lock (_subscribed) _subscribed.Clear();
                _status.Connected = true;
                _status.State = "Connecté";
                _status.Log("WebSocket connecté");
                retry = 0;

                await SendAsync("public/set_heartbeat", new { interval = 30 }, ct);
                await SyncSubscriptionsAsync(ct);
                await ReceiveLoopAsync(socket, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _status.Log($"WebSocket : {ex.Message}");
            }

            _status.Connected = false;
            _status.State = "Déconnecté, reconnexion...";
            retry++;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, retry)));
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // Aligne les abonnements sur la liste de canaux souhaitée (ajouts et retraits).
    public async Task SyncSubscriptionsAsync(CancellationToken ct)
    {
        if (!IsConnected) return;
        var desired = _desiredChannels().ToHashSet();
        List<string> toAdd, toRemove;
        lock (_subscribed)
        {
            toAdd = desired.Except(_subscribed).ToList();
            toRemove = _subscribed.Except(desired).ToList();
        }

        foreach (var chunk in toAdd.Chunk(100))
        {
            await SendAsync("public/subscribe", new { channels = chunk }, ct);
            lock (_subscribed) _subscribed.UnionWith(chunk);
        }
        foreach (var chunk in toRemove.Chunk(100))
        {
            await SendAsync("public/unsubscribe", new { channels = chunk }, ct);
            lock (_subscribed) _subscribed.ExceptWith(chunk);
        }
    }

    private async Task SendAsync(string method, object parameters, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return;
        await _limiter.WaitAsync(ct);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _requestId),
            method,
            @params = parameters
        });
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("connexion fermée par le serveur");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            await HandleMessageAsync(message.ToArray(), ct);
        }
    }

    private async Task HandleMessageAsync(byte[] bytes, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        if (root.TryGetProperty("method", out var method))
        {
            var name = method.GetString();
            if (name == "subscription" && root.TryGetProperty("params", out var p))
            {
                var channel = p.GetProperty("channel").GetString() ?? "";
                var data = p.GetProperty("data").Clone();
                _publish(new MarketEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "ws", channel, null, data));
            }
            else if (name == "heartbeat" && root.TryGetProperty("params", out var hb) &&
                     hb.TryGetProperty("type", out var type) && type.GetString() == "test_request")
            {
                await SendAsync("public/test", new { }, ct);
            }
        }
        else if (root.TryGetProperty("error", out var error))
        {
            _status.Log($"Erreur Deribit : {Encoding.UTF8.GetString(bytes)[..Math.Min(200, bytes.Length)]} ({error})");
        }
    }
}
