using System.ComponentModel;
using System.Diagnostics;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Projects;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class ShellTools(ProjectRegistry projects, AuditLog audit)
{
    [McpServerTool(Name = "shell_exec"), Description("Executes a shell command in a configured project directory and returns exit code, stdout, and stderr.")]
    public async Task<object> Exec(string project, string command, int timeoutSeconds = 60, CancellationToken cancellationToken = default)
    {
        var definition = projects.Get(project);
        if (!definition.Shell) throw new UnauthorizedAccessException($"Shell access is disabled for project '{project}'.");
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 600);

        var psi = new ProcessStartInfo("/bin/zsh")
        {
            WorkingDirectory = definition.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        psi.Environment.Remove("GITHUB_TOKEN");
        psi.Environment.Remove("OPENAI_API_KEY");
        psi.Environment.Remove("ANTHROPIC_API_KEY");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start shell process.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await audit.WriteAsync("shell.exec", $"{project}:{Trim(command)}", "timeout", cancellationToken);
            throw;
        }

        var stdout = Limit(await stdoutTask);
        var stderr = Limit(await stderrTask);
        await audit.WriteAsync("shell.exec", $"{project}:{Trim(command)}", $"exit:{process.ExitCode}", cancellationToken);
        return new { exitCode = process.ExitCode, stdout, stderr };
    }

    private static string Limit(string value) => value.Length <= 200_000 ? value : value[..200_000] + "\n[output truncated]";
    private static string Trim(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
