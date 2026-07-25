using System.Collections.Concurrent;
using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// Stores the evidence supplied with an incident submission so that the
/// evidence collection activity can return it deterministically. In the local
/// milestone all evidence is mock data submitted together with the incident.
/// </summary>
public sealed class InMemoryEvidenceStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<IncidentEvidence>> _evidenceByIncident =
        new(StringComparer.Ordinal);

    /// <summary>Stores the evidence for an incident, replacing any prior evidence.</summary>
    /// <param name="incidentId">The incident the evidence belongs to.</param>
    /// <param name="evidence">The evidence items.</param>
    public void Store(string incidentId, IReadOnlyList<IncidentEvidence> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);
        ArgumentNullException.ThrowIfNull(evidence);
        _evidenceByIncident[incidentId] = evidence;
    }

    /// <summary>Gets the stored evidence for an incident.</summary>
    /// <param name="incidentId">The incident identifier.</param>
    /// <returns>The stored evidence, or an empty list when none was supplied.</returns>
    public IReadOnlyList<IncidentEvidence> Get(string incidentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);
        return _evidenceByIncident.TryGetValue(incidentId, out IReadOnlyList<IncidentEvidence>? evidence)
            ? evidence
            : [];
    }
}
