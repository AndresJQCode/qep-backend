using System.Reflection;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using Modules.Pipeline.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Pipeline (spec 2026-10-13 D2 y D9), copia de <see cref="MessagingLayerTests"/>. Pipeline nunca
/// referencia Messaging, Customers ni Quotations: las conversaciones, los clientes y los nombres de miembros entran por
/// puertos propios con adaptadores en Bootstrapper, y las cotizaciones llegan por el outbox.
/// </summary>
public sealed class PipelineLayerTests
{
    private static readonly Assembly Domain = typeof(PipelineErrorCodes).Assembly;
    private static readonly Assembly Application = typeof(PipelinePermissions).Assembly;
    private static readonly Assembly Infrastructure = typeof(PipelineInfrastructureExtensions).Assembly;
    private static readonly Assembly Api = typeof(PipelineEndpoints).Assembly;

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

    // D9: Tenancy (ITenantModules, TenantModuleGuard, IExecutionContext, IMembershipDirectory, Currencies) y nada más.
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Pipeline", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));

    [Fact]
    public void DomainReferencesNoOtherModule() =>
        Assert.DoesNotContain(ReferenceNamesOf(Domain), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Pipeline", StringComparison.Ordinal));

    // D9: ni siquiera Infrastructure. Los adaptadores viven en Bootstrapper; las cotizaciones llegan por el outbox.
    [Theory]
    [InlineData("Modules.Messaging")]
    [InlineData("Modules.Customers")]
    [InlineData("Modules.Quotations")]
    [InlineData("Modules.Identity")]
    public void NoLayerReferencesMessagingCustomersQuotationsOrIdentity(string forbiddenPrefix)
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
