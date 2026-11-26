namespace Modules.Siigo.Application;

/// <summary>
/// Spec 2026-11-18 §5.2 y §5.3: la configuración entera de una conexión, más el <c>readiness</c> por paso y el estado de la conexión,
/// para que la pantalla marque los pasos y el asistente sepa dónde seguir. Backend for frontend: las listas de tamaño fijo viajan
/// completas (todo IVA en uso, aunque no tenga vínculo; toda cuenta de cobro de la empresa) para que la pantalla no tenga que
/// calcular lo que falta. La versión es también el <c>ETag</c> y lo que los <c>PUT</c> mandan en <c>If-Match</c>. Nunca lleva la clave
/// de acceso ni el token.
/// </summary>
public sealed record SiigoSettingsDto(
    Guid ConnectionId,
    SiigoConnectionDto Connection,
    Guid? CompanyId,
    string? CompanyName,
    SiigoDocumentDto Document,
    SiigoTaxesDto Taxes,
    SiigoPaymentsDto Payments,
    IReadOnlyList<SiigoSellerDto> Sellers,
    DateTimeOffset? SellersAutoLinkedAt,
    DateTimeOffset? ProductsAutoLinkedAt,
    long Version,
    IReadOnlyList<SiigoStepReadinessDto> Readiness);

/// <summary>Una fila del paso «Vendedores» (spec 2026-11-20): un miembro y el usuario de Siigo vinculado, o null. <paramref name="Name"/> es
/// el nombre de la membresía (puede faltar); <paramref name="Active"/> false es un miembro que ya no está pero conserva su vínculo.</summary>
public sealed record SiigoSellerDto(
    Guid MembershipId, string? Name, string? Email, bool Active, string? SiigoUserId, string? SiigoUserName);

/// <summary>La respuesta de <c>POST …/sellers/auto-link</c>: cuántos se vincularon y la configuración que quedó.</summary>
public sealed record SiigoSellerAutoLinkResultDto(int Linked, SiigoSettingsDto Settings);

/// <summary>El nombre y el estado de la conexión en Integrations (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>).</summary>
public sealed record SiigoConnectionDto(string Name, string Status);

/// <summary>El paso «Documento». <c>SendToDian</c> es <c>false</c> de fábrica: la pantalla avisa mientras esté apagado.</summary>
public sealed record SiigoDocumentDto(
    string? DocumentTypeId,
    string? CostCenterId,
    string? WarehouseId,
    bool SendToDian,
    bool SendMail,
    bool FreightSeparateInvoice,
    string? FreightProductCode,
    string PriceMode);

/// <summary>El paso «Impuestos y retenciones»: el IVA por porcentaje y las retenciones de la empresa, cada uno con su impuesto de
/// Siigo (<c>null</c> es «sin vincular»).</summary>
public sealed record SiigoTaxesDto(IReadOnlyList<SiigoVatLinkDto> Vat, IReadOnlyList<SiigoWithholdingLinkDto> Withholdings);

public sealed record SiigoVatLinkDto(int TaxPercentage, string? SiigoTaxId);

public sealed record SiigoWithholdingLinkDto(Guid TaxRateId, string Name, string Type, decimal Percentage, string? SiigoTaxId);

/// <summary>
/// El paso «Formas de pago». <c>InCompany</c> es <c>false</c> en un vínculo cuya cuenta ya no está en la empresa (se renombró después
/// de congelar pedidos con la foto vieja): la pantalla lo deja ver y editar, porque esos pedidos salen con el faltante
/// <c>payment_unlinked</c> mientras no se vincule el texto viejo.
/// </summary>
public sealed record SiigoPaymentsDto(
    IReadOnlyList<SiigoAccountLinkDto> Accounts,
    string? CreditPaymentTypeId,
    int? CreditDays,
    string? CashOnDeliveryPaymentTypeId,
    string? DefaultPaymentTypeId);

public sealed record SiigoAccountLinkDto(string BankAccountKey, string? BankName, string? AccountNumber, string? SiigoPaymentTypeId, bool InCompany);

/// <summary>Un paso del asistente: <c>Step</c> por nombre (<c>Credentials</c>, <c>Company</c>…), completo, o con los códigos de lo que
/// falta.</summary>
public sealed record SiigoStepReadinessDto(string Step, bool Complete, IReadOnlyList<string> MissingCodes);

/// <summary>Una opción de una lista de Siigo para un selector. <c>Type</c> y <c>Percentage</c> sólo viajan en la lista de impuestos; <c>Description</c>, cuando Siigo la trae (tipos de documento).</summary>
public sealed record SiigoOptionDto(string Id, string Name, string? Type, decimal? Percentage, string? Description = null);
