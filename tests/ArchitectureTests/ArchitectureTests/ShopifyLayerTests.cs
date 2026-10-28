using System.Reflection;
using Modules.Shopify.Api;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Shopify (spec 2026-10-15), copia de <see cref="PosLayerTests"/>. La regla que más importa es la última:
/// Shopify lee conexiones, clientes, productos, ciudades y crea pedidos de otros módulos, y todo eso entra por puertos con
/// adaptador en Bootstrapper (§6.2). Sin esta aserción, el primero que necesite un producto agrega el ProjectReference a
/// Catalog y nada se pone rojo. Tenancy (módulos, membresías) y Audit (la auditoría atómica propia) sí están permitidos.
/// </summary>
public sealed class ShopifyLayerTests
{
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(ShopifyDomainException).Assembly,
        typeof(IShopifyUnitOfWork).Assembly,
        typeof(ShopifyInfrastructureExtensions).Assembly,
        typeof(ShopifyEndpoints).Assembly,
    ];

    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(ShopifyDomainException).Assembly,
            typeof(IShopifyUnitOfWork).Assembly,
            typeof(ShopifyInfrastructureExtensions).Assembly,
            typeof(ShopifyEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(IShopifyUnitOfWork).Assembly,
            typeof(ShopifyInfrastructureExtensions).Assembly,
            typeof(ShopifyEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(ShopifyInfrastructureExtensions).Assembly,
            typeof(ShopifyEndpoints).Assembly);
    }

    // Traducir errores de base (los únicos del espejo y de las entregas) es tarea de ShopifyUnitOfWork, en Infrastructure.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(IShopifyUnitOfWork).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    [Fact]
    public void ApplicationOnlyReferencesTenancyAndAuditAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(IShopifyUnitOfWork).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Shopify", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Audit", StringComparison.Ordinal));
    }

    // §6.2: la conexión (Integrations), el pedido (Quotations), el cliente (Customers), el producto (Catalog) y la ciudad
    // (Geography) entran por puertos de Shopify.Application con adaptador en Bootstrapper, en ninguna capa del módulo.
    [Theory]
    [InlineData("Modules.Integrations")]
    [InlineData("Modules.Quotations")]
    [InlineData("Modules.Customers")]
    [InlineData("Modules.Catalog")]
    [InlineData("Modules.Geography")]
    public void ShopifyDoesNotReferenceOtherBusinessModules(string forbiddenPrefix)
    {
        foreach (var assembly in ModuleAssemblies)
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null && name.StartsWith(forbiddenPrefix, StringComparison.Ordinal));
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
