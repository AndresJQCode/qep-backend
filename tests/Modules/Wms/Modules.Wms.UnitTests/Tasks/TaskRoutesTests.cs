using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Api;
using Modules.Wms.Application;

namespace Modules.Wms.UnitTests.Tasks;

/// <summary>Las rutas del motor (spec 2026-11-26 §6.11) y la política de cada una. Armar los endpoints no necesita base: además
/// comprueba que cada parámetro se pueda enlazar, que si no sólo fallaría al arrancar la API.</summary>
public sealed class TaskRoutesTests
{
    private const string Base = "/api/v1/tenants/{tenantId:guid}/wms/tasks";

    [Fact]
    public void EveryTaskRouteAsksForItsPermission()
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
        const string manage = WmsPermissions.TaskManage;
        (string Method, string Route, string Policy)[] expected =
        [
            ("GET", "", read),
            ("POST", "", manage),
            ("GET", "/assignees", manage),
            ("GET", "/mine", read),
            ("POST", "/next", read),
            ("GET", "/summary", read),
            ("GET", "/{id:guid}", read),
            ("POST", "/{id:guid}/assign", manage),
            ("POST", "/{id:guid}/cancel", manage),
            ("POST", "/{id:guid}/claim", read),
            ("POST", "/{id:guid}/heartbeat", read),
            ("POST", "/{id:guid}/hold", manage),
            ("POST", "/{id:guid}/lines/{lineId:guid}/cancel", manage),
            ("POST", "/{id:guid}/lines/{lineId:guid}/confirm", read),
            ("POST", "/{id:guid}/lines/{lineId:guid}/count", read),
            ("POST", "/{id:guid}/lines/{lineId:guid}/short", read),
            ("POST", "/{id:guid}/priority", manage),
            ("POST", "/{id:guid}/publish", manage),
            ("POST", "/{id:guid}/unassign", manage),
            ("POST", "/{id:guid}/unclaim", read),
        ];
        Assert.Equal(expected, routes);
    }
}
