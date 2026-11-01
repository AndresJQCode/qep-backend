using System.Reflection;
using Modules.Loans.Api;
using Modules.Loans.Application;
using Modules.Loans.Domain;
using Modules.Loans.Infrastructure;

namespace ArchitectureTests;

public sealed class LoansLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Loan).Assembly,
            typeof(LoansPermissions).Assembly,
            typeof(LoansInfrastructureExtensions).Assembly,
            typeof(LoansEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(LoansPermissions).Assembly,
            typeof(LoansInfrastructureExtensions).Assembly,
            typeof(LoansEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(LoansInfrastructureExtensions).Assembly,
            typeof(LoansEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. la unicidad de (loan_id, period_end) que hace idempotente la
    // generación de cuotas) es tarea de Infrastructure, no de Application.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(LoansPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `loans` no se acopla a ningún otro módulo de negocio. La única dependencia legítima hacia
    /// afuera es `Modules.Tenancy.Application` (<c>IExecutionContext</c>, <c>Currencies</c>). Clientes y
    /// miembros entran por los puertos <c>ILoans*</c> declarados
    /// acá, cuyos adaptadores viven en `Bootstrapper`. Sin esta aserción esa decisión es un comentario.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(LoansPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Loans", StringComparison.Ordinal) &&
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
