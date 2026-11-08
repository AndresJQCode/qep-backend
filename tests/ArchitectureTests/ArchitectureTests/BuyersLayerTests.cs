using System.Reflection;
using Modules.Buyers.Api;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using Modules.Buyers.Infrastructure;

namespace ArchitectureTests;

public sealed class BuyersLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(BuyerErrorCodes).Assembly,
            typeof(BuyerIntegrationEvents).Assembly,
            typeof(BuyersInfrastructureExtensions).Assembly,
            typeof(PublicBuyerEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(BuyerIntegrationEvents).Assembly,
            typeof(BuyersInfrastructureExtensions).Assembly,
            typeof(PublicBuyerEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(BuyersInfrastructureExtensions).Assembly,
            typeof(PublicBuyerEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(BuyerIntegrationEvents).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>Las cuentas de comprador no se mezclan con las de los usuarios del tenant: ninguna capa referencia Identity.</summary>
    [Fact]
    public void NoBuyersLayerReferencesIdentity()
    {
        foreach (var layer in AllLayers())
        {
            Assert.DoesNotContain(ReferenceNamesOf(layer), name =>
                name is not null &&
                name.StartsWith("Modules.Identity", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ApplicationAndInfrastructureReferenceNoOtherBusinessModule()
    {
        Assembly[] layers =
        [
            typeof(BuyerIntegrationEvents).Assembly,
            typeof(BuyersInfrastructureExtensions).Assembly,
        ];

        foreach (var layer in layers)
        {
            var modules = ReferenceNamesOf(layer)
                .Where(name => name is not null && name.StartsWith("Modules.", StringComparison.Ordinal));

            Assert.All(modules, name => Assert.StartsWith("Modules.Buyers", name, StringComparison.Ordinal));
        }
    }

    /// <summary>Api resuelve el store del host con el cliente de storefront, así que `Modules.Store.Api` es su única salida a otro módulo.</summary>
    [Fact]
    public void ApiReferencesOnlyStoreApiAmongOtherModules()
    {
        var modules = ReferenceNamesOf(typeof(PublicBuyerEndpoints).Assembly)
            .Where(name => name is not null && name.StartsWith("Modules.", StringComparison.Ordinal));

        Assert.All(modules, name =>
            Assert.True(
                name!.StartsWith("Modules.Buyers", StringComparison.Ordinal) ||
                string.Equals(name, "Modules.Store.Api", StringComparison.Ordinal),
                $"Unexpected module reference: {name}"));
    }

    private static Assembly[] AllLayers() =>
    [
        typeof(BuyerErrorCodes).Assembly,
        typeof(BuyerIntegrationEvents).Assembly,
        typeof(BuyersInfrastructureExtensions).Assembly,
        typeof(PublicBuyerEndpoints).Assembly,
    ];

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
