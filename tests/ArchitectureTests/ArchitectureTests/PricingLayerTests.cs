using System.Reflection;
using Modules.Pricing.Api;
using Modules.Pricing.Application;
using Modules.Pricing.Domain;
using Modules.Pricing.Infrastructure;

namespace ArchitectureTests;

public sealed class PricingLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(PriceList).Assembly,
            typeof(ListPriceListsQuery).Assembly,
            typeof(PricingInfrastructureExtensions).Assembly,
            typeof(PriceListEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(ListPriceListsQuery).Assembly,
            typeof(PricingInfrastructureExtensions).Assembly,
            typeof(PriceListEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(PricingInfrastructureExtensions).Assembly,
            typeof(PriceListEndpoints).Assembly);
    }

    // Traducir un error de base es tarea de Infrastructure, no de Application: las violaciones de
    // IX_price_lists_tenant_name y de IX_price_lists_tenant_prefix se vuelven sus codigos de
    // dominio en PricingUnitOfWork. Mismo criterio que Catalog y Customers.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(ListPriceListsQuery).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `pricing` no se acopla a ningun otro modulo de negocio, ni siquiera a los dos que
    /// necesita para validar "esta lista esta en uso" (<c>catalog</c> vía sus escalas de
    /// producto, <c>customers</c> vía sus asignaciones). La unica dependencia legitima hacia
    /// afuera es <c>Modules.Tenancy.Application</c>, que aporta <c>IExecutionContext</c>/
    /// <c>IClock</c> — el mismo permiso que ya tienen Catalog, Storage, Companies y Customers.
    ///
    /// El camino para "esta lista esta en uso" es <c>IPriceListUsageLookup</c>, declarado acá y
    /// adaptado en <c>Bootstrapper</c> contra los repositorios de <c>catalog</c> y
    /// <c>customers</c> — mismo patron que <c>ICustomerGeographyLookup</c>.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(ListPriceListsQuery).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Pricing", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));
    }

    private static string?[] ReferenceNamesOf(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    private static void AssertDoesNotReference(
        Assembly source,
        params Assembly[] forbiddenAssemblies)
    {
        var references = source
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in forbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden.GetName().Name, references);
        }
    }
}
