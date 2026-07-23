using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.Tier1SreAgent;

/// <summary>
/// A single knowledge entry: a runbook, a known pattern, or a prior incident summary.
/// Entries are version-controlled fixtures; no vector database is used.
/// </summary>
/// <param name="EntryId">The unique identifier of the entry, used as the evidence source identifier.</param>
/// <param name="EntryType">The kind of entry, for example "runbook" or "prior-incident".</param>
/// <param name="Title">A short human-readable title.</param>
/// <param name="Keywords">Keywords used for deterministic matching.</param>
/// <param name="Content">The entry content.</param>
/// <param name="RelatedActionTypes">Allow-listed action types related to this entry.</param>
public sealed record KnowledgeEntry(
    string EntryId,
    string EntryType,
    string Title,
    IReadOnlyList<string> Keywords,
    string Content,
    IReadOnlyList<string> RelatedActionTypes);

/// <summary>
/// The version-controlled knowledge base document loaded from the <c>knowledge</c> directory.
/// </summary>
/// <param name="SchemaVersion">The fixture schema version.</param>
/// <param name="Entries">The knowledge entries.</param>
public sealed record KnowledgeBase(
    string SchemaVersion,
    IReadOnlyList<KnowledgeEntry> Entries)
{
    /// <summary>Loads a knowledge base from a JSON fixture file.</summary>
    /// <param name="path">The fixture file path.</param>
    /// <returns>The loaded knowledge base.</returns>
    public static KnowledgeBase LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ContractSerialization.Deserialize<KnowledgeBase>(File.ReadAllText(path));
    }
}

/// <summary>
/// A single Insights retrieval hit with its source identifier and match score.
/// </summary>
/// <param name="Entry">The matched knowledge entry.</param>
/// <param name="MatchedKeywords">The keywords that matched the query.</param>
public sealed record InsightsHit(
    KnowledgeEntry Entry,
    IReadOnlyList<string> MatchedKeywords);

/// <summary>
/// The structured result of an Insights retrieval.
/// </summary>
/// <param name="Hits">The matched entries ordered by descending relevance.</param>
public sealed record InsightsResult(
    IReadOnlyList<InsightsHit> Hits);

/// <summary>
/// The Insights capability: deterministic keyword and metadata search over
/// version-controlled runbooks, known patterns, and prior incident summaries.
/// Insights is a Tier 1 sub-capability. It returns evidence with source
/// identifiers; it never decides whether an action is executed.
/// </summary>
public sealed class InsightsCapability
{
    private readonly KnowledgeBase _knowledgeBase;

    /// <summary>Initializes the capability over the supplied knowledge base.</summary>
    /// <param name="knowledgeBase">The knowledge base to search.</param>
    public InsightsCapability(KnowledgeBase knowledgeBase)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        _knowledgeBase = knowledgeBase;
    }

    /// <summary>
    /// Searches the knowledge base using the incident text and evidence content.
    /// Matching is deterministic: an entry matches when at least one of its
    /// keywords appears in the combined search text (ordinal, case-insensitive).
    /// </summary>
    /// <param name="incident">The incident under investigation.</param>
    /// <param name="evidence">The evidence collected for the incident.</param>
    /// <returns>The structured retrieval result ordered by descending keyword match count.</returns>
    public InsightsResult Search(Incident incident, IReadOnlyList<IncidentEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(evidence);

        string searchText = string.Join(
            '\n',
            new[] { incident.Title, incident.Description }
                .Concat(evidence.Select(item => item.Content)));

        var hits = new List<InsightsHit>();
        foreach (KnowledgeEntry entry in _knowledgeBase.Entries)
        {
            string[] matched = entry.Keywords
                .Where(keyword => searchText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matched.Length > 0)
            {
                hits.Add(new InsightsHit(entry, matched));
            }
        }

        return new InsightsResult(
            hits.OrderByDescending(hit => hit.MatchedKeywords.Count)
                .ThenBy(hit => hit.Entry.EntryId, StringComparer.Ordinal)
                .ToArray());
    }
}
