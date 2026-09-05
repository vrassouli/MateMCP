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
    public InteractiveShellOptions InteractiveShell { get; set; } = new();
    public RelayOptions Relay { get; set; } = new();
    public List<ProjectOptions> Projects { get; set; } = [];
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
