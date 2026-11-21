using System.Text.Json;
using Modules.Coordinadora.Domain;

namespace Modules.Coordinadora.Application;

/// <summary>El nombre y el estado de la conexión en Integrations (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>).</summary>
public sealed record CoordinadoraConnectionDto(string Name, string Status);

/// <summary>Un paquete como viaja y como se recibe (spec 2026-11-21 §5.3).</summary>
public sealed record CoordinadoraPackageDto(int HeightCm, int WidthCm, int LengthCm, decimal WeightKg, int Units)
{
    public static CoordinadoraPackageDto From(CoordinadoraPackage package) =>
        new(package.HeightCm, package.WidthCm, package.LengthCm, package.WeightKg, package.Units);
}

/// <summary>La ciudad del remitente tal como la pantalla la pinta: el nombre de Geography y el de Coordinadora (<c>null</c> si no la atiende).</summary>
public sealed record CoordinadoraCityDto(Guid Id, string Name, string? CoordinadoraName);

/// <summary>
/// Spec 2026-11-21 §5.2: la configuración entera de la conexión, más <c>readiness</c> (completa, o los códigos de lo que falta) y el
/// estado de la conexión. BFF: la ciudad del remitente viaja resuelta (nombre y nombre de Coordinadora) para que la pantalla no tenga que
/// pedirla a Geography. La versión es también el <c>ETag</c> y lo que el <c>PUT</c> manda en <c>If-Match</c>. Nunca lleva una credencial.
/// </summary>
public sealed record CoordinadoraSettingsDto(
    Guid ConnectionId,
    CoordinadoraConnectionDto Connection,
    string? SenderName,
    string? SenderAddress,
    string? SenderPhone,
    CoordinadoraCityDto? SenderCity,
    string? AccountCode,
    string? ProductCode,
    int? ServiceLevel,
    string? Div,
    CoordinadoraPackageDto? DefaultPackage,
    int PackagingType,
    decimal DeclaredValueMinimum,
    bool CollectOnDelivery,
    string? LabelFormat,
    long Version,
    CoordinadoraReadinessDto Readiness);

/// <summary>Completa, o con los códigos de lo que falta (<c>sender_incomplete</c>, <c>sender_city_not_served</c>, <c>package_defaults_missing</c>).</summary>
public sealed record CoordinadoraReadinessDto(bool Complete, IReadOnlyList<string> MissingCodes);

/// <summary>
/// Una guía tal como la pantalla la dibuja (spec §5.5). <c>hasLabel</c> dice si «Imprimir rótulo» tiene sentido (la guía existe en
/// Coordinadora; el PDF se guardó o se pide en vivo). <b>Nunca</b> lleva la petición (<c>request_json</c>), el rótulo ni datos del destinatario;
/// <c>errorDetail</c> es lo que dijo Coordinadora, recortado, y sólo lo ve quien tiene el permiso de guías.
/// </summary>
public sealed record CoordinadoraShipmentDto(
    Guid Id,
    string Status,
    string? GuideNumber,
    decimal DeclaredValue,
    bool HasLabel,
    string? TrackingStatus,
    string? TrackingDetail,
    DateTimeOffset? TrackedAt,
    string? ErrorCode,
    string? ErrorDetail,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CancelledAt)
{
    public static CoordinadoraShipmentDto From(CoordinadoraShipment shipment) =>
        new(
            shipment.Id,
            shipment.Status.ToString(),
            shipment.GuideNumber,
            shipment.DeclaredValue,
            shipment.IsCreated,
            shipment.TrackingStatus,
            shipment.TrackingDetail,
            shipment.TrackedAt,
            shipment.ErrorCode,
            shipment.ErrorDetail,
            shipment.Attempts,
            shipment.CreatedAt,
            shipment.UpdatedAt,
            shipment.CancelledAt);
}

/// <summary>
/// <c>GET …/preview</c>: la petición armada (<c>guide</c>, el <c>p</c> de <c>Guias_generarGuia</c> sin credenciales) o los faltantes, y el
/// paquete y el valor declarado que se usarían. <c>guide</c> lleva el destinatario: sólo lo ve quien tiene el permiso de guías, que es quien
/// lo va a mandar.
/// </summary>
public sealed record CoordinadoraShipmentPreviewDto(
    bool Ready,
    IReadOnlyList<CoordinadoraMissingPreviewDto> Missing,
    CoordinadoraPackageDto? Package,
    decimal? DeclaredValue,
    JsonElement? Guide);

public sealed record CoordinadoraMissingPreviewDto(string Code, string? Reference, string? Label);

/// <summary><c>POST …/quote</c>: lo que Coordinadora devolvió (<c>result</c> tal cual, V-4) y el flete total si se pudo leer.</summary>
public sealed record CoordinadoraQuoteDto(
    decimal? FreightTotal,
    CoordinadoraPackageDto Package,
    decimal DeclaredValue,
    JsonElement Result);

/// <summary>El PDF del rótulo, listo para <c>Results.File</c>.</summary>
public sealed record CoordinadoraLabelDto(byte[] Content, string FileName);
