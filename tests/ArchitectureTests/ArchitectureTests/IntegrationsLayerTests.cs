using System.Reflection;
using Modules.Integrations.Api;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Integrations (spec 2026-10-08, «Proyectos»), copia de <see cref="PosLayerTests"/>
/// con dos reglas propias. El dominio sólo ve <c>Modules.Tenancy.Domain</c>: el catálogo nombra sus
/// módulos consumidores con <c>TenantModuleKeys</c>. Y ningún otro módulo referencia
/// <c>Modules.Integrations.Application</c>: un consumidor declara su propio puerto y el adaptador vive
/// en Bootstrapper, como <c>QuotationFileLookup</c> (CAT-05). Sin esta prueba, el primer consumidor
/// agrega el ProjectReference y nada se pone rojo.
/// </summary>
public sealed class IntegrationsLayerTests
{
    private static readonly Assembly Domain = typeof(IntegrationsErrorCodes).Assembly;
    private static readonly Assembly Application = typeof(IIntegrationsUnitOfWork).Assembly;
    private static readonly Assembly Infrastructure = typeof(IntegrationsInfrastructureExtensions).Assembly;
    private static readonly Assembly Api = typeof(IntegrationsEndpoints).Assembly;

    [Fact]
    public void DomainDoesNotReferenceOuterLayers() =>
        AssertDoesNotReference(Domain, Application, Infrastructure, Api);

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi() =>
        AssertDoesNotReference(Application, Infrastructure, Api);

    [Fact]
    public void InfrastructureDoesNotReferenceApi() =>
        AssertDoesNotReference(Infrastructure, Api);

    // Traducir errores de base (el índice único del nombre, la concurrencia) es tarea de
    // IntegrationsUnitOfWork, en Infrastructure.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));

    // Spec, «Proyectos»: Tenancy (ITenantModules, IExecutionContext) y Audit (AuditActorType de la
    // auditoría atómica propia, P1 del plan). Ningún otro Modules.*.
    [Fact]
    public void ApplicationOnlyReferencesTenancyAndAuditAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Integrations", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Audit", StringComparison.Ordinal));

    [Fact]
    public void DomainOnlyReferencesTheTenancyDomainAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Domain), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Integrations", StringComparison.Ordinal) &&
            !string.Equals(name, "Modules.Tenancy.Domain", StringComparison.Ordinal));

    [Fact]
    public void NoOtherModuleReferencesTheIntegrationsApplication()
    {
        var applicationName = Application.GetName().Name;
        var others = Directory
            .GetFiles(AppContext.BaseDirectory, "Modules.*.dll")
            .Where(path => !Path.GetFileName(path).StartsWith("Modules.Integrations.", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .ToArray();

        // Ancla: sin módulos cargados, la aserción de abajo pasaría por vacía.
        Assert.NotEmpty(others);
        Assert.DoesNotContain(others, assembly => assembly
            .GetReferencedAssemblies()
            .Any(reference => string.Equals(reference.Name, applicationName, StringComparison.Ordinal)));
    }

    private static string?[] ReferenceNamesOf(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    private static void AssertDoesNotReference(Assembly source, params Assembly[] forbiddenAssemblies)
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
