using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BoldMigrate.Core;
using BoldMigrate.Core.Dto;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;

namespace BoldMigrate.Infrastructure.SourceAdapters;

public class RestV2SourceAdapter : ISourceCatalog, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SocketsHttpHandler _handler;
    private readonly string _baseUrl;

    public RestV2SourceAdapter(string baseUrl, NetworkCredential? credential = null, HttpMessageHandler? handlerOverride = null)
    {
        var clean = BoldMigrate.Core.Common.HostResolver.Resolve(baseUrl).TrimEnd('/');
        if (clean.EndsWith("/ReportServer", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(0, clean.Length - "/ReportServer".Length) + "/Reports";
        }
        _baseUrl = clean;

        _handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            PreAuthenticate = true,
            Credentials = credential ?? CredentialCache.DefaultCredentials
        };

        _httpClient = handlerOverride != null
            ? new HttpClient(handlerOverride, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(100) }
            : new HttpClient(_handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
        GC.SuppressFinalize(this);
    }

    private string GetUrl(string relativePath) => $"{_baseUrl}/{relativePath.TrimStart('/')}";

    public async Task<SourceProbeResult> ProbeAsync(string baseUrl, NetworkCredential? credential, CancellationToken ct = default)
    {
        try
        {
            var cleanBase = BoldMigrate.Core.Common.HostResolver.Resolve(baseUrl ?? _baseUrl).TrimEnd('/');
            if (cleanBase.EndsWith("/ReportServer", StringComparison.OrdinalIgnoreCase))
            {
                cleanBase = cleanBase.Substring(0, cleanBase.Length - "/ReportServer".Length) + "/Reports";
            }
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{cleanBase}/api/v2.0/$metadata");
            var res = await _httpClient.SendAsync(req, ct);

            if (res.IsSuccessStatusCode)
            {
                return new SourceProbeResult
                {
                    Success = true,
                    SourceVersion = "SSRS 2017+ / PBIRS",
                    SourceApi = SourceApi.RestV2,
                    AuthMode = credential != null ? "Windows Integrated" : "Default Service Account"
                };
            }

            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new SourceProbeResult
                {
                    Success = false,
                    ErrorCode = ErrorCatalogue.Src002,
                    ErrorMessage = ErrorCatalogue.FormatMessage(ErrorCatalogue.Src002)
                };
            }

            if (res.StatusCode == HttpStatusCode.Forbidden)
            {
                return new SourceProbeResult
                {
                    Success = false,
                    ErrorCode = ErrorCatalogue.Src003,
                    ErrorMessage = ErrorCatalogue.FormatMessage(ErrorCatalogue.Src003)
                };
            }

            return new SourceProbeResult
            {
                Success = false,
                ErrorCode = ErrorCatalogue.Src001,
                ErrorMessage = $"HTTP {res.StatusCode} from REST v2.0 endpoint."
            };
        }
        catch (Exception ex)
        {
            return new SourceProbeResult
            {
                Success = false,
                ErrorCode = ErrorCatalogue.Src001,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await _httpClient.GetAsync(GetUrl("api/v2.0/Folders"), ct);
            if (!res.IsSuccessStatusCode) return Array.Empty<string>();

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("value", out var valueElem) && valueElem.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var f in valueElem.EnumerateArray())
                {
                    if (f.TryGetProperty("Path", out var path))
                    {
                        list.Add(path.GetString() ?? "");
                    }
                }
                return list;
            }
        }
        catch
        {
            // fallback
        }
        return Array.Empty<string>();
    }

    public async Task<IReadOnlyList<CatalogItemDto>> ListItemsAsync(CancellationToken ct = default)
    {
        var items = new List<CatalogItemDto>();
        var res = await _httpClient.GetAsync(GetUrl("api/v2.0/CatalogItems"), ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"SSRS REST v2.0 CatalogItems returned HTTP {(int)res.StatusCode} ({res.ReasonPhrase}).");
        }

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.TryGetProperty("value", out var valueElem) && valueElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in valueElem.EnumerateArray())
            {
                var id = item.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? "" : "";
                var name = item.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var path = item.TryGetProperty("Path", out var pathProp) ? pathProp.GetString() ?? "" : "";
                var typeStr = item.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                var size = item.TryGetProperty("Size", out var sizeProp) && sizeProp.TryGetInt64(out var s) ? s : 0;
                var modDate = item.TryGetProperty("ModifiedDate", out var mdProp) && mdProp.TryGetDateTime(out var md) ? md : DateTime.UtcNow;

                var itemType = typeStr switch
                {
                    "Folder" => ItemType.Folder,
                    "Report" => ItemType.Report,
                    "DataSource" => ItemType.DataSource,
                    "DataSet" => ItemType.DataSet,
                    "Resource" => ItemType.Resource,
                    _ => ItemType.Unsupported
                };

                var parentPath = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

                items.Add(new CatalogItemDto
                {
                    Id = id,
                    Name = name,
                    Path = path,
                    ParentPath = string.IsNullOrEmpty(parentPath) ? "/" : parentPath,
                    ItemType = itemType,
                    SizeBytes = size,
                    ModifiedUtc = modDate
                });
            }
        }

        return items;
    }

    public async Task<byte[]> GetDefinitionAsync(string itemId, CancellationToken ct = default)
    {
        var res = await _httpClient.GetAsync(GetUrl($"api/v2.0/CatalogItems({itemId})/Content/$value"), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<IReadOnlyList<DataSourceRef>> GetDataSourcesForItemAsync(string itemId, CancellationToken ct = default)
    {
        var list = new List<DataSourceRef>();
        try
        {
            var res = await _httpClient.GetAsync(GetUrl($"api/v2.0/Reports({itemId})/DataSources"), ct);
            if (!res.IsSuccessStatusCode) return list;

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Array)
            {
                foreach (var ds in val.EnumerateArray())
                {
                    var name = ds.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    var refPath = ds.TryGetProperty("Path", out var p) ? p.GetString() : null;
                    var isEmbedded = ds.TryGetProperty("IsEmbedded", out var ie) && ie.GetBoolean();

                    list.Add(new DataSourceRef
                    {
                        Name = name,
                        ReferencePath = refPath,
                        IsEmbedded = isEmbedded
                    });
                }
            }
        }
        catch
        {
            // fallback
        }
        return list;
    }

    public async Task<IReadOnlyList<ParameterDef>> GetParametersAsync(string reportId, CancellationToken ct = default)
    {
        var list = new List<ParameterDef>();
        try
        {
            var res = await _httpClient.GetAsync(GetUrl($"api/v2.0/Reports({reportId})/ParameterDefinitions"), ct);
            if (!res.IsSuccessStatusCode) return list;

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in val.EnumerateArray())
                {
                    var name = p.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    var dt = p.TryGetProperty("DataType", out var d) ? d.GetString() : "String";
                    var prompt = p.TryGetProperty("Prompt", out var pr) ? pr.GetString() : "";
                    var nullable = p.TryGetProperty("Nullable", out var nu) && nu.GetBoolean();
                    var mv = p.TryGetProperty("MultiValue", out var m) && m.GetBoolean();
                    var defVals = new List<string>();
                    if (p.TryGetProperty("DefaultValues", out var dv) && dv.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in dv.EnumerateArray())
                        {
                            var s = v.GetString();
                            if (s != null) defVals.Add(s);
                        }
                    }

                    list.Add(new ParameterDef
                    {
                        Name = name,
                        DataType = dt,
                        Prompt = prompt,
                        Nullable = nullable,
                        MultiValue = mv,
                        DefaultValues = defVals
                    });
                }
            }
        }
        catch
        {
            // fallback
        }
        return list;
    }

    public async Task<IReadOnlyList<PolicyEntry>> GetPoliciesAsync(string itemId, CancellationToken ct = default)
    {
        var list = new List<PolicyEntry>();
        try
        {
            var res = await _httpClient.GetAsync(GetUrl($"api/v2.0/CatalogItems({itemId})/Policies"), ct);
            if (!res.IsSuccessStatusCode) return list;

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("Policies", out var val) && val.ValueKind == JsonValueKind.Array)
            {
                foreach (var pol in val.EnumerateArray())
                {
                    var principal = pol.TryGetProperty("GroupUserName", out var g) ? g.GetString() ?? "" : "";
                    var roles = new List<string>();
                    if (pol.TryGetProperty("Roles", out var r) && r.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var role in r.EnumerateArray())
                        {
                            var rn = role.TryGetProperty("Name", out var rnn) ? rnn.GetString() : null;
                            if (rn != null) roles.Add(rn);
                        }
                    }

                    list.Add(new PolicyEntry
                    {
                        Principal = principal,
                        Roles = roles
                    });
                }
            }
        }
        catch
        {
            // fallback
        }
        return list;
    }

    public async Task<IReadOnlyList<SubscriptionInfo>> ListSubscriptionsAsync(string reportPath, CancellationToken ct = default)
    {
        var list = new List<SubscriptionInfo>();
        try
        {
            var res = await _httpClient.GetAsync(GetUrl("api/v2.0/Subscriptions"), ct);
            if (!res.IsSuccessStatusCode) return list;

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Array)
            {
                foreach (var sub in val.EnumerateArray())
                {
                    var path = sub.TryGetProperty("Report", out var rp) ? rp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(reportPath) && !string.Equals(path, reportPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var id = sub.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? "" : "";
                    var desc = sub.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : "";
                    var delivery = sub.TryGetProperty("DeliveryExtension", out var de) ? de.GetString() ?? "Email" : "Email";

                    list.Add(new SubscriptionInfo
                    {
                        Id = id,
                        ReportPath = path,
                        Description = desc,
                        DeliveryMethod = delivery,
                        Schedule = "Scheduled"
                    });
                }
            }
        }
        catch
        {
            // fallback
        }
        return list;
    }

    /// <summary>
    /// Renders a report through SSRS URL access. <paramref name="reportPathOrId"/> must be the
    /// report's catalog PATH (for example "/Sales/Monthly") — URL access does not accept item
    /// GUIDs. Throws when the server will not render, so callers can distinguish a failed
    /// render from a report that legitimately returned no rows.
    /// </summary>
    public async Task<byte[]> RenderAsync(string reportPathOrId, string format, IDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var query = string.Join("&", parameters.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        // URL access lives on the /ReportServer endpoint, not the /Reports web portal.
        var trimmed = _baseUrl.TrimEnd('/');
        string serverBase;
        if (trimmed.EndsWith("/Reports", StringComparison.OrdinalIgnoreCase))
        {
            serverBase = trimmed[..^"/Reports".Length] + "/ReportServer";
        }
        else if (trimmed.EndsWith("/ReportServer", StringComparison.OrdinalIgnoreCase))
        {
            serverBase = trimmed;
        }
        else
        {
            serverBase = trimmed + "/ReportServer";
        }

        var path = "/" + reportPathOrId.TrimStart('/');
        var url = $"{serverBase}?{Uri.EscapeDataString(path)}&rs:Format={Uri.EscapeDataString(format)}&rs:Command=Render";
        if (!string.IsNullOrEmpty(query)) url += "&" + query;

        var res = await _httpClient.GetAsync(url, ct);
        if (res.IsSuccessStatusCode)
        {
            return await res.Content.ReadAsByteArrayAsync(ct);
        }

        var body = "";
        try { body = await res.Content.ReadAsStringAsync(ct); } catch { }
        var detail = body.Length > 300 ? body[..300] : body;
        throw new HttpRequestException(
            $"SSRS refused to render '{path}' as {format} (HTTP {(int)res.StatusCode}). {detail}".Trim());
    }
}
