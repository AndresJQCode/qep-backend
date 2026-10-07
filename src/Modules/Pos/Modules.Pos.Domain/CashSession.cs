namespace Modules.Pos.Domain;

/// <summary>
/// Caja o turno de un cajero (spec 2026-10-07, «CashSession»). Los acumulados se mueven en la
/// misma transacción que cada venta y la venta mueve la Version: si un cierre y una venta se
/// cruzan, el que commitea segundo choca por concurrencia (412) en vez de dejar una venta fuera
/// del arqueo.
/// </summary>
public sealed class CashSession
{
    // EF Core materializa por aquí; el código sólo crea cajas con Open.
    private CashSession()
    {
        CashierName = string.Empty;
        CompanyName = string.Empty;
        CompanyTaxId = string.Empty;
    }

    public CashSessionId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Quien abrió. Sólo él vende y cierra en esta caja.</summary>
    public MemberId CashierId { get; private set; }

    public string CashierName { get; private set; }

    public Guid CompanyId { get; private set; }

    public string CompanyName { get; private set; }

    public string CompanyTaxId { get; private set; }

    public string? CompanyAddress { get; private set; }

    public string? CompanyPhone { get; private set; }

    public CashSessionStatus Status { get; private set; }

    public decimal OpeningFloat { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public int SalesCount { get; private set; }

    public int VoidedCount { get; private set; }

    public decimal SalesTotal { get; private set; }

    /// <summary>Efectivo neto: lo aplicado de cada venta (recibido − cambio), no el billete.</summary>
    public decimal CashTotal { get; private set; }

    public decimal CardTotal { get; private set; }

    public decimal TransferTotal { get; private set; }

    public decimal? ExpectedCash { get; private set; }

    public decimal? CountedCash { get; private set; }

    public decimal? CashDifference { get; private set; }

    public string? ClosingNote { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>El esperado mientras la caja sigue abierta: base + efectivo neto.</summary>
    public decimal LiveExpectedCash => OpeningFloat + CashTotal;

    public static CashSession Open(
        CashSessionId id,
        Guid tenantId,
        MemberId cashier,
        string cashierName,
        PosCompanySnapshot company,
        decimal openingFloat,
        DateTimeOffset at)
    {
        if (openingFloat < 0 || openingFloat > PosLimits.MaxCashAmount || !PosLimits.HasValidScale(openingFloat))
        {
            throw new PosDomainException(
                "pos.session.opening_float_invalid",
                "The opening float must be between 0 and 100000000 with at most 2 decimals.");
        }

        return new CashSession
        {
            Id = id,
            TenantId = tenantId,
            CashierId = cashier,
            CashierName = cashierName,
            CompanyId = company.CompanyId,
            CompanyName = company.Name,
            CompanyTaxId = company.TaxId,
            CompanyAddress = company.Address,
            CompanyPhone = company.Phone,
            Status = CashSessionStatus.Open,
            OpeningFloat = openingFloat,
            OpenedAt = at,
            // Nace en 1, como Company: el If-Match del cierre manda esta versión.
            Version = 1,
            CreatedAt = at,
            UpdatedAt = at,
        };
    }

    /// <summary>
    /// Congela el arqueo. La versión esperada la compara el handler, no el dominio (mismo reparto
    /// que UpdateOrdersExportLayout).
    /// </summary>
    public void Close(decimal countedCash, string? note, DateTimeOffset at)
    {
        EnsureOpen("pos.session.not_open", "The cash session is not open.");

        if (countedCash < 0 || countedCash > PosLimits.MaxCountedCash || !PosLimits.HasValidScale(countedCash))
        {
            throw new PosDomainException(
                "pos.session.counted_cash_invalid",
                "The counted cash must be between 0 and 1000000000 with at most 2 decimals.");
        }

        var expected = LiveExpectedCash;
        ExpectedCash = expected;
        CountedCash = countedCash;
        CashDifference = countedCash - expected;
        ClosingNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Status = CashSessionStatus.Closed;
        ClosedAt = at;
        Touch(at);
    }

    /// <summary>Suma la venta a los acumulados. Exige la caja abierta.</summary>
    public void RegisterSale(PosSale sale, DateTimeOffset at)
    {
        EnsureOpen("pos.session.not_open", "The cash session is not open.");
        EnsureOwns(sale);

        // Una venta anulada ya no entra al arqueo. No se detecta el doble registro de una venta
        // completada: la venta no guarda si ya fue contada, y el handler la registra una sola vez.
        if (sale.Status != PosSaleStatus.Completed)
        {
            throw new InvalidOperationException($"Sale '{sale.Id}' is not completed and cannot be registered.");
        }

        Accumulate(sale, +1);
        SalesCount++;
        Touch(at);
    }

    /// <summary>
    /// Resta exactamente lo que sumó RegisterSale. Una caja cerrada es un arqueo que alguien ya
    /// firmó: tocarla lo dejaría mintiendo.
    /// </summary>
    public void RegisterVoid(PosSale sale, DateTimeOffset at)
    {
        EnsureOpen("pos.sale.void_session_closed", "The sale belongs to a closed cash session.");
        EnsureOwns(sale);

        // Restar una venta que sigue completada dejaría el arqueo bajo lo que de verdad entró.
        if (sale.Status != PosSaleStatus.Voided)
        {
            throw new InvalidOperationException($"Sale '{sale.Id}' is not voided and cannot be unregistered.");
        }

        Accumulate(sale, -1);
        SalesCount--;
        VoidedCount++;
        Touch(at);
    }

    private void EnsureOwns(PosSale sale)
    {
        if (sale.CashSessionId != Id)
        {
            throw new InvalidOperationException($"Sale '{sale.Id}' does not belong to cash session '{Id}'.");
        }
    }

    // Lo aplicado por medio: en Cash es lo aplicado al total (recibido menos cambio), no el billete.
    private void Accumulate(PosSale sale, int sign)
    {
        SalesTotal += sign * sale.Total;
        foreach (var payment in sale.Payments)
        {
            switch (payment.Method)
            {
                case PosPaymentMethod.Cash:
                    CashTotal += sign * payment.Amount;
                    break;
                case PosPaymentMethod.Card:
                    CardTotal += sign * payment.Amount;
                    break;
                case PosPaymentMethod.Transfer:
                    TransferTotal += sign * payment.Amount;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown payment method '{payment.Method}'.");
            }
        }
    }

    private void EnsureOpen(string code, string message)
    {
        if (Status != CashSessionStatus.Open)
        {
            throw new PosDomainException(code, message);
        }
    }

    private void Touch(DateTimeOffset at)
    {
        Version++;
        UpdatedAt = at;
    }
}
