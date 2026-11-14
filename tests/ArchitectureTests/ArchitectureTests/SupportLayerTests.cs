using System.Reflection;
using Modules.Support.Api;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure;

namespace ArchitectureTests;

public sealed class SupportLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(SupportDomainException).Assembly,
            typeof(SupportPermissions).Assembly,
            typeof(SupportInfrastructureExtensions).Assembly,
            typeof(SupportEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(SupportPermissions).Assembly,
            typeof(SupportInfrastructureExtensions).Assembly,
            typeof(SupportEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(SupportInfrastructureExtensions).Assembly,
            typeof(SupportEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. el nombre de tipo repetido, IX_ticket_types_tenant_name) es tarea de
    // Infrastructure, no de Application.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(SupportPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `support` no se acopla a ningún otro módulo de negocio (spec 2026-11-14). La única dependencia legítima hacia
    /// afuera es `Modules.Tenancy.*` (<c>IExecutionContext</c>, <c>ITenantModules</c>). Clientes, miembros y el calendario
    /// de la bandeja entran por los puertos <c>ISupport*</c> declarados acá, cuyos adaptadores viven en `Bootstrapper`:
    /// Soporte nunca referencia Mensajería. Sin esta aserción esa decisión es un comentario.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(SupportPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Support", StringComparison.Ordinal) &&
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
