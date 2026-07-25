using AzureAgenticOps.Contracts;
using AzureAgenticOps.Safety;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// Builds deterministic demo remediation actions targeting the incident's
/// primary affected service in the demo namespace. The builder is shared by the
/// deterministic stub model client and the rule fast path so both produce
/// identical, policy-checkable actions.
/// </summary>
public static class DemoRemediationActionBuilder
{
    /// <summary>Builds a remediation action for the incident's primary service.</summary>
    /// <param name="incident">The incident under remediation.</param>
    /// <param name="actionType">The predefined action type.</param>
    /// <param name="origin">The origin marker embedded in the idempotency key, for example "rule" or "tier1".</param>
    /// <param name="maxAttempts">The maximum number of executions, floored at 1.</param>
    /// <returns>The deterministic remediation action.</returns>
    public static RemediationAction Build(Incident incident, string actionType, string origin, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        string service = PrimaryService(incident);
        return new RemediationAction(
            actionType,
            new ActionTarget("demo", "deployment", service),
            Parameters: new Dictionary<string, string> { ["service"] = service },
            IdempotencyKey: SanitizeKey($"{incident.IncidentId}-{origin}-{actionType}"),
            MaxExecutionCount: Math.Max(1, maxAttempts));
    }

    /// <summary>Gets the incident's primary affected service.</summary>
    /// <param name="incident">The incident under remediation.</param>
    /// <returns>The first affected service, or a placeholder when none is listed.</returns>
    public static string PrimaryService(Incident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);
        return incident.AffectedServices.Count > 0 ? incident.AffectedServices[0] : "unknown-service";
    }

    private static string SanitizeKey(string key)
    {
        char[] characters = key.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            char character = characters[index];
            bool allowed = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.';
            if (!allowed)
            {
                characters[index] = '-';
            }
        }

        string sanitized = new(characters);
        return sanitized.Length > IdempotencyKeyValidator.MaxLength
            ? sanitized[..IdempotencyKeyValidator.MaxLength]
            : sanitized;
    }
}
