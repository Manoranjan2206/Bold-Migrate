using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Enums;

/// <summary>
/// API adapter used to communicate with the source server.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceApi
{
    RestV2,
    Soap2010
}
