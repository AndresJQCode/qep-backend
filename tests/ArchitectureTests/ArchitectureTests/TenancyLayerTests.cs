using System.Reflection;
using Modules.Tenancy.Api;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure;

namespace ArchitectureTests;

public sealed class TenancyLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Tenant).Assembly,
            typeof(GetTenantSettingsQuery).Assembly,
            typeof(TenancyInfrastructureExtensions).Assembly,
            typeof(TenantSettingsEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(GetTenantSettingsQuery).Assembly,
            typeof(TenancyInfrastructureExtensions).Assembly,
            typeof(TenantSettingsEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(TenancyInfrastructureExtensions).Assembly,
            typeof(TenantSettingsEndpoints).Assembly);
    }

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
