using System.Net;
using System.Text;
using System.Xml.Linq;
using BoldMigrate.Core;
using BoldMigrate.Core.Common;
using BoldMigrate.Core.Dto;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;

namespace BoldMigrate.Infrastructure.SourceAdapters;

public class SoapSourceAdapter : ISourceCatalog, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SocketsHttpHandler _handler;
    private readonly string _endpointUrl;

    public SoapSourceAdapter(string baseUrl, NetworkCredential? credential = null, HttpMessageHandler? handlerOverride = null)
    {
        var cleanBase = NormalizeReportServerBase(baseUrl);
        _endpointUrl = $"{cleanBase}/ReportService2010.asmx";

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
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string NormalizeReportServerBase(string url)
    {
        var resolved = BoldMigrate.Core.Common.HostResolver.Resolve(url).TrimEnd('/');
        if (Uri.TryCreate(resolved, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/ReportService2010.asmx", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^"/ReportService2010.asmx".Length];
            }

            // Convert SSRS web portal URLs (/Reports or /Reports/Pages/...) to /ReportServer
            var reportsNormalized = System.Text.RegularExpressions.Regex.Replace(
                path,
                @"/Reports(?:/.*)?$",
                "/ReportServer",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!reportsNormalized.Contains("/ReportServer", StringComparison.OrdinalIgnoreCase))
            {
                reportsNormalized += "/ReportServer";
            }

            return $"{uri.Scheme}://{uri.Authority}{reportsNormalized}";
        }

        return resolved.EndsWith("/ReportServer", StringComparison.OrdinalIgnoreCase)
            ? resolved
            : resolved + "/ReportServer";
    }

    public async Task<SourceProbeResult> ProbeAsync(string baseUrl, NetworkCredential? credential, CancellationToken ct = default)
    {
        try
        {
            var cleanBase = NormalizeReportServerBase(baseUrl ?? _endpointUrl);
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
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
        {
            Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml")
        };
        req.Headers.Add("SOAPAction", "\"http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer/ListChildren\"");

        var res = await _httpClient.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"SSRS SOAP ListChildren returned HTTP {(int)res.StatusCode} ({res.ReasonPhrase}).");
        }

        var xml = await res.Content.ReadAsStringAsync(ct);
        var doc = SafeXml.Parse(xml);
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
        var doc = SafeXml.Parse(xml);
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
        var serverBase = _endpointUrl.EndsWith("/ReportService2010.asmx", StringComparison.OrdinalIgnoreCase)
            ? _endpointUrl[..^"/ReportService2010.asmx".Length]
            : _endpointUrl.TrimEnd('/');

        var path = "/" + (reportId ?? string.Empty).TrimStart('/');
        var query = SsrsUrlAccess.BuildQuery(parameters);
        var url = $"{serverBase}?{Uri.EscapeDataString(path)}&rs:Format={Uri.EscapeDataString(format)}&rs:Command=Render";
        if (!string.IsNullOrEmpty(query)) url += "&" + query;

        var res = await _httpClient.GetAsync(url, ct);
        if (res.IsSuccessStatusCode)
        {
            return await res.Content.ReadAsByteArrayAsync(ct);
        }

        throw new HttpRequestException($"SSRS SOAP URL access refused to render '{path}' as {format} (HTTP {(int)res.StatusCode}).");
    }
}
