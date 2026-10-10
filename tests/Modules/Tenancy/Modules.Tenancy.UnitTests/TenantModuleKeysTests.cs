using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-07, «Catálogo de módulos»: lista cerrada, en orden topológico, y la única
/// conversión de texto a clave.</summary>
public sealed class TenantModuleKeysTests
{
    [Fact]
    public void AllKeepsTheContractOrder()
    {
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos", "messaging"],
            TenantModuleKeys.All.Select(key => key.Value));
    }

    // FromStored recorre All una sola vez: si una clave apareciera antes que una de sus
    // dependencias, la vería todavía apagada y la contaría como apagada.
    [Fact]
    public void EveryKeyComesAfterAllOfItsDependencies()
    {
        for (var index = 0; index < TenantModuleKeys.All.Count; index++)
        {
            var key = TenantModuleKeys.All[index];
            foreach (var dependency in TenantModuleKeys.DependenciesOf(key))
            {
                Assert.Contains(dependency, TenantModuleKeys.All);
                Assert.True(
                    TenantModuleKeys.All.ToList().IndexOf(dependency) < index,
                    $"{dependency} must come before {key} in TenantModuleKeys.All.");
            }
        }
    }

    [Fact]
    public void DependenciesAreTheOnesOfTheSpec()
    {
        Assert.Equal(
            [TenantModuleKeys.Catalog, TenantModuleKeys.Customers, TenantModuleKeys.Companies],
            TenantModuleKeys.DependenciesOf(TenantModuleKeys.Quotations));
        Assert.Equal([TenantModuleKeys.Quotations], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Orders));
        Assert.Equal(
            [TenantModuleKeys.Catalog, TenantModuleKeys.Companies],
            TenantModuleKeys.DependenciesOf(TenantModuleKeys.Pos));
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Reporting));
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Catalog));
    }

    // Spec 2026-10-09 §6.2: messaging es vendible y se prende por tenant, como pos.
    [Fact]
    public void DefaultForNewTenantsIsEverythingButPosAndMessaging()
    {
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting"],
            TenantModuleKeys.DefaultForNewTenants.Select(key => key.Value));
    }

    [Fact]
    public void MessagingHasNoDependencies() =>
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Messaging));

    [Fact]
    public void ParseReturnsTheSameInstanceForEveryKey()
    {
        foreach (var key in TenantModuleKeys.All)
        {
            Assert.Same(key, TenantModuleKey.Parse(key.Value));
        }
    }

    // Igual que el CHECK de la tabla: ni claves inventadas ni otra capitalización.
    [Theory]
    [InlineData("inventory")]
    [InlineData("")]
    [InlineData("Catalog")]
    public void ParseRejectsAnythingElse(string value)
    {
        Assert.Throws<ArgumentException>(() => TenantModuleKey.Parse(value));
    }
}
