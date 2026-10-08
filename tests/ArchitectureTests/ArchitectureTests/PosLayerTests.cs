using System.Reflection;
using Modules.Pos.Api;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using Modules.Pos.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Pos, copia de <see cref="CompaniesLayerTests"/>. La regla que más importa es la
/// última: Pos lee productos, empresas y cajeros de otros módulos, y todo eso entra por puertos con
/// adaptador en Bootstrapper (spec 2026-10-07, «Aplicación»). Sin esta aserción, el primero que
/// necesite un producto agrega el ProjectReference a Catalog y nada se pone rojo.
/// </summary>
public sealed class PosLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(PosDomainException).Assembly,
            typeof(IPosUnitOfWork).Assembly,
            typeof(PosInfrastructureExtensions).Assembly,
            typeof(PosEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(IPosUnitOfWork).Assembly,
            typeof(PosInfrastructureExtensions).Assembly,
            typeof(PosEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(PosInfrastructureExtensions).Assembly,
            typeof(PosEndpoints).Assembly);
    }

    // Traducir errores de base (23505 del índice parcial, PK_sales, concurrencia) es tarea de
    // PosUnitOfWork, en Infrastructure.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(IPosUnitOfWork).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(IPosUnitOfWork).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Pos", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));
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
