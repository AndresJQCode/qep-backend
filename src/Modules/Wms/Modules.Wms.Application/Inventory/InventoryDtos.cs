using System.Buffers.Binary;
using System.Buffers.Text;

namespace Modules.Wms.Application;

/// <summary>
/// Una fila de saldo tal como la pinta la pantalla de existencias (spec §24.3). <c>Pickable</c> (S10) es <c>available</c> sólo si la
/// ubicación tiene <c>allows_picking</c>: la app de picking filtra por esto y la pantalla no conoce <c>allows_picking</c>, así que
/// sin el campo un bin de reserva lleno se vería como «sin existencia». <c>DisplayQty</c> es un adorno: el saldo siempre está en la
/// unidad base y la presentación preferida del perfil sólo lo convierte para mostrarlo.
/// </summary>
public sealed record StockRowDto(
    Guid OwnerId,
    string OwnerName,
    Guid WarehouseId,
    string WarehouseCode,
    Guid LocationId,
    string LocationCode,
    string LocationPath,
    Guid VariantId,
    string ProductName,
    string VariantCode,
    string? Barcode,
    Guid? LotId,
    string? LotCode,
    DateOnly? ExpirationDate,
    string Status,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    decimal Pickable,
    string BaseUom,
    string? DisplayUom,
    decimal? DisplayQty,
    DateTimeOffset UpdatedAt);

/// <summary><c>Pickable</c> viaja con los totales (spec §24.3): sin él la pantalla no sabe cuánto se puede tomar sobre el filtro completo.
/// <para>Inventario básico §5.4: <c>Reserved</c> y <c>Available</c> suman también las reservas de demanda de los pedidos (por bodega y
/// variante, sin ubicación), así que el total de la lista coincide con el resumen; las filas por ubicación muestran sólo lo asignado.
/// Por eso <c>Pickable</c>, que es lo físico que se puede tomar, <b>puede superar a <c>Available</c></b> (10 en el bin, 3 reservados por
/// pedidos: se pueden tomar 10, quedan 7 por vender) y <c>Available</c> puede ser negativo si se reservó más de lo que hay. Es lo
/// correcto: la pantalla no debe «arreglarlo».</para></summary>
public sealed record StockTotalsDto(decimal OnHand, decimal Reserved, decimal Available, decimal Pickable);

/// <summary><c>Totals</c> viaja porque sumar la página da un total que depende de la página que se esté mirando, y
/// <c>pageSize</c> tiene tope: la pantalla no puede pedir el filtro entero para sumarlo ella.</summary>
public sealed record StockPageDto(IReadOnlyList<StockRowDto> Items, int Page, int PageSize, int TotalCount, StockTotalsDto Totals);

/// <summary><c>Expected</c> vale 0 en este corte (RF-026, lo esperado por documentos abiertos, es fase 2) y se compone en el handler
/// y no en la vista: <c>v_stock_summary</c> sólo conoce saldos, y así el día que exista el origen de lo esperado la vista no cambia.</summary>
public sealed record StockSummaryDto(
    decimal OnHand,
    decimal Received,
    decimal Available,
    decimal Pickable,
    decimal Reserved,
    decimal Blocked,
    decimal InTransit,
    decimal Expected);

public sealed record MovementEndpointDto(string LocationCode, string Status);

/// <summary>
/// Un movimiento del ledger. <c>ActorName</c> y <c>DocumentNumber</c> van <c>null</c> en este corte: el nombre del actor vive en
/// Tenancy y el número del documento en los módulos de documentos (ajustes, recepciones), que este módulo no resuelve todavía; el
/// campo existe para que la pantalla no cambie cuando se llenen.
/// </summary>
public sealed record MovementDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string Reason,
    decimal Quantity,
    string BaseUom,
    string? CapturedUom,
    decimal? CapturedQty,
    decimal? ConversionFactor,
    MovementEndpointDto From,
    MovementEndpointDto To,
    Guid VariantId,
    string ProductName,
    string VariantCode,
    string? LotCode,
    string? Serial,
    string? DocumentType,
    Guid? DocumentId,
    string? DocumentNumber,
    Guid BatchId,
    Guid? ReversesMovementId,
    Guid ActorId,
    string? ActorName,
    string ActorType,
    string? DeviceId);

/// <summary>La historia va por cursor y no por página (decisión del owner, 2026-11-01): crece sin tope y un <c>COUNT(*)</c> sobre el
/// ledger particionado recorrería todas las particiones. <c>NextCursor</c> es <c>null</c> cuando no hay más.</summary>
public sealed record MovementPageDto(IReadOnlyList<MovementDto> Items, string? NextCursor);

/// <summary>La posición de un movimiento en el orden <c>(occurred_at, id)</c> descendente de la historia, la clave de la PK del ledger.
/// Viaja como texto opaco (base64url de los ticks UTC y el id): el cliente sólo lo devuelve tal cual. Copia de
/// <c>Modules.Messaging.Application.ConversationCursor</c>: los módulos no se ven entre sí.</summary>
public sealed record MovementCursor(DateTimeOffset OccurredAt, Guid Id)
{
    private const int Length = sizeof(long) + 16;

    public static string Encode(MovementCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Span<byte> bytes = stackalloc byte[Length];
        BinaryPrimitives.WriteInt64BigEndian(bytes, cursor.OccurredAt.UtcTicks);
        cursor.Id.TryWriteBytes(bytes[sizeof(long)..], bigEndian: true, out _);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary><c>false</c> para todo lo que no salió de <see cref="Encode"/>: el validador lo convierte en <c>validation.failed</c>
    /// con la clave <c>Cursor</c>.</summary>
    public static bool TryDecode(string? value, out MovementCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        // TryDecodeFromChars lanza (no devuelve false) ante una longitud imposible: IsValid lo filtra antes.
        Span<byte> bytes = stackalloc byte[Length];
        if (!Base64Url.IsValid(value, out var decodedLength) || decodedLength != Length
            || !Base64Url.TryDecodeFromChars(value, bytes, out var written) || written != Length)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        cursor = new MovementCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new Guid(bytes.Slice(sizeof(long), 16), bigEndian: true));
        return true;
    }
}
