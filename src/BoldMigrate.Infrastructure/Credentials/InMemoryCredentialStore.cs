using System.Net;

namespace BoldMigrate.Infrastructure.Credentials;

public interface ICredentialStore : IDisposable
{
    void SetSourceCredential(string key, NetworkCredential credential);
    NetworkCredential? GetSourceCredential(string key);

    void SetDataSourcePassword(string dataSourceName, string password);
    string? GetDataSourcePassword(string dataSourceName);

    void SetDestinationToken(string token);
    string? GetDestinationToken();

    void ClearAll();
}

public class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, NetworkCredential> _sourceCreds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dsPasswords = new(StringComparer.OrdinalIgnoreCase);
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

    public void SetDataSourcePassword(string dataSourceName, string password)
    {
        _dsPasswords[dataSourceName] = password;
    }

    public string? GetDataSourcePassword(string dataSourceName)
    {
        _dsPasswords.TryGetValue(dataSourceName, out var pwd);
        return pwd;
    }

    public void SetDestinationToken(string token)
    {
        _destinationToken = token;
    }

    public string? GetDestinationToken() => _destinationToken;

    public void ClearAll()
    {
        _sourceCreds.Clear();
        _dsPasswords.Clear();
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
