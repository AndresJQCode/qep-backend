using System.Reflection;
using Modules.Customers.Api;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure;

namespace ArchitectureTests;

public sealed class CustomersLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Customer).Assembly,
            typeof(ListCustomersQuery).Assembly,
            typeof(CustomersInfrastructureExtensions).Assembly,
            typeof(CustomerEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(ListCustomersQuery).Assembly,
            typeof(CustomersInfrastructureExtensions).Assembly,
            typeof(CustomerEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(CustomersInfrastructureExtensions).Assembly,
            typeof(CustomerEndpoints).Assembly);
    }

    // Traducir un error de base es tarea de Infrastructure, no de Application: las violaciones de
    // IX_customers_tenant_identification y de IX_customers_tenant_cuc se vuelven sus codigos de
    // dominio en CustomersUnitOfWork. Esto protege la frontera con la que se cerro SDD-CT-06.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(ListCustomersQuery).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `customers` no se acopla a ningun otro modulo de negocio.
    ///
    /// La unica dependencia legitima hacia afuera es `Modules.Tenancy.Application`, que aporta
    /// <c>IExecutionContext</c> — el mismo permiso que ya tienen Catalog, Storage y Companies.
    ///
    /// Esta asercion importa mas aca que en los otros modulos: `customers` tiene **dos**
    /// dependencias declaradas en su ficha hacia modulos que todavia no existen —`pricing` para la
    /// lista de precios e `identifiers` para el CUC—, y el atajo tentador el dia que lleguen es un
    /// ProjectReference. Sin esta prueba ese atajo compila y nada se pone rojo. El camino es un
    /// puerto declarado en Application y su adaptador en `Bootstrapper`, que es el composition root
    /// y cuyo trabajo es exactamente cablear dos modulos — <c>ICucGenerator</c> ya esta escrito asi.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(ListCustomersQuery).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Customers", StringComparison.Ordinal) &&
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
