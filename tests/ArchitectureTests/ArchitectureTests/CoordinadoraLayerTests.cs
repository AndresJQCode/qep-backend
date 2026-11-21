using System.Reflection;
using Modules.Coordinadora.Api;
using Modules.Coordinadora.Application;
using Modules.Coordinadora.Domain;
using Modules.Coordinadora.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Coordinadora (spec 2026-11-21, integración Coordinadora), copia de <see cref="SiigoLayerTests"/>. La regla que más
/// importa es la última: Coordinadora lee la conexión, el pedido, el cliente y la ciudad de otros módulos, y todo eso entra por puertos
/// con adaptador en Bootstrapper. Sin esta aserción, el primero que necesite un pedido agrega el ProjectReference a Quotations y nada se
/// pone rojo. Tenancy (módulos, contexto de ejecución) y Audit (la auditoría atómica propia) sí están permitidos.
/// </summary>
public sealed class CoordinadoraLayerTests
{
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(CoordinadoraDomainException).Assembly,
        typeof(ICoordinadoraUnitOfWork).Assembly,
        typeof(CoordinadoraInfrastructureExtensions).Assembly,
        typeof(CoordinadoraEndpoints).Assembly,
    ];

    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(CoordinadoraDomainException).Assembly,
            typeof(ICoordinadoraUnitOfWork).Assembly,
            typeof(CoordinadoraInfrastructureExtensions).Assembly,
            typeof(CoordinadoraEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(ICoordinadoraUnitOfWork).Assembly,
            typeof(CoordinadoraInfrastructureExtensions).Assembly,
            typeof(CoordinadoraEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(CoordinadoraInfrastructureExtensions).Assembly,
            typeof(CoordinadoraEndpoints).Assembly);
    }

    // Traducir errores de base es tarea de CoordinadoraUnitOfWork, en Infrastructure.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(ICoordinadoraUnitOfWork).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal) ||
             name.StartsWith("StackExchange.Redis", StringComparison.Ordinal)));
    }

    [Fact]
    public void ApplicationOnlyReferencesTenancyAndAuditAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(ICoordinadoraUnitOfWork).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Coordinadora", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Audit", StringComparison.Ordinal));
    }

    // La conexión (Integrations), el pedido (Quotations), el cliente (Customers) y la ciudad (Geography) entran por puertos de
    // Coordinadora.Application con adaptador en Bootstrapper, en ninguna capa del módulo.
    [Theory]
    [InlineData("Modules.Integrations")]
    [InlineData("Modules.Quotations")]
    [InlineData("Modules.Customers")]
    [InlineData("Modules.Geography")]
    [InlineData("Modules.Siigo")]
    public void CoordinadoraDoesNotReferenceOtherBusinessModules(string forbiddenPrefix)
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
