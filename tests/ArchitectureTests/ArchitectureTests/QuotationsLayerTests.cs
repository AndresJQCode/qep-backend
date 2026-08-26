using System.Reflection;
using Modules.Quotations.Api;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure;

namespace ArchitectureTests;

public sealed class QuotationsLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Quotation).Assembly,
            typeof(CreateQuotationCommand).Assembly,
            typeof(QuotationsInfrastructureExtensions).Assembly,
            typeof(QuotationEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(CreateQuotationCommand).Assembly,
            typeof(QuotationsInfrastructureExtensions).Assembly,
            typeof(QuotationEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(QuotationsInfrastructureExtensions).Assembly,
            typeof(QuotationEndpoints).Assembly);
    }

    // Traducir un error de base es tarea de Infrastructure, no de Application: la violacion de
    // IX_quotations_tenant_number se vuelve su codigo de dominio en QuotationsUnitOfWork. Mismo
    // criterio que CatalogLayerTests/CustomersLayerTests -- la leccion de SDD-CT-06.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(CreateQuotationCommand).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `quotations` no se acopla a ningun otro modulo de negocio. La unica dependencia legitima
    /// hacia afuera es <c>Modules.Tenancy.Application</c> (IExecutionContext, IMembershipDirectory)
    /// -- el mismo permiso que ya tienen Catalog, Customers y Companies. Los puertos hacia
    /// Customers (CUC/activo) y Catalog (precio/escalas) se declaran en este modulo y se cablean
    /// en Bootstrapper -- ver IQuotationCustomerLookup/IQuotationProductPricingLookup -- mismo
    /// patron que CAT-05. Sin esta prueba, referenciar Modules.Catalog.Application o
    /// Modules.Customers.Application directo compila y nada se pone rojo.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(CreateQuotationCommand).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Quotations", StringComparison.Ordinal) &&
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
