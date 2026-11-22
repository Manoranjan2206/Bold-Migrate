using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Dto;

/// <summary>
/// Represents subscription information for a report on the source server.
/// </summary>
public class SubscriptionInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("reportPath")]
    public string ReportPath { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("deliveryMethod")]
    public string DeliveryMethod { get; set; } = string.Empty;

    [JsonPropertyName("schedule")]
    public string Schedule { get; set; } = string.Empty;

    [JsonPropertyName("recipientCount")]
    public int RecipientCount { get; set; }

    [JsonPropertyName("extensionSettings")]
    public IReadOnlyDictionary<string, string> ExtensionSettings { get; set; } = new Dictionary<string, string>();
}
