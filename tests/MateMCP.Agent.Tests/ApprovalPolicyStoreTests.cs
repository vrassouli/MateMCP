using MateMCP.Agent.Security;

namespace MateMCP.Agent.Tests;

public sealed class ApprovalPolicyStoreTests
{
    [Fact]
    public async Task Session_rule_is_scoped_to_credential_and_command_target()
    {
        var store = NewStore();
        store.AllowForSession("secret.use", "prod-ssh@cmd:abc");

        Assert.True(store.IsSessionAllowed("secret.use", "prod-ssh@cmd:abc"));
        Assert.False(store.IsSessionAllowed("secret.use", "prod-ssh@cmd:def"));
        Assert.False(store.IsSessionAllowed("secret.use", "other@cmd:abc"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Always_rule_persists_and_can_be_removed()
    {
        var path = NewPolicyPath();
        var first = new ApprovalPolicyStore(path);
        await first.AllowAlwaysAsync("secret.use", "prod-ssh@cmd:abc");

        var reloaded = new ApprovalPolicyStore(path);
        Assert.True(await reloaded.IsAlwaysAllowedAsync("secret.use", "prod-ssh@cmd:abc"));
        Assert.False(await reloaded.IsAlwaysAllowedAsync("secret.use", "prod-ssh@cmd:def"));
        Assert.True(await reloaded.RemoveAlwaysAsync("secret.use", "prod-ssh@cmd:abc"));
        Assert.False(await reloaded.IsAlwaysAllowedAsync("secret.use", "prod-ssh@cmd:abc"));
    }

    private static ApprovalPolicyStore NewStore() => new(NewPolicyPath());

    private static string NewPolicyPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "matemcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "approval-policies.json");
    }
}
