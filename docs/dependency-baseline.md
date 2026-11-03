# Dependency and toolchain baseline

MateMCP targets .NET 10. Keep the SDK and NuGet dependency graph reproducible and secure.

## SDK and workloads

- SDK baseline: .NET SDK 10.0.403 (see global.json).
- MAUI Companion targets .NET 10; Companion CI pins workload set 10.0.403 explicitly in each platform job. Windows installs only maui-windows; macOS selects Xcode 27.1 on the xcode-27 runner before installing the MAUI workload for Mac Catalyst.
- The .NET Mac Catalyst 27 SDK requires a minimum deployment target of 17.0 (macOS 14 Sonoma); the Companion Mac Catalyst minimum is 17.0. The Windows minimum remains 10.0.17763.0.
- CI and Docker SDK images use the same pinned SDK servicing release. The workload-set pin is deliberately scoped to the Companion MAUI jobs so non-MAUI restore/build jobs don't require platform workload manifests. When advancing the SDK feature band, validate the MAUI workload against both the Windows runner and the pinned macOS/Xcode image before changing the pin.

## NuGet package management

- Package versions are centralized in Directory.Packages.props.
- Keep Microsoft ASP.NET Core and EF Core package families aligned on the same servicing release.
- Keep OpenIddict modules on one version.
- Keep the official Model Context Protocol SDK current; review release notes for security fixes before updating.
- Microsoft.Data.SqlClient stays on its 6.1 LTS servicing line (6.1.8) rather than moving to the shorter-lived 7.1 STS line. MateMCP does not use driver-provided Entra authentication.
- Test projects use xUnit v3 and Microsoft Testing Platform. xUnit v3 test projects are executable test assemblies.
- The Windows-only legacy toast package pulls System.Drawing.Common; keep it explicitly pinned to the current .NET 10 servicing release (10.0.14).

## Updating dependencies

1. Review stable versions and compatibility with the target frameworks and platform workloads.
2. Update the central version once per package.
3. Run restore with NuGet audit enabled; do not suppress audit warnings or lower warning severity.
4. Run the complete solution build and test suite.
5. Validate Companion builds and packaging on Windows and macOS with the selected MAUI workload and Xcode image.
6. Inspect direct and transitive outdated packages and known vulnerabilities before merging.
