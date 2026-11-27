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
    ITenantDefaultCurrency tenantDefaultCurrency,
    IQuotationWithholdingLookup withholdingLookup,
    IQuotationGeographyLookup geographyLookup)
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

        // Sin cuenta de cobro, la cotización nace en la moneda por defecto del tenant. Se lee siempre, haya
        // cuenta o no: el agregado decide cuál gana, no el handler. Antes de resolver la cuenta porque la empresa sin
        // cuenta (spec 2026-11-17, R3) se arma en esa moneda.
        var defaultCurrency = Currencies.Normalize(
            await tenantDefaultCurrency.GetAsync(input.TenantId, cancellationToken));

        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
            companyLookup, input.TenantId, edits.BillingAccount, edits.BillingCompanyId, defaultCurrency, cancellationToken);

        // Se resuelve acá y queda guardada: calcularla al leer haría que la misma cotización mostrara una fecha
        // distinta cada día. Y antes de ApplyAsync, porque UpdateDetails con null la borra.
        var validUntil = edits.ValidUntil ?? input.Today.AddDays(DefaultValidityDays);

        // Las tasas del cliente, de todos sus países (D16, D17), con la UVT del hoy del tenant: es la fecha del
        // documento que nace. Quedan congeladas; sólo ChangeClient las vuelve a copiar.
        var customerWithholdings = await withholdingLookup.ForCustomerAsync(
            input.TenantId, input.ClientId, country: null, input.Today, cancellationToken);

        // Spec ReteICA por empresa §7.6, «En la creación» (owner 2026-11-14, plan P13): la empresa y el municipio salen del
        // mismo cuerpo, y el contexto se resuelve una sola vez y se pasa igual a Create y a ApplyAsync. Así el UpdateDetails
        // de adentro ve la misma empresa y el mismo municipio, no dispara la re-resolución, y un id municipal de otro
        // municipio es 422 en vez de ignorarse como en A11.
        var municipal = await MunicipalContexts.ResolveAsync(
            withholdingLookup,
            geographyLookup,
            input.TenantId,
            input.ClientId,
            billingAccount,
            Quotation.OperationCityAtCreation(billingAccount, edits.OperationCity),
            explicitCity: edits.OperationCity.Kind == OperationCityChoiceKind.Explicit,
            input.Today,
            "quotation",
            cancellationToken);

        // Ruling L1: el contrato de antes, { clientId }, deja exactamente lo de antes —versión 1 incluida—, así
        // que no pasa por UpdateDetails, que sube la versión.
        if (IsOnlyTheClient(edits))
        {
            var empty = Quotation.Create(
                QuotationId.New(), input.TenantId, input.QuotationNumber, input.ClientId, input.AdvisorId,
                validUntil, null, null, QuotationParties.Empty, null, defaultCurrency,
                customer.VatSurplus, customerWithholdings, MunicipalContext.None, input.AdvisorId, input.Now);
            return new QuotationDraft(empty, customer.Name);
        }

        // Las partes no van a Create sino a UpdateDetails: Create exige el documento de la facturación, y el
        // cálculo previo no puede exigirlo mientras la persona escribe. La creación lo exige después
        // (EnsureBillingIdentified), igual que el guardado.
        var quotation = Quotation.Create(
            QuotationId.New(), input.TenantId, input.QuotationNumber, input.ClientId, input.AdvisorId,
            validUntil, edits.PaymentMethod, edits.Notes, QuotationParties.Empty, billingAccount, defaultCurrency,
            customer.VatSurplus, customerWithholdings, municipal, input.AdvisorId, input.Now);

        var outcome = await QuotationEditsApplication.ApplyAsync(
            quotation, new ResolvedEdits(edits, validUntil), input.TenantId, companyLookup,
            pricingLookup, settingsStore, priceRounding, shippingResolver,
            new QuotationWithholdingContext(withholdingLookup, geographyLookup, _ => Task.FromResult(input.Today)),
            input.AdvisorId, input.Now, cancellationToken, municipal);

        // El resumen del encabezado se ignora a propósito: el encabezado es parte de la creación (§7.4). Lo mismo los
        // porcentajes, las tarifas descartadas y la aplicación a mano (P13): lo elegido es parte de la creación, y la fila
        // guarda el porcentaje efectivo y el del catálogo.
        return new QuotationDraft(quotation, customer.Name) { ItemEdits = outcome.ItemEdits };
    }

    // Un cuerpo con municipio, porcentajes o «Sin retención» nunca se salta la validación de ApplyAsync.
    private static bool IsOnlyTheClient(IQuotationEdits edits) =>
        edits.ValidUntil is null
        && edits.PaymentMethod is null
        && edits.Notes is null
        && edits.Parties is null
        && edits.BillingAccount is null
        && edits.BillingCompanyId is null
        && edits.Shipping is null
        && edits.Items.Count == 0
        && edits.GlobalScaleFloor is null
        && !edits.IsRetail
        && edits.OperationCity.Kind == OperationCityChoiceKind.Keep
        && edits.WithholdingPercentages is null
        && edits.ClearedWithholdingTypes is null;

    // El cuerpo con la vigencia ya resuelta: lo demás pasa tal cual.
    private sealed record ResolvedEdits(IQuotationEdits Body, DateOnly ResolvedValidUntil) : IQuotationEdits
    {
        public DateOnly? ValidUntil => ResolvedValidUntil;

        public string? PaymentMethod => Body.PaymentMethod;

        public string? Notes => Body.Notes;

        public QuotationPartiesRequest? Parties => Body.Parties;

        public QuotationBillingAccountRequest? BillingAccount => Body.BillingAccount;

        public Guid? BillingCompanyId => Body.BillingCompanyId;

        public IReadOnlyList<QuotationItemAddition> Items => Body.Items;

        public int? GlobalScaleFloor => Body.GlobalScaleFloor;

        public bool IsRetail => Body.IsRetail;

        public ShippingRequest? Shipping => Body.Shipping;

        public OperationCityChoice OperationCity => Body.OperationCity;

        public IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages => Body.WithholdingPercentages;

        public IReadOnlyList<string>? ClearedWithholdingTypes => Body.ClearedWithholdingTypes;

        public ProjectScopeRequest? Project => Body.Project;
    }
}
