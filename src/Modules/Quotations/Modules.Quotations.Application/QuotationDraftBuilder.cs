using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Lo que la creación y su cálculo previo le pasan al constructor. <paramref name="QuotationNumber"/> ya
/// viene resuelto: el número real al crear, <see cref="QuotationDraftBuilder.PreviewQuotationNumber"/> al
/// calcular.</summary>
public sealed record QuotationDraftInput(
    Guid TenantId,
    Guid ClientId,
    string QuotationNumber,
    MemberId AdvisorId,
    IQuotationEdits Edits,
    DateOnly Today,
    DateTimeOffset Now);

/// <summary>La cotización armada en memoria, el nombre del cliente para el historial de la creación y los
/// cambios de línea para el historial de cada una.</summary>
public sealed record QuotationDraft(Quotation Quotation, string CustomerName)
{
    internal IReadOnlyList<QuotationItemEdit> ItemEdits { get; init; } = [];
}

/// <summary>
/// Arma una cotización nueva con el documento entero (spec 2026-10-18 §7.3), gemelo de
/// <see cref="OrderDraftBuilder"/>. Existe porque crear (<c>POST /quotations</c>) y calcular sin id
/// (<c>POST /quotations/preview</c>) comparten los pasos: dos copias se desincronizan y la pantalla muestra un
/// total que la creación no deja. Sobre el agregado nuevo aplica <see cref="QuotationEditsApplication"/>, los
/// mismos pasos del guardado y su cálculo previo.
///
/// <b>No guarda nada</b>: ni consecutivo, ni historial, ni auditoría. Eso lo hace quien lo llama.
/// Público porque el composition root lo registra.
/// </summary>
public sealed class QuotationDraftBuilder(
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationSettingsStore settingsStore,
    ITenantPriceRounding priceRounding,
    ShippingResolver shippingResolver,
    ITenantDefaultCurrency tenantDefaultCurrency)
{
    /// <summary>El número del cálculo previo. El handler lo borra antes de responder.</summary>
    public const string PreviewQuotationNumber = "PREVIEW";

    /// <summary>Sin vigencia en el cuerpo, quince días desde el hoy del tenant (spec 2026-09-17, punto 2c).</summary>
    public const int DefaultValidityDays = 15;

    public async Task<QuotationDraft> BuildAsync(QuotationDraftInput input, CancellationToken cancellationToken)
    {
        var edits = input.Edits;

        // US-1/US-18: cliente inexistente, incompleto, sin CUC o inactivo bloquea la creación, y también su
        // cálculo previo (D-L9), en el orden de QuotationCustomerEligibility.
        var customer = await customerLookup.FindAsync(input.TenantId, input.ClientId, cancellationToken);
        QuotationCustomerEligibility.Ensure(customer, input.TenantId, input.ClientId);

        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
            companyLookup, input.TenantId, edits.BillingAccount, cancellationToken);

        // Sin cuenta de cobro, la cotización nace en la moneda por defecto del tenant. Se lee siempre, haya
        // cuenta o no: el agregado decide cuál gana, no el handler.
        var defaultCurrency = Currencies.Normalize(
            await tenantDefaultCurrency.GetAsync(input.TenantId, cancellationToken));

        // Se resuelve acá y queda guardada: calcularla al leer haría que la misma cotización mostrara una fecha
        // distinta cada día. Y antes de ApplyAsync, porque UpdateDetails con null la borra.
        var validUntil = edits.ValidUntil ?? input.Today.AddDays(DefaultValidityDays);

        // Ruling L1: el contrato de antes, { clientId }, deja exactamente lo de antes —versión 1 incluida—, así
        // que no pasa por UpdateDetails, que sube la versión.
        if (IsOnlyTheClient(edits))
        {
            var empty = Quotation.Create(
                QuotationId.New(), input.TenantId, input.QuotationNumber, input.ClientId, input.AdvisorId,
                validUntil, null, null, QuotationParties.Empty, null, defaultCurrency,
                customer.WithRetention, customer.VatSurplus, input.AdvisorId, input.Now);
            return new QuotationDraft(empty, customer.Name);
        }

        // Las partes no van a Create sino a UpdateDetails: Create exige el documento de la facturación, y el
        // cálculo previo no puede exigirlo mientras la persona escribe. La creación lo exige después
        // (EnsureBillingIdentified), igual que el guardado.
        var quotation = Quotation.Create(
            QuotationId.New(), input.TenantId, input.QuotationNumber, input.ClientId, input.AdvisorId,
            validUntil, edits.PaymentMethod, edits.Notes, QuotationParties.Empty, billingAccount, defaultCurrency,
            customer.WithRetention, customer.VatSurplus, input.AdvisorId, input.Now);

        var outcome = await QuotationEditsApplication.ApplyAsync(
            quotation, new ResolvedEdits(edits, validUntil), input.TenantId, customerLookup, companyLookup,
            pricingLookup, settingsStore, priceRounding, shippingResolver, input.AdvisorId, input.Now, cancellationToken);

        // El resumen del encabezado se ignora a propósito: el encabezado es parte de la creación (§7.4).
        return new QuotationDraft(quotation, customer.Name) { ItemEdits = outcome.ItemEdits };
    }

    private static bool IsOnlyTheClient(IQuotationEdits edits) =>
        edits.ValidUntil is null
        && edits.PaymentMethod is null
        && edits.Notes is null
        && edits.Parties is null
        && edits.BillingAccount is null
        && edits.Shipping is null
        && edits.Items.Count == 0
        && edits.GlobalScaleFloor is null
        && !edits.IsRetail;

    // El cuerpo con la vigencia ya resuelta: lo demás pasa tal cual.
    private sealed record ResolvedEdits(IQuotationEdits Body, DateOnly ResolvedValidUntil) : IQuotationEdits
    {
        public DateOnly? ValidUntil => ResolvedValidUntil;

        public string? PaymentMethod => Body.PaymentMethod;

        public string? Notes => Body.Notes;

        public QuotationPartiesRequest? Parties => Body.Parties;

        public QuotationBillingAccountRequest? BillingAccount => Body.BillingAccount;

        public IReadOnlyList<QuotationItemAddition> Items => Body.Items;

        public int? GlobalScaleFloor => Body.GlobalScaleFloor;

        public bool IsRetail => Body.IsRetail;

        public ShippingRequest? Shipping => Body.Shipping;
    }
}
