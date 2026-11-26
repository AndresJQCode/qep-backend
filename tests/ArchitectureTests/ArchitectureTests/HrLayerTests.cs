using System.Reflection;
using Modules.Hr.Api;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Hr.Infrastructure;

namespace ArchitectureTests;

public sealed class HrLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Employee).Assembly,
            typeof(HrPermissions).Assembly,
            typeof(HrInfrastructureExtensions).Assembly,
            typeof(HrEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(HrPermissions).Assembly,
            typeof(HrInfrastructureExtensions).Assembly,
            typeof(HrEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(HrInfrastructureExtensions).Assembly,
            typeof(HrEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. la unicidad del documento del empleado) es tarea de Infrastructure, no de Application.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(HrPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal) ||
             name.StartsWith("StackExchange.Redis", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `hr` no se acopla a ningún otro módulo de negocio. La única dependencia legítima hacia afuera es
    /// `Modules.Tenancy.Application` (<c>IExecutionContext</c>, <c>ITenantModules</c>). Empresas, ciudades, países, archivos y
    /// miembros entran por los puertos <c>IHr*</c> declarados acá, cuyos adaptadores viven en `Bootstrapper`. Sin esta
    /// aserción esa decisión es un comentario.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(HrPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Hr", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));
    }

    // La empresa empleadora entra sólo por IHrCompanyLookup (adaptador en Bootstrapper).
    [Fact]
    public void NoHrLayerReferencesCompanies() => AssertNoHrLayerReferencesPrefix("Modules.Companies");

    // Ciudades y países entran sólo por IHrGeographyLookup (adaptador en Bootstrapper).
    [Fact]
    public void NoHrLayerReferencesGeography() => AssertNoHrLayerReferencesPrefix("Modules.Geography");

    private static void AssertNoHrLayerReferencesPrefix(string prefix)
    {
        Assembly[] layers =
        [
            typeof(Employee).Assembly,
            typeof(HrPermissions).Assembly,
            typeof(HrInfrastructureExtensions).Assembly,
            typeof(HrEndpoints).Assembly,
        ];

        foreach (var layer in layers)
        {
            Assert.DoesNotContain(ReferenceNamesOf(layer), name =>
                name is not null && name.StartsWith(prefix, StringComparison.Ordinal));
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
