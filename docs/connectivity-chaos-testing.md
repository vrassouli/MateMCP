# Connectivity recovery and chaos test matrix

This document maps the resilience scenarios from GitHub Epic #133 to deterministic automated tests. The goal is to exercise transport-loss behavior without depending on flaky external networking in CI.

## Test strategy

The cross-component `ConnectivityChaosMatrixTests` harness uses the real Relay `AgentRegistry` / `AgentConnection` transport request path, serializes the real Relay request payload through a fake WebSocket, deserializes it into the real Agent Relay request model, executes through the real Agent `RelayOperationRegistry`, and delivers the response back to the Relay connection when the scenario allows it.

The fake WebSocket only controls fault timing: fail before delivery, drop a response after Agent execution, or cancel the physical transport while an operation is still running. Business recovery, OperationId deduplication, SessionId correlation, pending-request handling, and reconnect generation ownership remain production code.

## Epic #133 scenario coverage

| # | Scenario | Automated coverage | Expected invariant |
|---|---|---|---|
| 1 | Disconnect before Agent accepts an operation | `ConnectivityChaosMatrixTests.Disconnect_before_agent_accepts_then_reconnect_executes_once` | Failed physical delivery does not create an Agent operation; retry on the replacement connection executes once. |
| 2 | Disconnect while an operation is running | `ConnectivityChaosMatrixTests.Disconnect_while_running_retry_waits_same_operation_and_executes_once`; `RelayOperationRegistryTests.Transport_wait_cancellation_does_not_cancel_running_operation` | Losing the transport waiter does not cancel the logical operation; retry waits/replays the same execution. |
| 3 | Disconnect after execution but before result delivery | `ConnectivityChaosMatrixTests.Lost_response_after_execution_reconnects_and_replays_without_reexecution`; `RelayOperationRegistryTests.Completed_duplicate_replays_cached_result_without_reexecution` | Stable OperationId recovers the cached result and the side effect runs once. |
| 4 | Disconnect while streaming shell output | `ShellReplayCheckpointTests.Acknowledgement_survives_later_reads`; `Available_checkpoint_replays_without_gap`; `Old_sequence_reports_replay_gap_after_buffer_trim` | The shell checkpoint is independent of Relay ConnectionId; available output replays and an expired replay window reports an explicit gap. |
| 5 | Agent reconnect within the grace window | `AgentRegistryConcurrencyTests.Reconnect_within_grace_rebinds_presence_without_offline_gap`; `AgentConnectionRecoveryTests.Wait_for_online_returns_replacement_connection_after_reconnect` | Replacement connection rebinds logical presence without an offline transition. |
| 6 | Agent reconnect after lease expiry | `AgentRegistryConcurrencyTests.Expired_reconnect_lease_is_removed_once`; `AgentConnectionRecoveryTests.Wait_for_online_returns_null_when_grace_expires_without_reconnect` | Expired presence is removed and recovery wait terminates cleanly. |
| 7 | Relay restart during an active session | `RelayShutdownDrainTests.Hosted_service_waits_for_pending_work_then_disconnects_agents`; `Draining_connection_preserves_existing_request_and_rejects_new_work` | Planned shutdown stops new admission, allows bounded completion, then deliberately disconnects Agents for reconnect. |
| 8 | Two concurrent clients, one reconnecting while the other is active | `ConnectivityChaosMatrixTests.Concurrent_sessions_survive_one_transport_replacement_without_cross_session_execution`; `AgentRegistryConcurrencyTests.Two_concurrent_clients_share_one_registered_agent_without_registry_gap` | SessionId/OperationId remain isolated and each logical operation executes once across physical connection replacement. |
| 9 | Slow consumer / backpressure | `RelayBackpressureTests.Saturated_connection_returns_agent_busy_without_disturbing_inflight_request`; cancellation/timeout/disconnect capacity-recovery tests | Pending work is bounded; overload rejects only new work and all terminal paths reclaim capacity. |
| 10 | Replay buffer gap / overflow | `ShellReplayCheckpointTests.Old_sequence_reports_replay_gap_after_buffer_trim`; `AgentFileTransferResumeTests.Status_returns_committed_resume_offset_and_gap_is_explicit` | Lost history is explicit instead of silently corrupting stream state. |
| 11 | Duplicate retry of the same OperationId | `RelayOperationRegistryTests.Concurrent_duplicate_operation_executes_once_and_rebinds_response_id`; `Completed_duplicate_replays_cached_result_without_reexecution`; cross-component lost-response tests | Concurrent/rerouted duplicate delivery never repeats the logical execution within the bounded retention window. |
| 12 | Graceful deployment / drain | `RelayShutdownDrainTests.Registry_marks_all_current_connections_draining_and_aborts_them_for_shutdown`; `Hosted_service_waits_for_pending_work_then_disconnects_agents`; `Zero_second_drain_aborts_pending_work_immediately` | Drain behavior is bounded, observable, and deterministic for both graceful completion and timeout/immediate shutdown. |

## File-transfer recovery coverage

Attachment transfer recovery is checkpoint-based rather than replay-buffer based. `AgentFileTransferResumeTests` proves exact committed chunk replay is idempotent, conflicting bytes are rejected, status exposes the committed resume offset, replay cannot cross the committed/uncommitted boundary, and completion can be safely repeated after a lost response.

## Limits of the guarantee

MateMCP's current recovery guarantee is bounded and in-memory. It survives transient transport loss and physical Relay connection replacement while the Agent process and its recovery state remain alive. The graceful Relay shutdown path drains admitted work before intentionally forcing Agent reconnect.

A hard Agent process crash destroys in-memory operation/shell/transfer state and is not claimed to be recoverable by Epic #133. Likewise, a hard Relay process crash cannot preserve Relay-local pending waiters; stable Agent OperationId state still prevents duplicate execution when the client retries, but fully crash-survivable distributed session state would require external persistence and is a separate architecture project.
