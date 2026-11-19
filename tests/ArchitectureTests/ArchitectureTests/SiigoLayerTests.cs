using System.Reflection;
using Modules.Siigo.Api;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Siigo (spec 2026-11-18, integración Siigo), copia de <see cref="ShopifyLayerTests"/>. La regla que más importa
/// es la última: Siigo lee la conexión, el pedido, el cliente, el producto, la ciudad y la empresa de otros módulos, y todo eso
/// entra por puertos con adaptador en Bootstrapper. Sin esta aserción, el primero que necesite un pedido agrega el ProjectReference
/// a Quotations y nada se pone rojo. Tenancy (módulos, contexto de ejecución) y Audit (la auditoría atómica propia) sí están
/// permitidos.
/// </summary>
public sealed class SiigoLayerTests
{
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(SiigoDomainException).Assembly,
        typeof(ISiigoUnitOfWork).Assembly,
        typeof(SiigoInfrastructureExtensions).Assembly,
        typeof(SiigoEndpoints).Assembly,
    ];

    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(SiigoDomainException).Assembly,
            typeof(ISiigoUnitOfWork).Assembly,
            typeof(SiigoInfrastructureExtensions).Assembly,
            typeof(SiigoEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(ISiigoUnitOfWork).Assembly,
            typeof(SiigoInfrastructureExtensions).Assembly,
            typeof(SiigoEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(SiigoInfrastructureExtensions).Assembly,
            typeof(SiigoEndpoints).Assembly);
    }

    // Traducir errores de base es tarea de SiigoUnitOfWork, en Infrastructure.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(ISiigoUnitOfWork).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal) ||
             name.StartsWith("StackExchange.Redis", StringComparison.Ordinal)));
    }

    [Fact]
    public void ApplicationOnlyReferencesTenancyAndAuditAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(ISiigoUnitOfWork).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Siigo", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Audit", StringComparison.Ordinal));
    }

    // La conexión (Integrations), el pedido (Quotations), el cliente (Customers), el producto (Catalog), la ciudad (Geography) y la
    // empresa (Companies) entran por puertos de Siigo.Application con adaptador en Bootstrapper, en ninguna capa del módulo.
    [Theory]
    [InlineData("Modules.Integrations")]
    [InlineData("Modules.Quotations")]
    [InlineData("Modules.Customers")]
    [InlineData("Modules.Catalog")]
    [InlineData("Modules.Geography")]
    [InlineData("Modules.Companies")]
    public void SiigoDoesNotReferenceOtherBusinessModules(string forbiddenPrefix)
    {
        foreach (var assembly in ModuleAssemblies)
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null && name.StartsWith(forbiddenPrefix, StringComparison.Ordinal));
        }
    }

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
