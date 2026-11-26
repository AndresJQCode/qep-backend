using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.UnitTests;

/// <summary>D-5 y D-21: los módulos con que nace o se vincula un distribuidor.</summary>
public sealed class WholesaleModuleSetsTests
{
    [Fact]
    public void InvitedDistributorModulesAreCatalogSuppliersAndPurchasesOnly()
    {
        Assert.Equal(
            ["catalog", "suppliers", "purchases"],
            WholesaleModuleSets.InvitedDistributor.Select(key => key.Value));
        Assert.DoesNotContain(TenantModuleKeys.Shopify, WholesaleModuleSets.InvitedDistributor);
        Assert.Equal(["catalog", "suppliers", "purchases"], WholesaleModuleSets.LinkedDistributorRequired.Select(key => key.Value));
    }

    // Tenancy rechaza un lote que deja un módulo sin sus dependencias: cada conjunto tiene que venir cerrado.
    [Fact]
    public void EveryModuleSetIsClosedUnderItsDependencies()
    {
        foreach (var set in new[] { WholesaleModuleSets.InvitedDistributor, WholesaleModuleSets.LinkedDistributorRequired })
        {
            Assert.All(set, key => Assert.All(TenantModuleKeys.DependenciesOf(key), dependency => Assert.Contains(dependency, set)));
            Assert.Equal(set.Count, set.Distinct().Count());
        }
    }
}
