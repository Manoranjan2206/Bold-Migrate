using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Enums;

/// <summary>
/// Storage mode for source server credentials.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CredentialMode
{
    InMemory,
    DpapiLocal,
    ExternalVault
}
