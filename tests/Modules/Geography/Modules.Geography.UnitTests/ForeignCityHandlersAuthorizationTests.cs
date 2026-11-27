using BuildingBlocks.Application;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Geography.UnitTests;

/// <summary>
/// La segunda capa de autorización (<c>IOperatorTenant.IsOperator</c>) de los handlers de <c>/operator/cities/foreign</c> (spec
/// 2026-11-26). Por HTTP no se puede ver: en el stub de desarrollo, un tenant que no es el operador pierde los permisos
/// <c>operator.*</c> antes de llegar al handler, así que el 403 de <c>ForeignCityOperatorApiTests</c> sale de la política de la
/// ruta. Acá el contexto sí lleva los dos permisos y el tenant de la ruta, y lo único que falla es que ese tenant no es el
/// operador. Los puertos van nulos a propósito: la autorización va primero, antes de validar o tocar cualquiera, y un handler que
/// la saltara reventaría con una referencia nula en vez de con 403.
/// </summary>
public sealed class ForeignCityHandlersAuthorizationTests
{
    private static readonly TenantId OperatorTenant = new(Guid.CreateVersion7());
    private static readonly TenantId OtherTenant = new(Guid.CreateVersion7());
    private static readonly UnresolvedForeignCityId SomeRow = UnresolvedForeignCityId.New();
    private static readonly ForeignCitySynonymId SomeSynonym = ForeignCitySynonymId.New();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IOperatorTenant Operator = new CityOperatorDoubles(OperatorTenant).OperatorTenant;

    // Todos los permisos de operador, pero en un tenant que no es el operador.
    private static CityOperatorDoubles.Context NonOperatorContext() =>
        new(OtherTenant, GeographyOperatorPermissions.CitiesRead, GeographyOperatorPermissions.CitiesManage);

    private static UnresolvedForeignCityTriage Triage() =>
        new(null!, null!, null!, null!, NonOperatorContext(), Operator, null!);

    private static async Task AssertDeniedAsync(Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(call);
        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public Task TheCountriesAreDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new ListBillingCountriesHandler(null!, null!, NonOperatorContext(), Operator)
            .HandleAsync(new ListBillingCountriesQuery(OtherTenant), Ct));

    [Fact]
    public Task TheCatalogIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new SearchBillingCitiesHandler(
                null!, NonOperatorContext(), Operator, new SearchBillingCitiesValidator())
            .HandleAsync(new SearchBillingCitiesQuery(OtherTenant, "Us", "Boston"), Ct));

    [Fact]
    public Task TheInboxIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new ListUnresolvedForeignCitiesHandler(
                null!, null!, NonOperatorContext(), Operator, new ListUnresolvedForeignCitiesValidator())
            .HandleAsync(new ListUnresolvedForeignCitiesQuery(OtherTenant, null, null, null, 1, 25), Ct));

    [Fact]
    public Task TheDetailIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new GetUnresolvedForeignCityHandler(null!, null!, null!, NonOperatorContext(), Operator)
            .HandleAsync(new GetUnresolvedForeignCityQuery(OtherTenant, SomeRow), Ct));

    [Fact]
    public Task TheSynonymListIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new ListForeignCitySynonymsHandler(
                null!, null!, null!, NonOperatorContext(), Operator, new ListForeignCitySynonymsValidator())
            .HandleAsync(new ListForeignCitySynonymsQuery(OtherTenant, null, null, 1, 25), Ct));

    [Fact]
    public Task ResolveIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new ResolveUnresolvedForeignCityHandler(
                null!, null!, null!, null!, null!, null!, NonOperatorContext(), Operator, null!,
                new ResolveUnresolvedForeignCityValidator())
            .HandleAsync(new ResolveUnresolvedForeignCityCommand(OtherTenant, SomeRow, Guid.CreateVersion7()), Ct));

    [Fact]
    public Task DiscardIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new DiscardUnresolvedForeignCityHandler(Triage())
            .HandleAsync(new DiscardUnresolvedForeignCityCommand(OtherTenant, SomeRow), Ct));

    [Fact]
    public Task RestoreIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new RestoreUnresolvedForeignCityHandler(Triage())
            .HandleAsync(new RestoreUnresolvedForeignCityCommand(OtherTenant, SomeRow), Ct));

    [Fact]
    public Task CorrectIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new CorrectForeignCitySynonymHandler(
                null!, null!, null!, null!, null!, NonOperatorContext(), Operator, null!,
                new CorrectForeignCitySynonymValidator())
            .HandleAsync(new CorrectForeignCitySynonymCommand(OtherTenant, SomeSynonym, Guid.CreateVersion7(), 1), Ct));

    [Fact]
    public Task DeleteIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new DeleteForeignCitySynonymHandler(
                null!, null!, null!, NonOperatorContext(), Operator, null!)
            .HandleAsync(new DeleteForeignCitySynonymCommand(OtherTenant, SomeSynonym), Ct));
}
