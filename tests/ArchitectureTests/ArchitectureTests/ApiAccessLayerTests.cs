using System.Reflection;
using Modules.ApiAccess.Api;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;
using Modules.ApiAccess.Infrastructure;

namespace ArchitectureTests;

public sealed class ApiAccessLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(ApiKey).Assembly,
            typeof(ApiAccessPermissions).Assembly,
            typeof(ApiAccessInfrastructureExtensions).Assembly,
            typeof(ApiKeyEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(ApiAccessPermissions).Assembly,
            typeof(ApiAccessInfrastructureExtensions).Assembly,
            typeof(ApiKeyEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(ApiAccessInfrastructureExtensions).Assembly,
            typeof(ApiKeyEndpoints).Assembly);
    }

    // Traducir un error de base es tarea de Infrastructure, no de Application: el índice único del
    // nombre entre claves activas lo traduce el ApiAccessUnitOfWork.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(ApiAccessPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `api_access` no se acopla a ningún módulo de negocio. Las únicas dependencias legítimas hacia
    /// afuera son `Modules.Tenancy.Application` (<c>IExecutionContext</c>, el guard de módulos) y
    /// `Modules.Audit` (<c>AuditActorType</c>). Messaging y demás entran por puertos con adaptador en
    /// `Bootstrapper`. Sin esta aserción esa decisión es un comentario, no una regla.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAndAuditAmongTheModules()
    {
        var references = ReferenceNamesOf(typeof(ApiAccessPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.ApiAccess", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Audit", StringComparison.Ordinal));
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
