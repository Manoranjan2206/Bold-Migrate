using BoldMigrate.Core;
using Xunit;

namespace BoldMigrate.Core.Tests;

public class ErrorCatalogueTests
{
    [Fact]
    public void ErrorCatalogue_AllExpectedCodes_ExistAndHaveMessages()
    {
        var expectedCodes = new[]
        {
            ErrorCatalogue.Src001, ErrorCatalogue.Src002, ErrorCatalogue.Src003, ErrorCatalogue.Src004,
            ErrorCatalogue.Dst001, ErrorCatalogue.Dst002, ErrorCatalogue.Dst003, ErrorCatalogue.Dst004,
            ErrorCatalogue.Cred001, ErrorCatalogue.Cred002, ErrorCatalogue.Cred003,
            ErrorCatalogue.Xfrm001, ErrorCatalogue.Xfrm002, ErrorCatalogue.Xfrm003, ErrorCatalogue.Xfrm004,
            ErrorCatalogue.Vald001, ErrorCatalogue.Vald002, ErrorCatalogue.Vald003, ErrorCatalogue.Vald004, ErrorCatalogue.Vald005,
            ErrorCatalogue.Sys001, ErrorCatalogue.Sys002, ErrorCatalogue.Sys003, ErrorCatalogue.Sys004
        };

        foreach (var code in expectedCodes)
        {
            var def = ErrorCatalogue.GetDefinition(code);
            Assert.NotNull(def);
            Assert.False(string.IsNullOrWhiteSpace(def.MessageTemplate));
            Assert.False(string.IsNullOrWhiteSpace(def.Category));
        }
    }

    [Fact]
    public void FormatMessage_WithPlaceholders_SubstitutesValuesCorrectly()
    {
        var msg = ErrorCatalogue.FormatMessage(ErrorCatalogue.Dst003, new Dictionary<string, string>
        {
            ["serverMessage"] = "Schema element invalid"
        });

        Assert.Contains("Schema element invalid", msg);
    }
}
