using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Shopify.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 (onboarding) §7, plan Tarea 9 (D6): la empresa quedó creada y la asignación perdió por versión. El 412 sigue
/// siendo <c>concurrency.conflict</c> y lleva <c>details.companyId</c>, para que el reintento asigne esa empresa y no cree otra.
/// </summary>
public sealed class ShopifySetupCompanyConflictExceptionTests
{
    private static readonly Guid CompanyId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");

    [Fact]
    public void TheConflictCarriesTheCompanyIdInDetails()
    {
        var exception = new ShopifySetupCompanyConflictException(CompanyId);

        var details = JsonSerializer.Serialize(exception.ProblemExtensions["details"]);

        Assert.Equal($$"""{"companyId":"{{CompanyId}}"}""", details);
        Assert.Equal(CompanyId, exception.CompanyId);
    }

    [Fact]
    public void ItIsStillAConcurrencyConflict()
    {
        var inner = new RequestConcurrencyException("concurrency.conflict", "The store changed.");

        RequestConcurrencyException exception = new ShopifySetupCompanyConflictException(CompanyId, inner);

        Assert.Equal("concurrency.conflict", exception.Code);
        Assert.Same(inner, exception.InnerException);
    }
}
