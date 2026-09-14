using MateMCP.Agent.Security;

namespace MateMCP.Agent.Configuration;

public sealed class MateOptions
{
    public const string SectionName = "Mate";
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 45871;
    public bool AllowInsecureHttp { get; set; }
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
    public string? AccessToken { get; set; }
    public bool RequireShellApproval { get; set; } = true;
    public int ApprovalTimeoutSeconds { get; set; } = 120;
    public ApprovalRiskPolicyOptions ApprovalRiskPolicy { get; set; } = new();
    public SecondarySemanticAnalysisOptions SecondarySemanticAnalysis { get; set; } = new();
    public ProactiveMemoryOptions ProactiveMemory { get; set; } = new();
    public InteractiveShellOptions InteractiveShell { get; set; } = new();
    public RelayOptions Relay { get; set; } = new();
    public List<ProjectOptions> Projects { get; set; } = [];
}

public enum ProactiveMemoryMode
{
    Off,
    Suggested,
    Automatic
}

public sealed class ProactiveMemoryOptions
{
    /// <summary>
    /// Automatic is the default because only bounded, relevance-ranked durable context is surfaced.
    /// Suggested emits a hint when relevant context exists without returning its content.
    /// Off disables proactive lookup/injection; explicit memory tools remain available.
    /// </summary>
    public ProactiveMemoryMode Mode { get; set; } = ProactiveMemoryMode.Automatic;

    public int MaxItems { get; set; } = 3;
    public int MaxChars { get; set; } = 3_500;
}

public sealed class SecondarySemanticAnalysisOptions
{
    /// <summary>Disabled by default. Deterministic analysis remains authoritative.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// OpenAI-compatible chat-completions endpoint. Only loopback endpoints are accepted so
    /// proposed actions are not silently disclosed to a remote model/provider.
    /// </summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:11434/v1/chat/completions";
    public string? Model { get; set; }
    public int TimeoutSeconds { get; set; } = 5;
    public int MaxInputChars { get; set; } = 2_000;
}

public sealed class InteractiveShellOptions
{
    public int MaxSessions { get; set; } = 8;
    public int IdleTimeoutSeconds { get; set; } = 600;
    public int MaxLifetimeSeconds { get; set; } = 3600;
    public int MaxOutputChars { get; set; } = 500_000;
    public int MaxInputChars { get; set; } = 65_536;
    public int SecretInjectionMaxAttempts { get; set; } = 5;
    public int SecretInjectionWindowSeconds { get; set; } = 60;
}

public sealed class RelayOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "https://relay.matemcp.com";
    public string ControlPlaneUrl { get; set; } = "https://api.matemcp.com";
    public string? DeviceId { get; set; }
    public bool EnrollmentSuppressed { get; set; }
    public int MaxMessageBytes { get; set; } = 8 * 1024 * 1024;
    public int MaxConcurrentRequests { get; set; } = 8;
}

public sealed class ProjectOptions
{
    public string? Id { get; set; }
    public required string Name { get; set; }
    public required string Root { get; set; }
    public bool Read { get; set; } = true;
    public bool Write { get; set; } = true;
    public bool Shell { get; set; } = true;
}
