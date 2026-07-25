namespace AzureAgenticOps.ScribeService;

/// <summary>
/// Options for the Scribe host. The logical Pub/Sub component and topic names
/// stay stable across environments and match the publisher configuration.
/// </summary>
public sealed class ScribeOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Scribe";

    /// <summary>Gets or sets the logical Pub/Sub component name.</summary>
    public string PubSubName { get; set; } = "incident-pubsub";

    /// <summary>Gets or sets the lifecycle event topic name.</summary>
    public string TopicName { get; set; } = "incident-lifecycle";

    /// <summary>Gets or sets the route lifecycle events are delivered to.</summary>
    public string SubscriptionRoute { get; set; } = "/events/incident-lifecycle";
}
