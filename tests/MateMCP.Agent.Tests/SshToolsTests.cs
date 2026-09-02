using System.Reflection;
using MateMCP.Agent.Tools;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tests;

public sealed class SshToolsTests
{
    [Fact]
    public void Structured_ssh_command_supports_expected_destination()
    {
        var command = SshSessionCommand.Build("192.168.200.37", "administrator");

        Assert.Equal("ssh -p 22 administrator@192.168.200.37", command);
    }

    [Fact]
    public void Structured_ssh_command_supports_custom_port_and_dns_name()
    {
        var command = SshSessionCommand.Build("ubuntu.internal", "deploy-user", 2222);

        Assert.Equal("ssh -p 2222 deploy-user@ubuntu.internal", command);
    }

    [Theory]
    [InlineData("192.168.200.37 && whoami", "administrator")]
    [InlineData("server.local;shutdown", "administrator")]
    [InlineData("server.local", "admin;whoami")]
    [InlineData("server.local", "admin user")]
    [InlineData("-oProxyCommand=whoami", "administrator")]
    public void Structured_ssh_command_rejects_shell_injection(string host, string username)
    {
        Assert.Throws<ArgumentException>(() => SshSessionCommand.Build(host, username));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Structured_ssh_command_rejects_invalid_port(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SshSessionCommand.Build("192.168.200.37", "administrator", port));
    }

    [Fact]
    public void Ssh_authenticate_is_a_narrow_non_destructive_open_world_tool()
    {
        var method = typeof(SshTools).GetMethod(nameof(SshTools.Authenticate));
        Assert.NotNull(method);

        var tool = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(tool);
        Assert.Equal("ssh_session_authenticate", tool!.Name);
        Assert.False(tool.ReadOnly);
        Assert.False(tool.Destructive);
        Assert.False(tool.Idempotent);
        Assert.True(tool.OpenWorld);

        var parameterNames = method.GetParameters().Select(x => x.Name).ToArray();
        Assert.Contains("sessionId", parameterNames);
        Assert.Contains("credential", parameterNames);
        Assert.DoesNotContain("secret", parameterNames);
    }

    [Fact]
    public void Ssh_start_is_a_narrow_non_destructive_open_world_tool()
    {
        var method = typeof(SshTools).GetMethod(nameof(SshTools.Start));
        Assert.NotNull(method);

        var tool = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(tool);
        Assert.Equal("ssh_session_start", tool!.Name);
        Assert.False(tool.ReadOnly);
        Assert.False(tool.Destructive);
        Assert.False(tool.Idempotent);
        Assert.True(tool.OpenWorld);

        var parameterNames = method.GetParameters().Select(x => x.Name).ToArray();
        Assert.Contains("host", parameterNames);
        Assert.Contains("username", parameterNames);
        Assert.Contains("port", parameterNames);
        Assert.DoesNotContain("command", parameterNames);
    }
}
