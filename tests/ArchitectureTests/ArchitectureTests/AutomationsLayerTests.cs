using System.Reflection;
using Modules.Automations.Api;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Automations.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Automations (spec 2026-10-21 §4.1), copia de <see cref="PipelineLayerTests"/>. Automations nunca
/// referencia Messaging, Integrations, Customers ni Identity: las conversaciones, la salida del bot, las conexiones y la
/// «API externa» entran por puertos propios con adaptadores en Bootstrapper.
/// </summary>
public sealed class AutomationsLayerTests
{
    private static readonly Assembly Domain = typeof(AutomationsErrorCodes).Assembly;
    private static readonly Assembly Application = typeof(AutomationsCaller).Assembly;
    private static readonly Assembly Infrastructure = typeof(AutomationsInfrastructureExtensions).Assembly;
    private static readonly Assembly Api = typeof(AutomationsEndpoints).Assembly;

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

    // §4.1: Tenancy (ITenantModules, TenantModuleGuard, IExecutionContext, IMembershipDirectory) y nada más.
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Automations", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));

    [Fact]
    public void DomainReferencesNoOtherModule() =>
        Assert.DoesNotContain(ReferenceNamesOf(Domain), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Automations", StringComparison.Ordinal));

    // §4.1: ni siquiera Infrastructure. Los adaptadores a Messaging e Integrations viven en Bootstrapper.
    [Theory]
    [InlineData("Modules.Messaging")]
    [InlineData("Modules.Integrations")]
    [InlineData("Modules.Customers")]
    [InlineData("Modules.Identity")]
    public void NoLayerReferencesMessagingIntegrationsCustomersOrIdentity(string forbiddenPrefix)
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
