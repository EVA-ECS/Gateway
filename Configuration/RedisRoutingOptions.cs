namespace Gateway.Configuration;

public sealed class RedisRoutingOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";
    public string PresenceKeyPrefix { get; set; } = "eva-chat:online:";
    public int PresenceTtlSeconds { get; set; } = 60;
    public string SingleGatewayDeliveryChannel { get; set; } = "gateway:delivery";
}
