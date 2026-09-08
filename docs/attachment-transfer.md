# Conversation attachment transfer

MateMCP exposes a chunked upload capability so an AI client that can read a conversation attachment can transfer the file to the selected Agent without buffering the complete file in Agent memory.

## MCP flow

1. Call `agent_file_upload_start` with the original file name, exact byte size, optional MIME type, optional SHA-256 digest, and optional target project.
2. The Agent requests local approval for the transfer. A project-scoped upload is also rejected when that project does not allow writes.
3. Send sequential base64 chunks with `agent_file_upload_chunk`. Each decoded chunk is limited to 512 KiB and is written directly to a `.part` file.
4. Call `agent_file_upload_complete`. The Agent verifies the declared size, calculates SHA-256 over the on-disk file, compares it with the optional expected digest, and atomically renames the partial file to its final name.
5. Use `remotePath` with shell tools. Project-scoped transfers additionally return `projectRelativePath`, which can be passed to filesystem tools for that project.
6. Call `agent_file_upload_cancel` when the file is no longer needed and immediate cleanup is desired.

The client should calculate offsets from raw decoded byte counts, not from base64 string length.

## Isolation and policy

Project-scoped uploads are written only under:

```text
<project>/.matemcp/attachments/<transfer-id>/<sanitized-file-name>
```

The target project must have write permission. Non-project uploads are written under the operating system temporary directory in a MateMCP-specific attachment directory and are intended for Agent-level shell use.

Every new upload requires the existing MateMCP approval workflow using capability `agent.file-upload`. Approval decisions therefore participate in the same local/remote approval policy mechanism and audit trail as other protected Agent operations.

File names are reduced to a safe leaf name, invalid platform characters are replaced, and the Agent generates the transfer directory id. Clients cannot choose arbitrary destination paths.

## Lifecycle

Incomplete transfers expire after one hour of inactivity. Completed transferred files expire after 24 hours. A process-wide cleanup timer removes expired files every five minutes while the Agent is running. Cancellation removes partial or completed transfer data immediately.

A transfer that is interrupted can be continued only while its in-memory transfer handle remains valid and the next request uses the exact expected byte offset. After Agent restart, the transfer handle is no longer valid and the client must start a new transfer.

## Integrity and limits

- Maximum decoded chunk size: 512 KiB.
- Maximum declared file size: 50 GiB.
- Declared size is enforced while chunks are appended and again at completion.
- SHA-256 can be supplied at start and is verified with a fixed-time digest comparison at completion.
- The complete file is never loaded into Agent memory; SHA-256 is calculated from a streaming file read.
- Invalid base64, unexpected offsets, oversize chunks, size mismatches, integrity failures, expired handles, and filesystem failures are returned as explicit MCP errors and written to the audit log without logging file contents.

## ChatGPT integration boundary

These tools provide the MateMCP-side transfer primitive. An AI host such as ChatGPT still needs to expose the active conversation attachment bytes (or an authorized attachment stream/reference that the host can dereference) to the model/tool orchestration layer. Once those bytes are available to the client, the four MCP tools above carry them securely to the Agent.
