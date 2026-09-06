namespace Modules.Quotations.Application;

public sealed record SalePaymentProofDto(Guid Id, Guid FileId, decimal Amount, DateTimeOffset UploadedAt);

public sealed record SaleDto(
    Guid Id,
    string SaleNumber,
    Guid QuotationId,
    string Status,
    // PaymentStatus es texto y no el enum del dominio: ningún DTO expone un enum de dominio
    // directamente, mismo criterio que QuotationDto.Status.
    string PaymentStatus,
    string? Notes,
    DateTimeOffset ConvertedAt,
    Guid ConvertedBy,
    /// <summary>Cuándo se aprobó y quién. Null mientras la venta sigue pendiente de revisión.
    /// </summary>
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    string? RitualCollectionSyncId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<SalePaymentProofDto> PaymentProofs);

/// <summary>Un comprobante de pago, tal como viaja en el request de conversión (US-14): el
/// archivo ya se subió a Storage por fuera de este llamado, acá sólo se referencia.</summary>
public sealed record SalePaymentProofRequest(Guid FileId, decimal Amount);

/// <summary>US-13 a US-16: el asistente de conversión. No lleva cliente/productos/totales —
/// todo eso se hereda de la cotización, que ya existe.</summary>
public sealed record ConvertQuotationToSaleRequest(
    string PaymentStatus,
    string? Notes,
    IReadOnlyCollection<SalePaymentProofRequest> PaymentProofs);

public sealed record SalePaymentProofResponse(Guid Id, Guid FileId, decimal Amount, DateTimeOffset UploadedAt);

public sealed record SaleResponse(
    Guid Id,
    string SaleNumber,
    Guid QuotationId,
    string Status,
    string PaymentStatus,
    string? Notes,
    DateTimeOffset ConvertedAt,
    Guid ConvertedBy,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    string? RitualCollectionSyncId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<SalePaymentProofResponse> PaymentProofs);
