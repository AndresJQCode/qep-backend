namespace Modules.Shopify.Domain;

/// <summary>
/// Spec 2026-10-15 §6.1: la configuración de una tienda, una fila por conexión. Nace vacía al primer contacto del módulo con la
/// conexión (el primer webhook o la primera lectura de la pantalla; no hay evento de conexión creada, H14) y la llena el
/// <c>PUT</c> de §7. Mientras no tenga asesor y cuenta de cobro, todo pedido espera con <c>store_not_configured</c>.
/// </summary>
public sealed class ShopifyStore
{
    public const string DefaultDocumentAttributeKey = "Documento";
    public const string DefaultDocumentTypeAttributeKey = "Tipo de documento";
    public const string DefaultIdentificationTypeCode = "CC";

    public const int AttributeKeyMaxLength = 100;
    public const int IdentificationTypeMaxLength = 16;
    public const int ShopDomainMaxLength = 255;

    private ShopifyStore()
    {
    }

    /// <summary>PK; la conexión de Integrations es una referencia blanda (otro módulo, sin FK).</summary>
    public Guid ConnectionId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Ruling S16: el dominio <c>*.myshopify.com</c> en minúsculas, anotado en el primer contacto del webhook. Con él
    /// <c>shop/redact</c> encuentra la tienda aunque la conexión de Integrations y su ruta ya no existan (§5.5). Nulo en una
    /// tienda que todavía no recibió nada.</summary>
    public string? ShopDomain { get; private set; }

    /// <summary>Membresía del asesor a cuyo nombre entran los pedidos. Se valida activa al guardar.</summary>
    public Guid? AdvisorMembershipId { get; private set; }

    /// <summary>La cuenta de cobro, copiada como la de una cotización. Nula hasta configurar la tienda.</summary>
    public ShopifyBillingAccount? Billing { get; private set; }

    /// <summary>Con ella nace un cliente completo; sin ella, el comprador siempre nace incompleto (§6.2.1).</summary>
    public Guid? DefaultClassificationId { get; private set; }

    /// <summary>Clave en <c>customAttributes</c> del pedido donde viene el documento del comprador.</summary>
    public string DocumentAttributeKey { get; private set; } = DefaultDocumentAttributeKey;

    public string DocumentTypeAttributeKey { get; private set; } = DefaultDocumentTypeAttributeKey;

    /// <summary>Se usa cuando el pedido trae documento pero no su tipo.</summary>
    public string DefaultIdentificationType { get; private set; } = DefaultIdentificationTypeCode;

    /// <summary>§5.3: desde dónde trae la próxima conciliación (menos 5 minutos de margen).</summary>
    public DateTimeOffset? LastReconciledAt { get; private set; }

    /// <summary>§5.3: una corrida no se solapa con otra de la misma tienda.</summary>
    public DateTimeOffset? ReconcileLeaseUntil { get; private set; }

    /// <summary>Concurrencia optimista con <c>If-Match</c> en el <c>PUT</c>.</summary>
    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Membresía de quien guardó por última vez; nula mientras la fila sigue vacía.</summary>
    public Guid? UpdatedBy { get; private set; }

    /// <summary>Anota el dominio de la tienda. No sube <see cref="Version"/>: la versión es el <c>If-Match</c> del <c>PUT</c> de la
    /// configuración, y un dato que el usuario no edita no puede invalidar lo que está editando.</summary>
    public void RecordShopDomain(string shopDomain)
    {
        var normalized = shopDomain?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > ShopDomainMaxLength)
        {
            throw new ArgumentException($"The shop domain is required and cannot exceed {ShopDomainMaxLength} characters.", nameof(shopDomain));
        }

        ShopDomain = normalized;
    }

    public bool IsConfigured => AdvisorMembershipId is not null && Billing is not null;

    /// <summary>La fila vacía. <paramref name="linkedAt"/> es el momento del enlace: la conciliación arranca ahí y no trae
    /// historial (D-S4).</summary>
    public static ShopifyStore Create(Guid connectionId, Guid tenantId, DateTimeOffset linkedAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(connectionId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        return new ShopifyStore
        {
            ConnectionId = connectionId,
            TenantId = tenantId,
            LastReconciledAt = linkedAt,
            Version = 1,
            UpdatedAt = linkedAt,
        };
    }
}

/// <summary>La empresa y la cuenta a la que se cobran los pedidos de la tienda; copia, como <c>QuotationBillingAccount</c>, con sus
/// mismos anchos (los de <c>companies.company_bank_accounts</c>).</summary>
public sealed record ShopifyBillingAccount(Guid CompanyId, string BankName, string AccountNumber, string Currency)
{
    public const int BankNameMaxLength = 120;
    public const int AccountNumberMaxLength = 32;
    public const int CurrencyLength = 3;
}
