using System.Text.Json.Serialization;
using BoldMigrate.Core.Enums;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Extends a CatalogItem of type Report with migration analysis, classification, and execution state.
/// </summary>
public class ReportRecord
{
    [JsonPropertyName("catalogItemId")]
    public Guid CatalogItemId { get; set; }

    [JsonPropertyName("bucket")]
    public ReportBucket Bucket { get; set; } = ReportBucket.Clean;

    [JsonPropertyName("blockReasonCode")]
    public string? BlockReasonCode { get; set; }

    [JsonPropertyName("state")]
    public ReportState State { get; set; } = ReportState.Discovered;

    [JsonPropertyName("rdlSchemaVersion")]
    public string? RdlSchemaVersion { get; set; }

    /// <summary>
    /// JSON array of source data source references found and destination rebound identifiers.
    /// </summary>
    [JsonPropertyName("dataSourceRefs")]
    public string? DataSourceRefs { get; set; }

    /// <summary>
    /// JSON array of source shared dataset references and destination mapping decisions.
    /// </summary>
    [JsonPropertyName("dataSetRefs")]
    public string? DataSetRefs { get; set; }

    [JsonPropertyName("parameterCount")]
    public int ParameterCount { get; set; }

    [JsonPropertyName("uploadAttempts")]
    public int UploadAttempts { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    [JsonPropertyName("excludedReason")]
    public string? ExcludedReason { get; set; }

    /// <summary>
    /// When the staged definition was last saved from the local Report Designer (Step 5).
    /// A locally edited report is never re-staged from the source server.
    /// </summary>
    [JsonPropertyName("localEditUtc")]
    public DateTime? LocalEditUtc { get; set; }

    /// <summary>
    /// True when a local edit has not yet been sent to Bold Reports Server.
    /// </summary>
    [JsonPropertyName("localEditPendingUpload")]
    public bool LocalEditPendingUpload { get; set; }

    [JsonIgnore]
    public CatalogItem? CatalogItem { get; set; }
}
