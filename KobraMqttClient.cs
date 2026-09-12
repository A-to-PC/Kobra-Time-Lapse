using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;

namespace KobraTimeLapse;

public enum PrintState
{
    Unknown,
    Standby,
    Printing,
    Paused,
    Complete,
    Cancelled,
    Error,
}

// Replaces MoonrakerClient for stock (non-Rinkhals) Kobra firmware, which has no Moonraker to
// poll -- this talks the printer's own reverse-engineered LAN MQTT protocol instead, using the
// exact same handshake and report parsing already proven working in Kobra LAN Monitor.
//
// Kept poll-shaped (GetStatusAsync, called on the same interval loop CaptureService already
// uses) rather than switching CaptureService to a push/event model: the MQTT connection itself
// is opened once and kept alive in the background, with each report just updating cached
// fields -- GetStatusAsync only ever reads those cached values, no network round-trip per call.
//
// NOTE: the mapping from the printer's raw "state" string to PrintState below is a best-effort
// guess based on the state strings actually observed live during testing ("Auto_leveling",
// "Printing") -- the exact strings for a finished/cancelled/errored print were never directly
// confirmed against a real completed print. Verify against a real print that's allowed to
// finish before trusting the Complete/Cancelled/Error transitions specifically.
public sealed class KobraMqttClient(string printerHost) : IAsyncDisposable
{
    private IMqttClient? _client;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private volatile string _lastState = "unknown";
    private int? _currLayer;
    private int? _totalLayers;
    private volatile string? _printCommandTopic;

    public async Task<(PrintState State, int? CurrLayer, int? TotalLayers)> GetStatusAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        return (MapState(_lastState), _currLayer, _totalLayers);
    }

    // Same topic/payload shape as Kobra LAN Monitor's already live-verified pause command
    // (confirmed against a real print, not just documentation) -- ported directly rather than
    // reinvented, since it's already proven correct against this exact printer.
    public async Task<bool> SendPauseCommandAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var client = _client;
        var topic = _printCommandTopic;
        if (client is not { IsConnected: true } || topic == null) return false;

        var payload = new
        {
            type = "print",
            action = "pause",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgid = Guid.NewGuid().ToString(),
            data = new { taskid = "-1" },
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(payload))
            .Build();

        await client.PublishAsync(message, ct);
        return true;
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true }) return;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_client is { IsConnected: true }) return;

            var creds = await LanCredentialDiscovery.DiscoverAsync(printerHost, ct);
            var brokerUri = new Uri(creds.Broker.Replace("mqtts://", "https://"));
            var clientCert = X509Certificate2.CreateFromPem(creds.DeviceCrt, creds.DevicePk);

            var factory = new MqttClientFactory();
            _client?.Dispose();
            _client = factory.CreateMqttClient();

            var reportSubscription = $"anycubic/anycubicCloud/v1/printer/+/{creds.ModelId}/{creds.DeviceId}/#";
            var queryTopicBase = $"anycubic/anycubicCloud/v1/web/printer/{creds.ModelId}/{creds.DeviceId}";
            _printCommandTopic = $"{queryTopicBase}/print";

            _client.ApplicationMessageReceivedAsync += e =>
            {
                var payloadText = e.ApplicationMessage.ConvertPayloadToString();
                if (string.IsNullOrEmpty(payloadText)) return Task.CompletedTask;

                try
                {
                    using var doc = JsonDocument.Parse(payloadText);
                    var reportType = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;

                    if (reportType != "info") return Task.CompletedTask;
                    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                        return Task.CompletedTask;
                    if (!data.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object)
                        return Task.CompletedTask;

                    if (project.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String)
                        _lastState = s.GetString() ?? "unknown";
                    if (project.TryGetProperty("curr_layer", out var cl) && cl.TryGetInt32(out var currLayer))
                        _currLayer = currLayer;
                    if (project.TryGetProperty("total_layers", out var tl) && tl.TryGetInt32(out var totalLayers))
                        _totalLayers = totalLayers;
                }
                catch (JsonException)
                {
                    // Ignore malformed/partial payloads -- next report will correct state.
                }

                return Task.CompletedTask;
            };

            var options = new MqttClientOptionsBuilder()
                .WithClientId($"KobraTimeLapse-{Guid.NewGuid():N}")
                .WithTcpServer(brokerUri.Host, brokerUri.Port)
                .WithCredentials(creds.Username, creds.Password)
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
                .WithTlsOptions(o =>
                {
                    o.UseTls();
                    o.WithClientCertificates(new List<X509Certificate2> { clientCert });
                    o.WithCertificateValidationHandler(_ => true);
                })
                .WithCleanSession()
                .Build();

            await _client.ConnectAsync(options, ct);
            await _client.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder().WithTopicFilter(reportSubscription).Build(), ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private static PrintState MapState(string raw)
    {
        var s = raw.ToLowerInvariant();
        if (s.Contains("pause")) return PrintState.Paused;
        if (s.Contains("print") || s.Contains("leveling") || s.Contains("heating")) return PrintState.Printing;
        if (s.Contains("cancel") || s.Contains("stop")) return PrintState.Cancelled;
        if (s.Contains("error") || s.Contains("fail")) return PrintState.Error;
        if (s.Contains("complet") || s.Contains("finish") || s.Contains("idle") || s.Contains("standby")) return PrintState.Complete;
        return PrintState.Unknown;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            try { await _client.DisconnectAsync(); } catch { /* best effort */ }
            _client.Dispose();
        }
        _connectLock.Dispose();
    }
}
