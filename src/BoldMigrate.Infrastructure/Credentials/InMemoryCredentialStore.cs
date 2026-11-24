using System.Net;

namespace BoldMigrate.Infrastructure.Credentials;

public interface ICredentialStore : IDisposable
{
    void SetSourceCredential(string key, NetworkCredential credential);
    NetworkCredential? GetSourceCredential(string key);

    /// <summary>
    /// Stores the account a shared data source connects with. SSRS never exports stored
    /// data source passwords, so these are entered in Step 1 and reused by the local
    /// report viewer/designer. Keyed by the data source's catalog item id.
    /// </summary>
    void SetDataSourceCredential(string dataSourceKey, NetworkCredential credential);
    NetworkCredential? GetDataSourceCredential(string dataSourceKey);
    void RemoveDataSourceCredential(string dataSourceKey);

    void SetDestinationToken(string token);
    string? GetDestinationToken();

    void ClearAll();
}

public class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, NetworkCredential> _sourceCreds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NetworkCredential> _dsCreds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dsLock = new();
    private string? _destinationToken;
    private bool _disposed;

    public void SetSourceCredential(string key, NetworkCredential credential)
    {
        _sourceCreds[key] = credential;
    }

    public NetworkCredential? GetSourceCredential(string key)
    {
        _sourceCreds.TryGetValue(key, out var cred);
        return cred;
    }

    // The viewer and designer read these from request threads while Step 1 writes them,
    // so access is serialised.
    public void SetDataSourceCredential(string dataSourceKey, NetworkCredential credential)
    {
        lock (_dsLock) _dsCreds[dataSourceKey] = credential;
    }

    public NetworkCredential? GetDataSourceCredential(string dataSourceKey)
    {
        lock (_dsLock) return _dsCreds.TryGetValue(dataSourceKey, out var cred) ? cred : null;
    }

    public void RemoveDataSourceCredential(string dataSourceKey)
    {
        lock (_dsLock) _dsCreds.Remove(dataSourceKey);
    }

    public void SetDestinationToken(string token)
    {
        _destinationToken = token;
    }

    public string? GetDestinationToken() => _destinationToken;

    public void ClearAll()
    {
        _sourceCreds.Clear();
        lock (_dsLock) _dsCreds.Clear();
        _destinationToken = null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            ClearAll();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
