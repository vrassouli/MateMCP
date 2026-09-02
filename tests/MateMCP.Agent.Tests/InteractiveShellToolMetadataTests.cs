using System.Reflection;
using MateMCP.Agent.Tools;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tests;

public sealed class InteractiveShellToolMetadataTests
{
    [Theory]
    [InlineData(nameof(InteractiveShellTools.Start), "shell_session_start", false, true, false, true)]
    [InlineData(nameof(InteractiveShellTools.Read), "shell_session_read", true, false, true, false)]
    [InlineData(nameof(InteractiveShellTools.Write), "shell_session_write", false, true, false, true)]
    [InlineData(nameof(InteractiveShellTools.SendSecret), "shell_session_send_secret", false, false, false, true)]
    [InlineData(nameof(InteractiveShellTools.Close), "shell_session_close", false, true, true, false)]
    [InlineData(nameof(InteractiveShellTools.ListSecrets), "secret_list", true, false, true, false)]
    public void Interactive_shell_tools_have_explicit_accurate_metadata(
        string methodName, string toolName, bool readOnly, bool destructive, bool idempotent, bool openWorld)
    {
        var method = typeof(InteractiveShellTools).GetMethod(methodName);
        Assert.NotNull(method);

        var tool = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(tool);
        Assert.Equal(toolName, tool!.Name);
        Assert.Equal(readOnly, tool.ReadOnly);
        Assert.Equal(destructive, tool.Destructive);
        Assert.Equal(idempotent, tool.Idempotent);
        Assert.Equal(openWorld, tool.OpenWorld);
    }

    [Fact]
    public void Non_interactive_shell_exec_is_explicitly_distinct_from_interactive_sessions()
    {
        var method = typeof(ShellTools).GetMethod(nameof(ShellTools.Exec));
        Assert.NotNull(method);
        var tool = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(tool);
        Assert.Equal("shell_exec", tool!.Name);
        Assert.False(tool.ReadOnly);
        Assert.True(tool.Destructive);
        Assert.False(tool.Idempotent);
        Assert.True(tool.OpenWorld);
    }
}
