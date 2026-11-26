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

    /// <summary>
    /// Tenant mayorista detrás del proveedor, fijado al guardar el borrador (D-10). Con valor, la orden es vinculada: se
    /// envía como pedido al mayorista y la confirma su aprobación, no una persona.
    /// </summary>
    public Guid? SupplierTenantId { get; private set; }

    public bool IsLinked => SupplierTenantId is not null;

    /// <summary>Pedido que la orden creó en el tenant mayorista al enviarse; nulo antes de enviar.</summary>
    public Guid? LinkedOrderId { get; private set; }

    /// <summary>Número de ese pedido en el mayorista (<c>PED-0042</c>), para pintarlo sin cruzar tenants.</summary>
    public string? LinkedOrderNumber { get; private set; }

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

    /// <summary>Resultado del último correo al proveedor; nulo mientras no haya habido intento.</summary>
    public PurchaseOrderEmailStatus? LastEmailStatus { get; private set; }

    /// <summary>Dirección a la que salió (o habría salido) ese correo, tal como estaba en el proveedor.</summary>
    public string? LastEmailTo { get; private set; }

    public DateTimeOffset? LastEmailAt { get; private set; }

    /// <summary>El correo al proveedor vale desde que la orden se envió, y se pierde al cancelarla.</summary>
    public bool CanEmailSupplier =>
        Status is PurchaseOrderStatus.Sent or PurchaseOrderStatus.Confirmed or PurchaseOrderStatus.Received;

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;

    /// <summary>Pasos del ciclo en orden cronológico, desde el <c>Draft</c> del alta.</summary>
    public IReadOnlyList<PurchaseOrderHistoryEntry> History => _history;

    public static PurchaseOrder Create(
        Guid tenantId,
        string number,
        Guid supplierId,
        Guid? supplierTenantId,
        string currency,
        string? notes,
        IReadOnlyList<PurchaseOrderLineDraft> lines,
        Guid memberId,
        DateTimeOffset now,
        bool withPrices = true)
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
        order.Apply(supplierId, supplierTenantId, currency, notes, lines, withPrices);
        order.AppendHistory(PurchaseOrderStatus.Draft, memberId, now);
        return order;
    }

    /// <summary>
    /// Reemplaza proveedor, modo (vinculado o externo), moneda, notas y líneas (con ids nuevos). Sólo en <c>Draft</c>.
    /// </summary>
    public void Replace(
        Guid supplierId,
        Guid? supplierTenantId,
        string currency,
        string? notes,
        IReadOnlyList<PurchaseOrderLineDraft> lines,
        DateTimeOffset now,
        bool withPrices = true)
    {
        EnsureDraft();
        Apply(supplierId, supplierTenantId, currency, notes, lines, withPrices);
        Touch(now);
    }

    /// <summary>
    /// Anota cómo salió el correo al proveedor. No es un cambio de la orden: no sube <see cref="Version"/> (un reenvío no
    /// puede dejar a otro miembro con un <c>If-Match</c> viejo) ni <see cref="UpdatedAt"/>.
    /// </summary>
    public void RecordEmail(PurchaseOrderEmailStatus status, string? to, DateTimeOffset at)
    {
        if (!CanEmailSupplier)
        {
            throw new PurchasesDomainException(
                PurchasesErrorCodes.EmailNotAllowed,
                "El correo al proveedor sólo vale en una orden enviada, confirmada o recibida.");
        }

        LastEmailStatus = status;
        LastEmailTo = to;
        LastEmailAt = at;
    }

    /// <summary>Borrar es definitivo y sólo vale para un borrador.</summary>
    public void EnsureDeletable() => EnsureDraft();

    /// <summary>Envío de una orden externa: el proveedor la recibe por fuera (PDF) y alguien registra su respuesta.</summary>
    public void Send(Guid memberId, DateTimeOffset now)
    {
        if (IsLinked)
        {
            throw new InvalidOperationException("Una orden vinculada se envía con SendToWholesaler.");
        }

        MoveTo(PurchaseOrderStatus.Sent, PurchaseOrderAction.Send, memberId, now);
    }

    /// <summary>
    /// Envío de una orden vinculada, ya con el pedido creado en el mayorista. Las líneas pasan a ser las del pedido, con
    /// sus ids (D-8): en un reintento después de que el pedido ya existía, la orden adopta lo que el pedido guardó.
    /// Con <paramref name="withPrices"/> en <c>false</c> (lista oculta, spec 2026-11-19) las adopta sin montos: el precio
    /// del pedido no se copia de vuelta, y llega con la aprobación.
    /// </summary>
    public void SendToWholesaler(
        Guid linkedOrderId,
        string linkedOrderNumber,
        IReadOnlyList<LinkedLineSnapshot> lines,
        Guid memberId,
        DateTimeOffset now,
        bool withPrices = true)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkedOrderNumber);
        EnsureLinked();
        EnsureAllowed(PurchaseOrderAction.Send);
        if (lines.Count == 0)
        {
            throw new PurchasesDomainException(PurchasesErrorCodes.Empty, "La orden necesita al menos una línea.");
        }

        LinkedOrderId = linkedOrderId;
        LinkedOrderNumber = linkedOrderNumber;
        _lines.Clear();
        _lines.AddRange(lines.Select(line => PurchaseOrderLine.From(line, withPrices)));
        MoveTo(PurchaseOrderStatus.Sent, PurchaseOrderAction.Send, memberId, now);
    }

    /// <summary>
    /// Aplica las líneas finales del pedido que aprobó el mayorista (D-21) y confirma la orden. Sólo actúa sobre una orden
    /// vinculada en <c>Sent</c>: repetir el evento, o recibirlo con la orden cancelada, devuelve false y no cambia nada.
    /// El historial lo firma el mayorista (<see cref="Guid.Empty"/>, D-19).
    /// </summary>
    public bool ApplyWholesalerApproval(IReadOnlyList<WholesalerFinalLine> lines, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!IsLinked || Status != PurchaseOrderStatus.Sent)
        {
            return false;
        }

        var pending = _lines.ToDictionary(line => line.Id);
        var added = new List<PurchaseOrderLine>();
        foreach (var final in lines)
        {
            // Un id que no es de la orden, o que ya se usó, es una línea que el mayorista agregó.
            if (final.SourceLineId is { } sourceId && pending.Remove(sourceId, out var line))
            {
                line.Confirm(final.Quantity, final.UnitCost);
            }
            else
            {
                added.Add(PurchaseOrderLine.Added(final));
            }
        }

        foreach (var dropped in pending.Values)
        {
            dropped.Confirm(0m, dropped.UnitCost);
        }

        _lines.AddRange(added);
        ConfirmedWithChanges = _lines.Any(line => line.Status != PurchaseOrderLineStatus.Unchanged);
        Status = PurchaseOrderStatus.Confirmed;
        AppendHistory(PurchaseOrderStatus.Confirmed, Guid.Empty, now);
        Touch(now);
        return true;
    }

    /// <summary>
    /// El mayorista anuló el pedido: la orden se cancela desde cualquier estado salvo <c>Received</c> (D-21). Ya recibida o
    /// ya cancelada devuelve false y no cambia nada; una orden externa también (ruling X8c: el evento de un mayorista no toca
    /// lo que no es suyo).
    /// </summary>
    public bool ApplyWholesalerCancellation(DateTimeOffset now)
    {
        if (!IsLinked || Status is PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled)
        {
            return false;
        }

        Status = PurchaseOrderStatus.Cancelled;
        AppendHistory(PurchaseOrderStatus.Cancelled, Guid.Empty, now);
        Touch(now);
        return true;
    }

    /// <summary>
    /// Líneas confirmadas de una orden vinculada que todavía no tienen variante en el catálogo propio. Las rechazadas no
    /// se compran, así que no hacen falta.
    /// </summary>
    public IReadOnlyList<PurchaseOrderLine> LinesNeedingCatalog() =>
        IsLinked && Status == PurchaseOrderStatus.Confirmed
            ? _lines.Where(line => line.ConfirmedQuantity > 0m && line.VariantId is null).ToList()
            : [];

    /// <summary>
    /// Enlaza una línea con la variante del catálogo propio que se encontró o se creó. No sube <see cref="Version"/>: es
    /// efecto del sistema dentro de la misma transacción que la confirmación. Sólo en una orden vinculada confirmada (ruling
    /// X8c); fuera de eso devuelve false y no cambia nada.
    /// </summary>
    public bool LinkLineToCatalog(Guid lineId, Guid variantId, bool created)
    {
        var line = _lines.FirstOrDefault(candidate => candidate.Id == lineId)
            ?? throw new ArgumentException($"La línea '{lineId}' no es de la orden.", nameof(lineId));
        if (!IsLinked || Status != PurchaseOrderStatus.Confirmed)
        {
            return false;
        }

        line.LinkToCatalog(variantId, created);
        return true;
    }

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
    public IReadOnlyList<PurchaseOrderAction> AllowedActions() => IsLinked ? LinkedAllowedActions() : Status switch
    {
        PurchaseOrderStatus.Draft =>
            [PurchaseOrderAction.Edit, PurchaseOrderAction.Delete, PurchaseOrderAction.Send, PurchaseOrderAction.Cancel],
        PurchaseOrderStatus.Sent => [PurchaseOrderAction.Confirm, PurchaseOrderAction.Cancel],
        PurchaseOrderStatus.Confirmed => [PurchaseOrderAction.Receive, PurchaseOrderAction.Cancel],
        _ => [],
    };

    /// <summary>
    /// False en una orden vinculada que nació sin precio y todavía no la aprobó el mayorista (D-11): alguna línea no tiene
    /// subtotal. Se deriva, no se guarda. Una externa siempre tiene precio (D-7).
    /// </summary>
    public bool HasPrices => _lines.All(line => line.Subtotal is not null);

    /// <summary>Totales efectivos (D-5): con lo confirmado en las líneas que lo tienen. Nulo si falta algún precio.</summary>
    public PurchaseOrderTotals? Totals() =>
        _lines.All(line => line.Subtotal is not null && line.Tax is not null)
            ? PurchaseOrderMath.Sum(_lines.Select(line => (line.Subtotal!.Value, line.Tax!.Value)))
            : null;

    /// <summary>
    /// Totales con lo pedido, sin importar lo confirmado. Nulo si el pedido no tuvo precio: una orden que nació sin precio no
    /// tiene total pedido, ni siquiera después de aprobarse.
    /// </summary>
    public PurchaseOrderTotals? RequestedTotals() =>
        _lines.All(line => line.RequestedSubtotal is not null && line.RequestedTax is not null)
            ? PurchaseOrderMath.Sum(_lines.Select(line => (line.RequestedSubtotal!.Value, line.RequestedTax!.Value)))
            : null;

    // D-13: la confirmación la hace la aprobación del mayorista, y la cancelación después de confirmar también es suya.
    private IReadOnlyList<PurchaseOrderAction> LinkedAllowedActions() => Status switch
    {
        PurchaseOrderStatus.Draft =>
            [PurchaseOrderAction.Edit, PurchaseOrderAction.Delete, PurchaseOrderAction.Send, PurchaseOrderAction.Cancel],
        PurchaseOrderStatus.Sent => [PurchaseOrderAction.Cancel],
        PurchaseOrderStatus.Confirmed => [PurchaseOrderAction.Receive],
        _ => [],
    };

    private void Apply(
        Guid supplierId,
        Guid? supplierTenantId,
        string currency,
        string? notes,
        IReadOnlyList<PurchaseOrderLineDraft> lines,
        bool withPrices)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            throw new PurchasesDomainException(PurchasesErrorCodes.Empty, "La orden necesita al menos una línea.");
        }

        // Application arma los borradores según el modo; uno mezclado es un error de programación, no del usuario.
        var linked = supplierTenantId is not null;
        if (!withPrices && !linked)
        {
            // D-7: una externa exige costo; sin precio sólo existe en una vinculada.
            throw new ArgumentException("Una orden externa no puede guardarse sin precios.", nameof(withPrices));
        }

        if (lines.Any(line => linked ? line.Listing is null || line.VariantId is not null : line.Listing is not null || line.VariantId is null))
        {
            throw new ArgumentException(
                linked
                    ? "Una orden vinculada lleva la lista del mayorista en cada línea y ninguna variante propia."
                    : "Una orden externa lleva la variante en cada línea y ninguna lista de mayorista.",
                nameof(lines));
        }

        var normalizedCurrency = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedCurrency.Length != CurrencyLength || !normalizedCurrency.All(char.IsAsciiLetterUpper))
        {
            throw new PurchasesDomainException(
                PurchasesErrorCodes.InvalidCurrency,
                "La moneda tiene que ser un código ISO 4217 de tres letras.");
        }

        SupplierId = supplierId;
        SupplierTenantId = supplierTenantId;
        Currency = normalizedCurrency;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        _lines.Clear();
        _lines.AddRange(lines.Select(line => PurchaseOrderLine.From(line, withPrices)));
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

    private void EnsureLinked()
    {
        if (!IsLinked)
        {
            throw new PurchasesDomainException(
                PurchasesErrorCodes.SupplierNotLinked,
                "La orden no es de un proveedor vinculado.");
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
