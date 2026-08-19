using System.Reflection;
using Modules.Companies.Api;
using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Companies.Infrastructure;

namespace ArchitectureTests;

public sealed class CompaniesLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Company).Assembly,
            typeof(ListCompaniesQuery).Assembly,
            typeof(CompaniesInfrastructureExtensions).Assembly,
            typeof(CompanyEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(ListCompaniesQuery).Assembly,
            typeof(CompaniesInfrastructureExtensions).Assembly,
            typeof(CompanyEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(CompaniesInfrastructureExtensions).Assembly,
            typeof(CompanyEndpoints).Assembly);
    }

    // Traducir un error de base es tarea de Infrastructure, no de Application. La frontera se fijo
    // con SDD-CT-06, cuando la violacion de IX_companies_tenant_account_number se traducia en
    // CompaniesUnitOfWork; EMP-08 borro ese indice, pero la regla no se movio — el conflicto de
    // concurrencia se sigue traduciendo ahi, y el dia que vuelva a haber un indice unico su
    // traduccion va al mismo lugar.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(ListCompaniesQuery).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `companies` no se acopla a ningun otro modulo de negocio.
    ///
    /// La unica dependencia legitima hacia afuera es `Modules.Tenancy.Application`, que aporta
    /// <c>IExecutionContext</c> — el mismo permiso que ya tienen Catalog y Storage. Cualquier
    /// otra —empezando por `Modules.Catalog`, que es la que va a hacer falta el dia que una
    /// cotizacion cruce empresa y producto— va por un puerto declarado aca y su adaptador en
    /// `Bootstrapper`, que es el composition root y cuyo trabajo es exactamente cablear dos
    /// modulos.
    ///
    /// **Sin esta asercion esa decision es un comentario, no una regla**: el primero que necesite
    /// un dato de otro modulo agrega el ProjectReference y nada se pone rojo.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(ListCompaniesQuery).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Companies", StringComparison.Ordinal) &&
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
