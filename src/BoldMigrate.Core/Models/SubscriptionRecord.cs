using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Inventory record of a report subscription discovered on the source server.
/// </summary>
public class SubscriptionRecord
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("runId")]
    public Guid RunId { get; set; }

    [JsonPropertyName("reportPath")]
    public string ReportPath { get; set; } = string.Empty;

    [JsonPropertyName("subscriptionType")]
    public string SubscriptionType { get; set; } = string.Empty;

    [JsonPropertyName("deliveryMethod")]
    public string DeliveryMethod { get; set; } = string.Empty;

    [JsonPropertyName("schedule")]
    public string Schedule { get; set; } = string.Empty;

    [JsonPropertyName("recipientCount")]
    public int RecipientCount { get; set; }

    [JsonPropertyName("migrationPlan")]
    public string? MigrationPlan { get; set; }
}
