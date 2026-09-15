# macOS release signing and TCC identity

MateMCP Computer Use relies on macOS Accessibility and Screen Recording consent. Those TCC grants are tied to code identity, so production updates should preserve a stable Developer ID identity instead of shipping a different ad-hoc signature on every build.

## Stable identities

Production signing uses these identities:

- Agent executable: `com.matemcp.agent`
- ScreenCaptureKit helper: `com.matemcp.agent.screencapturekit`
- Companion app bundle: `com.matemcp.agent.companion`

The Companion bundle identifier is also declared by the MAUI project through `ApplicationId`.

Development and pull-request builds may still be ad-hoc/unsigned. They are not expected to preserve TCC grants across rebuilds and must not be used to validate upgrade permission persistence.

## Release signing workflow

`.github/workflows/macos-release-signing.yml` runs after successful `build` and `companion-build` workflows on `main`. It is deliberately inert until the complete production credential set is configured. When credentials are available it:

1. downloads the current macOS `agent-latest` release asset(s);
2. imports the Developer ID Application certificate into an ephemeral keychain;
3. signs the Agent/helper with stable explicit identifiers and hardened runtime;
4. signs the Companion app bundle with Developer ID and hardened runtime;
5. rejects a signed artifact that still reports `TeamIdentifier=not set`;
6. submits the package to Apple notary service and waits for success;
7. staples and validates the Companion app ticket;
8. replaces only the corresponding macOS release asset.

The temporary keychain and imported certificate are deleted at the end of the job.

## Required repository secrets

Configure all of the following before enabling production signing:

- `MACOS_DEVELOPER_ID_P12_BASE64` — base64-encoded `.p12` containing the Developer ID Application certificate/private key.
- `MACOS_DEVELOPER_ID_P12_PASSWORD` — password for the `.p12`.
- `APPLE_NOTARY_APPLE_ID` — Apple ID used for notarization.
- `APPLE_NOTARY_APP_PASSWORD` — app-specific password for that Apple ID.
- `APPLE_TEAM_ID` — Apple Developer team ID.

If any value is missing, the post-release workflow exits successfully without modifying release assets.

## Verification before closing #143

The production acceptance test must use two consecutively signed/notarized releases built with the same Developer ID identity:

1. install release A;
2. confirm `codesign -dv --verbose=4` on the installed Agent reports `Identifier=com.matemcp.agent` and a real TeamIdentifier;
3. grant Accessibility and Screen Recording;
4. verify Computer Use works;
5. update normally to release B;
6. verify the installed Agent still has the same identifier/team signing identity;
7. verify Accessibility and Screen Recording remain effective without removing/re-adding the Agent in System Settings;
8. if a grant is missing/revoked, verify MateMCP reports an actionable permission message instead of claiming the Computer Use action succeeded.

Do not close #143 based only on CI signing checks; the release-A to release-B TCC persistence test must be performed on a real Mac.
