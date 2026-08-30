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
    public List<ProjectOptions> Projects { get; set; } = [];
}

public sealed class ProjectOptions
{
    public required string Name { get; set; }
    public required string Root { get; set; }
    public bool Read { get; set; } = true;
    public bool Write { get; set; } = true;
    public bool Shell { get; set; } = true;
}
