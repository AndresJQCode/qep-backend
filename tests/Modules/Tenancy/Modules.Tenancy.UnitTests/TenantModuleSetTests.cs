using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-07, «Módulos efectivos (fail closed)» y causas raíz.</summary>
public sealed class TenantModuleSetTests
{
    private static TenantModuleSet AllBut(params TenantModuleKey[] missing) =>
        TenantModuleSet.FromStored(TenantModuleKeys.All.Except(missing));

    [Fact]
    public void WithoutCustomersQuotationsAndOrdersAreOff()
    {
        var set = AllBut(TenantModuleKeys.Customers);

        Assert.False(set.IsEnabled(TenantModuleKeys.Quotations));
        Assert.False(set.IsEnabled(TenantModuleKeys.Orders));
        Assert.True(set.IsContracted(TenantModuleKeys.Quotations));
        Assert.True(set.IsEnabled(TenantModuleKeys.Catalog));
        Assert.True(set.IsEnabled(TenantModuleKeys.Reporting));
        Assert.True(set.IsEnabled(TenantModuleKeys.Pos));
    }

    // Causa raíz, no la dependencia directa: contratar quotations no arreglaría orders.
    [Fact]
    public void MissingDependenciesNamesTheRootCause()
    {
        var set = AllBut(TenantModuleKeys.Customers);

        Assert.Equal([TenantModuleKeys.Customers], set.MissingDependencies(TenantModuleKeys.Quotations));
        Assert.Equal([TenantModuleKeys.Customers], set.MissingDependencies(TenantModuleKeys.Orders));
    }

    [Fact]
    public void SeveralRootCausesComeInTheOrderOfAll()
    {
        var set = AllBut(TenantModuleKeys.Customers, TenantModuleKeys.Catalog);

        Assert.Equal(
            [TenantModuleKeys.Catalog, TenantModuleKeys.Customers],
            set.MissingDependencies(TenantModuleKeys.Orders));
    }

    [Fact]
    public void AKeyNotContractedOrEnabledReportsNoMissingDependencies()
    {
        var set = AllBut(TenantModuleKeys.Customers);

        Assert.Empty(set.MissingDependencies(TenantModuleKeys.Customers));
        Assert.Empty(set.MissingDependencies(TenantModuleKeys.Catalog));
    }

    [Fact]
    public void StoredAndEffectiveKeepTheOrderOfAll()
    {
        var set = TenantModuleSet.FromStored([TenantModuleKeys.Reporting, TenantModuleKeys.Catalog]);

        Assert.Equal([TenantModuleKeys.Catalog, TenantModuleKeys.Reporting], set.Stored);
        Assert.Equal([TenantModuleKeys.Catalog, TenantModuleKeys.Reporting], set.Effective);
    }

    [Fact]
    public void EmptyHasNothing()
    {
        Assert.Empty(TenantModuleSet.Empty.Stored);
        Assert.Empty(TenantModuleSet.Empty.Effective);
        Assert.All(TenantModuleKeys.All, key => Assert.False(TenantModuleSet.Empty.IsEnabled(key)));
    }

    [Fact]
    public void FromStoredIgnoresRepeatedKeys()
    {
        var set = TenantModuleSet.FromStored([TenantModuleKeys.Catalog, TenantModuleKeys.Catalog]);

        Assert.Equal([TenantModuleKeys.Catalog], set.Stored);
    }
}
