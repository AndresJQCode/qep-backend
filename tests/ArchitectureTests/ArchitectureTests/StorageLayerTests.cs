using System.Reflection;
using Modules.Storage.Api;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure;

namespace ArchitectureTests;

public sealed class StorageLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(FileResource).Assembly,
            typeof(IObjectStorage).Assembly,
            typeof(StorageInfrastructureExtensions).Assembly,
            typeof(StorageEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(IObjectStorage).Assembly,
            typeof(StorageInfrastructureExtensions).Assembly,
            typeof(StorageEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(StorageInfrastructureExtensions).Assembly,
            typeof(StorageEndpoints).Assembly);
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
