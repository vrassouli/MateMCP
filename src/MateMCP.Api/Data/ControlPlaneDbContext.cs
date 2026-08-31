using Microsoft.EntityFrameworkCore;

namespace MateMCP.Api.Data;

public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<AgentDevice> Agents => Set<AgentDevice>();
    public DbSet<EnrollmentSession> Enrollments => Set<EnrollmentSession>();
    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
        builder.Entity<UserAccount>().HasIndex(x => x.NormalizedEmail).IsUnique();
        builder.Entity<AgentDevice>().HasIndex(x => x.PublicId).IsUnique();
        builder.Entity<AgentDevice>().HasIndex(x => x.CredentialHash).IsUnique();
        builder.Entity<EnrollmentSession>().HasIndex(x => x.UserCode).IsUnique();
        builder.Entity<EnrollmentSession>().HasIndex(x => x.DeviceCodeHash).IsUnique();
        builder.Entity<ApprovalRequest>().HasIndex(x => new { x.AgentDeviceId, x.Status });
    }
}

public sealed class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsDisabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AgentDevice> Agents { get; set; } = [];
}

public sealed class AgentDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string PublicId { get; set; }
    public required Guid OwnerId { get; set; }
    public UserAccount? Owner { get; set; }
    public required string Name { get; set; }
    public required string Platform { get; set; }
    public required string CredentialHash { get; set; }
    public string AllowedScopes { get; set; } = "mcp:read mcp:write mcp:shell";
    public bool IsRevoked { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
}

public sealed class EnrollmentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string DeviceCodeHash { get; set; }
    public required string UserCode { get; set; }
    public required string DeviceName { get; set; }
    public required string Platform { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public bool Consumed { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ApprovalRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid AgentDeviceId { get; set; }
    public AgentDevice? AgentDevice { get; set; }
    public required string Capability { get; set; }
    public required string Target { get; set; }
    public required string Summary { get; set; }
    public required string OperationHash { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class AuditEvent
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? AgentDeviceId { get; set; }
    public required string EventType { get; set; }
    public required string Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
