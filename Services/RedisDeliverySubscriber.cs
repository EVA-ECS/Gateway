using System.Text;
using System.Text.Json;
using Chat.Contracts.Events;
using Gateway.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gateway.Services;

public sealed class RedisDeliverySubscriber : BackgroundService
{
    private const string PlaintextMvpMarker = "plaintext-mvp-not-encrypted";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly IWebSocketConnectionRegistry _connections;
    private readonly RedisRoutingOptions _options;
    private readonly ILogger<RedisDeliverySubscriber> _logger;

    public RedisDeliverySubscriber(
        IConnectionMultiplexer redis,
        IWebSocketConnectionRegistry connections,
        IOptions<RedisRoutingOptions> options,
        ILogger<RedisDeliverySubscriber> logger)
    {
        _redis = redis;
        _connections = connections;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = RedisChannel.Literal(
            _options.SingleGatewayDeliveryChannel);
        var queue = await _redis.GetSubscriber().SubscribeAsync(channel);

        _logger.LogInformation(
            "Gateway subscribed to Redis delivery channel {Channel}.",
            _options.SingleGatewayDeliveryChannel);

        try
        {
            await foreach (var delivery in queue.WithCancellation(stoppingToken))
            {
                try
                {
                    await ForwardAsync(delivery.Message, stoppingToken);
                }
                catch (OperationCanceledException) when (
                    stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not forward a Redis delivery to its WebSocket.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            await queue.UnsubscribeAsync();
        }
    }

    private async Task ForwardAsync(
        RedisValue value,
        CancellationToken cancellationToken)
    {
        ChatMessageEvent? message;

        try
        {
            message = JsonSerializer.Deserialize<ChatMessageEvent>(
                value.ToString(),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Ignored malformed Redis delivery message.");
            return;
        }

        if (message is null || !Guid.TryParse(message.TargetId, out var targetId) ||
            targetId == Guid.Empty)
        {
            _logger.LogWarning("Ignored Redis delivery without a valid target ID.");
            return;
        }

        var webSocketMessage = new
        {
            message.MessageId,
            message.SenderId,
            message.TargetId,
            Timestamp = new DateTimeOffset(
                message.Timestamp.ToUniversalTime()).ToUnixTimeMilliseconds(),
            Payload = new
            {
                EncryptedKey = PlaintextMvpMarker,
                Iv = PlaintextMvpMarker,
                message.Ciphertext,
                Signature = PlaintextMvpMarker
            }
        };

        var payload = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(webSocketMessage, JsonOptions));
        var delivered = await _connections.SendAsync(
            message.TargetId,
            payload,
            cancellationToken);

        if (!delivered)
        {
            _logger.LogInformation(
                "Target {TargetId} has no active WebSocket on this Gateway.",
                message.TargetId);
        }
    }
}
