using System.Reflection;
using Modules.Suppliers.Api;
using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;
using Modules.Suppliers.Infrastructure;

namespace ArchitectureTests;

public sealed class SuppliersLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Supplier).Assembly,
            typeof(SuppliersPermissions).Assembly,
            typeof(SuppliersInfrastructureExtensions).Assembly,
            typeof(SupplierEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(SuppliersPermissions).Assembly,
            typeof(SuppliersInfrastructureExtensions).Assembly,
            typeof(SupplierEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(SuppliersInfrastructureExtensions).Assembly,
            typeof(SupplierEndpoints).Assembly);
    }

    // Traducir un error de base es tarea de Infrastructure, no de Application: desde SUP-05 existe
    // el índice único IX_suppliers_tenant_identification, y el SuppliersUnitOfWork traduce su
    // violación a suppliers.supplier.identification_taken.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(SuppliersPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `suppliers` no se acopla a ningún otro módulo de negocio. La única dependencia legítima hacia
    /// afuera es `Modules.Tenancy.Application` (<c>IExecutionContext</c>). Geography entra por
    /// <c>ISupplierGeographyLookup</c>, un puerto declarado acá cuyo adaptador vive en `Bootstrapper`.
    /// Sin esta aserción esa decisión es un comentario, no una regla.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(SuppliersPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Suppliers", StringComparison.Ordinal) &&
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
