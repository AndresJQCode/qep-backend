using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Coordinadora.Domain;
using Modules.Tenancy.Application;

namespace Modules.Coordinadora.Application.Settings;

/// <summary>Lo que comparten los casos de uso de la configuración (spec 2026-11-21 §5.2): la doble capa de autorización, la conexión
/// del tenant, la configuración con su versión, la auditoría y el armado del DTO.</summary>
internal static class CoordinadoraSettingsSupport
{
    private const string ConcurrencyConflict = "concurrency.conflict";

    /// <summary>Tenant de la ruta, permiso <c>coordinadora.settings.manage</c> y módulo <c>coordinadora</c> (403, nunca 404).</summary>
    public static Task AuthorizeAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, CancellationToken cancellationToken) =>
        CoordinadoraAuthorization.EnsureAsync(
            executionContext, tenantModules, tenantId, CoordinadoraPermissions.SettingsManage, cancellationToken);

    /// <summary>La conexión de Coordinadora del tenant; una de otro tenant, inexistente o de otro proveedor da 403 <c>authorization.denied</c>.</summary>
    public static async Task<CoordinadoraConnectionInfo> ConnectionAsync(
        ICoordinadoraConnectionDirectory connections, Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await connections.FindAsync(tenantId, connectionId, cancellationToken) ?? throw CoordinadoraAuthorization.Denied();

    /// <summary>La configuración, creada vacía al primer acceso. Con otro tenant en la fila, 403.</summary>
    public static async Task<CoordinadoraSettings> SettingsAsync(
        ICoordinadoraSettingsRepository settings, Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await settings.FindOrCreateAsync(tenantId, connectionId, cancellationToken) ?? throw CoordinadoraAuthorization.Denied();

    /// <summary>412 <c>concurrency.conflict</c> si el <c>If-Match</c> no es la versión que se tiene.</summary>
    public static void EnsureVersion(CoordinadoraSettings settings, long expectedVersion)
    {
        if (settings.Version != expectedVersion)
        {
            throw new RequestConcurrencyException(ConcurrencyConflict, "The Coordinadora settings changed after they were loaded.");
        }
    }

    /// <summary>En la misma unidad de trabajo que el cambio. Lleva sólo la versión que quedó: ni el remitente ni la cuenta.</summary>
    public static void Audit(
        ICoordinadoraAuditRecorder recorder, IExecutionContext executionContext, CoordinadoraSettings settings, DateTimeOffset now) =>
        recorder.Record(
            settings.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            CoordinadoraAuditActions.SettingsUpdated,
            CoordinadoraAuditActions.ConnectionResourceType,
            settings.ConnectionId.ToString(),
            JsonSerializer.Serialize(new { version = settings.Version }),
            now);

    public static async Task<CoordinadoraSettingsDto> ToDtoAsync(
        CoordinadoraSettings settings,
        CoordinadoraConnectionInfo connection,
        ICoordinadoraGeographyLookup geography,
        CancellationToken cancellationToken)
    {
        CoordinadoraCityRef? city = null;
        if (settings.SenderCityId is { } cityId)
        {
            city = await geography.FindCityAsync(cityId, cancellationToken);
        }

        var missing = settings.MissingForShipping().ToList();
        if (settings.SenderCityId is not null && city is null && !missing.Contains(CoordinadoraSettingsReadiness.SenderIncomplete))
        {
            // Una ciudad que Geography ya no tiene es un remitente incompleto, no una ciudad sin atender.
            missing.Add(CoordinadoraSettingsReadiness.SenderIncomplete);
        }
        else if (city is { IsServed: false })
        {
            missing.Add(Shipments.CoordinadoraMissing.SenderCityNotServed);
        }

        return new CoordinadoraSettingsDto(
            settings.ConnectionId,
            new CoordinadoraConnectionDto(connection.Name, connection.Status),
            settings.SenderName,
            settings.SenderAddress,
            settings.SenderPhone,
            city is null ? null : new CoordinadoraCityDto(city.CityId, city.Name, city.CoordinadoraName),
            settings.AccountCode,
            settings.ProductCode,
            settings.ServiceLevel,
            settings.Div,
            settings.DefaultPackage is { } package ? CoordinadoraPackageDto.From(package) : null,
            settings.PackagingType,
            settings.DeclaredValueMinimum,
            settings.CollectOnDelivery,
            settings.LabelFormat,
            settings.Version,
            new CoordinadoraReadinessDto(missing.Count == 0, missing));
    }
}
