using System.ComponentModel;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class AgentFileTransferTools(ProjectRegistry projects, AgentFileTransferManager transfers, ApprovalService approvals, AuditLog audit)
{
    [McpServerTool(
        Name = "agent_file_upload_start",
        Title = "Start uploading a conversation attachment to this Agent",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Starts a secure chunked file upload to this MateMCP Agent. Use this for a file supplied by the user in the active AI conversation. After approval, send base64 chunks with agent_file_upload_chunk, then call agent_file_upload_complete. If project is supplied, the temporary file is isolated under that writable project; otherwise it is stored in the Agent temporary area for shell use.")]
    public async Task<object> Start(
        [Description("Original attachment file name.")] string fileName,
        [Description("Exact attachment size in bytes.")] long size,
        [Description("Optional target MateMCP project. The project must allow writes.")] string? project = null,
        [Description("Optional MIME type supplied by the chat attachment metadata.")] string? mimeType = null,
        [Description("Optional lowercase/uppercase 64-character SHA-256 hex digest for end-to-end integrity validation.")] string? sha256 = null,
        CancellationToken cancellationToken = default)
    {
        string root;
        string scope;
        if (!string.IsNullOrWhiteSpace(project))
        {
            var definition = projects.Get(project);
            if (!definition.Write)
            {
                await audit.WriteAsync("agent.file-upload.start", $"project:{definition.Name}:{Safe(fileName)}", "denied:project-policy", cancellationToken);
                throw new McpException($"File transfer is not allowed because project '{definition.Name}' does not allow writes.");
            }

            root = projects.ResolvePath(definition.Name, Path.Combine(".matemcp", "attachments"), requireWrite: true);
            scope = $"project:{definition.Name}";
            project = definition.Name;
        }
        else
        {
            root = Path.Combine(Path.GetTempPath(), "MateMCP", "attachments");
            scope = "agent-temporary";
        }

        var decision = await approvals.RequestAsync("agent.file-upload", scope, $"{Safe(fileName)} ({size} bytes, {mimeType ?? "unknown MIME"})", cancellationToken);
        if (decision == ApprovalDecision.Deny)
        {
            await audit.WriteAsync("agent.file-upload.start", $"{scope}:{Safe(fileName)}", "denied:approval", cancellationToken);
            throw new McpException("File transfer denied by local user.");
        }
        if (decision == ApprovalDecision.Timeout)
        {
            await audit.WriteAsync("agent.file-upload.start", $"{scope}:{Safe(fileName)}", "denied:approval-timeout", cancellationToken);
            throw new McpException("File transfer approval timed out.");
        }

        try
        {
            var started = transfers.Start(root, fileName, mimeType, size, sha256, project);
            await audit.WriteAsync("agent.file-upload.start", $"{scope}:{started.TransferId}:{started.FileName}", "ok", cancellationToken);
            return new
            {
                transferId = started.TransferId,
                started.Project,
                started.FileName,
                started.MimeType,
                size = started.Size,
                expectedSha256 = started.ExpectedSha256,
                remotePath = started.RemotePath,
                maxChunkBytes = started.MaxChunkBytes,
                incompleteCleanupMinutes = (int)started.IncompleteTtl.TotalMinutes,
                nextOffset = 0L
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await audit.WriteAsync("agent.file-upload.start", $"{scope}:{Safe(fileName)}", $"error:{ex.GetType().Name}", CancellationToken.None);
            throw new McpException($"Could not start file transfer: {ex.Message}", ex);
        }
    }

    [McpServerTool(
        Name = "agent_file_upload_chunk",
        Title = "Upload the next attachment chunk",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Appends the next base64-encoded chunk to an attachment transfer. Chunks must be sequential and no larger than the maxChunkBytes returned by agent_file_upload_start. This streams to disk and does not buffer the whole file in Agent memory.")]
    public async Task<object> UploadChunk(
        [Description("Transfer id returned by agent_file_upload_start.")] string transferId,
        [Description("Zero-based byte offset of this chunk in the original file. Must equal the next expected offset.")] long offset,
        [Description("Base64-encoded raw file bytes for this chunk.")] string base64Data,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var progress = await transfers.AppendChunkAsync(transferId, offset, base64Data, cancellationToken);
            return new { progress.TransferId, progress.BytesReceived, progress.ExpectedSize, progress.ReadyToComplete, nextOffset = progress.BytesReceived };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await audit.WriteAsync("agent.file-upload.chunk", transferId, $"error:{ex.GetType().Name}", CancellationToken.None);
            throw new McpException($"File transfer chunk failed: {ex.Message}", ex);
        }
    }

    [McpServerTool(
        Name = "agent_file_upload_complete",
        Title = "Complete and verify an attachment upload",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Completes a chunked attachment upload, verifies declared size and optional SHA-256, atomically publishes the temporary file, and returns the remote path usable by shell tools. For project-scoped transfers the path is inside that project and can also be addressed by filesystem tools using the returned projectRelativePath.")]
    public async Task<object> Complete(
        [Description("Transfer id returned by agent_file_upload_start.")] string transferId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var completed = await transfers.CompleteAsync(transferId, cancellationToken);
            string? projectRelativePath = null;
            if (!string.IsNullOrWhiteSpace(completed.Project))
            {
                var root = projects.Get(completed.Project).Root;
                projectRelativePath = Path.GetRelativePath(root, completed.RemotePath);
            }

            await audit.WriteAsync("agent.file-upload.complete", $"{transferId}:{completed.FileName}", "ok", cancellationToken);
            return new
            {
                completed.TransferId,
                completed.Project,
                completed.FileName,
                completed.MimeType,
                size = completed.Size,
                sha256 = completed.Sha256,
                remotePath = completed.RemotePath,
                projectRelativePath,
                cleanupAfterHours = (int)completed.CleanupAfter.TotalHours
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await audit.WriteAsync("agent.file-upload.complete", transferId, $"error:{ex.GetType().Name}", CancellationToken.None);
            throw new McpException($"Could not complete file transfer: {ex.Message}", ex);
        }
    }

    [McpServerTool(
        Name = "agent_file_upload_cancel",
        Title = "Cancel an attachment upload and remove temporary data",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Cancels an attachment transfer and removes its partial or completed temporary file immediately. Transfers are also cleaned up automatically after their lifecycle expires.")]
    public async Task<string> Cancel(
        [Description("Transfer id returned by agent_file_upload_start.")] string transferId,
        CancellationToken cancellationToken = default)
    {
        await transfers.CancelAsync(transferId, cancellationToken);
        await audit.WriteAsync("agent.file-upload.cancel", transferId, "ok", cancellationToken);
        return "cancelled";
    }

    private static string Safe(string value)
    {
        value = Path.GetFileName(value ?? string.Empty);
        return value.Length <= 200 ? value : value[..200] + "…";
    }
}
