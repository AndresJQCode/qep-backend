using System.Reflection;
using Modules.Ai.Api;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Ai.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Ai (spec 2026-10-29 D-A3), copia de <see cref="AutomationsLayerTests"/> con dos prohibiciones más: Ai no ve
/// Automations (un agente no es un chatbot, D-1b), y ningún SDK de proveedor de modelos sale de Infrastructure (plan 2026-11-12,
/// Global Constraints): Application habla con el modelo sólo por su puerto, con tipos propios.
/// </summary>
public sealed class AiLayerTests
{
    private static readonly Assembly Domain = typeof(AiProviders).Assembly;
    private static readonly Assembly Application = typeof(AiPermissions).Assembly;
    private static readonly Assembly Infrastructure = typeof(AiInfrastructureExtensions).Assembly;
    private static readonly Assembly Api = typeof(AiEndpoints).Assembly;

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

    // D-A3: Tenancy (ITenantModules, TenantModuleGuard, IExecutionContext) y nada más.
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Ai", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));

    [Fact]
    public void DomainReferencesNoOtherModule() =>
        Assert.DoesNotContain(ReferenceNamesOf(Domain), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Ai", StringComparison.Ordinal));

    // D-A3: ni siquiera Infrastructure. Los adaptadores a Mensajería, Integrations y los módulos de negocio viven en Bootstrapper.
    [Theory]
    [InlineData("Modules.Automations")]
    [InlineData("Modules.Messaging")]
    [InlineData("Modules.Integrations")]
    [InlineData("Modules.Customers")]
    [InlineData("Modules.Identity")]
    [InlineData("Modules.Quotations")]
    [InlineData("Modules.Catalog")]
    public void NoLayerReferencesAnotherBusinessModule(string forbiddenPrefix)
    {
        foreach (var assembly in new[] { Domain, Application, Infrastructure, Api })
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null && name.StartsWith(forbiddenPrefix, StringComparison.Ordinal));
        }
    }

    // Plan 2026-11-12: el SDK de Anthropic (y el de OpenAI que elija P-1) sólo en Infrastructure.
    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public void OnlyInfrastructureMayReferenceAModelProviderSdk(string forbiddenPrefix)
    {
        foreach (var assembly in new[] { Domain, Application, Api })
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
