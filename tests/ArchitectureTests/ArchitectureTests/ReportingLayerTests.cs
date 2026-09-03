using System.Reflection;
using Modules.Reporting.Api;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;
using Modules.Reporting.Infrastructure;

namespace ArchitectureTests;

public sealed class ReportingLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(ReportingDomainException).Assembly,
            typeof(ListSalesReportQuery).Assembly,
            typeof(ReportingInfrastructureExtensions).Assembly,
            typeof(ReportingEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(ListSalesReportQuery).Assembly,
            typeof(ReportingInfrastructureExtensions).Assembly,
            typeof(ReportingEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(ReportingInfrastructureExtensions).Assembly,
            typeof(ReportingEndpoints).Assembly);
    }

    /// <summary>
    /// Reporting no tiene DbContext, ni tablas, ni migraciones — es lectura pura sobre datos de
    /// otros modulos. Que Application no vea EF Core ni Npgsql es la misma frontera que protege
    /// al resto de los modulos, y aca ademas es lo que obliga a que las consultas vivan en los
    /// adaptadores del composition root en vez de colarse en un handler.
    /// </summary>
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferencedAssemblyNames(typeof(ListSalesReportQuery).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// **La regla que sostiene el diseño entero de este modulo.**
    ///
    /// Los cuatro reportes leen datos de <c>quotations</c>, <c>customers</c>, <c>catalog</c> e
    /// <c>identity</c>. La tentacion —y el camino corto— es agregarle a
    /// <c>Modules.Reporting.Application</c> un <c>ProjectReference</c> a cada uno y consultar
    /// desde el handler. Esta asercion lo impide: los puertos <c>I*ReportSource</c> se declaran
    /// en Application y los adaptadores viven en <c>Bootstrapper</c>, que es el composition root
    /// y el unico lugar donde el acoplamiento entre dos modulos de negocio es legitimo (CAT-05).
    ///
    /// Sin esto la decision es un comentario, no una regla: el primero que necesite un dato de
    /// otro modulo agrega la referencia y nada se pone rojo.
    ///
    /// <c>Modules.Tenancy.Application</c> queda afuera de la prohibicion a proposito: es de donde
    /// sale <c>IExecutionContext</c>, y todos los modulos lo referencian.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        string[] forbidden =
        [
            "Modules.Quotations",
            "Modules.Customers",
            "Modules.Catalog",
            "Modules.Identity"
        ];

        var references = ReferencedAssemblyNames(typeof(ListSalesReportQuery).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private static string?[] ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();

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
