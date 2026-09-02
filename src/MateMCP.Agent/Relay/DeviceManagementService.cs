using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Security;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Relay;

public sealed class DeviceManagementService(
    IOptionsMonitor<MateOptions> options,
    IHttpClientFactory clients,
    AgentCredentialStore credentials,
    EnrollmentStateStore enrollmentState,
    ILogger<DeviceManagementService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DeviceManagementStatus> GetStatusAsync(CancellationToken ct)
    {
        var current = options.CurrentValue;
        var currentId = current.Relay.DeviceId;
        if (string.IsNullOrWhiteSpace(currentId))
            return new DeviceManagementStatus(false, current.Relay.EnrollmentSuppressed, null, []);

        var credential = await credentials.GetAsync(currentId, ct);
        if (string.IsNullOrWhiteSpace(credential))
            return new DeviceManagementStatus(false, current.Relay.EnrollmentSuppressed, currentId, []);

        using var client = CreateClient(current, credential);
        var requestPath = $"api/agents/{Uri.EscapeDataString(currentId)}/devices";
        try
        {
            using var response = await client.GetAsync(requestPath, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new DeviceManagementStatus(false, current.Relay.EnrollmentSuppressed, currentId, [],
                    "The control plane rejected this device credential. Remove this device and enroll it again.");

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadResponseDetailAsync(response, ct);
                var message = $"Control-plane device lookup {requestPath} returned {(int)response.StatusCode} ({response.ReasonPhrase}){detail}.";
                logger.LogWarning("{Message}", message);
                return new DeviceManagementStatus(true, current.Relay.EnrollmentSuppressed, currentId, [], message);
            }

            var devices = await response.Content.ReadFromJsonAsync<List<ManagedDevice>>(Json, ct) ?? [];
            return new DeviceManagementStatus(true, current.Relay.EnrollmentSuppressed, currentId, devices);
        }
        catch (HttpRequestException ex)
        {
            var message = $"Could not reach the MateMCP control plane for {requestPath}: {ex.Message}";
            logger.LogWarning(ex, "Device management lookup failed for {RequestPath}.", requestPath);
            return new DeviceManagementStatus(true, current.Relay.EnrollmentSuppressed, currentId, [], message);
        }
    }

    public async Task SignOutCurrentAsync(CancellationToken ct)
    {
        var current = options.CurrentValue;
        var currentId = current.Relay.DeviceId;
        if (string.IsNullOrWhiteSpace(currentId))
        {
            enrollmentState.MarkSignedOut();
            return;
        }

        var credential = await credentials.GetAsync(currentId, ct);
        if (!string.IsNullOrWhiteSpace(credential))
        {
            using var client = CreateClient(current, credential);
            using var response = await client.DeleteAsync(
                $"api/agents/{Uri.EscapeDataString(currentId)}/devices/{Uri.EscapeDataString(currentId)}", ct);

            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.NotFound))
                response.EnsureSuccessStatusCode();
        }

        // Clear the configured identity before removing the secure-store value. If secure-store
        // deletion fails, the orphaned credential is no longer referenced and the server identity
        // is already revoked.
        enrollmentState.MarkSignedOut();
        await credentials.DeleteAsync(currentId, ct);
    }

    public async Task RevokeOtherAsync(string targetDeviceId, CancellationToken ct)
    {
        var current = options.CurrentValue;
        var currentId = current.Relay.DeviceId ?? throw new InvalidOperationException("This Agent is not enrolled.");
        if (string.Equals(currentId, targetDeviceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Use the current-device sign-out operation to remove this Agent.");

        var credential = await credentials.GetAsync(currentId, ct)
            ?? throw new InvalidOperationException("This Agent has no relay credential. Re-enroll it before managing other devices.");

        using var client = CreateClient(current, credential);
        using var response = await client.DeleteAsync(
            $"api/agents/{Uri.EscapeDataString(currentId)}/devices/{Uri.EscapeDataString(targetDeviceId)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public void EnableEnrollment()
    {
        var current = options.CurrentValue;
        if (!string.IsNullOrWhiteSpace(current.Relay.DeviceId))
            throw new InvalidOperationException("Remove the current device before starting a new enrollment.");
        enrollmentState.EnableEnrollment();
    }

    private HttpClient CreateClient(MateOptions current, string credential)
    {
        var client = clients.CreateClient();
        client.BaseAddress = new Uri(current.Relay.ControlPlaneUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private static async Task<string> ReadResponseDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (text.Length == 0) return string.Empty;
            if (text.Length > 300) text = text[..300];
            return $": {text.Replace('\r', ' ').Replace('\n', ' ')}";
        }
        catch
        {
            return string.Empty;
        }
    }
}

public sealed record DeviceManagementStatus(
    bool Enrolled,
    bool EnrollmentSuppressed,
    string? CurrentDeviceId,
    IReadOnlyList<ManagedDevice> Devices,
    string? UpstreamError = null);

public sealed record ManagedDevice(
    string Id,
    string Name,
    string Platform,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    string McpUrl,
    bool IsCurrent);
