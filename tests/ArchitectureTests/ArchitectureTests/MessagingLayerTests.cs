using System.Reflection;
using Modules.Messaging.Api;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Messaging (spec 2026-10-09 §6.4), copia de <see cref="IntegrationsLayerTests"/>.
/// Messaging nunca referencia Integrations, Customers ni Storage: lo que necesita de ellos entra por
/// puertos propios con adaptadores en Bootstrapper (<c>IMessagingConnectionDirectory</c>,
/// <c>IMessagingCustomerDirectory</c>, <c>IMessagingMediaStore</c>).
/// </summary>
public sealed class MessagingLayerTests
{
    private static readonly Assembly Domain = typeof(MessagingErrorCodes).Assembly;
    private static readonly Assembly Application = typeof(MessagingPermissions).Assembly;
    private static readonly Assembly Infrastructure = typeof(MessagingInfrastructureExtensions).Assembly;
    private static readonly Assembly Api = typeof(MessagingEndpoints).Assembly;

    [Fact]
    public void DomainDoesNotReferenceOuterLayers() =>
        AssertDoesNotReference(Domain, Application, Infrastructure, Api);

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi() =>
        AssertDoesNotReference(Application, Infrastructure, Api);

    [Fact]
    public void InfrastructureDoesNotReferenceApi() =>
        AssertDoesNotReference(Infrastructure, Api);

    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));

    // Spec §6.4: Tenancy (ITenantModules, TenantModuleGuard, IExecutionContext) y Audit (AuditActorType).
    [Fact]
    public void ApplicationOnlyReferencesTenancyAndAuditAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Messaging", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Audit", StringComparison.Ordinal));

    [Fact]
    public void DomainOnlyReferencesTheTenancyDomainAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Domain), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Messaging", StringComparison.Ordinal) &&
            !string.Equals(name, "Modules.Tenancy.Domain", StringComparison.Ordinal));

    // Spec §6.4: ni siquiera Infrastructure: los adaptadores viven en Bootstrapper.
    [Theory]
    [InlineData("Modules.Integrations")]
    [InlineData("Modules.Customers")]
    [InlineData("Modules.Storage")]
    public void NoLayerReferencesIntegrationsCustomersOrStorage(string forbiddenPrefix)
    {
        foreach (var assembly in new[] { Domain, Application, Infrastructure, Api })
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null && name.StartsWith(forbiddenPrefix, StringComparison.Ordinal));
        }
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
