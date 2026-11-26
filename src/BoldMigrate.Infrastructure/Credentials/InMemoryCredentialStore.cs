using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoldMigrate.Core.Enums;

namespace BoldMigrate.Infrastructure.Credentials;

public interface ICredentialStore : IDisposable
{
    CredentialMode ActiveMode { get; }
    void SetMode(CredentialMode mode);

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

/// <summary>
/// Thread-safe credential vault supporting InMemory (volatile) and encrypted local persistence (DPAPI on Windows / AES-GCM key on Linux).
/// </summary>
public class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, NetworkCredential> _sourceCreds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NetworkCredential> _dsCreds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly string? _encryptedVaultPath;
    private readonly string? _keyFilePath;
    private string? _destinationToken;
    private CredentialMode _mode = CredentialMode.InMemory;
    private bool _disposed;

    public InMemoryCredentialStore(string? encryptedStoreDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(encryptedStoreDirectory))
        {
            Directory.CreateDirectory(encryptedStoreDirectory);
            _encryptedVaultPath = Path.Combine(encryptedStoreDirectory, "credentials.vault");
            _keyFilePath = Path.Combine(encryptedStoreDirectory, "credentials.key");
            TryLoadEncryptedVault();
        }
    }

    public CredentialMode ActiveMode
    {
        get { lock (_lock) return _mode; }
    }

    public void SetMode(CredentialMode mode)
    {
        lock (_lock)
        {
            _mode = mode;
            if (mode == CredentialMode.InMemory)
            {
                DeleteEncryptedVault();
            }
            else if (mode == CredentialMode.DpapiLocal)
            {
                PersistEncryptedVault();
            }
        }
    }

    public void SetSourceCredential(string key, NetworkCredential credential)
    {
        lock (_lock)
        {
            _sourceCreds[key] = credential;
            if (_mode == CredentialMode.DpapiLocal) PersistEncryptedVault();
        }
    }

    public NetworkCredential? GetSourceCredential(string key)
    {
        lock (_lock) return _sourceCreds.TryGetValue(key, out var cred) ? cred : null;
    }

    public void SetDataSourceCredential(string dataSourceKey, NetworkCredential credential)
    {
        lock (_lock)
        {
            _dsCreds[dataSourceKey] = credential;
            if (_mode == CredentialMode.DpapiLocal) PersistEncryptedVault();
        }
    }

    public NetworkCredential? GetDataSourceCredential(string dataSourceKey)
    {
        lock (_lock) return _dsCreds.TryGetValue(dataSourceKey, out var cred) ? cred : null;
    }

    public void RemoveDataSourceCredential(string dataSourceKey)
    {
        lock (_lock)
        {
            _dsCreds.Remove(dataSourceKey);
            if (_mode == CredentialMode.DpapiLocal) PersistEncryptedVault();
        }
    }

    public void SetDestinationToken(string token)
    {
        lock (_lock)
        {
            _destinationToken = token;
            if (_mode == CredentialMode.DpapiLocal) PersistEncryptedVault();
        }
    }

    public string? GetDestinationToken()
    {
        lock (_lock) return _destinationToken;
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _sourceCreds.Clear();
            _dsCreds.Clear();
            _destinationToken = null;
            DeleteEncryptedVault();
        }
    }

    private sealed record PersistedVaultDto(
        Dictionary<string, CredDto> SourceCreds,
        Dictionary<string, CredDto> DsCreds,
        string? DestinationToken);

    private sealed record CredDto(string UserName, string Password, string Domain);

    private void PersistEncryptedVault()
    {
        if (string.IsNullOrEmpty(_encryptedVaultPath)) return;
        try
        {
            var dto = new PersistedVaultDto(
                _sourceCreds.ToDictionary(k => k.Key, k => new CredDto(k.Value.UserName, k.Value.Password, k.Value.Domain)),
                _dsCreds.ToDictionary(k => k.Key, k => new CredDto(k.Value.UserName, k.Value.Password, k.Value.Domain)),
                _destinationToken);

            var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto));
            byte[] protectedBytes;

            if (OperatingSystem.IsWindows())
            {
                protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            }
            else
            {
                protectedBytes = ProtectAesGcm(plain);
            }

            File.WriteAllBytes(_encryptedVaultPath, protectedBytes);
        }
        catch
        {
            // Never crash callers on optional encrypted vault flush failure
        }
    }

    private void TryLoadEncryptedVault()
    {
        if (string.IsNullOrEmpty(_encryptedVaultPath) || !File.Exists(_encryptedVaultPath)) return;
        try
        {
            var cipher = File.ReadAllBytes(_encryptedVaultPath);
            byte[] plain;
            if (OperatingSystem.IsWindows())
            {
                plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            }
            else
            {
                plain = UnprotectAesGcm(cipher);
            }

            var dto = JsonSerializer.Deserialize<PersistedVaultDto>(plain);
            if (dto == null) return;

            _mode = CredentialMode.DpapiLocal;
            foreach (var kv in dto.SourceCreds)
                _sourceCreds[kv.Key] = new NetworkCredential(kv.Value.UserName, kv.Value.Password, kv.Value.Domain);
            foreach (var kv in dto.DsCreds)
                _dsCreds[kv.Key] = new NetworkCredential(kv.Value.UserName, kv.Value.Password, kv.Value.Domain);
            _destinationToken = dto.DestinationToken;
        }
        catch
        {
            // Corrupt or wrong-user vault is ignored cleanly
        }
    }

    private void DeleteEncryptedVault()
    {
        try
        {
            if (!string.IsNullOrEmpty(_encryptedVaultPath) && File.Exists(_encryptedVaultPath))
                File.Delete(_encryptedVaultPath);
        }
        catch { }
    }

    private byte[] GetOrCreateAesKey()
    {
        if (string.IsNullOrEmpty(_keyFilePath)) return RandomNumberGenerator.GetBytes(32);
        if (File.Exists(_keyFilePath))
        {
            var existing = File.ReadAllBytes(_keyFilePath);
            if (existing.Length == 32) return existing;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_keyFilePath, key);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(_keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
        return key;
    }

    private byte[] ProtectAesGcm(byte[] plain)
    {
        var key = GetOrCreateAesKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);

        var result = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, 12);
        Buffer.BlockCopy(tag, 0, result, 12, 16);
        Buffer.BlockCopy(cipher, 0, result, 28, cipher.Length);
        return result;
    }

    private byte[] UnprotectAesGcm(byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException("Invalid encrypted vault payload.");
        var key = GetOrCreateAesKey();
        var nonce = blob[..12];
        var tag = blob[12..28];
        var cipher = blob[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_lock)
            {
                _sourceCreds.Clear();
                _dsCreds.Clear();
                _destinationToken = null;
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
