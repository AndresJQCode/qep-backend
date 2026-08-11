using System.Reflection;
using Modules.Catalog.Api;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure;

namespace ArchitectureTests;

public sealed class CatalogLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Product).Assembly,
            typeof(ListProductsQuery).Assembly,
            typeof(CatalogInfrastructureExtensions).Assembly,
            typeof(ProductEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(ListProductsQuery).Assembly,
            typeof(CatalogInfrastructureExtensions).Assembly,
            typeof(ProductEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(CatalogInfrastructureExtensions).Assembly,
            typeof(ProductEndpoints).Assembly);
    }

    // Traducir un error de base es tarea de Infrastructure, no de Application: la violación
    // de unicidad en IX_products_tenant_code se vuelve catalog.product.code_taken ahí. Esto
    // protege la frontera con la que se cerró SDD-CT-06.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = typeof(ListProductsQuery).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

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
