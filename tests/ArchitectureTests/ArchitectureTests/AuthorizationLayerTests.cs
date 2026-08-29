using System.Reflection;
using Modules.Authorization.Application;
using Modules.Authorization.Domain;

namespace ArchitectureTests;

/// <summary>
/// El modulo que decide quien puede que cosa, ahora que tiene capas.
/// </summary>
/// <remarks>
/// No existia porque `Authorization` era solo `Application`: un catalogo en memoria armado en
/// composicion, sin dominio ni persistencia. Los roles custom le dan un agregado y una base,
/// y con eso las mismas fronteras que ya protegen a los otros modulos.
/// </remarks>
public sealed class AuthorizationLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Role).Assembly,
            typeof(TenantRoleCatalog).Assembly);
    }

    /// <summary>
    /// El dominio no conoce ningun otro modulo de negocio, Tenancy incluido.
    /// </summary>
    /// <remarks>
    /// `Application` si depende de `Modules.Tenancy.Application`, de donde salen
    /// <c>IRolePermissionChecker</c> e <c>IRoleReferenceValidator</c>. El dominio no: un
    /// <see cref="Role"/> es un conjunto de permisos y nada mas, y no necesita saber que
    /// existe una membresia para validarse.
    /// </remarks>
    [Fact]
    public void DomainDoesNotReferenceAnyBusinessModule()
    {
        var references = ReferenceNamesOf(typeof(Role).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Authorization", StringComparison.Ordinal));
    }

    /// <summary>
    /// Traducir un error de base es tarea de Infrastructure, la misma regla que en Companies
    /// y Tenancy.
    /// </summary>
    /// <remarks>
    /// Importa mas aca que en otros modulos: <see cref="TenantRoleCatalog"/> decide permisos
    /// en cada request. Si pudiera tocar EF Core, una consulta mal escrita convertiria un
    /// problema de base en una decision de autorizacion.
    /// </remarks>
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(TenantRoleCatalog).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    [Fact]
    public void DomainDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(Role).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
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
