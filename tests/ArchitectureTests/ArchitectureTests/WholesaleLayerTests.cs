using System.Reflection;
using Modules.Wholesale.Api;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;
using Modules.Wholesale.Infrastructure;

namespace ArchitectureTests;

public sealed class WholesaleLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Distributor).Assembly,
            typeof(WholesalePermissions).Assembly,
            typeof(WholesaleInfrastructureExtensions).Assembly,
            typeof(WholesaleEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(WholesalePermissions).Assembly,
            typeof(WholesaleInfrastructureExtensions).Assembly,
            typeof(WholesaleEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(WholesaleInfrastructureExtensions).Assembly,
            typeof(WholesaleEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. la unicidad del nombre de una lista o del documento de un distribuidor) es tarea de
    // Infrastructure, no de Application.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(WholesalePermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal) ||
             name.StartsWith("StackExchange.Redis", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `wholesale` no se acopla a ningún otro módulo de negocio. La única dependencia legítima hacia afuera es
    /// `Modules.Tenancy.*` (<c>IExecutionContext</c>, el alta del tenant invitado, los módulos que se prenden al vincular).
    /// Catálogo, empresas, miembros, proveedores y clientes entran por puertos declarados acá, cuyos adaptadores viven en
    /// `Bootstrapper`. Sin esta aserción esa decisión es un comentario.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(WholesalePermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Wholesale", StringComparison.Ordinal) &&
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
