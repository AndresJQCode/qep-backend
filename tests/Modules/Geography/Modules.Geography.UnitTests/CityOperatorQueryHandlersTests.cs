using BuildingBlocks.Application;
using FluentValidation;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Geography.UnitTests;

/// <summary>
/// Spec 2026-10-26 §6: las lecturas de la consola de ciudades del operador. La autorización va antes de validar (un 403 nunca
/// depende de la entrada), la bandeja sólo filtra por <c>Pending</c> o <c>Discarded</c>, y la mejor sugerencia es la primera
/// de <see cref="CitySuggestions.Rank"/>.
/// </summary>
public sealed class CityOperatorQueryHandlersTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly DateTimeOffset Seen = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuthorizationGoesBeforeValidation()
    {
        var doubles = new CityOperatorDoubles(Operator);
        var foreign = TenantId.New();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => doubles.ListHandler(foreign, GeographyOperatorPermissions.CitiesRead)
            .HandleAsync(new ListUnresolvedCitiesQuery(foreign, null, null, null, 0, 25), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(doubles.UnresolvedCities.Listed);
    }

    [Fact]
    public async Task AResolvedStatusFilterIs422()
    {
        var doubles = new CityOperatorDoubles(Operator);

        var error = await Assert.ThrowsAsync<ValidationException>(() => doubles.ListHandler(Operator, GeographyOperatorPermissions.CitiesRead)
            .HandleAsync(new ListUnresolvedCitiesQuery(Operator, "Resolved", null, null, 1, 25), TestContext.Current.CancellationToken));

        Assert.Equal("Status", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(doubles.UnresolvedCities.Listed);
    }

    [Fact]
    public async Task TheBestSuggestionIsTheFirstRanked()
    {
        var doubles = new CityOperatorDoubles(Operator);
        var valle = doubles.AddDepartment("76", "VALLE DEL CAUCA");
        var cali = doubles.AddCity("76001", "SANTIAGO DE CALI", "CALI", valle);
        doubles.AddCity("76126", "CALIMA", null, valle);
        doubles.UnresolvedCities.Add(UnresolvedCity.Create(UnresolvedCityId.New(), valle, "Calli", "Calle 5", null, Seen), 3, 2);

        var page = await doubles.ListHandler(Operator, GeographyOperatorPermissions.CitiesRead)
            .HandleAsync(new ListUnresolvedCitiesQuery(Operator, null, null, null, 1, 25), TestContext.Current.CancellationToken);

        var item = Assert.Single(page.Items);
        Assert.Equal(new CitySuggestionDto(cali.Id.Value, "CALI", "76001", "SANTIAGO DE CALI", 80), item.BestSuggestion);
        Assert.Equal((3, 2, "Pending"), (item.OrderCount, item.TenantCount, item.Status));
        Assert.Equal(new DepartmentRefDto(valle.Value, "VALLE DEL CAUCA", "76"), item.Department);
        Assert.Equal((1, 1, 25), (page.Total, page.Page, page.PageSize));
        Assert.Equal(UnresolvedCityStatus.Pending, Assert.Single(doubles.UnresolvedCities.Listed).Status);
    }

    [Fact]
    public async Task WithoutSuggestionsBestIsNull()
    {
        var doubles = new CityOperatorDoubles(Operator);
        var valle = doubles.AddDepartment("76", "VALLE DEL CAUCA");
        doubles.AddCity("76001", "SANTIAGO DE CALI", "CALI", valle);
        doubles.UnresolvedCities.Add(UnresolvedCity.Create(UnresolvedCityId.New(), valle, "Xyzwvu", "Calle 5", null, Seen), 1, 1);

        var page = await doubles.ListHandler(Operator, GeographyOperatorPermissions.CitiesRead)
            .HandleAsync(new ListUnresolvedCitiesQuery(Operator, "Pending", null, null, 1, 25), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(page.Items).BestSuggestion);
    }
}
