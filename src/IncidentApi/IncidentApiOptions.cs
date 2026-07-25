namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// Options for the IncidentApi host. All values have safe local-demo defaults
/// and can be overridden through configuration under the <c>IncidentApi</c> section.
/// </summary>
public sealed class IncidentApiOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "IncidentApi";

    /// <summary>
    /// Gets or sets the path to the prompts directory, relative to the content
    /// root when not absolute.
    /// </summary>
    public string PromptsRoot { get; set; } = "prompts";

    /// <summary>
    /// Gets or sets the path to the knowledge base file, relative to the content
    /// root when not absolute.
    /// </summary>
    public string KnowledgeBasePath { get; set; } = Path.Combine("knowledge", "knowledge-base.json");

    /// <summary>
    /// Gets or sets the approval timeout in seconds. Defaults to 15 minutes.
    /// </summary>
    public int ApprovalTimeoutSeconds { get; set; } = 900;
}
