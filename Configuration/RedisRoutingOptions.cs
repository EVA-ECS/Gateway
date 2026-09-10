namespace Gateway.Configuration;

public sealed class RedisRoutingOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";
    public string PresenceKeyPrefix { get; set; } = "presence:";
    public int PresenceTtlSeconds { get; set; } = 60;
    public string SingleGatewayDeliveryChannel { get; set; } = "gateway:delivery";
}
