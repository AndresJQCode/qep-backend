using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Api;
using Modules.Wms.Application;

namespace Modules.Wms.UnitTests.Control;

/// <summary>Las rutas de novedades (spec 2026-11-26 §8.5) y la política de cada una. Armar los endpoints no necesita base: además
/// comprueba que cada parámetro se pueda enlazar, que si no sólo fallaría al arrancar la API.</summary>
public sealed class IncidentRoutesTests
{
    private const string Base = "/api/v1/tenants/{tenantId:guid}/wms/incidents";

    [Fact]
    public void EveryIncidentRouteAsksForItsPermission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IRequestDispatcher>(_ => throw new NotSupportedException());
        using var app = builder.Build();
        app.MapWmsEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith(Base, StringComparison.Ordinal))
            .Select(endpoint => (
                Method: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single(),
                Route: endpoint.RoutePattern.RawText![Base.Length..],
                Policy: endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy!))
            .OrderBy(route => route.Route, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ToList();

        const string read = WmsPermissions.DocumentRead;
        const string resolve = WmsPermissions.DiscrepancyResolve;
        (string Method, string Route, string Policy)[] expected =
        [
            ("GET", "", read),
            ("POST", "", read),
            ("GET", "/{id:guid}", read),
            ("POST", "/{id:guid}/reject", resolve),
            ("POST", "/{id:guid}/resolve", resolve),
            ("POST", "/{id:guid}/review", resolve),
        ];
        Assert.Equal(expected, routes);
    }
}
