using System.Security.Claims;
using System.Text.RegularExpressions;
using Bootstrapper.Authentication;
using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Api;

public static class WebTelemetryEndpoints
{
    public static IEndpointRouteBuilder MapWebTelemetryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Sólo autenticación, sin permiso: lo manda cualquier sesión del tenant, y exigir uno dejaría sin medir justo a
        // quien tiene menos. El efecto es una métrica, no un dato: no escribe en la base ni pasa por un handler.
        endpoints
            .MapPost("/api/v1/tenants/{tenantId:guid}/telemetry/web-vitals", RecordAsync)
            .WithTags("Telemetry")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiterPolicies.Telemetry)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    private static async Task<IResult> RecordAsync(
        Guid tenantId,
        HttpContext httpContext,
        IValidator<WebTelemetryRequest> validator,
        WebTelemetryMetrics metrics,
        WebTelemetryRequest? request,
        CancellationToken cancellationToken)
    {
        // Los claims directo, como /authorization/me: sin membresía activa en el tenant pedido el claim no existe, y eso es
        // un 403, no un 500.
        var claimedTenant = httpContext.User.FindFirstValue(QepClaimTypes.TenantId);
        if (!Guid.TryParse(claimedTenant, out var authenticatedTenant) || authenticatedTenant != tenantId)
        {
            throw new RequestForbiddenException(
                "telemetry.denied",
                "The subject cannot send telemetry for this tenant.");
        }

        // Sin cuerpo llega null y es un 422 con el campo, no un 400 del binder.
        request ??= new WebTelemetryRequest(null);
        await validator.ValidateAndThrowAsync(request, cancellationToken);

        foreach (var sample in request.Samples!)
        {
            var value = sample.Value!.Value;
            switch (sample.Metric)
            {
                case WebTelemetryRequestValidator.Navigation:
                    metrics.RecordNavigation(sample.Route!, value);
                    break;
                case WebTelemetryRequestValidator.Cls:
                    metrics.RecordCls(value);
                    break;
                default:
                    // lcp, inp, ttfb, fcp. La ruta que el cliente haya mandado se descarta a propósito.
                    metrics.RecordVital(sample.Metric!, value);
                    break;
            }
        }

        return Results.NoContent();
    }
}

/// <summary>
/// El lote que manda la SPA. Es un contrato con el frontend: <c>{ samples: [{ metric, route?, value }] }</c>.
/// </summary>
/// <remarks>
/// Todo viaja anulable y <c>Metric</c> es texto, a propósito: un cuerpo sin campo, o con una métrica que no existe, llega
/// al validador y es un 422 con <c>errors</c> —el único que el cliente sabe leer—, no un 400 del binder de JSON (mismo
/// criterio que <c>ChooseWhatsAppChannelRequest</c>).
/// </remarks>
public sealed record WebTelemetryRequest(IReadOnlyList<WebTelemetrySample>? Samples);

/// <summary>
/// Una medición del navegador. <c>Value</c> va en segundos, salvo <c>cls</c>, que es un puntaje sin unidad.
/// </summary>
/// <remarks>
/// <c>Route</c> es la <b>plantilla</b> de la ruta del SPA (<c>/quotations/$id</c>), nunca la URL: el navegador conoce el
/// patrón que casó el router y el backend no puede deducirlo del path (un id no dice qué pantalla fue). Mandar la URL
/// llevaría ids de negocio y de cliente a una etiqueta de métrica, que es cardinalidad sin tope y dato personal en
/// Prometheus. Por eso sólo la navegación la lleva, y el validador acota su forma. Es la regla de backend for frontend del
/// repo: la forma de la petición la decide lo que el cliente sabe y el servidor no.
/// </remarks>
public sealed record WebTelemetrySample(string? Metric, string? Route, double? Value);

public sealed partial class WebTelemetryRequestValidator : AbstractValidator<WebTelemetryRequest>
{
    public const string Navigation = "navigation";
    public const string Cls = "cls";

    public const int MaxSamples = 50;
    public const double MaxValue = 120;

    private static readonly string[] Metrics = [Navigation, "lcp", "inp", "ttfb", "fcp", Cls];

    // Barra inicial, hasta 160 caracteres, sólo los de una plantilla de ruta: sin espacios, query ni "?" ni "#".
    [GeneratedRegex(@"^/[A-Za-z0-9_$./-]{0,159}$")]
    private static partial Regex RoutePattern();

    public WebTelemetryRequestValidator()
    {
        RuleFor(request => request.Samples)
            .NotNull()
            .Must(samples => samples is null || samples.Count <= MaxSamples)
            .WithMessage($"A batch carries at most {MaxSamples} samples.");

        RuleForEach(request => request.Samples!)
            .ChildRules(sample =>
            {
                sample.RuleFor(item => item).NotNull();
                sample.RuleFor(item => item.Metric)
                    .NotEmpty()
                    .Must(metric => metric is null || Metrics.Contains(metric, StringComparer.Ordinal))
                    .WithMessage("Unknown metric.");
                sample.RuleFor(item => item.Value)
                    .NotNull()
                    .InclusiveBetween(0, MaxValue);
                // Sólo la navegación lleva ruta; en las demás se ignora, venga o no.
                sample.RuleFor(item => item.Route)
                    .NotEmpty()
                    .Must(route => route is null || RoutePattern().IsMatch(route))
                    .WithMessage("The route is not a route template.")
                    .When(item => item.Metric == Navigation);
            })
            .When(request => request.Samples is not null);
    }
}
