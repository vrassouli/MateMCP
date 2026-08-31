using System.ComponentModel;
using System.Diagnostics;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Security;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class ShellTools(
    ProjectRegistry projects,
    AuditLog audit,
    ApprovalService approvals,
    IOptions<MateOptions> options)
{
    [McpServerTool(Name = "shell_exec"), Description("Executes a shell command in a configured project directory and returns exit code, stdout, and stderr. Shell execution may require explicit local approval.")]
    public async Task<object> Exec(string project, string command, int timeoutSeconds = 60, CancellationToken cancellationToken = default)
    {
        var definition = projects.Get(project);
        if (!definition.Shell)
        {
            await audit.WriteAsync("shell.exec", project, "denied:project-policy", cancellationToken);
            throw new UnauthorizedAccessException($"Shell access is disabled for project '{project}'.");
        }

        if (options.Value.RequireShellApproval)
        {
            var approved = await approvals.RequestAsync(
                "shell.exec",
                $"project:{project}",
                Trim(command),
                cancellationToken);
            if (!approved)
            {
                await audit.WriteAsync("shell.exec", $"{project}:{Trim(command)}", "denied:approval", cancellationToken);
                throw new UnauthorizedAccessException("Shell execution was denied or approval timed out.");
            }
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 600);

        var psi = CreateShellProcess(command, definition.Root);
        foreach (var key in new[] { "GITHUB_TOKEN", "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "AZURE_OPENAI_API_KEY", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN" })
            psi.Environment.Remove(key);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start shell process.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await audit.WriteAsync("shell.exec", $"{project}:{Trim(command)}", "timeout", CancellationToken.None);
            throw;
        }

        var stdout = Limit(await stdoutTask);
        var stderr = Limit(await stderrTask);
        await audit.WriteAsync("shell.exec", $"{project}:{Trim(command)}", $"exit:{process.ExitCode}", cancellationToken);
        return new { exitCode = process.ExitCode, stdout, stderr };
    }

    private static ProcessStartInfo CreateShellProcess(string command, string workingDirectory)
    {
        ProcessStartInfo psi;

        if (OperatingSystem.IsWindows())
        {
            var powerShell = ResolvePowerShell();
            psi = new ProcessStartInfo(powerShell)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
            return psi;
        }

        var shell = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh";
        psi = new ProcessStartInfo(shell)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        return psi;
    }

    private static string ResolvePowerShell()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(pwsh) ? pwsh : "powershell.exe";
    }

    private static string Limit(string value) => value.Length <= 200_000 ? value : value[..200_000] + "\n[output truncated]";
    private static string Trim(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
