using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Enums;

/// <summary>
/// Type of catalog item discovered on the source server.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ItemType
{
    Folder,
    DataSource,
    DataSet,
    Report,
    Resource,
    Unsupported
}
