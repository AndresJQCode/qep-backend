using System.Reflection;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure;

namespace ArchitectureTests;

public sealed class IdentityLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(User).Assembly,
            typeof(IIdentityProvisioning).Assembly,
            typeof(IdentityInfrastructureExtensions).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructure()
    {
        AssertDoesNotReference(
            typeof(IIdentityProvisioning).Assembly,
            typeof(IdentityInfrastructureExtensions).Assembly);
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
