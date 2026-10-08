using BuildingBlocks.Domain.Pricing;

namespace Modules.Pos.Domain;

/// <summary>
/// Venta de mostrador (spec 2026-10-07, «PosSale»). Atómica: sólo existe completada o anulada. Sin
/// Version propia: la anulación la serializa la Version de la caja.
/// </summary>
public sealed class PosSale
{
    private readonly List<PosSaleLine> _lines = [];
    private readonly List<PosPayment> _payments = [];

    private PosSale()
    {
        RequestFingerprint = string.Empty;
        SaleNumber = string.Empty;
        CustomerName = string.Empty;
        CustomerIdentificationNumber = string.Empty;
    }

    public PosSaleId Id { get; private set; }

    /// <summary>SHA-256 del cuerpo canónico: sólo sirve para reconocer una repetición.</summary>
    public string RequestFingerprint { get; private set; }

    public Guid TenantId { get; private set; }

    public CashSessionId CashSessionId { get; private set; }

    public MemberId CashierId { get; private set; }

    /// <summary>Vacío hasta AssignNumber, que corre adentro de la transacción, después de validar todo.</summary>
    public string SaleNumber { get; private set; }

    public Guid? CustomerId { get; private set; }

    public string CustomerName { get; private set; }

    public string? CustomerIdentificationType { get; private set; }

    public string CustomerIdentificationNumber { get; private set; }

    public IReadOnlyList<PosSaleLine> Lines => _lines;

    public IReadOnlyList<PosPayment> Payments => _payments;

    public decimal Subtotal { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal Total { get; private set; }

    public decimal ChangeAmount { get; private set; }

    public PosSaleStatus Status { get; private set; }

    public string? VoidReason { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public MemberId? VoidedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Valida y calcula líneas → encabezado → pagos, y sólo entonces construye: nada se asigna
    /// hasta que todo valida (lección de Company.Update).
    /// </summary>
    public static PosSale Create(
        PosSaleId id,
        string fingerprint,
        CashSession session,
        IReadOnlyList<PosSaleLineInput> lines,
        IReadOnlyList<PosPaymentInput> payments,
        DateTimeOffset at)
    {
        if (lines.Count == 0)
        {
            throw new PosDomainException("pos.sale.lines_required", "A sale needs at least one line.");
        }

        if (lines.Count > PosLimits.MaxLines)
        {
            throw new PosDomainException("pos.sale.too_many_lines", "A sale cannot have more than 200 lines.");
        }

        var builtLines = lines.Select((line, index) => PosSaleLine.Create(id, index + 1, line)).ToList();

        // Idéntico a Quotation.RecalculateTotals sin excedente ni retención.
        var subtotal = VatIncludedLine.Round(builtLines.Sum(line => line.Subtotal));
        var discount = VatIncludedLine.Round(builtLines.Sum(line => line.DiscountAmount));
        var tax = VatIncludedLine.Round(builtLines.Sum(line => line.TaxAmount));
        var total = subtotal + tax;

        var (builtPayments, change) = BuildPayments(id, total, payments);

        var sale = new PosSale
        {
            Id = id,
            RequestFingerprint = fingerprint,
            TenantId = session.TenantId,
            CashSessionId = session.Id,
            CashierId = session.CashierId,
            CustomerId = null,
            CustomerName = PosFinalConsumer.Name,
            CustomerIdentificationType = null,
            CustomerIdentificationNumber = PosFinalConsumer.IdentificationNumber,
            Subtotal = subtotal,
            DiscountAmount = discount,
            TaxAmount = tax,
            Total = total,
            ChangeAmount = change,
            Status = PosSaleStatus.Completed,
            CreatedAt = at,
        };
        sale._lines.AddRange(builtLines);
        sale._payments.AddRange(builtPayments);
        return sale;
    }

    public void AssignNumber(long value)
    {
        if (SaleNumber.Length > 0)
        {
            throw new InvalidOperationException($"Sale '{Id}' already has number '{SaleNumber}'.");
        }

        SaleNumber = PosSaleNumber.Format(value);
    }

    public IReadOnlyList<PosTaxBreakdownEntry> TaxBreakdown() => _lines
        .GroupBy(line => line.TaxPercentage)
        .OrderBy(group => group.Key)
        .Select(group => new PosTaxBreakdownEntry(
            group.Key,
            VatIncludedLine.Round(group.Sum(line => line.Subtotal)),
            VatIncludedLine.Round(group.Sum(line => line.TaxAmount))))
        .ToArray();

    public void Void(string reason, MemberId by, DateTimeOffset at)
    {
        if (Status == PosSaleStatus.Voided)
        {
            throw new PosDomainException("pos.sale.already_voided", "The sale is already voided.");
        }

        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new PosDomainException("pos.sale.void_reason_required", "A void reason is required.");
        }

        Status = PosSaleStatus.Voided;
        VoidReason = trimmed;
        VoidedBy = by;
        VoidedAt = at;
    }

    /// <summary>Las ocho reglas de pago del spec, en su orden.</summary>
    private static (List<PosPayment> Payments, decimal Change) BuildPayments(
        PosSaleId saleId, decimal total, IReadOnlyList<PosPaymentInput> inputs)
    {
        if (inputs.Count == 0)
        {
            throw new PosDomainException("pos.sale.payment_required", "A sale needs at least one payment.");
        }

        if (inputs.Count > PosLimits.MaxPayments)
        {
            throw new PosDomainException("pos.sale.too_many_payments", "A sale cannot have more than 5 payments.");
        }

        if (inputs.Count(input => input.Method == PosPaymentMethod.Cash) > 1)
        {
            throw new PosDomainException("pos.sale.duplicate_cash_payment", "A sale takes at most one cash payment.");
        }

        foreach (var input in inputs)
        {
            if (input.Method == PosPaymentMethod.Cash)
            {
                if (input.Amount is not null)
                {
                    throw new PosDomainException(
                        "pos.sale.cash_amount_not_allowed",
                        "A cash payment carries only the tendered amount; the server computes what it applies.");
                }

                if (input.Tendered is not { } tendered || tendered < 0 || tendered > PosLimits.MaxCashAmount
                    || !PosLimits.HasValidScale(tendered))
                {
                    throw new PosDomainException(
                        "pos.sale.tendered_invalid",
                        "The tendered cash must be between 0 and 100000000 with at most 2 decimals.");
                }
            }
            else
            {
                if (input.Amount is not { } amount || amount <= 0 || !PosLimits.HasValidScale(amount))
                {
                    throw new PosDomainException(
                        "pos.sale.payment_amount_invalid",
                        "A card or transfer payment needs an amount greater than 0 with at most 2 decimals.");
                }

                if (input.Tendered is not null)
                {
                    throw new PosDomainException(
                        "pos.sale.tendered_only_for_cash", "Only a cash payment carries a tendered amount.");
                }

                // Antes de sumar: dos importes enormes desbordarían decimal (500 en vez de 422).
                if (amount > total)
                {
                    throw new PosDomainException(
                        "pos.sale.payment_exceeds_total", "A card or transfer gives no change.");
                }
            }
        }

        var nonCash = inputs.Where(input => input.Method != PosPaymentMethod.Cash).Sum(input => input.Amount!.Value);
        if (nonCash > total)
        {
            throw new PosDomainException("pos.sale.payment_exceeds_total", "A card or transfer gives no change.");
        }

        var cashDue = total - nonCash;
        var cash = inputs.SingleOrDefault(input => input.Method == PosPaymentMethod.Cash);
        decimal change;

        if (total == 0)
        {
            // Con total 0 es la única venta cuyo Cash guarda 0. Cualquier billete dejaría un
            // ChangeAmount igual al billete, que el arqueo no distingue de un vuelto real.
            if (cash is null || cash.Tendered!.Value > 0)
            {
                throw new PosDomainException(
                    "pos.sale.tendered_invalid", "A zero-total sale takes a single cash payment of 0.");
            }

            change = 0;
        }
        else if (cashDue > 0)
        {
            if (cash is null || cash.Tendered!.Value < cashDue)
            {
                throw new PosDomainException(
                    "pos.sale.payment_insufficient", "The payments do not cover the sale total.");
            }

            change = cash.Tendered.Value - cashDue;
        }
        else
        {
            if (cash is not null)
            {
                throw new PosDomainException(
                    "pos.sale.cash_payment_unneeded", "The other payments already cover the total.");
            }

            change = 0;
        }

        var payments = inputs.Select((input, index) => input.Method == PosPaymentMethod.Cash
            ? PosPayment.Create(saleId, index + 1, PosPaymentMethod.Cash, cashDue, input.Tendered, null)
            : PosPayment.Create(
                saleId, index + 1, input.Method, input.Amount!.Value, null,
                string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim()))
            .ToList();

        return (payments, change);
    }
}
