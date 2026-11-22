using System.Net;
using BoldMigrate.Core.Dto;

namespace BoldMigrate.Core.Interfaces;

/// <summary>
/// Unified abstraction for communicating with the source SSRS server, implemented via REST v2.0 or SOAP.
/// The extraction layer reads the source server and writes nothing to it.
/// </summary>
public interface ISourceCatalog
{
    /// <summary>
    /// Probes the source server to detect version, supported API, and authentication status.
    /// </summary>
    Task<SourceProbeResult> ProbeAsync(string baseUrl, NetworkCredential? credential, CancellationToken ct = default);

    /// <summary>
    /// Lists all folder paths present on the source server.
    /// </summary>
    Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all catalog items (folders, reports, data sources, datasets, resources) with metadata.
    /// </summary>
    Task<IReadOnlyList<CatalogItemDto>> ListItemsAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the raw definition bytes of a report, dataset, or data source.
    /// </summary>
    Task<byte[]> GetDefinitionAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the data source references used by a specific report.
    /// </summary>
    Task<IReadOnlyList<DataSourceRef>> GetDataSourcesForItemAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the parameter definitions (including defaults and valid values) for a report.
    /// </summary>
    Task<IReadOnlyList<ParameterDef>> GetParametersAsync(string reportId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the security role assignments / policies configured on an item.
    /// </summary>
    Task<IReadOnlyList<PolicyEntry>> GetPoliciesAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves subscriptions defined for a specific report.
    /// </summary>
    Task<IReadOnlyList<SubscriptionInfo>> ListSubscriptionsAsync(string reportPath, CancellationToken ct = default);

    /// <summary>
    /// Renders a report on the source server into a specific format for verification.
    /// </summary>
    Task<byte[]> RenderAsync(string reportId, string format, IDictionary<string, string> parameters, CancellationToken ct = default);
}
