namespace Gateway.Configuration;

public sealed class RedisRoutingOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";
    public string PresenceKeyPrefix { get; set; } = "presence:";
    public string GatewayMappingKeyPrefix { get; set; } = "gateway_for_user:";
    public string DeliveryChannelPrefix { get; set; } = "gateway:delivery:";
}
