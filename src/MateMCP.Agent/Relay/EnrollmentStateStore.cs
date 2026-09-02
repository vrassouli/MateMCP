using System.Text.Json;
using System.Text.Json.Nodes;
using MateMCP.Agent.Configuration;

namespace MateMCP.Agent.Relay;

public sealed class EnrollmentStateStore
{
    private readonly string _configurationPath;
    private readonly object _gate = new();

    public EnrollmentStateStore(string configurationPath)
    {
        _configurationPath = configurationPath;
    }

    public void MarkEnrolled(string agentId)
        => Update(relay =>
        {
            relay["DeviceId"] = agentId;
            relay["EnrollmentSuppressed"] = false;
        });

    public void MarkSignedOut()
        => Update(relay =>
        {
            relay["DeviceId"] = null;
            relay["EnrollmentSuppressed"] = true;
        });

    public void EnableEnrollment()
        => Update(relay => relay["EnrollmentSuppressed"] = false);

    private void Update(Action<JsonObject> update)
    {
        lock (_gate)
        {
            var root = JsonNode.Parse(File.ReadAllText(_configurationPath))?.AsObject()
                ?? throw new InvalidOperationException("MateMCP Agent configuration is invalid.");
            var mate = root["Mate"]?.AsObject()
                ?? throw new InvalidOperationException("MateMCP Agent configuration does not contain the Mate section.");
            var relay = mate["Relay"]?.AsObject()
                ?? throw new InvalidOperationException("MateMCP Agent configuration does not contain the Relay section.");

            update(relay);
            File.WriteAllText(_configurationPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            ConfigurationBootstrap.TryRestrictPermissions(_configurationPath);
        }
    }
}
