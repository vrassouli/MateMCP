namespace MateMCP.Relay;

public sealed class RelayOptions
{
    public const string SectionName = "Relay";
    public string AgentToken { get; set; } = "change-me";
    public string ClientToken { get; set; } = "change-me";
    public int MaxBodyBytes { get; set; } = 4 * 1024 * 1024;
    public int RequestTimeoutSeconds { get; set; } = 120;
}
