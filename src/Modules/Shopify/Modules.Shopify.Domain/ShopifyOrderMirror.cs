using System.Text.Json;

namespace Modules.Shopify.Domain;

/// <summary>Lo que la bandeja muestra de un pedido sin abrir el payload (spec 2026-10-15 §5.4).</summary>
public sealed record ShopifyOrderHeader(
    string OrderName,
    long? ShopifyCustomerId,
    DateTimeOffset ShopifyCreatedAt,
    DateTimeOffset? CancelledAt,
    string? FinancialStatus,
    string Currency,
    decimal TotalPrice,
    bool TaxesIncluded);

/// <summary>Spec 2026-10-15 §5.4: una fila de <c>shopify.orders</c> por pedido de Shopify y conexión.</summary>
public sealed class ShopifyOrderMirror
{
    public const int OrderNameMaxLength = 64;
    public const int FinancialStatusMaxLength = 32;
    public const int CurrencyLength = 3;
    public const int WaitReasonMaxLength = 64;
    public const int LastErrorMaxLength = 512;

    private ShopifyOrderMirror()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public long ShopifyOrderId { get; private set; }

    public string OrderName { get; private set; } = string.Empty;

    public long? ShopifyCustomerId { get; private set; }

    public DateTimeOffset ShopifyCreatedAt { get; private set; }

    public DateTimeOffset ShopifyUpdatedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? FinancialStatus { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public decimal TotalPrice { get; private set; }

    public bool TaxesIncluded { get; private set; }

    public string Payload { get; private set; } = string.Empty;

    public ShopifyImportState ImportState { get; private set; }

    public string? WaitReason { get; private set; }

    public string? WaitDetail { get; private set; }

    public string? Warnings { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public DateTimeOffset? ImportedAt { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? WarningsReviewedAt { get; private set; }

    public Guid? QepCustomerId { get; private set; }

    public Guid? QepOrderId { get; private set; }

    public decimal? TotalDifference { get; private set; }

    /// <summary>Concurrencia optimista de la fila: el worker de webhooks, la conciliación y el reintento manual pueden cargarla a
    /// la vez, y sin token el último en guardar pisaría un snapshot más nuevo o devolvería a <c>Waiting</c> una fila importada.
    /// Sube con cada cambio.</summary>
    public long Version { get; private set; }

    /// <summary>La fila nueva: en espera y sin motivo hasta que el importador la evalúe.</summary>
    public static ShopifyOrderMirror Create(
        Guid tenantId, Guid connectionId, long shopifyOrderId, string payloadJson, DateTimeOffset shopifyUpdatedAt, ShopifyOrderHeader header)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(connectionId, Guid.Empty);
        var row = new ShopifyOrderMirror
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            ShopifyOrderId = shopifyOrderId,
            ImportState = ShopifyImportState.Waiting,
            Version = 1,
        };
        row.Apply(payloadJson, shopifyUpdatedAt, Validated(header));
        return row;
    }

    /// <summary>§5.4: un snapshot con <c>updated_at</c> anterior al guardado no pisa nada (devuelve <c>false</c>); los webhooks llegan
    /// desordenados y la conciliación trae lo mismo con retraso. Uno igual o más nuevo reemplaza el payload y la cabecera, sin
    /// tocar el estado de importación: qué hacer con el cambio lo decide §6.3. También sobre una fila terminal
    /// (<c>Cancelled</c>, <c>Ignored</c>), que guarda la última foto y no cambia de estado.</summary>
    public bool ApplySnapshot(string payloadJson, DateTimeOffset shopifyUpdatedAt, ShopifyOrderHeader header)
    {
        var validated = Validated(header);
        if (shopifyUpdatedAt < ShopifyUpdatedAt)
        {
            return false;
        }

        Apply(payloadJson, shopifyUpdatedAt, validated);
        Touch();
        return true;
    }

    /// <summary>§6.2: la primera precondición que falló. Cuenta como intento (§6.4: sin tope). Sólo sobre una fila en espera:
    /// <c>Imported</c> no vuelve, y <c>Cancelled</c> e <c>Ignored</c> son terminales (Ruling S13).</summary>
    public void MarkWaiting(string reason, string? detailJson, DateTimeOffset attemptedAt)
    {
        if (!ShopifyWaitReasons.IsKnown(reason))
        {
            throw new ArgumentException($"'{reason}' is not a Shopify wait reason.", nameof(reason));
        }

        EnsureWaiting("go back to waiting");
        WaitReason = reason;
        WaitDetail = detailJson;
        CountAttempt(attemptedAt);
        Touch();
    }

    /// <summary>§6.2.4: el pedido entró a QEP. Sólo desde <c>Waiting</c>; los avisos van como arreglo JSON de
    /// <see cref="ShopifyWarnings"/> y uno vacío se guarda como nulo.</summary>
    public void MarkImported(Guid customerId, Guid orderId, decimal totalDifference, string? warningsJson, DateTimeOffset attemptedAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(customerId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(orderId, Guid.Empty);
        EnsureWaiting("be imported");
        ImportState = ShopifyImportState.Imported;
        WaitReason = null;
        WaitDetail = null;
        QepCustomerId = customerId;
        QepOrderId = orderId;
        TotalDifference = totalDifference;
        Warnings = HasAny(warningsJson) ? warningsJson : null;
        CountAttempt(attemptedAt);
        ImportedAt = attemptedAt;
        Touch();
    }

    /// <summary>§6.2.1: el cliente creado antes del pedido se guarda en la fila, para que un reintento no cree otro.</summary>
    public void AttachCustomer(Guid customerId)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("The customer id is required.", nameof(customerId));
        }

        QepCustomerId = customerId;
        Touch();
    }

    /// <summary>§5.4 y §6.3: pedido de prueba o cancelado antes de importarse; sólo desde <c>Waiting</c> (uno importado se
    /// cancela, no se ignora) y terminal. Repetirlo no hace nada. El motivo de espera se borra: ya no aplica.</summary>
    public void MarkIgnored()
    {
        if (ImportState == ShopifyImportState.Ignored)
        {
            return;
        }

        EnsureWaiting("be ignored");
        ImportState = ShopifyImportState.Ignored;
        WaitReason = null;
        WaitDetail = null;
        Touch();
    }

    /// <summary>§6.3: un pedido importado que se canceló en Shopify. El detalle dice si quedó algo por hacer a mano
    /// (<c>invoiced_before_cancel</c>). Uno en espera se ignora, no se cancela. Terminal.</summary>
    public void MarkCancelled(string? detailJson)
    {
        if (ImportState != ShopifyImportState.Imported)
        {
            throw new InvalidOperationException($"Only an imported Shopify order can be cancelled; this one is {ImportState}.");
        }

        ImportState = ShopifyImportState.Cancelled;
        WaitDetail = detailJson;
        Touch();
    }

    /// <summary>§6.3: un cambio posterior que Quotations rechazó (<c>order.order.not_pending</c>,
    /// <c>order.order.payment_has_proofs</c>, otro rechazo de la anulación) queda auditado en la fila, en cualquier estado, sin
    /// cambiarlo. Se recorta al ancho de la columna. El mismo error otra vez no es un cambio: la reacción se repite (bucle de
    /// concurrencia de la ingesta, reintento tras un guardado fallido) y no debe subir la versión.</summary>
    public void RecordLastError(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var trimmed = error.Trim();
        var recorded = trimmed.Length <= LastErrorMaxLength ? trimmed : trimmed[..LastErrorMaxLength];
        if (string.Equals(LastError, recorded, StringComparison.Ordinal))
        {
            return;
        }

        LastError = recorded;
        Touch();
    }

    /// <summary>§6.3: la operación que salió bien después de un rechazo borra el error viejo, para que una fila <c>Cancelled</c> o
    /// sincronizada no siga mostrando un problema que ya no existe. Sin error no hay cambio ni sube la versión.</summary>
    public void ClearLastError()
    {
        if (LastError is null)
        {
            return;
        }

        LastError = null;
        Touch();
    }

    /// <summary>D-S31: «Marcar como revisado». Sin avisos no hay nada que revisar; repetirlo conserva la primera revisión.</summary>
    public void ReviewWarnings(DateTimeOffset now)
    {
        if (!HasAny(Warnings))
        {
            throw new ShopifyDomainException(ShopifyErrorCodes.OrderNoWarnings, "The Shopify order has no warnings to review.");
        }

        if (WarningsReviewedAt is null)
        {
            WarningsReviewedAt = now;
            Touch();
        }
    }

    private static bool HasAny(string? warningsJson)
    {
        if (string.IsNullOrWhiteSpace(warningsJson))
        {
            return false;
        }

        using var document = JsonDocument.Parse(warningsJson);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.GetArrayLength() > 0
            : throw new ArgumentException("The warnings must be a JSON array.", nameof(warningsJson));
    }

    /// <summary>§5.4: los anchos de las columnas. Un valor fuera de ellos es un payload que QEP no sabe guardar, no una regla del
    /// usuario: <see cref="ArgumentException"/> para que el llamador (parser o worker) lo registre en <c>last_error</c> sin
    /// reintentar, en vez de un 22001 de Postgres que el worker reintentaría hasta agotar los intentos. La moneda se normaliza a
    /// mayúsculas, como <c>QuotationBillingAccount</c>.</summary>
    private static ShopifyOrderHeader Validated(ShopifyOrderHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (string.IsNullOrWhiteSpace(header.OrderName) || header.OrderName.Length > OrderNameMaxLength)
        {
            throw new ArgumentException($"The Shopify order name is required and cannot exceed {OrderNameMaxLength} characters.", nameof(header));
        }

        if (header.FinancialStatus is { Length: > FinancialStatusMaxLength })
        {
            throw new ArgumentException($"The Shopify financial status cannot exceed {FinancialStatusMaxLength} characters.", nameof(header));
        }

        if (header.Currency is null || header.Currency.Length != CurrencyLength || !header.Currency.All(char.IsAsciiLetter))
        {
            throw new ArgumentException("The Shopify order currency must be a three-letter ISO 4217 code.", nameof(header));
        }

        return header with { Currency = header.Currency.ToUpperInvariant() };
    }

    private void EnsureWaiting(string transition)
    {
        if (ImportState != ShopifyImportState.Waiting)
        {
            throw new InvalidOperationException($"Only a waiting Shopify order can {transition}; this one is {ImportState}.");
        }
    }

    private void Touch() => Version++;

    private void CountAttempt(DateTimeOffset attemptedAt)
    {
        Attempts++;
        LastAttemptAt = attemptedAt;
    }

    private void Apply(string payloadJson, DateTimeOffset shopifyUpdatedAt, ShopifyOrderHeader header)
    {
        Payload = payloadJson;
        ShopifyUpdatedAt = shopifyUpdatedAt;
        OrderName = header.OrderName;
        ShopifyCustomerId = header.ShopifyCustomerId;
        ShopifyCreatedAt = header.ShopifyCreatedAt;
        CancelledAt = header.CancelledAt;
        FinancialStatus = header.FinancialStatus;
        Currency = header.Currency;
        TotalPrice = header.TotalPrice;
        TaxesIncluded = header.TaxesIncluded;
    }
}
