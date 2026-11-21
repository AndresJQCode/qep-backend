using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Coordinadora.Domain;
using Modules.Tenancy.Application;

namespace Modules.Coordinadora.Application.Shipments;

/// <summary>El pedido, la conexión del tenant (si la hay) con su configuración, y las ciudades resueltas: todo lo que el armador necesita.</summary>
internal sealed record CoordinadoraShipmentContext(
    CoordinadoraShipmentInput Input,
    CoordinadoraConnectionInfo? Connection,
    CoordinadoraSettings? Settings,
    CoordinadoraCityRef? RecipientCity,
    CoordinadoraCityRef? SenderCity);

/// <summary>Lo que comparten los casos de uso de guías (spec 2026-11-21 §5.3 a §5.5): la doble capa de autorización, la carga del pedido
/// y la configuración, las credenciales, el armado, la fila vigente y la auditoría.</summary>
internal static class CoordinadoraShipmentSupport
{
    /// <summary>Tenant de la ruta, permiso <c>coordinadora.shipment.manage</c> y módulo <c>coordinadora</c> (403, nunca 404).</summary>
    public static Task AuthorizeAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, CancellationToken cancellationToken) =>
        CoordinadoraAuthorization.EnsureAsync(
            executionContext, tenantModules, tenantId, CoordinadoraPermissions.ShipmentManage, cancellationToken);

    /// <summary>El pedido (404 si no es del tenant), la conexión del tenant con su configuración si existen, y las dos ciudades. No lanza
    /// por faltantes: eso lo junta el armador.</summary>
    public static async Task<CoordinadoraShipmentContext> LoadAsync(
        ICoordinadoraOrderSource orders,
        ICoordinadoraConnectionDirectory connections,
        ICoordinadoraSettingsRepository settingsRepository,
        ICoordinadoraGeographyLookup geography,
        Guid tenantId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var input = await orders.GetAsync(tenantId, orderId, cancellationToken)
            ?? throw new ResourceNotFoundException("order.order.not_found", $"Order '{orderId}' was not found.");

        // El proveedor admite una conexión por tenant: la primera es la única.
        var tenantConnections = await connections.ListByTenantAsync(tenantId, cancellationToken);
        var connection = tenantConnections.Count > 0 ? tenantConnections[0] : null;
        var settings = connection is null ? null : await settingsRepository.FindAsync(tenantId, connection.Id, cancellationToken);

        CoordinadoraCityRef? recipientCity = null;
        if (input.Recipient.CityId is { } recipientCityId)
        {
            recipientCity = await geography.FindCityAsync(recipientCityId, cancellationToken);
        }

        CoordinadoraCityRef? senderCity = null;
        if (settings?.SenderCityId is { } senderCityId)
        {
            senderCity = await geography.FindCityAsync(senderCityId, cancellationToken);
        }

        return new CoordinadoraShipmentContext(input, connection, settings, recipientCity, senderCity);
    }

    /// <summary>Las credenciales de la conexión, o 409 <c>coordinadora.connection_unavailable</c> (pausada, clave rechazada, ilegible…).</summary>
    public static async Task<CoordinadoraCredentials> CredentialsAsync(
        ICoordinadoraConnectionDirectory connections, Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var resolution = await connections.ResolveAsync(tenantId, connectionId, cancellationToken);
        return resolution.Credentials
            ?? throw new ResourceConflictException(
                CoordinadoraErrorCodes.ConnectionUnavailable,
                $"The Coordinadora connection cannot be used right now ({resolution.UnavailableReason}).");
    }

    /// <summary>El armado, o 422 <c>not_ready</c> con todos los faltantes.</summary>
    public static CoordinadoraBuildResult.Ready BuildOrThrow(
        CoordinadoraShipmentContext context, string? senderNit, CoordinadoraShipmentOverrides? overrides) =>
        CoordinadoraGuideBuilder.Build(context.Input, context.Settings, context.RecipientCity, context.SenderCity, senderNit, overrides) switch
        {
            CoordinadoraBuildResult.Ready ready => ready,
            CoordinadoraBuildResult.NotReady notReady => throw new CoordinadoraShipmentNotReadyException(notReady.Missing),
            _ => throw new InvalidOperationException("Unknown build result."),
        };

    /// <summary>La fila que cuenta: la viva si la hay y, si no, la última (una <c>Rejected</c> o <c>Cancelled</c> sigue a la vista para que la
    /// pantalla muestre qué pasó). <c>null</c> sin filas.</summary>
    public static CoordinadoraShipment? Current(IEnumerable<CoordinadoraShipment> rows)
    {
        var list = rows.ToList();
        return list.Where(row => row.IsLive).OrderByDescending(row => row.CreatedAt).FirstOrDefault()
            ?? list.OrderByDescending(row => row.CreatedAt).FirstOrDefault();
    }

    /// <summary>La fila viva del pedido, o 404 <c>coordinadora.shipment.not_found</c>.</summary>
    public static CoordinadoraShipment LiveOrThrow(IEnumerable<CoordinadoraShipment> rows, Guid orderId) =>
        rows.Where(row => row.IsLive).OrderByDescending(row => row.CreatedAt).FirstOrDefault()
        ?? throw new ResourceNotFoundException(
            CoordinadoraErrorCodes.ShipmentNotFound, $"Order '{orderId}' has no current Coordinadora guide.");

    /// <summary>Las <c>Pending</c> cuyo reclamo venció pasan a <c>Unknown</c> al leerlas (§5.4). Devuelve cuántas cambiaron.</summary>
    public static int ExpireStale(IEnumerable<CoordinadoraShipment> rows, DateTimeOffset now) => rows.Count(row => row.ExpireIfStale(now));

    /// <summary>Auditoría atómica, en el mismo guardado que el cambio. Lleva el id del pedido, el estado, el número de guía y el código de
    /// error: nunca la petición, el destinatario ni las claves.</summary>
    public static void Audit(
        ICoordinadoraAuditRecorder recorder,
        IExecutionContext executionContext,
        CoordinadoraShipment shipment,
        string action,
        DateTimeOffset now,
        string? outcome = null) =>
        recorder.Record(
            shipment.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            action,
            CoordinadoraAuditActions.ShipmentResourceType,
            shipment.Id.ToString(),
            JsonSerializer.Serialize(new
            {
                orderId = shipment.OrderId,
                status = shipment.Status.ToString(),
                guideNumber = shipment.GuideNumber,
                errorCode = shipment.ErrorCode,
                outcome,
            }),
            now);

    /// <summary>El nombre del archivo del rótulo.</summary>
    public static string LabelFileName(CoordinadoraShipment shipment) => $"rotulo-coordinadora-{shipment.GuideNumber}.pdf";
}
