using BuildingBlocks.Application;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Geography.UnitTests;

/// <summary>
/// La segunda capa de autorización (<c>IOperatorTenant.IsOperator</c>) de los handlers nuevos de SC-12 y SC-13. En el stub de
/// desarrollo, un tenant que no es el operador pierde los permisos <c>operator.*</c> antes de llegar al handler, así que por HTTP
/// sólo se ve la capa de la política. Acá el contexto sí lleva el permiso y el tenant de la ruta, y lo único que falla es que ese
/// tenant no es el operador. Los demás puertos son nulos a propósito: la autorización va primero, antes de validar o tocar
/// cualquiera, y un handler que la pasara reventaría con una referencia nula en vez de con 403.
/// </summary>
public sealed class CitySynonymHandlersAuthorizationTests
{
    private static readonly TenantId OperatorTenant = new(Guid.CreateVersion7());
    private static readonly TenantId OtherTenant = new(Guid.CreateVersion7());
    private static readonly CitySynonymId SomeSynonym = CitySynonymId.New();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CityOperatorDoubles Doubles() => new(OperatorTenant);

    // Todos los permisos de operador, pero en un tenant que no es el operador.
    private static CityOperatorDoubles.Context NonOperatorContext() =>
        new CityOperatorDoubles.Context(
            OtherTenant, GeographyOperatorPermissions.CitiesRead, GeographyOperatorPermissions.CitiesManage);

    private static async Task AssertDeniedAsync(Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(call);
        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public Task TheListIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new ListCitySynonymsHandler(
                null!, null!, null!, null!, NonOperatorContext(), Doubles().OperatorTenant, new ListCitySynonymsValidator())
            .HandleAsync(new ListCitySynonymsQuery(OtherTenant, null, null, 1, 25), Ct));

    [Fact]
    public Task TheCorrectImpactIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new PreviewSynonymCorrectionImpactHandler(
                null!, null!, null!, null!, NonOperatorContext(), Doubles().OperatorTenant, new PreviewSynonymCorrectionImpactValidator())
            .HandleAsync(new PreviewSynonymCorrectionImpactQuery(OtherTenant, SomeSynonym, Guid.CreateVersion7()), Ct));

    [Fact]
    public Task CorrectIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new CorrectCitySynonymHandler(
                null!, null!, null!, null!, null!, null!, null!, NonOperatorContext(), Doubles().OperatorTenant, null!,
                new CorrectCitySynonymValidator())
            .HandleAsync(new CorrectCitySynonymCommand(OtherTenant, SomeSynonym, Guid.CreateVersion7(), 1), Ct));

    [Fact]
    public Task DeleteIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new DeleteCitySynonymHandler(
                null!, null!, null!, NonOperatorContext(), Doubles().OperatorTenant, null!)
            .HandleAsync(new DeleteCitySynonymCommand(OtherTenant, SomeSynonym), Ct));

    [Fact]
    public Task RetryIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new RetryCitySynonymCorrectionsHandler(
                null!, null!, null!, null!, null!, null!, null!, NonOperatorContext(), Doubles().OperatorTenant, null!)
            .HandleAsync(new RetryCitySynonymCorrectionsCommand(OtherTenant, SomeSynonym), Ct));

    [Fact]
    public Task TheBackfillIsDeniedForANonOperatorTenantHoldingThePermission() =>
        AssertDeniedAsync(() => new RunCityBackfillHandler(
                null!, null!, null!, NonOperatorContext(), Doubles().OperatorTenant, null!)
            .HandleAsync(new RunCityBackfillCommand(OtherTenant), Ct));
}
