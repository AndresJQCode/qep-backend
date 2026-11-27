namespace Modules.Siigo.Domain;

/// <summary>El paso «Documento» del asistente (spec 2026-11-18 §5.3): comprobante, centro de costo, bodega, los interruptores y el
/// código del producto que factura el flete. Los ids son los de Siigo, como texto. El vendedor no se configura: es el asesor de cada
/// pedido (spec 2026-11-20).</summary>
public sealed record SiigoDocumentSettings(
    string? DocumentTypeId,
    string? CostCenterId,
    string? WarehouseId,
    bool SendToDian,
    bool SendMail,
    bool FreightSeparateInvoice,
    string? FreightProductCode,
    SiigoPriceMode? PriceMode = null);

/// <summary>El paso «Formas de pago» (spec 2026-11-18 §5.2): un vínculo por cuenta de cobro de la empresa, la forma de pago a crédito
/// con sus días, la de contraentrega y la que se usa cuando el pedido no trae banco.</summary>
public sealed record SiigoPaymentSettings(
    IReadOnlyList<SiigoPaymentLink> Accounts,
    string? CreditPaymentTypeId,
    int? CreditDays,
    string? CashOnDeliveryPaymentTypeId,
    string? DefaultPaymentTypeId);

/// <summary>Un vínculo variante → producto de Siigo que el auto-vínculo propone (spec 2026-11-18 §5.3, paso 6).</summary>
public sealed record SiigoVariantLinkInput(Guid VariantId, string Code, string SiigoProductId, string Name);

/// <summary>
/// Spec 2026-11-18 §5.2: la configuración de una conexión de Siigo, una fila por conexión (PK <see cref="ConnectionId"/>), con los
/// vínculos de impuestos, retenciones, cuentas de cobro y productos. Nace vacía al primer acceso. <b>Toda escritura sube
/// <see cref="Version"/></b>, que es el <c>If-Match</c> de los <c>PUT</c> y el token de concurrencia de la fila; sin esa regla, dos
/// pestañas se pisarían en silencio. Los ids de Siigo (comprobante, impuestos…) son texto: la lista de Siigo no se copia y
/// la validez de cada uno se revisa al leerla en vivo, no acá. Nunca guarda la clave de acceso: esa es de la conexión de Integrations.
/// </summary>
public sealed class SiigoSettings
{
    public const int IdMaxLength = 50;
    public const int FreightProductCodeMaxLength = 100;
    public const int CreditDaysMax = 365;

    private readonly List<SiigoVariantLink> _variantLinks = [];
    private readonly List<SiigoTaxLink> _taxLinks = [];
    private readonly List<SiigoWithholdingLink> _withholdingLinks = [];
    private readonly List<SiigoPaymentLink> _paymentLinks = [];
    private readonly List<SiigoSellerLink> _sellerLinks = [];

    private SiigoSettings()
    {
    }

    /// <summary>PK; la conexión de Integrations es una referencia blanda (otro módulo, sin FK).</summary>
    public Guid ConnectionId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Empresa de QEP que factura con esta cuenta. Única por tenant (<c>IX_settings_tenant_company</c>). Referencia blanda.</summary>
    public Guid? CompanyId { get; private set; }

    public string? DocumentTypeId { get; private set; }

    public string? CostCenterId { get; private set; }

    public string? WarehouseId { get; private set; }

    /// <summary><c>stamp.send</c>. <b>Falso de fábrica</b> (owner, 2026-11-18): sin cuenta de pruebas, una factura emitida no se borra.</summary>
    public bool SendToDian { get; private set; }

    /// <summary><c>mail.send</c>: Siigo le manda la factura al cliente.</summary>
    public bool SendMail { get; private set; }

    /// <summary>El flete sale en una factura aparte (un ítem, cantidad 1) y no como un ítem más de la del pedido.</summary>
    public bool FreightSeparateInvoice { get; private set; }

    public string? FreightProductCode { get; private set; }

    /// <summary>Cómo viaja el precio de cada ítem (spec 2026-11-23). <see cref="SiigoPriceMode.BaseWithDiscount"/> de fábrica.</summary>
    public SiigoPriceMode PriceMode { get; private set; } = SiigoPriceMode.BaseWithDiscount;

    public string? CreditPaymentTypeId { get; private set; }

    public int? CreditDays { get; private set; }

    /// <summary>La forma de pago de contraentrega (spec §6.3): vive acá porque el pedido sólo trae «contraentrega» como texto.</summary>
    public string? CashOnDeliveryPaymentTypeId { get; private set; }

    /// <summary>La que usa un pedido sin banco en la foto de su cuenta de cobro.</summary>
    public string? DefaultPaymentTypeId { get; private set; }

    /// <summary>Concurrencia optimista y <c>If-Match</c>. Una configuración nueva vale 0; cada escritura sube uno.</summary>
    public long Version { get; private set; }

    public IReadOnlyList<SiigoVariantLink> VariantLinks => _variantLinks;

    public IReadOnlyList<SiigoTaxLink> TaxLinks => _taxLinks;

    public IReadOnlyList<SiigoWithholdingLink> WithholdingLinks => _withholdingLinks;

    public IReadOnlyList<SiigoPaymentLink> PaymentLinks => _paymentLinks;

    public IReadOnlyList<SiigoSellerLink> SellerLinks => _sellerLinks;

    /// <summary>Cuándo el asistente intentó vincular por correo por primera vez (spec 2026-11-20 §9). Null: nunca; la pantalla lo
    /// intenta sola al abrir el paso.</summary>
    public DateTimeOffset? SellersAutoLinkedAt { get; private set; }

    /// <summary>Igual que <see cref="SellersAutoLinkedAt"/>, para la vinculación de productos por código.</summary>
    public DateTimeOffset? ProductsAutoLinkedAt { get; private set; }

    public static SiigoSettings Create(Guid connectionId, Guid tenantId) =>
        new()
        {
            ConnectionId = connectionId,
            TenantId = tenantId,
            SendToDian = false,
            SendMail = false,
            FreightSeparateInvoice = false,
            PriceMode = SiigoPriceMode.BaseWithDiscount,
            Version = 0,
        };

    public void AssignCompany(Guid companyId)
    {
        CompanyId = companyId;
        Touch();
    }

    public void UpdateDocument(SiigoDocumentSettings document)
    {
        ArgumentNullException.ThrowIfNull(document);
        DocumentTypeId = Id(document.DocumentTypeId);
        CostCenterId = Id(document.CostCenterId);
        WarehouseId = Id(document.WarehouseId);
        SendToDian = document.SendToDian;
        SendMail = document.SendMail;
        FreightSeparateInvoice = document.FreightSeparateInvoice;
        FreightProductCode = Text(document.FreightProductCode, FreightProductCodeMaxLength);
        PriceMode = document.PriceMode ?? PriceMode;
        Touch();
    }

    /// <summary>Reemplaza la lista entera: lo que no viene se quita, lo que ya estaba cambia de impuesto sin borrarse y lo nuevo se agrega.
    /// Un porcentaje repetido es <c>siigo.settings.duplicate_link</c>.</summary>
    public void ReplaceTaxLinks(IReadOnlyList<SiigoTaxLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        Sync(
            _taxLinks,
            links,
            link => link.TaxPercentage,
            (existing, incoming) => existing.Relink(incoming.SiigoTaxId),
            (link, connectionId, tenantId) => (link.ConnectionId, link.TenantId) = (connectionId, tenantId));
        Touch();
    }

    /// <summary>Igual que <see cref="ReplaceTaxLinks"/>, por <c>MembershipId</c> (spec 2026-11-20): el vendedor de cada asesor.</summary>
    public void ReplaceSellerLinks(IReadOnlyList<SiigoSellerLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        Sync(
            _sellerLinks,
            links,
            link => link.MembershipId,
            (existing, incoming) => existing.Relink(incoming.SiigoUserId, incoming.SiigoUserName),
            (link, connectionId, tenantId) => (link.ConnectionId, link.TenantId) = (connectionId, tenantId));
        Touch();
    }

    /// <summary>El usuario de Siigo vinculado al asesor, o <c>null</c> si no tiene vínculo.</summary>
    public string? SellerFor(Guid membershipId) =>
        _sellerLinks.Find(link => link.MembershipId == membershipId)?.SiigoUserId;

    /// <summary>Deja constancia de que ya se intentó vincular vendedores por correo; vinculen o no, la pantalla no lo repite sola. No sube
    /// <see cref="Version"/>: es un registro, no configuración, y subirla dejaría vieja la versión que la pantalla acaba de leer (un 412 en
    /// el siguiente guardado sin que nadie haya cambiado nada). Si el auto-link vinculó algo, la versión ya subió con los vínculos.</summary>
    public void MarkSellersAutoLinked(DateTimeOffset at) => SellersAutoLinkedAt = at;

    /// <summary>Igual que <see cref="MarkSellersAutoLinked"/>, para productos.</summary>
    public void MarkProductsAutoLinked(DateTimeOffset at) => ProductsAutoLinkedAt = at;

    /// <summary>Igual que <see cref="ReplaceTaxLinks"/>, por <c>TaxRateId</c>.</summary>
    public void ReplaceWithholdingLinks(IReadOnlyList<SiigoWithholdingLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        Sync(
            _withholdingLinks,
            links,
            link => link.TaxRateId,
            (existing, incoming) => existing.Relink(incoming.SiigoTaxId),
            (link, connectionId, tenantId) => (link.ConnectionId, link.TenantId) = (connectionId, tenantId));
        Touch();
    }

    /// <summary>Guarda el paso «Formas de pago»: las cuentas se reemplazan como las demás listas. Los días de crédito sólo van con su
    /// forma de pago, de 1 a <see cref="CreditDaysMax"/>.</summary>
    public void UpdatePayments(SiigoPaymentSettings payments)
    {
        ArgumentNullException.ThrowIfNull(payments);
        var creditType = Id(payments.CreditPaymentTypeId);
        if (payments.CreditDays is { } days && (creditType is null || days is < 1 or > CreditDaysMax))
        {
            throw new SiigoDomainException(
                SiigoErrorCodes.SettingsCreditInvalid,
                $"The credit days need their credit payment type and must be between 1 and {CreditDaysMax}.");
        }

        Sync(
            _paymentLinks,
            payments.Accounts,
            link => link.BankAccountKey,
            (existing, incoming) => existing.Relink(incoming.SiigoPaymentTypeId),
            (link, connectionId, tenantId) => (link.ConnectionId, link.TenantId) = (connectionId, tenantId));
        CreditPaymentTypeId = creditType;
        CreditDays = payments.CreditDays;
        CashOnDeliveryPaymentTypeId = Id(payments.CashOnDeliveryPaymentTypeId);
        DefaultPaymentTypeId = Id(payments.DefaultPaymentTypeId);
        Touch();
    }

    /// <summary>Vincula la variante a un producto de Siigo. Si ya tenía vínculo lo reemplaza (un vínculo manual pisa uno automático).</summary>
    public void LinkVariant(
        Guid variantId,
        string code,
        string siigoProductId,
        string name,
        SiigoLinkOrigin origin,
        Guid memberId,
        DateTimeOffset at)
    {
        var existing = _variantLinks.Find(link => link.VariantId == variantId);
        if (existing is null)
        {
            _variantLinks.Add(new SiigoVariantLink(ConnectionId, TenantId, variantId, code, siigoProductId, name, origin, memberId, at));
        }
        else
        {
            existing.Replace(code, siigoProductId, name, origin, memberId, at);
        }

        Touch();
    }

    /// <summary>Vincula varias variantes de una vez (el auto-vínculo por código): <b>sube la versión una sola vez</b>, no una por vínculo, y
    /// nada si la lista viene vacía. Una variante que ya tiene vínculo se reemplaza como en <see cref="LinkVariant"/>; el llamador es
    /// quien decide no pasar las que ya tienen uno.</summary>
    public void LinkVariants(
        IReadOnlyList<SiigoVariantLinkInput> links, SiigoLinkOrigin origin, Guid memberId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (links.Count == 0)
        {
            return;
        }

        foreach (var link in links)
        {
            var existing = _variantLinks.Find(candidate => candidate.VariantId == link.VariantId);
            if (existing is null)
            {
                _variantLinks.Add(new SiigoVariantLink(ConnectionId, TenantId, link.VariantId, link.Code, link.SiigoProductId, link.Name, origin, memberId, at));
            }
            else
            {
                existing.Replace(link.Code, link.SiigoProductId, link.Name, origin, memberId, at);
            }
        }

        Touch();
    }

    /// <summary>Quita el vínculo de la variante. Sin vínculo no cambia nada, tampoco la versión.</summary>
    public void UnlinkVariant(Guid variantId)
    {
        if (_variantLinks.RemoveAll(link => link.VariantId == variantId) > 0)
        {
            Touch();
        }
    }

    /// <summary>Los ids de formas de pago de Siigo que la configuración guarda (los vínculos por cuenta de cobro, crédito, contraentrega
    /// y la de por defecto), sin repetir y sin los vacíos, en ese orden.</summary>
    public IReadOnlyList<string> SavedPaymentTypeIds() =>
    [
        .. _paymentLinks.Select(link => link.SiigoPaymentTypeId)
            .Append(CreditPaymentTypeId)
            .Append(CashOnDeliveryPaymentTypeId)
            .Append(DefaultPaymentTypeId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Las credenciales de la conexión pasaron a otra cuenta de Siigo: todo lo que guarda ids de la cuenta anterior se borra (comprobante,
    /// centro de costo, bodega, producto del flete, vínculos de impuestos, retenciones, formas de pago, vendedores y productos, y los
    /// registros de auto-vínculo) y los pasos quedan incompletos. Se conserva lo que no es de la cuenta: la empresa, la DIAN, el correo,
    /// el flete aparte y el modo de precio. Sube la versión una sola vez: es configuración.
    /// </summary>
    public void ResetForAnotherAccount()
    {
        DocumentTypeId = null;
        CostCenterId = null;
        WarehouseId = null;
        FreightProductCode = null;
        CreditPaymentTypeId = null;
        CreditDays = null;
        CashOnDeliveryPaymentTypeId = null;
        DefaultPaymentTypeId = null;
        _taxLinks.Clear();
        _withholdingLinks.Clear();
        _paymentLinks.Clear();
        _sellerLinks.Clear();
        _variantLinks.Clear();
        SellersAutoLinkedAt = null;
        ProductsAutoLinkedAt = null;
        Touch();
    }

    private void Touch() => Version++;

    private void Sync<TLink, TKey>(
        List<TLink> current,
        IReadOnlyList<TLink> incoming,
        Func<TLink, TKey> key,
        Action<TLink, TLink> update,
        Action<TLink, Guid, Guid> adopt)
        where TKey : notnull
    {
        if (incoming.Select(key).Distinct().Count() != incoming.Count)
        {
            throw new SiigoDomainException(SiigoErrorCodes.SettingsDuplicateLink, "A list of links cannot repeat its key.");
        }

        var keys = incoming.Select(key).ToHashSet();
        current.RemoveAll(link => !keys.Contains(key(link)));
        foreach (var link in incoming)
        {
            var existing = current.Find(candidate => EqualityComparer<TKey>.Default.Equals(key(candidate), key(link)));
            if (existing is null)
            {
                adopt(link, ConnectionId, TenantId);
                current.Add(link);
            }
            else
            {
                update(existing, link);
            }
        }
    }

    private static string? Id(string? value) => Text(value, IdMaxLength);

    private static string? Text(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : throw new SiigoDomainException(SiigoErrorCodes.SettingsValueTooLong, $"A value cannot exceed {maxLength} characters.");
    }
}
