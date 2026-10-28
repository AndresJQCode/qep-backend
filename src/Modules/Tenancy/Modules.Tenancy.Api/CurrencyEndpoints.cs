using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Tenancy.Application;

namespace Modules.Tenancy.Api;

public static class CurrencyEndpoints
{
    public static IEndpointRouteBuilder MapCurrencyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated and tenant-less: the catalogue is the same for every tenant (spec D1), so
        // there is no tenant permission to check and no tenant to leak.
        endpoints.MapGet("/api/v1/currencies", () => Results.Ok(Currencies.All))
            .RequireAuthorization()
            .WithTags("Currencies")
            .Produces<IReadOnlyList<CurrencyInfo>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return endpoints;
    }
}
