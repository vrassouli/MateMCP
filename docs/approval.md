# Approval model

MateMCP distinguishes pre-authorized project operations from operations that require an explicit local decision. Approval requests are independently analyzed before the user is asked to decide.

## Informed approval UI

The Agent exposes a loopback-only management UI at `http://127.0.0.1:45871/ui` (or the configured local port), and the Companion presents the same pending approvals as a global informed-approval overlay.

An approval leads with MateMCP's expected effect, risk, confidence, reversibility, affected resources, preflight facts, and safer alternatives when deterministic evidence is available. Raw capability/target/command details remain available under **Technical details**. `Critical` and `Unknown` actions are visually distinguished from routine approvals.

When an approval is created on macOS, MateMCP displays a desktop notification and opens the local management UI. The underlying decision endpoints remain loopback-only and are not exposed directly through the relay.

Remote approval remains available through the control plane. Remote summaries are bounded and redact credential-like values before publication. Status polling uses bounded backoff so a pending request does not generate a tight two-second HTTP loop for its full lifetime.

## Risk policy

Risk policy is evaluated from MateMCP's assessment before stored approval rules are reused. The default policy is intentionally conservative:

| Risk | Default behavior | Session / persistent trust |
| --- | --- | --- |
| Low | `AllowStoredRule` | allowed |
| Medium | `AllowStoredRule` | allowed |
| High | `RequireApproval` | disabled |
| Critical | `RequireApproval` | disabled |
| Unknown | `RequireApproval` | disabled |

This means an old `Always allow shell.exec` rule cannot silently authorize a newly detected High/Critical/Unknown command under the defaults. `RequireApproval` means a fresh decision every time; `AutoAllow` allows the individual action without creating a trust rule; `Deny` rejects it locally without prompting.

The policy can be configured in the user's MateMCP `appsettings.json` or through configuration/environment overrides:

```json
{
  "Mate": {
    "ApprovalRiskPolicy": {
      "Low": "AllowStoredRule",
      "Medium": "AllowStoredRule",
      "High": "RequireApproval",
      "Critical": "RequireApproval",
      "Unknown": "RequireApproval",
      "BroadTrustRisks": ["Low", "Medium"]
    }
  }
}
```

For managed deployments the same values can be supplied through deployment configuration, for example `MATEMCP_Mate__ApprovalRiskPolicy__Critical=Deny`. A risk level must both use `AllowStoredRule` **and** be present in `BroadTrustRisks` before MateMCP will reuse or create a session/persistent rule for that level.

Current user decisions are:

- **Allow once** — approve exactly one pending operation.
- **Allow for session** — cache the exact capability + target rule in memory until the Agent stops, only when the current risk policy permits broad trust.
- **Always allow** — persist the exact capability + target rule in the local policy file, only when the current risk policy permits broad trust.
- **Deny** — reject the pending operation.

Credential approvals use `secret.use` as the capability and `<credential>@cmd:<fingerprint>` as the target. This binds session and persistent approval rules to both the named credential and the exact interactive command; a rule for another credential or command does not match. Persistent rules can be inspected and removed from the loopback-only management UI.

## Deterministic analyzers, preflight, and extensions

Structured-tool semantics and deterministic shell analysis remain authoritative inputs. Context-aware preflight may inspect bounded local facts such as target existence/breadth or Git working-tree state, but never executes the requested command. Git inspection uses `GIT_OPTIONAL_LOCKS=0` and short timeouts so status checks do not refresh/write the index. Filesystem-like targets outside the caller-supplied inspection scope are not read.

Additional deterministic rule packs can implement `IActionImpactAnalyzerPack` and register with `ActionImpactAnalyzerPacks.Register(...)`. Packs run ahead of the built-in fallback without modifying the central analyzer. Assessment audit metadata records contributing analyzer sources and whether they were authoritative.

## Optional local semantic analysis

A secondary OpenAI-compatible local model can be enabled as an additional **non-authoritative** signal. It is disabled by default:

```json
{
  "Mate": {
    "SecondarySemanticAnalysis": {
      "Enabled": true,
      "Endpoint": "http://127.0.0.1:11434/v1/chat/completions",
      "Model": "qwen3:4b",
      "TimeoutSeconds": 5,
      "MaxInputChars": 2000
    }
  }
}
```

MateMCP refuses non-loopback semantic endpoints, so enabling this feature cannot silently send proposed actions to an Internet model/provider. Credential-like values are redacted and the input is bounded before the local request is sent. A semantic signal may **raise** the final risk or add a reason/safer alternative; it cannot lower deterministic risk or override a deterministic/user-configured `RequireApproval`/`Deny` policy. If the local model is missing, slow, malformed, or unavailable, MateMCP simply continues with deterministic analysis.

## Rule scope

Rules are owned by MateMCP, not MCP Roots.

Project-relative operations remain constrained by project read/write/shell flags. Operations outside project roots must never silently fall back to unrestricted filesystem access through structured filesystem tools. Shell commands are inherently broader, which is why their requested execution context is passed into deterministic preflight and their risk is assessed before approval.

Persistent rules are expressed in terms of capability + target scope. Broad rules such as unrestricted shell access or `/` filesystem access should require an explicit high-risk confirmation and remain visually distinguishable in the control UI.
