using System.Reflection;
using Modules.ServiceContracts.Api;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;
using Modules.ServiceContracts.Infrastructure;

namespace ArchitectureTests;

public sealed class ServiceContractsLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(ServiceContract).Assembly,
            typeof(ServiceContractsPermissions).Assembly,
            typeof(ServiceContractsInfrastructureExtensions).Assembly,
            typeof(ServiceContractsEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(ServiceContractsPermissions).Assembly,
            typeof(ServiceContractsInfrastructureExtensions).Assembly,
            typeof(ServiceContractsEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(ServiceContractsInfrastructureExtensions).Assembly,
            typeof(ServiceContractsEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. la unicidad de (service_id, due_date) que hace idempotente la
    // generación de cobros) es tarea de Infrastructure, no de Application.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(ServiceContractsPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `billing` no se acopla a ningún otro módulo de negocio. La única dependencia legítima hacia
    /// afuera es `Modules.Tenancy.Application` (<c>IExecutionContext</c>, <c>Currencies</c>). Clientes,
    /// miembros, correo, WhatsApp y consentimiento entran por los puertos <c>IServiceContracts*</c> declarados
    /// acá, cuyos adaptadores viven en `Bootstrapper`. Sin esta aserción esa decisión es un comentario.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(ServiceContractsPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.ServiceContracts", StringComparison.Ordinal) &&
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
