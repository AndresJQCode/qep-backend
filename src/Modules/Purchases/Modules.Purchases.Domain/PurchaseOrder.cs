namespace Modules.Purchases.Domain;

/// <summary>
/// Una orden de compra a un proveedor externo del directorio. Se edita en <c>Draft</c>, se envía, el proveedor la
/// confirma (con ajustes por línea), se recibe; hasta antes de recibirla se puede cancelar. Toda mutación sube
/// <see cref="Version"/> (el <c>If-Match</c> del endpoint) y cada cambio de estado deja una entrada en el historial.
/// </summary>
public sealed class PurchaseOrder
{
    public const int NotesMaxLength = 2000;

    public const int MaxLines = 200;

    private const int CurrencyLength = 3;

    private readonly List<PurchaseOrderLine> _lines = [];

    private readonly List<PurchaseOrderHistoryEntry> _history = [];

    // EF Core materializa por acá; Create es el único punto de entrada de código.
    private PurchaseOrder()
    {
        Number = string.Empty;
        Currency = string.Empty;
    }

    public PurchaseOrderId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Consecutivo por tenant (<c>OC-0001</c>); lo asigna Infrastructure.</summary>
    public string Number { get; private set; }

    /// <summary>FK blanda al directorio de proveedores: Guid y no un id tipado, como en el resto de módulos.</summary>
    public Guid SupplierId { get; private set; }

    /// <summary>ISO 4217 en mayúsculas. El dominio sólo valida la forma; el catálogo de monedas lo valida Application.</summary>
    public string Currency { get; private set; }

    public string? Notes { get; private set; }

    public PurchaseOrderStatus Status { get; private set; }

    /// <summary>True si al confirmar alguna línea no quedó <see cref="PurchaseOrderLineStatus.Unchanged"/>.</summary>
    public bool ConfirmedWithChanges { get; private set; }

    public Guid CreatedByMemberId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;

    /// <summary>Pasos del ciclo en orden cronológico, desde el <c>Draft</c> del alta.</summary>
    public IReadOnlyList<PurchaseOrderHistoryEntry> History => _history;

    public static PurchaseOrder Create(
        Guid tenantId,
        string number,
        Guid supplierId,
        string currency,
        string? notes,
        IReadOnlyList<PurchaseOrderLineDraft> lines,
        Guid memberId,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);

        var order = new PurchaseOrder
        {
            Id = PurchaseOrderId.New(),
            TenantId = tenantId,
            Number = number,
            Status = PurchaseOrderStatus.Draft,
            CreatedByMemberId = memberId,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        order.Apply(supplierId, currency, notes, lines);
        order.AppendHistory(PurchaseOrderStatus.Draft, memberId, now);
        return order;
    }

    /// <summary>Reemplaza proveedor, moneda, notas y líneas (con ids nuevos). Sólo en <c>Draft</c>.</summary>
    public void Replace(
        Guid supplierId,
        string currency,
        string? notes,
        IReadOnlyList<PurchaseOrderLineDraft> lines,
        DateTimeOffset now)
    {
        EnsureDraft();
        Apply(supplierId, currency, notes, lines);
        Touch(now);
    }

    /// <summary>Borrar es definitivo y sólo vale para un borrador.</summary>
    public void EnsureDeletable() => EnsureDraft();

    public void Send(Guid memberId, DateTimeOffset now) =>
        MoveTo(PurchaseOrderStatus.Sent, PurchaseOrderAction.Send, memberId, now);

    /// <summary>
    /// Registra lo que confirmó el proveedor. El cuerpo trae cada línea de la orden exactamente una vez; si falta una, se
    /// repite o trae una ajena, nada cambia (ni el estado ni la versión).
    /// </summary>
    public void Confirm(IReadOnlyList<PurchaseOrderLineConfirmation> lines, Guid memberId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);
        EnsureAllowed(PurchaseOrderAction.Confirm);

        var byId = lines
            .GroupBy(line => line.LineId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var matches = lines.Count == _lines.Count
            && byId.Count == _lines.Count
            && _lines.All(line => byId.ContainsKey(line.Id));
        if (!matches)
        {
            throw new PurchasesDomainException(
                PurchasesErrorCodes.ConfirmLinesMismatch,
                "La confirmación tiene que traer cada línea de la orden una sola vez.");
        }

        foreach (var line in _lines)
        {
            var confirmation = byId[line.Id][0];
            line.Confirm(confirmation.ConfirmedQuantity, confirmation.ConfirmedUnitCost);
        }

        ConfirmedWithChanges = _lines.Any(line => line.Status != PurchaseOrderLineStatus.Unchanged);
        MoveTo(PurchaseOrderStatus.Confirmed, PurchaseOrderAction.Confirm, memberId, now);
    }

    public void Receive(Guid memberId, DateTimeOffset now) =>
        MoveTo(PurchaseOrderStatus.Received, PurchaseOrderAction.Receive, memberId, now);

    public void Cancel(Guid memberId, DateTimeOffset now) =>
        MoveTo(PurchaseOrderStatus.Cancelled, PurchaseOrderAction.Cancel, memberId, now);

    /// <summary>
    /// Lo que el estado permite, en el orden del contrato. No mira permisos: sin <c>purchases.order.manage</c> la
    /// respuesta HTTP lo vacía (D-4).
    /// </summary>
    public IReadOnlyList<PurchaseOrderAction> AllowedActions() => Status switch
    {
        PurchaseOrderStatus.Draft =>
            [PurchaseOrderAction.Edit, PurchaseOrderAction.Delete, PurchaseOrderAction.Send, PurchaseOrderAction.Cancel],
        PurchaseOrderStatus.Sent => [PurchaseOrderAction.Confirm, PurchaseOrderAction.Cancel],
        PurchaseOrderStatus.Confirmed => [PurchaseOrderAction.Receive, PurchaseOrderAction.Cancel],
        _ => [],
    };

    /// <summary>Totales efectivos (D-5): con lo confirmado en las líneas que lo tienen.</summary>
    public PurchaseOrderTotals Totals() =>
        PurchaseOrderMath.Sum(_lines.Select(line => (line.Subtotal, line.Tax)));

    /// <summary>Totales con lo pedido, sin importar lo confirmado.</summary>
    public PurchaseOrderTotals RequestedTotals() =>
        PurchaseOrderMath.Sum(_lines.Select(line => (line.RequestedSubtotal, line.RequestedTax)));

    private void Apply(Guid supplierId, string currency, string? notes, IReadOnlyList<PurchaseOrderLineDraft> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            throw new PurchasesDomainException(PurchasesErrorCodes.Empty, "La orden necesita al menos una línea.");
        }

        var normalizedCurrency = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedCurrency.Length != CurrencyLength || !normalizedCurrency.All(char.IsAsciiLetterUpper))
        {
            throw new PurchasesDomainException(
                PurchasesErrorCodes.InvalidCurrency,
                "La moneda tiene que ser un código ISO 4217 de tres letras.");
        }

        SupplierId = supplierId;
        Currency = normalizedCurrency;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        _lines.Clear();
        _lines.AddRange(lines.Select(PurchaseOrderLine.From));
    }

    private void EnsureDraft()
    {
        if (Status != PurchaseOrderStatus.Draft)
        {
            throw new PurchasesDomainException(
                PurchasesErrorCodes.NotDraft,
                "Sólo se puede editar o borrar una orden en borrador.");
        }
    }

    // Última red (D-1): el handler compara con AllowedActions antes y responde 409; esto sólo frena a quien no lo hizo.
    private void EnsureAllowed(PurchaseOrderAction action)
    {
        if (!AllowedActions().Contains(action))
        {
            throw new PurchasesDomainException(
                PurchasesErrorCodes.InvalidTransition,
                $"La orden en estado {Status} no admite esa acción.");
        }
    }

    private void MoveTo(PurchaseOrderStatus status, PurchaseOrderAction action, Guid memberId, DateTimeOffset now)
    {
        EnsureAllowed(action);
        Status = status;
        AppendHistory(status, memberId, now);
        Touch(now);
    }

    private void AppendHistory(PurchaseOrderStatus status, Guid memberId, DateTimeOffset now) =>
        _history.Add(new PurchaseOrderHistoryEntry(status, now, memberId) { Sequence = _history.Count + 1 });

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
