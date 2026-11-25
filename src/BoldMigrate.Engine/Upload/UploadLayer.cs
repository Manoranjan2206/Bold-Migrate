using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Core.StateMachine;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Metrics;
using BoldMigrate.Engine.Preview;
using BoldMigrate.Infrastructure.Credentials;
using Microsoft.Extensions.DependencyInjection;

namespace BoldMigrate.Engine.Upload;

public class DestinationClient : IDisposable
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        UseCookies = false
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _serverUrl;
    private readonly string _tenantId;
    private string? _bearerToken;

    public DestinationClient(string serverUrl, string? initialToken = null, HttpMessageHandler? handlerOverride = null)
    {
        var resolved = BoldMigrate.Core.Common.HostResolver.Resolve(serverUrl.TrimEnd('/'));
        var tenant = "site1";
        if (Uri.TryCreate(resolved, UriKind.Absolute, out var uri))
        {
            var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, @"/site/([^/]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                tenant = match.Groups[1].Value;
            }
            // Strip any subpaths so _serverUrl is clean base origin (e.g. http://localhost:8090)
            _serverUrl = $"{uri.Scheme}://{uri.Authority}".TrimEnd('/');
        }
        else
        {
            _serverUrl = resolved.TrimEnd('/');
        }

        _tenantId = tenant;
        _bearerToken = initialToken;
        _ownsClient = handlerOverride != null;
        _httpClient = handlerOverride != null
            ? new HttpClient(handlerOverride, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(100),
                BaseAddress = Uri.TryCreate(_serverUrl + "/", UriKind.Absolute, out var baseUri) ? baseUri : null
            }
            : new HttpClient(SharedHandler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(100),
                BaseAddress = Uri.TryCreate(_serverUrl + "/", UriKind.Absolute, out var sharedBaseUri) ? sharedBaseUri : null
            };

        if (!string.IsNullOrEmpty(_bearerToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    public string GetTenantId() => _tenantId;

    public async Task<string> AuthenticateAsync(string email, string password, CancellationToken ct = default)
    {
        var cleanEmail = email?.Trim() ?? "";
        var cleanPassword = password ?? "";

        var tokenUrls = new List<string>
        {
            $"{_serverUrl}/reporting/api/site/{_tenantId}/token",
            $"{_serverUrl}/api/site/{_tenantId}/token",
            $"{_serverUrl}/reporting/api/site/site1/token",
            $"{_serverUrl}/api/v1.0/token",
            $"{_serverUrl}/token"
        };

        HttpResponseMessage? lastRes = null;
        string? serverErrorMessage = null;

        foreach (var url in tokenUrls.Distinct())
        {
            try
            {
                using var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "password"),
                    new KeyValuePair<string, string>("username", cleanEmail),
                    new KeyValuePair<string, string>("password", cleanPassword)
                });
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                var res = await _httpClient.SendAsync(req, ct);
                lastRes = res;

                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("access_token", out var tok))
                    {
                        _bearerToken = tok.GetString();
                        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                        return _bearerToken ?? "";
                    }
                }
                else
                {
                    try
                    {
                        var body = await res.Content.ReadAsStringAsync(ct);
                        if (!string.IsNullOrWhiteSpace(body) && !body.TrimStart().StartsWith("<"))
                        {
                            using var doc = JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("Message", out var msg))
                                serverErrorMessage = msg.GetString();
                            else if (doc.RootElement.TryGetProperty("error_description", out var desc))
                                serverErrorMessage = desc.GetString();
                            else if (doc.RootElement.TryGetProperty("errorMessage", out var emsg))
                                serverErrorMessage = emsg.GetString();
                        }
                    }
                    catch
                    {
                        // Ignore parse errors on error body
                    }
                }
            }
            catch
            {
                // Continue probing candidate token endpoints
            }
        }

        var status = lastRes != null ? $"HTTP {(int)lastRes.StatusCode}" : "Unreachable";
        var detail = !string.IsNullOrEmpty(serverErrorMessage) ? $": {serverErrorMessage}" : "";
        throw new HttpRequestException($"Authentication failed on destination server ({status}){detail}. Check URL, Usermail, and Password.");
    }

    public async Task<string> CreateItemAsync(
        string itemType,
        string name,
        string categoryName,
        byte[]? contentBytes,
        string? description = null,
        CancellationToken ct = default)
    {
        var tenant = GetTenantId();
        var urls = new[]
        {
            $"{_serverUrl}/reporting/api/site/{tenant}/v1.0/items",
            $"{_serverUrl}/api/site/{tenant}/v1.0/items",
            $"{_serverUrl}/api/v1.0/items"
        };

        HttpResponseMessage? lastRes = null;
        string? lastError = null;

        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                }

                using var multiContent = new MultipartFormDataContent();
                multiContent.Add(new StringContent(name), "Name");
                multiContent.Add(new StringContent(itemType), "ItemType");
                multiContent.Add(new StringContent(categoryName), "CategoryName");
                if (!string.IsNullOrEmpty(description))
                {
                    multiContent.Add(new StringContent(description), "Description");
                }

                if (contentBytes != null && contentBytes.Length > 0)
                {
                    multiContent.Add(new ByteArrayContent(contentBytes), "File", $"{name}.rdl");
                }

                request.Content = multiContent;

                var res = await _httpClient.SendAsync(request, ct);
                lastRes = res;

                if (res.StatusCode == HttpStatusCode.Conflict)
                {
                    return $"existing-{name}";
                }

                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("Id", out var idProp))
                    {
                        return idProp.GetString() ?? Guid.NewGuid().ToString();
                    }
                    return Guid.NewGuid().ToString();
                }

                lastError = await res.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        var status = lastRes != null ? $"HTTP {(int)lastRes.StatusCode}" : "Error";
        throw new HttpRequestException($"Failed to upload {itemType} '{name}' to Bold Reports ({status}): {lastError}");
    }

    public void SetBearerToken(string token)
    {
        _bearerToken = token;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
    }

    private readonly Dictionary<string, string> _categoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _categoryLock = new(1, 1);

    public async Task<string?> GetOrCreateCategoryAsync(
        string tenantId,
        string categoryName,
        string? parentId = null,
        CancellationToken ct = default)
    {
        await _categoryLock.WaitAsync(ct);
        try
        {
            var cacheKey = $"{parentId ?? ""}|{categoryName}";
            if (_categoryCache.TryGetValue(cacheKey, out var cachedId))
            {
                return cachedId;
            }

            var listUrls = new[]
            {
                $"{_serverUrl}/reporting/api/site/{tenantId}/v1.0/items?ItemType=Category",
                $"{_serverUrl}/api/site/{tenantId}/v1.0/items?ItemType=Category",
                $"{_serverUrl}/api/v1.0/items?ItemType=Category"
            };

            foreach (var url in listUrls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (!string.IsNullOrEmpty(_bearerToken))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                    }
                    var res = await _httpClient.SendAsync(req, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var cat in doc.RootElement.EnumerateArray())
                            {
                                var cName = cat.TryGetProperty("Name", out var np) ? np.GetString() : null;
                                var cId = cat.TryGetProperty("Id", out var ip) ? ip.GetString() : null;
                                var cParent = cat.TryGetProperty("ParentId", out var pip) ? pip.GetString() : null;
                                if (!string.IsNullOrEmpty(cName) && !string.IsNullOrEmpty(cId))
                                {
                                    var k = $"{cParent ?? ""}|{cName}";
                                    _categoryCache[k] = cId;
                                    if (string.Equals(cName, categoryName, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(cParent ?? "", parentId ?? "", StringComparison.OrdinalIgnoreCase))
                                    {
                                        return cId;
                                    }
                                }
                            }
                        }
                        break;
                    }
                }
                catch { }
            }

            var createUrls = new[]
            {
                $"{_serverUrl}/reporting/api/site/{tenantId}/v5.0/categories",
                $"{_serverUrl}/api/site/{tenantId}/v5.0/categories",
                $"{_serverUrl}/api/v1.0/categories"
            };

            var catBody = new Dictionary<string, object?>
            {
                { "Name", categoryName },
                { "Description", "Auto-created during migration" }
            };
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                catBody["ParentId"] = parentId;
            }

            var json = JsonSerializer.Serialize(catBody);

            foreach (var url in createUrls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    if (!string.IsNullOrEmpty(_bearerToken))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                    }

                    var res = await _httpClient.SendAsync(req, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                        if (doc.RootElement.TryGetProperty("Id", out var idProp) && !string.IsNullOrWhiteSpace(idProp.GetString()))
                        {
                            var id = idProp.GetString()!;
                            _categoryCache[cacheKey] = id;
                            return id;
                        }
                        if (doc.RootElement.TryGetProperty("Data", out var dataProp) && !string.IsNullOrWhiteSpace(dataProp.GetString()))
                        {
                            var id = dataProp.GetString()!;
                            _categoryCache[cacheKey] = id;
                            return id;
                        }
                    }
                }
                catch { }
            }

            return null;
        }
        finally
        {
            _categoryLock.Release();
        }
    }

    public async Task<(bool success, string id)> CreateDataSourceAsync(
        string tenantId,
        string name,
        string connectString,
        string serverType,
        string? username,
        string? password,
        string credentialRetrieval = "None",
        bool windowsCredentials = false,
        bool impersonateUser = false,
        CancellationToken ct = default)
    {
        var isWindows = windowsCredentials ||
                        string.Equals(credentialRetrieval, "Integrated", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(username) && username.Contains('\\'));

        var dsDef = new Dictionary<string, object>
        {
            { "ConnectString", connectString },
            { "CredentialRetrieval", credentialRetrieval },
            { "ImpersonateUser", impersonateUser },
            { "WindowsCredentials", isWindows },
            { "ServerType", serverType }
        };

        if (!string.IsNullOrWhiteSpace(username) && !string.Equals(credentialRetrieval, "Integrated", StringComparison.OrdinalIgnoreCase))
        {
            dsDef["UserName"] = username;
        }
        if (!string.IsNullOrWhiteSpace(password) && !string.Equals(credentialRetrieval, "Integrated", StringComparison.OrdinalIgnoreCase))
        {
            dsDef["Password"] = password;
        }

        var payload = new Dictionary<string, object>
        {
            { "Name", name },
            { "Description", $"Auto-created from RSDS: {name}" },
            { "DataSourceDefinition", dsDef }
        };

        var json = JsonSerializer.Serialize(payload);
        var urls = new[]
        {
            $"{_serverUrl}/reporting/api/site/{tenantId}/v1.0/reports/data-sources",
            $"{_serverUrl}/api/site/{tenantId}/v1.0/reports/data-sources"
        };

        HttpResponseMessage? lastRes = null;
        string? lastError = null;

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                }

                var res = await _httpClient.SendAsync(req, ct);
                lastRes = res;

                if (res.StatusCode == HttpStatusCode.Conflict)
                {
                    var existing = (await GetItemsAsync(tenantId, "Datasource", ct))
                        .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
                    return (false, existing?.Id ?? string.Empty);
                }

                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("PublishedItemId", out var pProp) && !string.IsNullOrWhiteSpace(pProp.GetString()))
                    {
                        return (true, pProp.GetString()!);
                    }
                    if (doc.RootElement.TryGetProperty("Id", out var idProp) && !string.IsNullOrWhiteSpace(idProp.GetString()))
                    {
                        return (true, idProp.GetString()!);
                    }
                    throw new HttpRequestException($"CreateDataSource '{name}' returned HTTP {(int)res.StatusCode} without an item Id.");
                }

                lastError = await res.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex.Message;
            }
        }

        var status = lastRes != null ? $"HTTP {(int)lastRes.StatusCode}" : "Unreachable";
        throw new HttpRequestException($"Failed to create DataSource '{name}' on Bold Reports ({status}): {lastError}");
    }

    public async Task<(bool success, string id)> CreateDataSetAsync(
        string tenantId,
        string name,
        string base64Content,
        string dataSourceName,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object>
        {
            { "Name", name },
            { "Description", $"Imported dataset: {name}" },
            { "DataSourceMappingInfo", new[] { new { Name = dataSourceName, DataSourceName = dataSourceName } } },
            { "ItemContent", base64Content }
        };

        var json = JsonSerializer.Serialize(payload, _relaxedJsonOptions);
        var urls = new[]
        {
            $"{_serverUrl}/reporting/api/site/{tenantId}/v1.0/reports/datasets",
            $"{_serverUrl}/api/site/{tenantId}/v1.0/reports/datasets"
        };

        HttpResponseMessage? lastRes = null;
        string? lastError = null;

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                }

                var res = await _httpClient.SendAsync(req, ct);
                lastRes = res;

                if (res.StatusCode == HttpStatusCode.Conflict)
                {
                    var existing = await GetDataSetsAsync(tenantId, ct);
                    return (false, existing.TryGetValue(name, out var existingId) ? existingId : string.Empty);
                }

                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("PublishedItemId", out var pProp) && !string.IsNullOrWhiteSpace(pProp.GetString()))
                    {
                        return (true, pProp.GetString()!);
                    }
                    if (doc.RootElement.TryGetProperty("Id", out var idProp) && !string.IsNullOrWhiteSpace(idProp.GetString()))
                    {
                        return (true, idProp.GetString()!);
                    }
                    throw new HttpRequestException($"CreateDataSet '{name}' returned HTTP {(int)res.StatusCode} without an item Id.");
                }

                lastError = await res.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex.Message;
            }
        }

        var status = lastRes != null ? $"HTTP {(int)lastRes.StatusCode}" : "Unreachable";
        throw new HttpRequestException($"Failed to create DataSet '{name}' on Bold Reports ({status}): {lastError}");
    }

    private static readonly JsonSerializerOptions _relaxedJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public async Task<(bool success, string id)> CreateReportAsync(
        string tenantId,
        string name,
        string base64Content,
        string? categoryId,
        List<Dictionary<string, object>>? dataSourceMappings,
        List<Dictionary<string, object>>? dataSetMappings,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object>
        {
            { "Name", name },
            { "ItemContent", base64Content },
            { "IsPublic", false }
        };

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            payload["CategoryId"] = categoryId;
        }

        if (dataSourceMappings != null && dataSourceMappings.Count > 0)
        {
            payload["DataSourceMappingInfo"] = dataSourceMappings;
        }

        if (dataSetMappings != null && dataSetMappings.Count > 0)
        {
            payload["DataSetMappingInfo"] = dataSetMappings;
        }

        var json = JsonSerializer.Serialize(payload, _relaxedJsonOptions);

        var urls = new[]
        {
            $"{_serverUrl}/reporting/api/site/{tenantId}/v1.0/reports",
            $"{_serverUrl}/reporting/api/site/{tenantId}/v5.0/reports",
            $"{_serverUrl}/api/site/{tenantId}/v1.0/reports",
            $"{_serverUrl}/api/v1.0/reports"
        };

        HttpResponseMessage? lastRes = null;
        string? lastError = null;

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                }

                var res = await _httpClient.SendAsync(req, ct);
                lastRes = res;

                if (res.StatusCode == HttpStatusCode.Conflict)
                {
                    var live = await GetItemsAsync(tenantId, "Report", ct);
                    var existing = live.FirstOrDefault(r =>
                        string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(categoryId) || string.Equals(r.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase)));
                    existing ??= live.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        return (true, existing.Id);
                    }
                    return (false, string.Empty);
                }

                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("PublishedItemId", out var pProp) && !string.IsNullOrWhiteSpace(pProp.GetString()))
                    {
                        return (true, pProp.GetString()!);
                    }
                    if (doc.RootElement.TryGetProperty("Id", out var idProp) && !string.IsNullOrWhiteSpace(idProp.GetString()))
                    {
                        return (true, idProp.GetString()!);
                    }
                    throw new HttpRequestException($"CreateReport '{name}' returned HTTP {(int)res.StatusCode} without an item Id.");
                }

                lastError = await res.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex.Message;
            }
        }

        var status = lastRes != null ? $"HTTP {(int)lastRes.StatusCode}" : "Unreachable";
        throw new HttpRequestException($"Upload of report '{name}' failed on Bold Reports ({status}): {lastError}");
    }

    /// <summary>
    /// Replaces the definition of a report that already exists on Bold Reports Server
    /// (PUT /v1.0/reports), keeping its id, URL and permissions. Used for reports edited in the
    /// local designer after they were uploaded.
    /// </summary>
    public async Task UpdateReportAsync(
        string tenantId,
        string reportId,
        string base64Content,
        string? categoryId,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object>
        {
            { "ItemId", reportId },
            { "ItemContent", base64Content },
            { "IsPublic", false }
        };

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            payload["CategoryId"] = categoryId;
        }

        var json = JsonSerializer.Serialize(payload, _relaxedJsonOptions);

        var urls = new[]
        {
            $"{_serverUrl}/reporting/api/site/{tenantId}/v1.0/reports",
            $"{_serverUrl}/api/site/{tenantId}/v1.0/reports",
            $"{_serverUrl}/api/v1.0/reports"
        };

        HttpResponseMessage? lastRes = null;
        string? lastError = null;

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                }

                var res = await _httpClient.SendAsync(req, ct);
                lastRes = res;
                var body = await res.Content.ReadAsStringAsync(ct);

                if (res.IsSuccessStatusCode)
                {
                    // The server reports a failed update in-band with HTTP 200.
                    if (TryReadFailure(body, out var failure))
                    {
                        lastError = failure;
                        break;
                    }
                    return;
                }

                // 404/405 means this route is not the API; anything else is a real answer.
                lastError = body;
                if (res.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)) break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex.Message;
            }
        }

        var status = lastRes != null ? $"HTTP {(int)lastRes.StatusCode}" : "Unreachable";
        throw new HttpRequestException($"Update of report '{reportId}' failed on Bold Reports ({status}): {lastError}");
    }

    private static bool TryReadFailure(string body, out string? message)
    {
        message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("Status", out var status) &&
                status.ValueKind == JsonValueKind.False)
            {
                message = doc.RootElement.TryGetProperty("StatusMessage", out var msg) ? msg.GetString() : "Status false";
                return true;
            }
        }
        catch (JsonException)
        {
            // Non-JSON success body: treat as success.
        }
        return false;
    }

    public async Task<Dictionary<string, string>> GetDataSetsAsync(string tenantId, CancellationToken ct = default)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var urls = new[]
        {
            $"{_serverUrl}/reporting/api/site/{tenantId}/v1.0/items?ItemType=DataSet",
            $"{_serverUrl}/api/site/{tenantId}/v1.0/items?ItemType=DataSet"
        };

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                }

                var res = await _httpClient.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var name = item.TryGetProperty("Name", out var np) ? np.GetString() : null;
                            var id = item.TryGetProperty("Id", out var ip) ? ip.GetString() : null;
                            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id))
                            {
                                lookup[name] = id;
                            }
                        }
                    }
                    break;
                }
            }
            catch { }
        }

        return lookup;
    }

    public async Task<bool> DeleteItemAsync(string? tenantId, string itemId, CancellationToken ct = default)
    {
        var tenant = !string.IsNullOrWhiteSpace(tenantId) ? tenantId : GetTenantId();
        var urls = new[]
        {
            $"{_serverUrl}/reporting/api/site/{tenant}/v1.0/items/{itemId}",
            $"{_serverUrl}/api/site/{tenant}/v1.0/items/{itemId}",
            $"{_serverUrl}/api/v1.0/items/{itemId}",
            $"{_serverUrl}/reporting/api/site/{tenant}/v5.0/reports/{itemId}",
            $"{_serverUrl}/api/site/{tenant}/v5.0/reports/{itemId}"
        };

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                }
                var res = await _httpClient.SendAsync(req, ct);
                if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.NotFound)
                {
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    public async Task<List<DestinationItemInfo>> GetItemsAsync(string? tenantId, string itemType = "Report", CancellationToken ct = default)
    {
        var tenant = !string.IsNullOrWhiteSpace(tenantId) ? tenantId : GetTenantId();
        var urls = new[]
        {
            $"{_serverUrl}/reporting/api/site/{tenant}/v1.0/items?ItemType={itemType}",
            $"{_serverUrl}/api/site/{tenant}/v1.0/items?ItemType={itemType}",
            $"{_serverUrl}/api/v1.0/items?ItemType={itemType}"
        };

        var list = new List<DestinationItemInfo>();
        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                }
                var res = await _httpClient.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var id = el.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
                            var name = el.TryGetProperty("Name", out var nProp) ? nProp.GetString() : null;
                            var catName = el.TryGetProperty("CategoryName", out var cProp) ? cProp.GetString() : null;
                            var catId = el.TryGetProperty("CategoryId", out var cidProp) ? cidProp.GetString() : null;
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                            {
                                list.Add(new DestinationItemInfo
                                {
                                    Id = id,
                                    Name = name,
                                    CategoryName = catName,
                                    CategoryId = catId,
                                    ItemType = itemType
                                });
                            }
                        }
                        if (list.Count > 0) return list;
                    }
                }
            }
            catch { }
        }
        return list;
    }

    /// <summary>
    /// Renders a report that already exists on the Bold Reports Server into the requested
    /// export format and returns the raw bytes. Used by the validation layer to compare the
    /// destination's actual output against the source server's output.
    /// Returns null when the server could not render the report, so callers can fail closed
    /// rather than assuming equivalence.
    /// </summary>
    public async Task<byte[]?> ExportReportAsync(
        string? tenantId,
        string destinationItemId,
        string format = "CSV",
        IDictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationItemId)) return null;

        var tenant = !string.IsNullOrWhiteSpace(tenantId) ? tenantId : GetTenantId();
        var urls = new[]
        {
            $"{_serverUrl}/reporting/api/site/{tenant}/v1.0/reports/export",
            $"{_serverUrl}/api/site/{tenant}/v1.0/reports/export",
            $"{_serverUrl}/api/v1.0/reports/export"
        };

        var payload = new Dictionary<string, object?>
        {
            ["ReportId"] = destinationItemId,
            ["ExportType"] = format
        };

        if (parameters is { Count: > 0 })
        {
            payload["Parameters"] = parameters
                .Select(p => new { Name = p.Key, Values = new[] { p.Value } })
                .ToArray();
        }

        var json = JsonSerializer.Serialize(payload);

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                }

                var res = await _httpClient.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) continue;

                using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var root = doc.RootElement;

                // The server reports render failures in-band with HTTP 200.
                if (root.TryGetProperty("Status", out var statusProp) &&
                    statusProp.ValueKind == JsonValueKind.False)
                {
                    return null;
                }

                if (root.TryGetProperty("FileContent", out var contentProp) &&
                    contentProp.ValueKind == JsonValueKind.String)
                {
                    var b64 = contentProp.GetString();
                    if (string.IsNullOrEmpty(b64)) return Array.Empty<byte>();
                    try
                    {
                        return Convert.FromBase64String(b64);
                    }
                    catch (FormatException)
                    {
                        return null;
                    }
                }
            }
            catch
            {
                // Try the next candidate route.
            }
        }

        return null;
    }
}

public class DestinationItemInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    public string? CategoryId { get; set; }
    public string ItemType { get; set; } = "Report";
}

public class UploadLayer
{
    private readonly IMigrationStore _store;
    private readonly IStagingStore _staging;
    private readonly MigrationLogger _logger;
    private readonly ICredentialStore? _credStore;
    private readonly MigrationSettings _settings;
    private readonly SemaphoreSlim _storeLock = new(1, 1);

    public UploadLayer(
        IMigrationStore store,
        IStagingStore staging,
        MigrationLogger logger,
        ICredentialStore? credStore = null,
        MigrationSettings? settings = null)
    {
        _store = store;
        _staging = staging;
        _logger = logger;
        _credStore = credStore;
        _settings = settings ?? new MigrationSettings();
    }

    public static bool IsEligibleForUpload(ReportState state) =>
        state is ReportState.AutoPassed or ReportState.Approved or ReportState.Queued or ReportState.Uploaded;

    public static string ComputeIdempotencyKey(Guid runId, ItemType itemType, string sourcePath)
    {
        var input = $"{runId}|{itemType}|{sourcePath}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<int> UploadEstateAsync(
        Guid runId,
        DestinationClient client,
        Func<bool> isPaused,
        CancellationToken ct = default)
    {
        var uploadedCount = 0;
        var tenantId = client.GetTenantId();
        var folderCategoryMap = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var datasetLookup = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var existingDatasets = await client.GetDataSetsAsync(tenantId, ct);
            foreach (var kv in existingDatasets) datasetLookup[kv.Key] = kv.Value;
        }
        catch { }

        // Stage 1: Categories (folders, parents before children, creating nested hierarchy)
        var folders = (await _store.GetCatalogItemsAsync(runId, ItemType.Folder, ct))
            .OrderBy(f => StagedCatalog.NormalizePath(f.SourcePath).Count(c => c == '/'))
            .ThenBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var folder in folders)
        {
            if (isPaused()) return uploadedCount;
            var normPath = StagedCatalog.NormalizePath(folder.SourcePath);
            var parentPath = StagedCatalog.ParentOf(normPath);
            folderCategoryMap.TryGetValue(parentPath, out var parentCatId);

            if (await UploadItemWithRetryAsync(runId, folder, client, "Category", folderCategoryMap, datasetLookup, parentCatId, ct))
            {
                if (!string.IsNullOrEmpty(folder.DestinationItemId))
                    folderCategoryMap[normPath] = folder.DestinationItemId;
                uploadedCount++;
            }
        }

        // Stage 2: Data Sources (honoring UploadMaxParallel)
        var maxParallel = Math.Clamp(_settings.UploadMaxParallel, 1, 16);
        var dataSources = (await _store.GetCatalogItemsAsync(runId, ItemType.DataSource, ct)).ToList();
        await Parallel.ForEachAsync(dataSources, new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct }, async (ds, token) =>
        {
            if (isPaused()) return;
            if (await UploadItemWithRetryAsync(runId, ds, client, "Datasource", folderCategoryMap, datasetLookup, null, token))
            {
                Interlocked.Increment(ref uploadedCount);
            }
        });

        // Stage 3: Datasets (upload actual .rsd Base64 content & DataSourceReference)
        var datasets = (await _store.GetCatalogItemsAsync(runId, ItemType.DataSet, ct)).ToList();
        await Parallel.ForEachAsync(datasets, new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct }, async (ds, token) =>
        {
            if (isPaused()) return;
            if (await UploadItemWithRetryAsync(runId, ds, client, "Dataset", folderCategoryMap, datasetLookup, null, token))
            {
                if (!string.IsNullOrEmpty(ds.DestinationItemId))
                {
                    datasetLookup[ds.Name] = ds.DestinationItemId;
                    datasetLookup[StagedCatalog.NormalizePath(ds.SourcePath)] = ds.DestinationItemId;
                }
                Interlocked.Increment(ref uploadedCount);
            }
        });

        // Stage 4: Reports (gated, dependency-mapped, parallelized)
        var reports = (await _store.GetCatalogItemsAsync(runId, ItemType.Report, ct)).ToList();
        await Parallel.ForEachAsync(reports, new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct }, async (report, token) =>
        {
            if (isPaused()) return;

            ReportRecord? record;
            await _storeLock.WaitAsync(token);
            try
            {
                record = await _store.GetReportRecordAsync(report.Id, token);
                if (record?.Bucket == ReportBucket.Blocked || record?.State == ReportState.Excluded)
                {
                    return;
                }

                if (record != null && !IsEligibleForUpload(record.State))
                {
                    _logger.Log(
                        Microsoft.Extensions.Logging.LogLevel.Information,
                        "UPLD-GATE-SKIP",
                        runId,
                        $"Skipping report '{report.Name}' (state: {record.State}): reports must be validated or approved in Step 4 before upload to Bold Reports.",
                        itemPath: report.SourcePath,
                        outcome: "SkippedNotValidated");
                    return;
                }

                if (record != null)
                {
                    ReportStateMachine.Transition(record, ReportState.Uploading);
                    record.UploadAttempts++;
                    await _store.UpdateReportRecordAsync(record, token);
                }
            }
            finally
            {
                _storeLock.Release();
            }

            var normFolder = StagedCatalog.ParentOf(report.SourcePath);
            folderCategoryMap.TryGetValue(normFolder, out var categoryId);

            var success = await UploadItemWithRetryAsync(runId, report, client, "Report", folderCategoryMap, datasetLookup, categoryId, token);

            await _storeLock.WaitAsync(token);
            try
            {
                if (success)
                {
                    Interlocked.Increment(ref uploadedCount);
                    MigrationMetrics.ItemsTotal.Add(1, new KeyValuePair<string, object?>("stage", "upload"), new KeyValuePair<string, object?>("outcome", "success"));
                    if (record != null)
                    {
                        ReportStateMachine.Transition(record, ReportState.Uploaded);
                        await _store.UpdateReportRecordAsync(record, token);
                    }
                }
                else
                {
                    MigrationMetrics.ItemsTotal.Add(1, new KeyValuePair<string, object?>("stage", "upload"), new KeyValuePair<string, object?>("outcome", "failed"));
                    if (record != null)
                    {
                        ReportStateMachine.Transition(record, ReportState.UploadFailed);
                        await _store.UpdateReportRecordAsync(record, token);
                    }
                }
            }
            finally
            {
                _storeLock.Release();
            }
        });

        return uploadedCount;
    }

    private async Task<bool> UploadItemWithRetryAsync(
        Guid runId,
        CatalogItem item,
        DestinationClient client,
        string destinationType,
        ConcurrentDictionary<string, string> folderCategoryMap,
        ConcurrentDictionary<string, string> datasetLookup,
        string? resolvedCategoryId,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(item.DestinationItemId) &&
            !item.DestinationItemId.StartsWith("dest-") &&
            !item.DestinationItemId.StartsWith("rep-") &&
            !item.DestinationItemId.StartsWith("existing-"))
        {
            // Already uploaded with a genuine destination ID. A report edited in the local
            // designer since then is pushed again so Bold Reports gets the edited definition.
            if (item.ItemType == ItemType.Report)
            {
                ReportRecord? edited;
                await _storeLock.WaitAsync(ct);
                try { edited = await _store.GetReportRecordAsync(item.Id, ct); }
                finally { _storeLock.Release(); }

                if (edited?.LocalEditPendingUpload == true)
                {
                    return await PushLocalEditAsync(runId, item, edited, client, resolvedCategoryId, ct);
                }
            }
            return true;
        }

        var tenantId = client.GetTenantId();
        var maxAttempts = Math.Clamp(_settings.UploadMaxAttempts, 1, 10);
        var backoffs = _settings.UploadRetryBackoffSeconds is { Length: > 0 } b ? b : new[] { 2, 8, 30 };

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string destId;
                if (item.ItemType == ItemType.Folder)
                {
                    var catId = await client.GetOrCreateCategoryAsync(tenantId, item.Name, resolvedCategoryId, ct);
                    if (string.IsNullOrWhiteSpace(catId))
                    {
                        throw new HttpRequestException($"Failed to create or resolve category '{item.Name}' on Bold Reports.");
                    }
                    destId = catId;
                }
                else if (item.ItemType == ItemType.DataSource)
                {
                    var connectString = "";
                    var serverType = "SQL";
                    var retrieval = "None";
                    string? userName = null;
                    string? password = null;
                    bool windowsCredentials = false;
                    bool impersonateUser = false;

                    byte[]? dsBytes = await ReadItemBytesAsync(item, ct);
                    if (dsBytes != null)
                    {
                        var parsed = SharedDataSourceDefinition.Parse(dsBytes);
                        if (parsed != null)
                        {
                            connectString = parsed.ConnectString;
                            serverType = parsed.Extension;
                            retrieval = parsed.CredentialRetrieval;
                            windowsCredentials = parsed.WindowsCredentials;
                            impersonateUser = parsed.ImpersonateUser;
                            userName = parsed.UserName;
                            password = parsed.Password;
                        }
                    }

                    var cred = _credStore?.GetDataSourceCredential(item.Id.ToString());
                    if (cred != null)
                    {
                        userName = string.IsNullOrEmpty(cred.Domain) ? cred.UserName : $"{cred.Domain}\\{cred.UserName}";
                        password = cred.Password;
                        retrieval = "Store";
                        if (!string.IsNullOrEmpty(cred.Domain) || (userName != null && userName.Contains('\\')))
                        {
                            windowsCredentials = true;
                        }
                    }

                    var (ok, dsId) = await client.CreateDataSourceAsync(
                        tenantId,
                        item.Name,
                        connectString,
                        serverType,
                        userName,
                        password,
                        retrieval,
                        windowsCredentials,
                        impersonateUser,
                        ct);

                    if (!ok && string.IsNullOrWhiteSpace(dsId))
                    {
                        throw new HttpRequestException($"DataSource '{item.Name}' conflicted and could not be resolved on destination.");
                    }
                    destId = dsId;
                }
                else if (item.ItemType == ItemType.DataSet)
                {
                    var rsdBytes = await ReadItemBytesAsync(item, ct);
                    if (rsdBytes == null || rsdBytes.Length == 0)
                    {
                        throw new FileNotFoundException($"RSD content bytes could not be located in staging for dataset '{item.Name}'.");
                    }

                    var dsRef = ReportDependencies.ReadDataSetDataSourceReference(rsdBytes);
                    var targetDsName = !string.IsNullOrWhiteSpace(dsRef)
                        ? dsRef.Trim('/').Split('/').Last()
                        : item.Name;

                    var base64 = Convert.ToBase64String(rsdBytes);
                    var (ok, dsetId) = await client.CreateDataSetAsync(tenantId, item.Name, base64, targetDsName, ct);
                    if (!ok && string.IsNullOrWhiteSpace(dsetId))
                    {
                        throw new HttpRequestException($"DataSet '{item.Name}' conflicted and could not be resolved on destination.");
                    }
                    destId = dsetId;
                }
                else // Report
                {
                    var categoryId = resolvedCategoryId;
                    if (string.IsNullOrWhiteSpace(categoryId))
                    {
                        categoryId = await EnsureNestedCategoryAsync(client, tenantId, item.ParentPath, folderCategoryMap, ct);
                    }

                    var content = await ReadItemBytesAsync(item, ct);
                    if (content == null || content.Length == 0)
                    {
                        throw new FileNotFoundException($"RDL content bytes could not be located on disk for report '{item.Name}'. Staged path: {item.StagedFilePath}");
                    }

                    var deps = ReportDependencies.Read(content);
                    var dsMappings = deps.DataSources
                        .Where(d => !string.IsNullOrWhiteSpace(d.SharedReference))
                        .Select(d => new Dictionary<string, object>
                        {
                            ["Name"] = d.Name,
                            ["DataSourceName"] = d.SharedReference!.Trim('/').Split('/').Last()
                        })
                        .ToList();

                    var dsetMappings = deps.SharedDataSets
                        .Select(d =>
                        {
                            var refName = d.SharedReference.Trim('/').Split('/').Last();
                            var map = new Dictionary<string, object>
                            {
                                ["Name"] = d.Name,
                                ["DataSetName"] = refName
                            };
                            if (datasetLookup.TryGetValue(refName, out var dsId) ||
                                datasetLookup.TryGetValue(StagedCatalog.NormalizePath(d.SharedReference), out dsId))
                            {
                                map["DataSetId"] = dsId;
                            }
                            return map;
                        })
                        .ToList();

                    var base64 = Convert.ToBase64String(content);
                    var (success, repId) = await client.CreateReportAsync(
                        tenantId,
                        item.Name,
                        base64,
                        categoryId,
                        dsMappings.Count > 0 ? dsMappings : null,
                        dsetMappings.Count > 0 ? dsetMappings : null,
                        ct);

                    if (!success || string.IsNullOrWhiteSpace(repId))
                    {
                        throw new HttpRequestException($"Report '{item.Name}' upload did not return a valid destination identifier.");
                    }
                    destId = repId;
                }

                sw.Stop();
                await _storeLock.WaitAsync(ct);
                try
                {
                    item.DestinationItemId = destId;
                    await _store.UpdateCatalogItemAsync(item, ct);

                    if (item.ItemType == ItemType.Report)
                    {
                        var uploadedRecord = await _store.GetReportRecordAsync(item.Id, ct);
                        if (uploadedRecord?.LocalEditPendingUpload == true)
                        {
                            uploadedRecord.LocalEditPendingUpload = false;
                            await _store.UpdateReportRecordAsync(uploadedRecord, ct);
                        }

                        var existingMappings = await _store.GetUrlMappingsAsync(runId, ct);
                        if (!existingMappings.Any(m => string.Equals(m.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            await _store.AddUrlMappingsAsync(new[]
                            {
                                new UrlMapping
                                {
                                    Id = Guid.NewGuid(),
                                    RunId = runId,
                                    SourcePath = item.SourcePath,
                                    DestinationItemId = destId,
                                    DestinationUrl = $"/reports/{destId}/{Uri.EscapeDataString(item.Name)}"
                                }
                            }, ct);
                        }
                    }

                    await _store.AddUploadAttemptAsync(new UploadAttempt
                    {
                        Id = Guid.NewGuid(),
                        ReportId = item.Id,
                        AttemptNumber = attempt,
                        HttpStatus = 200,
                        DurationMs = sw.ElapsedMilliseconds
                    }, ct);
                }
                finally
                {
                    _storeLock.Release();
                }

                _logger.Log(
                    Microsoft.Extensions.Logging.LogLevel.Information,
                    "UPLD-ITEM",
                    runId,
                    $"Uploaded {item.ItemType} '{item.Name}' to destination. Remote ID: {destId}",
                    itemPath: item.SourcePath,
                    durationMs: sw.ElapsedMilliseconds,
                    outcome: "Success");

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                await _storeLock.WaitAsync(ct);
                try
                {
                    await _store.AddUploadAttemptAsync(new UploadAttempt
                    {
                        Id = Guid.NewGuid(),
                        ReportId = item.Id,
                        AttemptNumber = attempt,
                        HttpStatus = 500,
                        DurationMs = sw.ElapsedMilliseconds,
                        ErrorCode = ex.Message
                    }, ct);
                }
                finally
                {
                    _storeLock.Release();
                }

                if (attempt < maxAttempts)
                {
                    var backoff = backoffs[Math.Min(attempt - 1, backoffs.Length - 1)];
                    _logger.Log(
                        Microsoft.Extensions.Logging.LogLevel.Warning,
                        "UPLD-RETRY",
                        runId,
                        $"Upload attempt {attempt}/{maxAttempts} failed for '{item.Name}'. Retrying in {backoff}s. Error: {ex.Message}",
                        itemPath: item.SourcePath,
                        attempt: attempt);

                    await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
                }
                else
                {
                    _logger.Log(
                        Microsoft.Extensions.Logging.LogLevel.Error,
                        "UPLD-FAIL",
                        runId,
                        $"Upload failed for '{item.Name}' after {attempt} attempts. Error: {ex.Message}",
                        itemPath: item.SourcePath,
                        attempt: attempt,
                        outcome: "Failure");
                }
            }
        }

        return false;
    }

    private async Task<byte[]?> ReadItemBytesAsync(CatalogItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.StagedFilePath)) return null;
        try
        {
            return await _staging.ReadStagedFileAsync(item.StagedFilePath, ct);
        }
        catch (Exception) when (File.Exists(item.StagedFilePath))
        {
            return await File.ReadAllBytesAsync(item.StagedFilePath, ct);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> EnsureNestedCategoryAsync(
        DestinationClient client,
        string tenantId,
        string? parentPath,
        ConcurrentDictionary<string, string> folderCategoryMap,
        CancellationToken ct)
    {
        var norm = StagedCatalog.NormalizePath(parentPath);
        if (norm == "/") return await client.GetOrCreateCategoryAsync(tenantId, "General", null, ct);
        if (folderCategoryMap.TryGetValue(norm, out var cached)) return cached;

        var segments = norm.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? currentParentId = null;
        var builtPath = "";
        foreach (var seg in segments)
        {
            builtPath += "/" + seg;
            if (!folderCategoryMap.TryGetValue(builtPath, out var id))
            {
                id = await client.GetOrCreateCategoryAsync(tenantId, seg, currentParentId, ct);
                if (!string.IsNullOrWhiteSpace(id)) folderCategoryMap[builtPath] = id;
            }
            currentParentId = id;
        }

        return currentParentId;
    }

    /// <summary>
    /// Sends a locally edited report over the copy already on Bold Reports Server. The edited
    /// definition comes from staging; nothing is fetched from the source server again.
    /// </summary>
    private async Task<bool> PushLocalEditAsync(
        Guid runId,
        CatalogItem item,
        ReportRecord record,
        DestinationClient client,
        string? resolvedCategoryId,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var content = await ReadItemBytesAsync(item, ct);
            if (content == null || content.Length == 0)
            {
                throw new FileNotFoundException($"The edited definition of '{item.Name}' is missing from staging.", item.StagedFilePath);
            }

            var tenantId = client.GetTenantId();
            var categoryId = resolvedCategoryId;
            if (string.IsNullOrWhiteSpace(categoryId))
            {
                var categoryName = string.IsNullOrEmpty(item.ParentPath) ? "General" : item.ParentPath.Trim('/').Split('/').Last();
                categoryId = await client.GetOrCreateCategoryAsync(tenantId, categoryName, null, ct);
            }

            await client.UpdateReportAsync(tenantId, item.DestinationItemId!, Convert.ToBase64String(content), categoryId, ct);
            sw.Stop();

            await _storeLock.WaitAsync(ct);
            try
            {
                record.LocalEditPendingUpload = false;
                await _store.UpdateReportRecordAsync(record, ct);

                await _store.AddUploadAttemptAsync(new UploadAttempt
                {
                    Id = Guid.NewGuid(),
                    ReportId = item.Id,
                    AttemptNumber = 1,
                    HttpStatus = 200,
                    DurationMs = sw.ElapsedMilliseconds
                }, ct);
            }
            finally
            {
                _storeLock.Release();
            }

            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Information,
                "UPLD-EDIT",
                runId,
                $"Sent the locally edited definition of '{item.Name}' to Bold Reports (report {item.DestinationItemId}).",
                itemPath: item.SourcePath,
                durationMs: sw.ElapsedMilliseconds,
                outcome: "Success");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            await _storeLock.WaitAsync(ct);
            try
            {
                await _store.AddUploadAttemptAsync(new UploadAttempt
                {
                    Id = Guid.NewGuid(),
                    ReportId = item.Id,
                    AttemptNumber = 1,
                    HttpStatus = 500,
                    DurationMs = sw.ElapsedMilliseconds,
                    ErrorCode = ex.Message
                }, ct);
            }
            finally
            {
                _storeLock.Release();
            }

            // The edit stays pending so the next upload or retry sends it again.
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Error,
                "UPLD-EDIT-FAIL",
                runId,
                $"Could not send the locally edited definition of '{item.Name}' to Bold Reports: {ex.Message}",
                itemPath: item.SourcePath,
                outcome: "Failure");
            return false;
        }
    }
}
