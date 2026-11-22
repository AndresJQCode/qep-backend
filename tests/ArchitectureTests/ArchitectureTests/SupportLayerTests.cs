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

    // Spec 2026-11-15 §6.3 (plan L2): the legal HTML is sanitized behind ISupportHtmlSanitizer; only Infrastructure knows the library.
    [Fact]
    public void ApplicationDoesNotReferenceTheHtmlSanitizer()
    {
        foreach (var assembly in new[] { typeof(SupportDomainException).Assembly, typeof(SupportPermissions).Assembly })
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null &&
                (name.StartsWith("HtmlSanitizer", StringComparison.Ordinal) ||
                 name.StartsWith("Ganss.Xss", StringComparison.Ordinal) ||
                 name.StartsWith("AngleSharp", StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// `support` no se acopla a ningún otro módulo de negocio (spec 2026-11-14). La única dependencia legítima hacia
    /// afuera es `Modules.Tenancy.*` (<c>IExecutionContext</c>, <c>ITenantModules</c>). Clientes, miembros y el calendario
    /// de la bandeja entran por los puertos <c>ISupport*</c> declarados acá, cuyos adaptadores viven en `Bootstrapper`:
    /// Soporte nunca referencia Mensajería. Sin esta aserción esa decisión es un comentario.
    /// <para>Una excepción deliberada (spec 2026-11-22, D-14): el horario de atención entra por <c>IBusinessHoursProvider</c>, un contrato
    /// compartido de <c>BuildingBlocks.Application</c> que implementa Mensajería, y no por un puerto <c>ISupport*</c> con adaptador. Es una
    /// capacidad que varios módulos van a usar, y un contrato compartido evita un adaptador por consumidor. La aserción no cambia: BuildingBlocks
    /// no es un módulo de negocio.</para>
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

    // Spec 2026-11-15 §9.3 (plan L30): the company that answers on the portal is read through ISupportOrganizationDirectory, whose
    // adapter lives in Bootstrapper. No layer of Support references Companies, not even for the FK, which is SQL in the migration.
    [Fact]
    public void SupportDoesNotReferenceCompanies()
    {
        foreach (var assembly in new[]
        {
            typeof(SupportDomainException).Assembly,
            typeof(SupportPermissions).Assembly,
            typeof(SupportInfrastructureExtensions).Assembly,
            typeof(SupportEndpoints).Assembly,
        })
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null && name.StartsWith("Modules.Companies", StringComparison.Ordinal));
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
