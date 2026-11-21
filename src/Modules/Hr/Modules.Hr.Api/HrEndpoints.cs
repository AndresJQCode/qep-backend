using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Hr.Application;

namespace Modules.Hr.Api;

public static class HrEndpoints
{
    public static IEndpointRouteBuilder MapHrEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como loans y billing: la política frena a quien no tiene el permiso y el handler
        // (HrAuthorization) a quien lo tiene en otro tenant. El resto de las rutas llega con las Tareas 6 a 10.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/hr")
            .WithTags("Hr");

        group.MapGet("/salary-rules", GetSalaryRulesAsync)
            .RequireAuthorization(HrPermissions.ContractRead)
            .Produces<SalaryRulesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    // La fecha llega como texto y se lee acá: un valor mal formado que dejara al binding automático lanzaría una
    // BadHttpRequestException, y ApiExceptionHandler la reduce a 500. Mal formada es un 422 con errors.date.
    private static async Task<IResult> GetSalaryRulesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? date = null)
    {
        DateOnly? parsed = null;
        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var value))
            {
                throw new ValidationException([new ValidationFailure("date", "La fecha no es válida. Usa el formato aaaa-mm-dd.")]);
            }

            parsed = value;
        }

        var rules = await dispatcher.QueryAsync(new GetSalaryRulesQuery(tenantId, parsed), cancellationToken);

        return Results.Ok(rules);
    }
}
