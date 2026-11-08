using System.Reflection;
using Modules.Store.Api;
using Modules.Store.Application;
using Modules.Store.Domain;
using Modules.Store.Infrastructure;

namespace ArchitectureTests;

public sealed class StoreLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(OnlineStore).Assembly,
            typeof(StorePermissions).Assembly,
            typeof(StoreInfrastructureExtensions).Assembly,
            typeof(StoreEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(StorePermissions).Assembly,
            typeof(StoreInfrastructureExtensions).Assembly,
            typeof(StoreEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(StoreInfrastructureExtensions).Assembly,
            typeof(StoreEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. la unicidad del dominio o del número de solicitud) es tarea de Infrastructure.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(StorePermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `store` no se acopla a ningún otro módulo de negocio. La única dependencia legítima hacia afuera es
    /// `Modules.Tenancy.Application` (<c>IExecutionContext</c>, <c>ITenantModules</c>). Clientes, geografía y cotizaciones entran por
    /// los puertos <c>IStore*</c> declarados acá, cuyos adaptadores viven en `Bootstrapper`. Sin esta aserción esa decisión es un comentario.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(StorePermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Store", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));
    }

    /// <summary>Lo mismo, nombrado: ninguna capa de `store` referencia Customers, Geography ni Quotations.</summary>
    [Fact]
    public void NoStoreLayerReferencesCustomersGeographyOrQuotations()
    {
        Assembly[] layers =
        [
            typeof(OnlineStore).Assembly,
            typeof(StorePermissions).Assembly,
            typeof(StoreInfrastructureExtensions).Assembly,
            typeof(StoreEndpoints).Assembly,
        ];

        foreach (var layer in layers)
        {
            Assert.DoesNotContain(ReferenceNamesOf(layer), name =>
                name is not null &&
                (name.StartsWith("Modules.Customers", StringComparison.Ordinal) ||
                 name.StartsWith("Modules.Geography", StringComparison.Ordinal) ||
                 name.StartsWith("Modules.Quotations", StringComparison.Ordinal)));
        }
    }

    /// <summary>Los compradores dependen de Store, nunca al revés: ninguna capa de `store` referencia `Modules.Buyers*`.</summary>
    [Fact]
    public void NoStoreLayerReferencesBuyers()
    {
        Assembly[] layers =
        [
            typeof(OnlineStore).Assembly,
            typeof(StorePermissions).Assembly,
            typeof(StoreInfrastructureExtensions).Assembly,
            typeof(StoreEndpoints).Assembly,
        ];

        foreach (var layer in layers)
        {
            Assert.DoesNotContain(ReferenceNamesOf(layer), name =>
                name is not null &&
                name.StartsWith("Modules.Buyers", StringComparison.Ordinal));
        }
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
