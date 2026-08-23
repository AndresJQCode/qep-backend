using System.Reflection;
using Modules.Geography.Api;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure;

namespace ArchitectureTests;

public sealed class GeographyLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Department).Assembly,
            typeof(IDepartmentRepository).Assembly,
            typeof(GeographyInfrastructureExtensions).Assembly,
            typeof(GeographyEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(IDepartmentRepository).Assembly,
            typeof(GeographyInfrastructureExtensions).Assembly,
            typeof(GeographyEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(GeographyInfrastructureExtensions).Assembly,
            typeof(GeographyEndpoints).Assembly);
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
