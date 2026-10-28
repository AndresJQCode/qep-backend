using System.Reflection;
using Modules.Geography.Api;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure;

namespace ArchitectureTests;

public sealed class GeographyLayerTests
{
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(Department).Assembly,
        typeof(IDepartmentRepository).Assembly,
        typeof(GeographyInfrastructureExtensions).Assembly,
        typeof(GeographyEndpoints).Assembly,
    ];

    /// <summary>
    /// Spec 2026-10-26 (D-C3): Geography ve sólo Tenancy (la autorización de operador) y Audit (su auditoría atómica). Lo que
    /// necesita de pedidos, clientes, correcciones, tenants o usuarios entra por <see cref="ICityCorrectionInsights"/>, con
    /// adaptador en Bootstrapper. Copia de la regla de <see cref="ShopifyLayerTests"/>: sin ella, el primero que necesite un
    /// pedido agrega el ProjectReference a Quotations y nada se pone rojo.
    /// </summary>
    [Fact]
    public void GeographySeesNoBusinessModule()
    {
        foreach (var assembly in ModuleAssemblies)
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies().Select(name => name.Name),
                name =>
                    name is not null &&
                    name.StartsWith("Modules.", StringComparison.Ordinal) &&
                    !name.StartsWith("Modules.Geography", StringComparison.Ordinal) &&
                    !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal) &&
                    !name.StartsWith("Modules.Audit", StringComparison.Ordinal));
        }
    }

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
