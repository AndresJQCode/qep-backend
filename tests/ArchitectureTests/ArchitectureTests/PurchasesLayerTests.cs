using System.Reflection;
using Modules.Purchases.Api;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;
using Modules.Purchases.Infrastructure;

namespace ArchitectureTests;

public sealed class PurchasesLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(PurchaseOrder).Assembly,
            typeof(PurchasesPermissions).Assembly,
            typeof(PurchasesInfrastructureExtensions).Assembly,
            typeof(PurchaseOrderEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(PurchasesPermissions).Assembly,
            typeof(PurchasesInfrastructureExtensions).Assembly,
            typeof(PurchaseOrderEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(PurchasesInfrastructureExtensions).Assembly,
            typeof(PurchaseOrderEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. la unicidad del número de la orden por tenant) es tarea de Infrastructure,
    // no de Application.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(PurchasesPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal) ||
             name.StartsWith("StackExchange.Redis", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `purchases` no se acopla a ningún otro módulo de negocio. La única dependencia legítima hacia afuera es
    /// `Modules.Tenancy.*` (<c>IExecutionContext</c>, <c>Currencies</c>). Proveedor, catálogo y miembros entran por
    /// los puertos <c>IPurchases*</c> declarados acá, cuyos adaptadores viven en `Bootstrapper`. Sin esta aserción
    /// esa decisión es un comentario.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(PurchasesPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Purchases", StringComparison.Ordinal) &&
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
