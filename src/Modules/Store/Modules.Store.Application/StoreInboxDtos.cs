namespace Modules.Store.Application;

public sealed record StoreMemberNameDto(Guid MemberId, string DisplayName);

public sealed record StoreRequestBillingDto(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string Phone,
    string PhoneE164,
    string Email,
    string Address,
    string Country,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? CityId,
    string CityName);

public sealed record StoreRequestShippingDto(
    string? RecipientName,
    string EffectiveRecipientName,
    string Phone,
    string PhoneE164,
    string Address,
    string Country,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? CityId,
    string CityName);

/// <summary>Una fila de «Desde la tienda». Cada fila trae su moneda: la interfaz nunca suma totales de filas distintas.</summary>
public record StoreOrderRequestRowDto(
    Guid Id,
    string Number,
    DateTimeOffset ReceivedAt,
    string Status,
    StoreRequestBillingDto Billing,
    StoreRequestShippingDto? Shipping,
    Guid? CustomerId,
    bool IsKnownCustomer,
    int LineCount,
    decimal UnitCount,
    bool HasScaleDiscount,
    decimal Total,
    string Currency,
    string? Note,
    StoreMemberNameDto? ConvertedBy,
    DateTimeOffset? ConvertedAt,
    Guid? OrderId,
    string? OrderNumber,
    decimal? TotalDifference,
    StoreMemberNameDto? DiscardedBy,
    DateTimeOffset? DiscardedAt,
    string? DiscardReason,
    string? DiscardNote,
    bool IsBeingTaken);

public sealed record StoreOrderRequestLineDto(
    Guid VariantId,
    Guid ProductId,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    decimal DiscountedUnitPrice,
    decimal LineTotal);

/// <summary>La fila más sus líneas. Los campos de la fila van aplanados, como en el contrato.</summary>
public sealed record StoreOrderRequestDetailDto : StoreOrderRequestRowDto
{
    public StoreOrderRequestDetailDto(StoreOrderRequestRowDto row, IReadOnlyList<StoreOrderRequestLineDto> lines)
        : base(row) => Lines = lines;

    public IReadOnlyList<StoreOrderRequestLineDto> Lines { get; }
}

public sealed record StoreOrderRequestPageDto(IReadOnlyList<StoreOrderRequestRowDto> Items, int Total, int Page, int PageSize);

public sealed record StoreInboxSummaryDto(int Untaken, int TakenByMe, int ReceivedToday);

public sealed record StoreCustomerCandidateDto(
    Guid Id,
    string Name,
    string? PhoneE164,
    string? IdentificationType,
    string? IdentificationNumber,
    bool IsComplete,
    IReadOnlyList<string> MatchedBy);

/// <summary>Vista previa, de sólo lectura, del cliente que «Tomar» crearía con el bloque de facturación.</summary>
public sealed record StoreNewCustomerPreviewDto(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string Phone,
    string Email,
    string Address,
    string Country,
    string CityName,
    string? DepartmentName);

public sealed record StoreCustomerCandidatesDto(
    IReadOnlyList<StoreCustomerCandidateDto> Candidates,
    Guid? SuggestedCustomerId,
    StoreNewCustomerPreviewDto NewCustomer);

public sealed record StoreTakeAddressesDto(string Billing, string? Shipping);

public sealed record StoreTakeResultDto(
    StoreOrderRequestRowDto Request,
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    bool CustomerCreated,
    bool CustomerCompleted,
    StoreTakeAddressesDto Addresses);
