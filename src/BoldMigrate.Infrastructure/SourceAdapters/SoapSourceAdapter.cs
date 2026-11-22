using System.Net;
using System.Text;
using System.Xml.Linq;
using BoldMigrate.Core;
using BoldMigrate.Core.Dto;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;

namespace BoldMigrate.Infrastructure.SourceAdapters;

public class SoapSourceAdapter : ISourceCatalog
{
    private readonly HttpClient _httpClient;
    private readonly string _endpointUrl;

    public SoapSourceAdapter(string baseUrl, NetworkCredential? credential = null)
    {
        var cleanBase = BoldMigrate.Core.Common.HostResolver.Resolve(baseUrl).TrimEnd('/');
        if (cleanBase.EndsWith("/Reports", StringComparison.OrdinalIgnoreCase))
        {
            cleanBase = cleanBase.Substring(0, cleanBase.Length - "/Reports".Length) + "/ReportServer";
        }
        _endpointUrl = $"{cleanBase}/ReportService2010.asmx";

        var handler = new HttpClientHandler();
        if (credential != null)
        {
            handler.Credentials = credential;
            handler.PreAuthenticate = true;
        }
        else
        {
            handler.UseDefaultCredentials = true;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(100)
        };
    }

    public async Task<SourceProbeResult> ProbeAsync(string baseUrl, NetworkCredential? credential, CancellationToken ct = default)
    {
        try
        {
            var cleanBase = BoldMigrate.Core.Common.HostResolver.Resolve(baseUrl ?? _endpointUrl).TrimEnd('/');
            var wsdlUrl = $"{cleanBase}/ReportService2010.asmx?wsdl";
            var res = await _httpClient.GetAsync(wsdlUrl, ct);

            if (res.IsSuccessStatusCode)
            {
                return new SourceProbeResult
                {
                    Success = true,
                    SourceVersion = "SSRS 2012-2016 (SOAP)",
                    SourceApi = SourceApi.Soap2010,
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

            return new SourceProbeResult
            {
                Success = false,
                ErrorCode = ErrorCatalogue.Src001,
                ErrorMessage = $"HTTP {res.StatusCode} from SOAP endpoint."
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
        var items = await ListItemsAsync(ct);
        return items.Where(i => i.ItemType == ItemType.Folder).Select(i => i.Path).ToList();
    }

    public async Task<IReadOnlyList<CatalogItemDto>> ListItemsAsync(CancellationToken ct = default)
    {
        var soapEnvelope = @"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <soap:Body>
    <ListChildren xmlns=""http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer"">
      <ItemPath>/</ItemPath>
      <Recursive>true</Recursive>
    </ListChildren>
  </soap:Body>
</soap:Envelope>";

        var items = new List<CatalogItemDto>();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
            {
                Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml")
            };
            req.Headers.Add("SOAPAction", "\"http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer/ListChildren\"");

            var res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return items;

            var xml = await res.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer";

            foreach (var elem in doc.Descendants(ns + "CatalogItem"))
            {
                var name = elem.Element(ns + "Name")?.Value ?? "";
                var path = elem.Element(ns + "Path")?.Value ?? "";
                var typeName = elem.Element(ns + "TypeName")?.Value ?? "";
                var size = long.TryParse(elem.Element(ns + "Size")?.Value, out var sz) ? sz : 0;
                var mod = DateTime.TryParse(elem.Element(ns + "ModifiedDate")?.Value, out var dt) ? dt : DateTime.UtcNow;

                var itemType = typeName switch
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
                    Id = path,
                    Name = name,
                    Path = path,
                    ParentPath = string.IsNullOrEmpty(parentPath) ? "/" : parentPath,
                    ItemType = itemType,
                    SizeBytes = size,
                    ModifiedUtc = mod
                });
            }
        }
        catch
        {
            // fallback
        }
        return items;
    }

    public async Task<byte[]> GetDefinitionAsync(string itemId, CancellationToken ct = default)
    {
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <soap:Body>
    <GetItemDefinition xmlns=""http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer"">
      <ItemPath>{System.Security.SecurityElement.Escape(itemId)}</ItemPath>
    </GetItemDefinition>
  </soap:Body>
</soap:Envelope>";

        var req = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
        {
            Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml")
        };
        req.Headers.Add("SOAPAction", "\"http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer/GetItemDefinition\"");

        var res = await _httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var xml = await res.Content.ReadAsStringAsync(ct);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer";

        var defBase64 = doc.Descendants(ns + "Definition").FirstOrDefault()?.Value;
        if (!string.IsNullOrEmpty(defBase64))
        {
            return Convert.FromBase64String(defBase64);
        }

        return Array.Empty<byte>();
    }

    public Task<IReadOnlyList<DataSourceRef>> GetDataSourcesForItemAsync(string itemId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<DataSourceRef>>(Array.Empty<DataSourceRef>());
    }

    public Task<IReadOnlyList<ParameterDef>> GetParametersAsync(string reportId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ParameterDef>>(Array.Empty<ParameterDef>());
    }

    public Task<IReadOnlyList<PolicyEntry>> GetPoliciesAsync(string itemId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PolicyEntry>>(Array.Empty<PolicyEntry>());
    }

    public Task<IReadOnlyList<SubscriptionInfo>> ListSubscriptionsAsync(string reportPath, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<SubscriptionInfo>>(Array.Empty<SubscriptionInfo>());
    }

    public async Task<byte[]> RenderAsync(string reportId, string format, IDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var root = _endpointUrl.Replace("/ReportService2010.asmx", "");
        var query = string.Join("&", parameters.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{root}/ReportServer?/{reportId}&rs:Format={format}&rs:Command=Render&{query}";
        var res = await _httpClient.GetAsync(url, ct);
        if (res.IsSuccessStatusCode)
        {
            return await res.Content.ReadAsByteArrayAsync(ct);
        }
        return Array.Empty<byte>();
    }
}
