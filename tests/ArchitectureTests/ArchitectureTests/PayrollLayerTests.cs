using System.Reflection;
using Modules.Payroll.Api;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using Modules.Payroll.Infrastructure;

namespace ArchitectureTests;

public sealed class PayrollLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(PayrollDomainException).Assembly,
            typeof(PayrollPermissions).Assembly,
            typeof(PayrollInfrastructureExtensions).Assembly,
            typeof(PayrollEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(PayrollPermissions).Assembly,
            typeof(PayrollInfrastructureExtensions).Assembly,
            typeof(PayrollEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(PayrollInfrastructureExtensions).Assembly,
            typeof(PayrollEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. la unicidad del documento del empleado) es tarea de Infrastructure, no de Application.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(PayrollPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal) ||
             name.StartsWith("StackExchange.Redis", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `payroll` no se acopla a ningún otro módulo de negocio. La única dependencia legítima hacia afuera es
    /// `Modules.Tenancy.Application` (<c>IExecutionContext</c>, <c>ITenantModules</c>). Empleados, contratos, empresas y valores
    /// legales entran por los puertos declarados acá, cuyos adaptadores viven en `Bootstrapper`.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(PayrollPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Payroll", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));
    }

    // Empleados y contratos entran sólo por los puertos de Payroll (adaptador en Bootstrapper).
    [Fact]
    public void NoPayrollLayerReferencesHr() => AssertNoPayrollLayerReferencesPrefix("Modules.Hr");

    [Fact]
    public void NoPayrollLayerReferencesCompanies() => AssertNoPayrollLayerReferencesPrefix("Modules.Companies");

    [Fact]
    public void NoPayrollLayerReferencesIdentity() => AssertNoPayrollLayerReferencesPrefix("Modules.Identity");

    private static void AssertNoPayrollLayerReferencesPrefix(string prefix)
    {
        Assembly[] layers =
        [
            typeof(PayrollDomainException).Assembly,
            typeof(PayrollPermissions).Assembly,
            typeof(PayrollInfrastructureExtensions).Assembly,
            typeof(PayrollEndpoints).Assembly,
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
