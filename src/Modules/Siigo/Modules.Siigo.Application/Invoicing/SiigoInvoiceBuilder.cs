using System.Globalization;
using System.Text.Json;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>Un pago de la factura armada, para que la vista previa lo muestre. <paramref name="ProofId"/> es el comprobante del que sale
/// (<c>null</c> en el saldo, que va a la forma de pago de la cuenta de cobro); <paramref name="DueDate"/> es el día de la factura.</summary>
public sealed record SiigoDraftPayment(Guid? ProofId, string SiigoPaymentTypeId, decimal Value, DateOnly DueDate);

/// <summary>Una factura armada: su tipo, el cuerpo ya serializado y el cliente que habría que crear si Siigo no lo tiene.
/// <paramref name="Total"/> es lo que suman sus pagos (el neto, con las retenciones ya descontadas).</summary>
public sealed record SiigoInvoiceDraft(
    SiigoInvoiceKind Kind,
    SiigoInvoiceRequest Request,
    SiigoCustomerRequest? NewCustomer,
    decimal Total = 0m,
    IReadOnlyList<SiigoDraftPayment>? Payments = null);

/// <summary>Cambia la forma de pago de un comprobante, en la factura de ese tipo (spec 2026-11-18 §6.3).</summary>
public sealed record SiigoPaymentOverride(SiigoInvoiceKind Kind, Guid ProofId, string SiigoPaymentTypeId);

/// <summary>El resultado de armar: lista para enviar, con faltantes, o con ítems que no cuadran con el total del pedido.</summary>
public abstract record SiigoBuildResult
{
    private SiigoBuildResult()
    {
    }

    public sealed record Ready(IReadOnlyList<SiigoInvoiceDraft> Drafts) : SiigoBuildResult;

    public sealed record NotReady(IReadOnlyList<SiigoMissing> Missing) : SiigoBuildResult;

    public sealed record TotalsMismatch(decimal QepTotal, decimal ComputedTotal) : SiigoBuildResult;
}

/// <summary>
/// Spec 2026-11-18 §6.3 y §6.4: arma la petición de factura a partir de la foto del pedido y la configuración. Código puro, sin acceso a
/// nada: dos corridas con la misma entrada dan los mismos bytes (el reintento reenvía los guardados, pero la prueba lo exige igual).
/// Junta <b>todos</b> los faltantes antes de rendirse. <c>price</c> sale sin IVA y <b>antes</b> del descuento; <c>taxed_price</c> es la
/// alternativa que decide la primera factura real (V-2).
/// </summary>
public static class SiigoInvoiceBuilder
{
    public const int ObservationsMaxLength = 4000;

    /// <summary>Spec §6.4: la diferencia entre los ítems y el pedido que no frena la factura (un peso).</summary>
    public const decimal Tolerance = 1m;

    private const string SupportedCurrency = "COP";

    public static SiigoBuildResult Build(
        SiigoInvoiceInput input, SiigoSettings settings, IReadOnlyList<SiigoPaymentOverride>? overrides)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        overrides ??= [];

        if (overrides.Any(value => input.PaymentProofs.All(proof => proof.ProofId != value.ProofId)))
        {
            throw new SiigoDomainException(
                SiigoErrorCodes.InvoicePaymentsMismatch, "A payment type was chosen for a proof that does not belong to the order.");
        }

        var hasFreight = input.FreightSubtotal > 0;
        var separateFreight = hasFreight && settings.FreightSeparateInvoice;
        var plan = PlanPayments(input, separateFreight);

        // El vendedor es el usuario de Siigo vinculado al asesor del pedido (spec 2026-11-20, vendedores vinculados). Sin vínculo la factura
        // no está lista: no hay vendedor por defecto.
        var sellerId = settings.SellerFor(input.AdvisorMembershipId);
        var missing = new List<SiigoMissing>();
        CollectMissing(input, settings, sellerId, hasFreight, plan, overrides, missing);
        if (missing.Count > 0)
        {
            return new SiigoBuildResult.NotReady(missing);
        }

        var customerRequest = SiigoCustomerMapper.Map(input.BilledParty);
        var identification = SiigoCustomerMapper.Identification(input.BilledParty)!;

        var lines = input.Lines.Select(line => Resolve(line, settings)).ToList();
        var freight = hasFreight ? ResolveFreight(input, settings) : null;

        // §6.4, paso 1: los ítems como Siigo los va a redondear contra lo que el pedido dice. Sin retenciones (no cambian el total con IVA).
        var orderIncludesFreight = hasFreight && !separateFreight;
        var qepOrderTotal = input.ProductsTotal + (orderIncludesFreight ? input.FreightSubtotal + input.FreightTaxAmount : 0m);
        var computedOrderTotal = lines.Sum(line => line.Subtotal + line.Tax) + (orderIncludesFreight ? freight!.Subtotal + freight.Tax : 0m);
        if (Math.Abs(qepOrderTotal - computedOrderTotal) > Tolerance)
        {
            return new SiigoBuildResult.TotalsMismatch(qepOrderTotal, computedOrderTotal);
        }

        if (separateFreight && Math.Abs(input.FreightSubtotal + input.FreightTaxAmount - (freight!.Subtotal + freight.Tax)) > Tolerance)
        {
            return new SiigoBuildResult.TotalsMismatch(input.FreightSubtotal + input.FreightTaxAmount, freight.Subtotal + freight.Tax);
        }

        // Los pagos tienen que sumar el total que Siigo calcula (IVA redondeado por línea desde la base ya redondeada), no el de QEP: la
        // diferencia (hasta un peso, ya validada arriba) se reparte en los pagos. Lo que este cálculo no prediga lo corrige el reenvío
        // (SiigoPaymentRounding).
        var freightDelta = separateFreight ? freight!.Subtotal + freight.Tax - (input.FreightSubtotal + input.FreightTaxAmount) : 0m;
        plan = PlanPayments(input, separateFreight, computedOrderTotal - qepOrderTotal - RetentionRoundingDelta(input, lines), freightDelta);

        if (plan.Exceeds)
        {
            throw new SiigoDomainException(
                SiigoErrorCodes.InvoicePaymentsExceedTotal, "The payment proofs add up to more than the net total of the order.");
        }

        var drafts = new List<SiigoInvoiceDraft>
        {
            Draft(input, settings, sellerId!, identification, SiigoInvoiceKind.Order, lines, orderIncludesFreight ? freight : null, plan, overrides, customerRequest),
        };
        if (separateFreight)
        {
            drafts.Add(Draft(input, settings, sellerId!, identification, SiigoInvoiceKind.Freight, [], freight, plan, overrides, customerRequest));
        }

        return new SiigoBuildResult.Ready(drafts);
    }

    /// <summary>
    /// Lo que la retención por ítem de Siigo se aparta de la de QEP. ReteFuente viaja en los <c>taxes</c> de cada ítem y Siigo la redondea
    /// ítem por ítem (<c>round(base × % / 100, 2)</c>); QEP la saca sobre la base entera, así que pueden diferir unos centavos y los pagos
    /// rechazan con <c>invalid_total_payments</c>. Como con el IVA, sólo se corrige el redondeo (hasta <see cref="Tolerance"/>): una
    /// diferencia mayor es otra base o un porcentaje distinto, y se deja a la vista en vez de taparla. Sin porcentaje (0) no se corrige.
    /// </summary>
    private static decimal RetentionRoundingDelta(SiigoInvoiceInput input, IReadOnlyList<ResolvedItem> lines)
    {
        var delta = 0m;
        foreach (var withholding in input.Withholdings.Where(value =>
                     SiigoWithholdingPlacement.For(value.Type) == SiigoWithholdingTarget.ItemTax && value.Percentage > 0m))
        {
            var perItem = lines.Sum(line => Math.Round(line.Subtotal * withholding.Percentage / 100m, 2, MidpointRounding.AwayFromZero));
            var difference = perItem - withholding.Amount;
            if (Math.Abs(difference) <= Tolerance)
            {
                delta += difference;
            }
        }

        return delta;
    }

    private static SiigoInvoiceDraft Draft(
        SiigoInvoiceInput input,
        SiigoSettings settings,
        string sellerId,
        string identification,
        SiigoInvoiceKind kind,
        IReadOnlyList<ResolvedItem> lines,
        ResolvedItem? freight,
        PaymentPlan plan,
        IReadOnlyList<SiigoPaymentOverride> overrides,
        SiigoCustomerRequest? customer)
    {
        var payments = Payments(input, settings, kind, plan, overrides);
        return new SiigoInvoiceDraft(
            kind,
            Serialize(Body(input, settings, sellerId, identification, kind, lines, freight, payments, overrides)),
            customer,
            payments.Sum(payment => payment.Value),
            payments);
    }

    // ---- Faltantes (todos juntos, en un orden estable) -------------------------------------------------------------------------

    private static void CollectMissing(
        SiigoInvoiceInput input,
        SiigoSettings settings,
        string? sellerId,
        bool hasFreight,
        PaymentPlan plan,
        IReadOnlyList<SiigoPaymentOverride> overrides,
        List<SiigoMissing> missing)
    {
        if (!string.Equals(input.Currency, SupportedCurrency, StringComparison.OrdinalIgnoreCase))
        {
            missing.Add(new SiigoMissing(SiigoMissing.CurrencyUnsupported, input.Currency, null));
        }

        foreach (var step in SiigoSettingsReadiness.Evaluate(settings).Where(step => !step.Complete && step.Step is SiigoSetupStep.Company or SiigoSetupStep.Document))
        {
            missing.Add(new SiigoMissing(SiigoMissing.SettingsIncomplete, step.Step.ToString().ToLowerInvariant(), null));
        }

        if (sellerId is null)
        {
            missing.Add(new SiigoMissing(SiigoMissing.SellerUnlinked, input.AdvisorMembershipId.ToString(), input.AdvisorLabel));
        }

        foreach (var line in input.Lines.DistinctBy(line => line.VariantId))
        {
            if (VariantLink(settings, line.VariantId) is null)
            {
                missing.Add(new SiigoMissing(SiigoMissing.VariantUnlinked, line.VariantId.ToString(), line.Name));
            }
        }

        var percentages = input.Lines.Select(line => line.TaxPercentage).ToList();
        if (hasFreight)
        {
            percentages.Add(input.FreightTaxPercentage);
        }

        foreach (var percentage in percentages.Distinct().Where(percentage => percentage > 0 && TaxId(settings, percentage) is null))
        {
            missing.Add(new SiigoMissing(SiigoMissing.TaxUnlinked, percentage.ToString(CultureInfo.InvariantCulture), null));
        }

        foreach (var withholding in input.Withholdings)
        {
            if (SiigoWithholdingPlacement.For(withholding.Type) == SiigoWithholdingTarget.Unsupported)
            {
                missing.Add(new SiigoMissing(SiigoMissing.WithholdingTypeUnsupported, withholding.TaxRateId.ToString(), withholding.Name));
            }
            else if (WithholdingId(settings, withholding.TaxRateId) is null)
            {
                missing.Add(new SiigoMissing(SiigoMissing.WithholdingUnlinked, withholding.TaxRateId.ToString(), withholding.Name));
            }
        }

        if (hasFreight && string.IsNullOrWhiteSpace(settings.FreightProductCode))
        {
            missing.Add(new SiigoMissing(SiigoMissing.FreightProductMissing, null, null));
        }

        CollectCustomerMissing(input.BilledParty, missing);
        CollectPaymentMissing(input, settings, plan, overrides, missing);
    }

    private static void CollectCustomerMissing(SiigoBilledPartyInput party, List<SiigoMissing> missing)
    {
        // El consumidor final lleva el documento genérico de la DIAN y, sin dirección, la ciudad de respaldo del mapeador.
        if (party.IsFinalConsumer)
        {
            return;
        }

        if (SiigoCustomerMapper.Identification(party) is null)
        {
            missing.Add(new SiigoMissing(SiigoMissing.CustomerIdentificationMissing, null, null));
        }

        if (party.IdentificationType is not { } kind || SiigoIdentificationTypes.CodeFor(kind) is null)
        {
            missing.Add(new SiigoMissing(
                SiigoMissing.CustomerIdentificationTypeUnsupported, party.IdentificationType?.ToString(), null));
        }

        // Sin país no se sabe qué carril de ciudad pedir (spec 2026-11-27): el faltante del país reemplaza al de la ciudad.
        if (party.CountryPending)
        {
            missing.Add(new SiigoMissing(SiigoMissing.CustomerCountryMissing, null, null));
        }
        else if (string.IsNullOrWhiteSpace(party.CityCode) || string.IsNullOrWhiteSpace(party.StateCode))
        {
            // Una ciudad escrita que no se vinculó no es «sin ciudad»: el operador la vincula una vez para todos (spec 2026-11-26).
            missing.Add(new SiigoMissing(
                party.UnlinkedCity is null ? SiigoMissing.CustomerCityMissing : SiigoMissing.CustomerCityUnlinked, null, null));
        }
    }

    private static void CollectPaymentMissing(
        SiigoInvoiceInput input,
        SiigoSettings settings,
        PaymentPlan plan,
        IReadOnlyList<SiigoPaymentOverride> overrides,
        List<SiigoMissing> missing)
    {
        // Spec 2026-11-20: comprobantes y saldo usan la forma de pago de la cuenta de cobro. Si hace falta alguna y no hay vínculo
        // (o el pedido no tiene cuenta), se reporta una sola vez, con la clave de la cuenta o sin referencia.
        var proposed = ProposedPaymentType(input, settings);
        var needsPayment = plan.Balances.Any(balance => balance.Amount > 0)
            || plan.Proofs.Any(part => !overrides.Any(value => value.Kind == part.Kind && value.ProofId == part.ProofId));
        if (needsPayment && proposed is null)
        {
            missing.Add(new SiigoMissing(SiigoMissing.PaymentUnlinked, input.BillingAccountKey, null));
        }
    }

    // ---- Pagos ------------------------------------------------------------------------------------------------------------------

    private sealed record ProofPart(SiigoInvoiceKind Kind, Guid ProofId, decimal Amount);

    private sealed record Balance(SiigoInvoiceKind Kind, decimal Amount);

    private sealed record PaymentPlan(IReadOnlyList<ProofPart> Proofs, IReadOnlyList<Balance> Balances, bool Exceeds);

    /// <summary>
    /// Reparte los comprobantes: primero llenan la factura del pedido y lo que sobra va a la de flete (si va aparte). Un comprobante puede
    /// partirse entre las dos. Lo que queda de cada neto es su saldo.
    /// <para>
    /// <paramref name="orderDelta"/> y <paramref name="freightDelta"/> son lo que el total de Siigo se aparta del de QEP (redondeo del IVA,
    /// hasta un peso). Un comprobante que pasa de la capacidad por no más de un peso se recorta a ella, y un saldo de no más de un peso es
    /// redondeo, no deuda: se suma al último comprobante de esa factura en vez de salir como un segundo pago a crédito.
    /// </para>
    /// </summary>
    private static PaymentPlan PlanPayments(SiigoInvoiceInput input, bool separateFreight, decimal orderDelta = 0m, decimal freightDelta = 0m)
    {
        var freightGross = input.FreightSubtotal > 0 ? input.FreightSubtotal + input.FreightTaxAmount : 0m;
        var capacities = new List<(SiigoInvoiceKind Kind, decimal Left)>
        {
            (SiigoInvoiceKind.Order, Money((separateFreight ? input.NetTotal - freightGross : input.NetTotal) + orderDelta)),
        };
        if (separateFreight)
        {
            capacities.Add((SiigoInvoiceKind.Freight, Money(freightGross + freightDelta)));
        }

        var parts = new List<ProofPart>();
        var excess = 0m;
        foreach (var proof in input.PaymentProofs)
        {
            var remaining = Money(proof.Amount);
            for (var index = 0; index < capacities.Count && remaining > 0; index++)
            {
                var take = Math.Min(remaining, capacities[index].Left);
                if (take <= 0)
                {
                    continue;
                }

                parts.Add(new ProofPart(capacities[index].Kind, proof.ProofId, take));
                capacities[index] = (capacities[index].Kind, capacities[index].Left - take);
                remaining -= take;
            }

            excess += remaining;
        }

        for (var index = 0; index < capacities.Count; index++)
        {
            var (kind, left) = capacities[index];
            var last = parts.FindLastIndex(part => part.Kind == kind);
            if (left > 0 && left <= Tolerance && last >= 0)
            {
                parts[last] = parts[last] with { Amount = parts[last].Amount + left };
                capacities[index] = (kind, 0m);
            }
        }

        return new PaymentPlan(parts, capacities.Select(value => new Balance(value.Kind, value.Left)).ToList(), excess > Tolerance);
    }

    private static string? ProposedPaymentType(SiigoInvoiceInput input, SiigoSettings settings)
    {
        // Sin cuenta de cobro no hay a dónde mandar el pago: ya no se cae a una forma de pago por defecto.
        if (input.BillingAccountKey is null)
        {
            return null;
        }

        return Text(settings.PaymentLinks.FirstOrDefault(link => link.BankAccountKey == input.BillingAccountKey)?.SiigoPaymentTypeId);
    }

    private static List<SiigoDraftPayment> Payments(
        SiigoInvoiceInput input,
        SiigoSettings settings,
        SiigoInvoiceKind kind,
        PaymentPlan plan,
        IReadOnlyList<SiigoPaymentOverride> overrides)
    {
        var payments = new List<SiigoDraftPayment>();
        var proposed = ProposedPaymentType(input, settings);

        // Todo pago vence el día de la factura: Siigo rechaza con `parameter_required` el que no lo trae si su forma de pago maneja
        // vencimiento, y eso se configura en Siigo, así que un comprobante puede caer en una así.
        foreach (var part in plan.Proofs.Where(part => part.Kind == kind))
        {
            var chosen = overrides.FirstOrDefault(value => value.Kind == kind && value.ProofId == part.ProofId)?.SiigoPaymentTypeId;
            payments.Add(new SiigoDraftPayment(part.ProofId, (chosen ?? proposed)!, part.Amount, input.Today));
        }

        var balance = plan.Balances.Single(value => value.Kind == kind).Amount;
        if (balance > 0)
        {
            // El saldo va a la misma forma de pago que los comprobantes (spec 2026-11-20).
            payments.Add(new SiigoDraftPayment(null, proposed!, balance, input.Today));
        }

        return payments;
    }

    // ---- Ítems ------------------------------------------------------------------------------------------------------------------

    private sealed record ResolvedItem(
        string Code,
        string Description,
        decimal Quantity,
        decimal Price,
        decimal Discount,
        string? TaxId,
        decimal Subtotal,
        decimal Tax,
        bool IsGift = false,
        decimal? TaxBase = null);

    // Siigo pide el tipo de contribuyente de un obsequio; siempre es la empresa (spec 2026-11-21).
    private const string GiftTaxpayer = "Company";

    private static ResolvedItem Resolve(SiigoInvoiceLineInput line, SiigoSettings settings)
    {
        var quantity = Math.Round(line.Quantity, 2, MidpointRounding.AwayFromZero);
        if (line.IsGift)
        {
            // Un obsequio va con precio 0 y no suma al total: Siigo calcula su IVA sobre `tax_base`, que es el precio de lista sin IVA
            // (el de lista viene con IVA incluido). Siigo documenta `tax_base` con «máximo 11 enteros y 2 decimales» (`price` admite 6): con más
            // decimales rechaza con `invalid_amount` en `items[n].tax_base` (observado el 2026-11-24 con 30168,067227).
            var taxBase = Math.Round(line.ListUnitPrice * 100m / (100m + line.TaxPercentage), 2, MidpointRounding.AwayFromZero);
            return new ResolvedItem(
                VariantLink(settings, line.VariantId)!.SiigoProductCode,
                line.Name,
                quantity,
                0m,
                0m,
                TaxId(settings, line.TaxPercentage),
                0m,
                0m,
                IsGift: true,
                TaxBase: taxBase);
        }

        var factor = 1m - (line.DiscountPercentage / 100m);

        // BaseWithDiscount: el precio es el de antes del descuento (el Subtotal del pedido ya lo trae aplicado y Siigo lo vuelve a aplicar
        // con `discount`). DiscountedPrice: el Subtotal ya trae el descuento, así que el precio es Subtotal / cantidad y `discount` va en 0.
        // El subtotal y el IVA de abajo salen igual en los dos modos, y por eso el total no cambia.
        var (price, discount) = settings.PriceMode == SiigoPriceMode.DiscountedPrice
            ? (quantity > 0 ? Math.Round(line.Subtotal / quantity, 6, MidpointRounding.AwayFromZero) : 0m, 0m)
            : quantity > 0 && factor > 0
                ? (Math.Round(line.Subtotal / (quantity * factor), 6, MidpointRounding.AwayFromZero), line.DiscountPercentage)
                : (0m, 0m);
        var subtotal = Math.Round(quantity * price * (1m - (discount / 100m)), 2, MidpointRounding.AwayFromZero);
        var tax = Math.Round(subtotal * line.TaxPercentage / 100m, 2, MidpointRounding.AwayFromZero);
        return new ResolvedItem(
            VariantLink(settings, line.VariantId)!.SiigoProductCode,
            line.Name,
            quantity,
            price,
            discount,
            TaxId(settings, line.TaxPercentage),
            subtotal,
            tax);
    }

    private static ResolvedItem ResolveFreight(SiigoInvoiceInput input, SiigoSettings settings)
    {
        var price = Math.Round(input.FreightSubtotal, 6, MidpointRounding.AwayFromZero);
        var subtotal = Math.Round(price, 2, MidpointRounding.AwayFromZero);
        var tax = Math.Round(subtotal * input.FreightTaxPercentage / 100m, 2, MidpointRounding.AwayFromZero);
        return new ResolvedItem(
            settings.FreightProductCode!.Trim(),
            "Flete",
            1m,
            price,
            0m,
            TaxId(settings, input.FreightTaxPercentage),
            subtotal,
            tax);
    }

    // ---- Cuerpo -----------------------------------------------------------------------------------------------------------------

    private static Dictionary<string, object?> Body(
        SiigoInvoiceInput input,
        SiigoSettings settings,
        string sellerId,
        string identification,
        SiigoInvoiceKind kind,
        IReadOnlyList<ResolvedItem> lines,
        ResolvedItem? freight,
        IReadOnlyList<SiigoDraftPayment> payments,
        IReadOnlyList<SiigoPaymentOverride> overrides)
    {
        var applied = kind == SiigoInvoiceKind.Order ? input.Withholdings : [];
        var itemWithholdings = applied
            .Where(value => SiigoWithholdingPlacement.For(value.Type) == SiigoWithholdingTarget.ItemTax)
            .Select(value => WithholdingId(settings, value.TaxRateId)!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var invoiceRetentions = applied
            .Where(value => SiigoWithholdingPlacement.For(value.Type) == SiigoWithholdingTarget.Invoice)
            .Select(value => WithholdingId(settings, value.TaxRateId)!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var items = lines.Select(line => Item(line, settings, itemWithholdings)).ToList();
        if (freight is not null)
        {
            // Sin retenciones: la base de retenciones de QEP sale de las líneas y no incluye el flete (spec §6.3).
            items.Add(Item(freight, settings, []));
        }

        var observations = kind == SiigoInvoiceKind.Freight
            ? $"Flete del pedido {input.OrderNumber}"
            : Observations(input);
        var body = new Dictionary<string, object?>
        {
            ["document"] = new Dictionary<string, object?> { ["id"] = IdValue(settings.DocumentTypeId!) },
            ["date"] = input.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["customer"] = new Dictionary<string, object?> { ["identification"] = identification, ["branch_office"] = 0 },
        };
        if (Text(settings.CostCenterId) is { } costCenter)
        {
            body["cost_center"] = IdValue(costCenter);
        }

        body["seller"] = IdValue(sellerId);
        body["stamp"] = new Dictionary<string, object?> { ["send"] = settings.SendToDian };
        body["mail"] = new Dictionary<string, object?> { ["send"] = settings.SendMail };
        body["observations"] = observations;

        // Sin `additional_fields.purchase_order`: un comprobante sin la orden de compra configurada lo rechaza con `document_settings`
        // (prueba real, 2026-11-20), y el número de pedido ya va en las observaciones.
        body["items"] = items;
        body["payments"] = payments.Select(payment => new Dictionary<string, object?>
        {
            ["id"] = IdValue(payment.SiigoPaymentTypeId),
            ["value"] = payment.Value,
            ["due_date"] = payment.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }).ToList();
        if (invoiceRetentions.Count > 0)
        {
            body["retentions"] = invoiceRetentions.Select(id => new Dictionary<string, object?> { ["id"] = IdValue(id) }).ToList();
        }

        return body;
    }

    private static Dictionary<string, object?> Item(ResolvedItem item, SiigoSettings settings, IReadOnlyList<string> withholdings)
    {
        var result = new Dictionary<string, object?>
        {
            ["code"] = item.Code,
            ["description"] = item.Description,
            ["quantity"] = item.Quantity,
            ["price"] = item.Price,
            ["discount"] = item.Discount,
        };
        if (Text(settings.WarehouseId) is { } warehouse)
        {
            result["warehouse"] = IdValue(warehouse);
        }

        var taxes = new List<Dictionary<string, object?>>();
        if (item.TaxId is { } vat)
        {
            taxes.Add(new Dictionary<string, object?> { ["id"] = IdValue(vat) });
        }

        // Un regalo no tiene base de retenciones: no recibe ninguna.
        if (!item.IsGift)
        {
            taxes.AddRange(withholdings.Select(id => new Dictionary<string, object?> { ["id"] = IdValue(id) }));
        }

        if (taxes.Count > 0)
        {
            result["taxes"] = taxes;
        }

        if (item.IsGift)
        {
            result["tax_base"] = item.TaxBase;
            result["taxpayer"] = GiftTaxpayer;
        }

        return result;
    }

    // Con dirección de despacho el texto es el que pidió el owner (2026-11-20) y reemplaza a la nota del comprador; sin ella (recogida en
    // tienda, parte de envío vacía) queda la forma de antes.
    private static string Observations(SiigoInvoiceInput input)
    {
        var text = !string.IsNullOrWhiteSpace(input.ShippingAddress)
            ? $"Pedido {input.OrderNumber}, Importante!! Dirección de despacho: {input.ShippingAddress.Trim()}"
                + (string.IsNullOrWhiteSpace(input.ShippingCity) ? string.Empty : $", {input.ShippingCity.Trim()}")
            : string.IsNullOrWhiteSpace(input.BuyerNotes)
                ? $"Pedido {input.OrderNumber}"
                : $"Pedido {input.OrderNumber}. {input.BuyerNotes.Trim()}";
        return text.Length <= ObservationsMaxLength ? text : text[..ObservationsMaxLength];
    }

    private static SiigoInvoiceRequest Serialize(Dictionary<string, object?> body) => new(JsonSerializer.Serialize(body));

    // ---- Vínculos ---------------------------------------------------------------------------------------------------------------

    private static SiigoVariantLink? VariantLink(SiigoSettings settings, Guid variantId) =>
        settings.VariantLinks.FirstOrDefault(link => link.VariantId == variantId);

    private static string? TaxId(SiigoSettings settings, int percentage) =>
        Text(settings.TaxLinks.FirstOrDefault(link => link.TaxPercentage == percentage)?.SiigoTaxId);

    private static string? WithholdingId(SiigoSettings settings, Guid taxRateId) =>
        Text(settings.WithholdingLinks.FirstOrDefault(link => link.TaxRateId == taxRateId)?.SiigoTaxId);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    // Los ids de las listas de Siigo son numéricos; se mandan como número y, si alguno no lo fuera, como texto.
    private static object IdValue(string id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : id;
}
