namespace Gateway.Configuration;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string Id { get; set; } = string.Empty;
    public int PresenceTtlSeconds { get; set; } = 60;
}
