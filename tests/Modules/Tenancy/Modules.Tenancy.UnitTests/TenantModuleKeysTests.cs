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
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos", "messaging", "pipeline", "shopify", "automations", "suppliers", "billing", "developer", "loans", "store", "ai", "copilot", "support", "purchases", "wholesale", "siigo", "hr", "payroll", "scheduling", "wms"],
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
        // Un pedido se crea sin cotización (spec 2026-10-15): orders y quotations son independientes.
        Assert.Equal(
            [TenantModuleKeys.Catalog, TenantModuleKeys.Customers, TenantModuleKeys.Companies],
            TenantModuleKeys.DependenciesOf(TenantModuleKeys.Orders));
        Assert.Equal(
            [TenantModuleKeys.Catalog, TenantModuleKeys.Companies],
            TenantModuleKeys.DependenciesOf(TenantModuleKeys.Pos));
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Reporting));
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Catalog));
    }

    // Spec 2026-10-09 §6.2 y spec 2026-10-13 D3: messaging y pipeline son vendibles y se prenden por tenant, como pos.
    [Fact]
    public void DefaultForNewTenantsAreTheSixBaseModulesPlusShopify()
    {
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "shopify"],
            TenantModuleKeys.DefaultForNewTenants.Select(key => key.Value));
    }

    [Fact]
    public void ShopifyDependsOnOrders() =>
        Assert.Equal([TenantModuleKeys.Orders], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Shopify));

    // Spec 2026-10-21 D-C11: el constructor de chatbots vive sobre la bandeja; fuera del signup.
    [Fact]
    public void AutomationsDependsOnMessaging()
    {
        Assert.Equal([TenantModuleKeys.Messaging], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Automations));
    }

    // Spec 2026-10-23 D-12: el directorio de proveedores; se prende por tenant.
    [Fact]
    public void SuppliersIsLastWithoutDependenciesAndOutsideTheDefaults()
    {
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Suppliers));
        Assert.DoesNotContain(TenantModuleKeys.Suppliers, TenantModuleKeys.DefaultForNewTenants);
    }

    // Spec 2026-10-29: cobros recurrentes; se prende por tenant y exige customers.
    [Fact]
    public void ServiceContractsDependsOnCustomersAndIsOutsideTheDefaults()
    {
        Assert.Equal([TenantModuleKeys.Customers], TenantModuleKeys.DependenciesOf(TenantModuleKeys.ServiceContracts));
        Assert.DoesNotContain(TenantModuleKeys.ServiceContracts, TenantModuleKeys.DefaultForNewTenants);
    }

    // Spec 2026-10-30, D7: las claves de API de integración; se prende por tenant.
    [Fact]
    public void DeveloperHasNoDependenciesAndIsOffForNewTenants()
    {
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Developer));
        Assert.DoesNotContain(TenantModuleKeys.Developer, TenantModuleKeys.DefaultForNewTenants);
    }

    // Spec 2026-10-31: préstamos; se emite a un cliente, así que depende de customers.
    [Fact]
    public void LoansDependsOnCustomersAndIsOffForNewTenants()
    {
        Assert.Equal([TenantModuleKeys.Customers], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Loans));
        Assert.DoesNotContain(TenantModuleKeys.Loans, TenantModuleKeys.DefaultForNewTenants);
    }

    // Spec 2026-11-03 §4.2: la tienda pública; una solicitud se convierte en pedido, así que depende de orders.
    [Fact]
    public void StoreDependsOnOrdersAndIsOffForNewTenants()
    {
        Assert.Equal([TenantModuleKeys.Orders], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Store));
        Assert.DoesNotContain(TenantModuleKeys.Store, TenantModuleKeys.DefaultForNewTenants);
    }

    // Spec 2026-10-29 D-1b (agentes IA): un agente responde por la bandeja, igual que un chatbot; se prende por tenant.
    [Fact]
    public void AiDependsOnMessagingAndIsOffForNewTenants()
    {
        Assert.Equal([TenantModuleKeys.Messaging], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Ai));
        Assert.DoesNotContain(TenantModuleKeys.Ai, TenantModuleKeys.DefaultForNewTenants);
    }

    // Spec 2026-11-17 (copiloto), D-C2 y OC-4: no cuelga de ai (que exige messaging); va justo después de ai (adenda D-14) y se
    // prende por tenant.
    [Fact]
    public void CopilotIsAModuleWithoutDependenciesOutOfTheDefaults()
    {
        var all = TenantModuleKeys.All.ToList();
        Assert.Equal("copilot", TenantModuleKeys.Copilot.Value);
        Assert.Equal(all.IndexOf(TenantModuleKeys.Ai) + 1, all.IndexOf(TenantModuleKeys.Copilot));
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Copilot));
        Assert.DoesNotContain(TenantModuleKeys.Copilot, TenantModuleKeys.DefaultForNewTenants);
    }

    // Spec 2026-11-14 (soporte, C9): tickets y PQRSF; no depende de ningún módulo y se prende por tenant.
    [Fact]
    public void SupportHasNoDependenciesAndIsOffForNewTenants()
    {
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Support));
        Assert.DoesNotContain(TenantModuleKeys.Support, TenantModuleKeys.DefaultForNewTenants);
    }

    // Spec 2026-11-17 (compras): la línea es una variante del catálogo y el proveedor sale del directorio; se prende por
    // tenant. Ya no es la última: mayorista va después.
    [Fact]
    public void PurchasesDependsOnSuppliersAndCatalogAndIsOffForNewTenants()
    {
        Assert.Equal([TenantModuleKeys.Suppliers, TenantModuleKeys.Catalog], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Purchases));
        Assert.DoesNotContain(TenantModuleKeys.Purchases, TenantModuleKeys.DefaultForNewTenants);
    }

    // Spec 2026-11-17 (mayorista): el pedido del distribuidor es un Order; se prende por tenant. Ya no es la última: siigo va
    // después.
    [Fact]
    public void WholesaleDependsOnOrdersAndIsOffForNewTenants()
    {
        Assert.Equal([TenantModuleKeys.Orders], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Wholesale));
        Assert.DoesNotContain(TenantModuleKeys.Wholesale, TenantModuleKeys.DefaultForNewTenants);
    }

    // Spec 2026-11-18 (integración Siigo): se factura un pedido, así que depende de orders; se prende por tenant.
    [Fact]
    public void SiigoIsAKnownKeyOutsideTheDefaults()
    {
        Assert.Same(TenantModuleKeys.Siigo, TenantModuleKey.Parse("siigo"));
        Assert.DoesNotContain(TenantModuleKeys.Siigo, TenantModuleKeys.DefaultForNewTenants);
        Assert.Equal([TenantModuleKeys.Orders], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Siigo));
    }

    // Spec 2026-11-20 (RRHH fase 1): la empresa empleadora es una empresa de Companies; se prende por tenant.
    [Fact]
    public void HrIsAKnownKeyOutsideTheDefaults()
    {
        Assert.Same(TenantModuleKeys.Hr, TenantModuleKey.Parse("hr"));
        Assert.DoesNotContain(TenantModuleKeys.Hr, TenantModuleKeys.DefaultForNewTenants);
    }

    [Fact]
    public void HrDependsOnCompanies() =>
        Assert.Equal([TenantModuleKeys.Companies], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Hr));

    // Spec 2026-11-21 (nómina, proyecto 1): liquida a los empleados de Hr; se prende por tenant.
    [Fact]
    public void PayrollIsAKnownKeyOutsideTheDefaults()
    {
        Assert.Same(TenantModuleKeys.Payroll, TenantModuleKey.Parse("payroll"));
        Assert.Equal(26, TenantModuleKeys.All.Count);
        Assert.DoesNotContain(TenantModuleKeys.Payroll, TenantModuleKeys.DefaultForNewTenants);
    }

    [Fact]
    public void PayrollDependsOnHr() =>
        Assert.Equal([TenantModuleKeys.Hr], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Payroll));

    // Spec 2026-11-21 (Agenda): toda cita es de un cliente; se prende por tenant. Ya no es la última: wms va después.
    [Fact]
    public void SchedulingIsAKnownKeyOutsideTheDefaults()
    {
        Assert.Same(TenantModuleKeys.Scheduling, TenantModuleKey.Parse("scheduling"));
        Assert.Equal(26, TenantModuleKeys.All.Count);
        Assert.DoesNotContain(TenantModuleKeys.Scheduling, TenantModuleKeys.DefaultForNewTenants);
    }

    [Fact]
    public void SchedulingDependsOnCustomers() =>
        Assert.Equal([TenantModuleKeys.Customers], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Scheduling));

    // Spec WMS 2026-10-31 §1.1: inventario de variantes del catálogo; se prende por tenant.
    [Fact]
    public void WmsIsLastDependsOnCatalogAndIsOffForNewTenants()
    {
        Assert.Equal("wms", TenantModuleKeys.All[^1].Value);
        Assert.Equal([TenantModuleKeys.Catalog], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Wms));
        Assert.DoesNotContain(TenantModuleKeys.Wms, TenantModuleKeys.DefaultForNewTenants);
    }

    [Fact]
    public void MessagingHasNoDependencies() =>
        Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Messaging));

    // Spec 2026-10-13 D30: un negocio siempre tiene cliente. Messaging y Quotations lo enriquecen, pero no lo condicionan.
    [Fact]
    public void PipelineDependsOnlyOnCustomers() =>
        Assert.Equal([TenantModuleKeys.Customers], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Pipeline));

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
