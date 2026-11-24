using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Models;
using BoldMigrate.Engine.Preview;

namespace BoldMigrate.Engine.Tests.Preview;

public class StagedCatalogTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static CatalogItem Item(ItemType type, string path) => new()
    {
        RunId = RunId,
        ItemType = type,
        SourcePath = path,
        Name = path.Split('/').Last(),
        ParentPath = StagedCatalog.ParentOf(path)
    };

    private static StagedCatalog Catalog(params CatalogItem[] items) => new(RunId, items);

    [Theory]
    [InlineData("/Sales/Monthly", "/Sales/Monthly")]
    [InlineData("Sales/Monthly/", "/Sales/Monthly")]
    [InlineData("\\Sales\\Monthly", "/Sales/Monthly")]
    [InlineData("/Sales/./Monthly", "/Sales/Monthly")]
    [InlineData("/Sales/../Finance/Q1", "/Finance/Q1")]
    [InlineData("/../../Q1", "/Q1")]
    [InlineData("", "/")]
    public void NormalizePath_collapses_segments_and_never_climbs_above_root(string input, string expected)
    {
        Assert.Equal(expected, StagedCatalog.NormalizePath(input));
    }

    [Fact]
    public void Absolute_reference_resolves_from_root()
    {
        var ds = Item(ItemType.DataSource, "/Data Sources/Warehouse");
        var catalog = Catalog(ds, Item(ItemType.DataSource, "/Sales/Warehouse"));

        Assert.Same(ds, catalog.ResolveReference("/Data Sources/Warehouse", "/Sales", ItemType.DataSource));
    }

    [Fact]
    public void Relative_reference_resolves_against_the_report_folder_like_SSRS()
    {
        var local = Item(ItemType.DataSource, "/Sales/Warehouse");
        var sibling = Item(ItemType.DataSource, "/Data Sources/Warehouse");
        var catalog = Catalog(local, sibling);

        Assert.Same(local, catalog.ResolveReference("Warehouse", "/Sales", ItemType.DataSource));
        Assert.Same(sibling, catalog.ResolveReference("../Data Sources/Warehouse", "/Sales", ItemType.DataSource));
    }

    [Fact]
    public void Bare_name_resolves_only_when_unambiguous()
    {
        var unique = Item(ItemType.DataSet, "/Shared/Orders");
        var a = Item(ItemType.DataSource, "/A/Warehouse");
        var b = Item(ItemType.DataSource, "/B/Warehouse");
        var catalog = Catalog(unique, a, b);

        Assert.Same(unique, catalog.ResolveReference("Orders", "/Reports", ItemType.DataSet));
        Assert.Null(catalog.ResolveReference("Warehouse", "/Reports", ItemType.DataSource));
    }

    [Fact]
    public void Lookup_ignores_case_extension_and_item_type()
    {
        var report = Item(ItemType.Report, "/Sales/Monthly Summary");
        var catalog = Catalog(report, Item(ItemType.DataSet, "/Sales/Monthly Summary"));

        Assert.Same(report, catalog.FindByPath("/sales/monthly summary.rdl", ItemType.Report));
        Assert.Null(catalog.FindByPath("/Sales/Monthly Summary", ItemType.DataSource));
    }

    [Fact]
    public void Items_from_other_runs_are_never_served()
    {
        var foreign = Item(ItemType.Report, "/Sales/Monthly");
        foreign.RunId = Guid.NewGuid();

        Assert.Null(Catalog(foreign).FindByPath("/Sales/Monthly", ItemType.Report));
    }

    [Fact]
    public void Traversal_in_a_reference_stays_inside_the_catalog()
    {
        var catalog = Catalog(Item(ItemType.Report, "/Sales/Monthly"));

        Assert.Null(catalog.ResolveReference("../../../../Windows/win.ini", "/Sales", ItemType.Report));
    }
}
