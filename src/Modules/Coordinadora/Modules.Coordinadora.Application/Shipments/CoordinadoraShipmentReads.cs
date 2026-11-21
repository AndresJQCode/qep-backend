using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Coordinadora.Domain;
using Modules.Tenancy.Application;

namespace Modules.Coordinadora.Application.Shipments;

/// <summary><c>GET …/orders/{orderId}/coordinadora-shipment</c> (<c>coordinadora.shipment.manage</c>).</summary>
public sealed record GetCoordinadoraShipmentQuery(Guid TenantId, Guid OrderId) : IQuery<CoordinadoraShipmentDto>;

/// <summary>
/// Spec 2026-11-21 §5.5: la fila que cuenta del pedido (la viva o, si todas están cerradas, la última), o 404. De paso aplica la
/// transición perezosa de §5.4: una <c>Pending</c> cuyo reclamo venció pasa a <c>Unknown</c> y se guarda, para que la pantalla ofrezca
/// «Resolver» sin esperar a otro «Generar guía».
/// </summary>
public sealed class GetCoordinadoraShipmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICoordinadoraShipmentRepository shipments,
    ICoordinadoraUnitOfWork unitOfWork,
    IClock clock) : IQueryHandler<GetCoordinadoraShipmentQuery, CoordinadoraShipmentDto>
{
    public async Task<CoordinadoraShipmentDto> HandleAsync(GetCoordinadoraShipmentQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await CoordinadoraShipmentSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var rows = await shipments.ListByOrderAsync(query.TenantId, query.OrderId, cancellationToken);
        if (CoordinadoraShipmentSupport.ExpireStale(rows, clock.UtcNow) > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var current = CoordinadoraShipmentSupport.Current(rows)
            ?? throw new ResourceNotFoundException(
                CoordinadoraErrorCodes.ShipmentNotFound, $"Order '{query.OrderId}' has no Coordinadora guide.");
        return CoordinadoraShipmentDto.From(current);
    }
}

/// <summary><c>GET …/orders/{orderId}/coordinadora-shipment/preview</c>: la petición armada con el paquete por defecto, o los faltantes.</summary>
public sealed record PreviewCoordinadoraShipmentQuery(Guid TenantId, Guid OrderId) : IQuery<CoordinadoraShipmentPreviewDto>;

/// <summary>Spec 2026-11-21 §5.5. No escribe nada ni llama a Coordinadora. Con la conexión sin credenciales legibles la vista previa sale igual
/// (el NIT va vacío): es para ver qué falta, no para mandar.</summary>
public sealed class PreviewCoordinadoraShipmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICoordinadoraOrderSource orders,
    ICoordinadoraConnectionDirectory connections,
    ICoordinadoraSettingsRepository settingsRepository,
    ICoordinadoraGeographyLookup geography) : IQueryHandler<PreviewCoordinadoraShipmentQuery, CoordinadoraShipmentPreviewDto>
{
    public async Task<CoordinadoraShipmentPreviewDto> HandleAsync(PreviewCoordinadoraShipmentQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await CoordinadoraShipmentSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var context = await CoordinadoraShipmentSupport.LoadAsync(
            orders, connections, settingsRepository, geography, query.TenantId, query.OrderId, cancellationToken);
        var nit = context.Connection is null
            ? null
            : (await connections.ResolveAsync(query.TenantId, context.Connection.Id, cancellationToken)).Credentials?.Nit;

        return CoordinadoraGuideBuilder.Build(context.Input, context.Settings, context.RecipientCity, context.SenderCity, nit, null) switch
        {
            CoordinadoraBuildResult.Ready ready => new CoordinadoraShipmentPreviewDto(
                true,
                [],
                CoordinadoraPackageDto.From(ready.Package),
                ready.DeclaredValue,
                JsonSerializer.Deserialize<JsonElement>(ready.GuideJson)),
            CoordinadoraBuildResult.NotReady notReady => new CoordinadoraShipmentPreviewDto(
                false,
                [.. notReady.Missing.Select(value => new CoordinadoraMissingPreviewDto(value.Code, value.Reference, value.Label))],
                context.Settings?.DefaultPackage is { } package ? CoordinadoraPackageDto.From(package) : null,
                null,
                null),
            _ => throw new InvalidOperationException("Unknown build result."),
        };
    }
}

/// <summary><c>GET …/orders/{orderId}/coordinadora-shipment/label</c>: el PDF del rótulo de la guía viva.</summary>
public sealed record GetCoordinadoraLabelQuery(Guid TenantId, Guid OrderId) : IQuery<CoordinadoraLabelDto>;

/// <summary>Spec 2026-11-21 §5.5: el PDF guardado al crear si lo hay; si no, <c>Guias_imprimirRotulos</c> en vivo (nunca se cachea). Sólo una
/// guía <c>Created</c> (422 <c>state_invalid</c> si no).</summary>
public sealed class GetCoordinadoraLabelHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICoordinadoraShipmentRepository shipments,
    ICoordinadoraConnectionDirectory connections,
    ICoordinadoraApi api) : IQueryHandler<GetCoordinadoraLabelQuery, CoordinadoraLabelDto>
{
    public async Task<CoordinadoraLabelDto> HandleAsync(GetCoordinadoraLabelQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await CoordinadoraShipmentSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var rows = await shipments.ListByOrderAsync(query.TenantId, query.OrderId, cancellationToken);
        var shipment = CoordinadoraShipmentSupport.LiveOrThrow(rows, query.OrderId);
        shipment.EnsureCreated("Only a guide created in Coordinadora has a label.");

        var content = shipment.LabelPdf;
        if (content is null)
        {
            var credentials = await CoordinadoraShipmentSupport.CredentialsAsync(connections, query.TenantId, shipment.ConnectionId, cancellationToken);
            content = await api.PrintLabelAsync(credentials, shipment.GuideNumber!, cancellationToken);
        }

        return new CoordinadoraLabelDto(content, CoordinadoraShipmentSupport.LabelFileName(shipment));
    }
}
