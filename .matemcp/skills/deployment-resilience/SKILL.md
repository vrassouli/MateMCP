---
description: Keep MateMCP production updates non-destructive when an image registry or delivery CDN is unavailable.
---

# MateMCP deployment resilience

Use this skill when changing production installers, container image delivery, or update/recreate behavior.

## Image acquisition

- Acquire or build the replacement image completely **before** recreating the running service.
- A registry pull failure must not stop, remove, or recreate the currently healthy container.
- The API installer normally pulls the configured image first.
- For the official `vrassouli/matemcp-api:*` image, the supported fallback is a local source build from the same `MATEMCP_INSTALL_REF` using the repository API Dockerfile.
- Do not silently apply the official-source fallback to a custom `MATEMCP_API_IMAGE`; fail closed instead.
- Log which image source was used without logging registry credentials, application secrets, or provider secrets.

## Verification

- Validate installer shell syntax and Compose rendering.
- For registry-failure changes, perform a production-path test where the primary image acquisition fails and confirm the fallback succeeds.
- After update, verify local health and the public HTTPS health endpoint.
- When all acquisition paths fail, verify the existing container remains running and healthy.

## Reproducible Desktop packaging toolchain

- Companion package CI must pin the .NET MAUI workload-set version that is known to match the selected GitHub runner/Xcode image; do not rely on the floating latest workload set.
- Treat MAUI workload-set, .NET SDK, macOS runner, and Xcode upgrades as one intentional compatibility change. Update the pin only after both macOS and Windows Companion package jobs pass.
