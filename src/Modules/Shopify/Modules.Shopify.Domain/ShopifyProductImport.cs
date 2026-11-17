using BuildingBlocks.Domain;

namespace Modules.Shopify.Domain;

/// <summary>Spec 2026-11-17 §5.2: <c>All</c> trae todo; <c>Missing</c>, sólo los productos con alguna variante sin vínculo.</summary>
public enum ShopifyProductImportMode
{
    All,
    Missing,
}

public enum ShopifyProductImportStatus
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>Lo que dejó una página (o un producto). Plan D3: <see cref="Products"/> y <see cref="Created"/> cuentan productos;
/// <see cref="Matched"/>, <see cref="WithoutSku"/> y <see cref="Skipped"/> cuentan variantes.</summary>
public sealed record ShopifyProductTally(int Products, int Created, int Matched, int WithoutSku, int Skipped)
{
    public static ShopifyProductTally Empty { get; } = new(0, 0, 0, 0, 0);

    public ShopifyProductTally Add(ShopifyProductTally other) =>
        new(Products + other.Products, Created + other.Created, Matched + other.Matched, WithoutSku + other.WithoutSku, Skipped + other.Skipped);
}

/// <summary>
/// Spec 2026-11-17 §5.2: el trabajo que trae el catálogo de una tienda. Una sola importación viva por conexión (índice único
/// parcial <c>IX_product_imports_connection_live</c>, traducido a <c>shopify.products.import_running</c>). El worker la reclama
/// con lease y <c>attempts</c> por SQL; <see cref="Cursor"/> (plan D2) le deja reanudar tras un lease vencido sin recontar.
/// </summary>
public sealed class ShopifyProductImport
{
    public const int CursorMaxLength = 512;

    private ShopifyProductImport()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public ShopifyProductImportMode Mode { get; private set; }

    public ShopifyProductImportStatus Status { get; private set; }

    /// <summary>Productos según <c>productsCount</c> al empezar.</summary>
    public int Total { get; private set; }

    /// <summary>Productos recorridos.</summary>
    public int Done { get; private set; }

    /// <summary>Productos creados en Catalog.</summary>
    public int Created { get; private set; }

    /// <summary>Variantes vinculadas a una variante que ya existía.</summary>
    public int Matched { get; private set; }

    /// <summary>Variantes que entraron sin SKU (<c>shp-{id}</c>).</summary>
    public int WithoutSku { get; private set; }

    /// <summary>Variantes saltadas (una falla de producto suma todas las suyas).</summary>
    public int Skipped { get; private set; }

    public string? FailureCode { get; private set; }

    /// <summary>La membresía de quien la pidió.</summary>
    public Guid RequestedBy { get; private set; }

    /// <summary>Plan D2 y P3: el usuario (sujeto) con que firma la auditoría.</summary>
    public Guid RequestedBySubjectId { get; private set; }

    /// <summary>El <c>endCursor</c> de la última página guardada.</summary>
    public string? Cursor { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public bool IsLive => Status is ShopifyProductImportStatus.Pending or ShopifyProductImportStatus.Running;

    public static ShopifyProductImport Request(
        Guid tenantId, Guid connectionId, ShopifyProductImportMode mode, Guid requestedBy, Guid requestedBySubjectId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(connectionId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(requestedBy, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(requestedBySubjectId, Guid.Empty);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown import mode.");
        }

        return new ShopifyProductImport
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            Mode = mode,
            Status = ShopifyProductImportStatus.Pending,
            RequestedBy = requestedBy,
            RequestedBySubjectId = requestedBySubjectId,
            CreatedAt = now,
        };
    }

    /// <summary>Pasa a <c>Running</c> con el total del conteo inicial. No toca los contadores: un reintento tras un lease
    /// vencido sigue desde <see cref="Cursor"/>.</summary>
    public void Start(int total)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        EnsureLive();
        Status = ShopifyProductImportStatus.Running;
        Total = total;
    }

    /// <summary>Suma lo que dejó una página y guarda el cursor de la siguiente.</summary>
    public void RecordPage(ShopifyProductTally tally, string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(tally);
        EnsureLive();
        if (nextCursor is { Length: > CursorMaxLength })
        {
            throw new ArgumentException($"The cursor cannot exceed {CursorMaxLength} characters.", nameof(nextCursor));
        }

        Done += tally.Products;
        Created += tally.Created;
        Matched += tally.Matched;
        WithoutSku += tally.WithoutSku;
        Skipped += tally.Skipped;
        Cursor = nextCursor;
    }

    /// <summary>Guardada una página, el reclamo que la guardó es el primer intento de la siguiente: <see cref="Attempts"/> cuenta
    /// los intentos de la página en curso, no los de la importación entera (el reclamo de la cola le suma uno).</summary>
    public void RestartAttempts()
    {
        EnsureLive();
        Attempts = 1;
    }

    public void Complete(DateTimeOffset now)
    {
        EnsureLive();
        Status = ShopifyProductImportStatus.Done;
        LeaseUntil = null;
        FinishedAt = now;
    }

    /// <summary>Termina con el motivo; los contadores quedan como estaban.</summary>
    public void Fail(string code, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        EnsureLive();
        FailureCode = code.Length > ShopifyProductCodes.CodeMaxLength ? code[..ShopifyProductCodes.CodeMaxLength] : code;
        Status = ShopifyProductImportStatus.Failed;
        LeaseUntil = null;
        FinishedAt = now;
    }

    private void EnsureLive()
    {
        if (!IsLive)
        {
            throw new InvalidOperationException($"The product import is already {Status}.");
        }
    }
}

/// <summary>Spec 2026-11-17 §5.2: un producto saltado o anotado en una importación (<c>product_import_issues</c>).</summary>
public sealed class ShopifyProductImportIssue
{
    public const int TitleMaxLength = 255;

    private ShopifyProductImportIssue()
    {
        Title = string.Empty;
        Code = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ImportId { get; private set; }

    public long ShopifyProductId { get; private set; }

    public string Title { get; private set; }

    public string Code { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>El título se recorta con <see cref="SnapshotText.Fit"/>; el código, a <see cref="ShopifyProductCodes.CodeMaxLength"/>.</summary>
    public static ShopifyProductImportIssue Create(Guid importId, long shopifyProductId, string title, string code, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(importId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var trimmedCode = code.Trim();
        return new ShopifyProductImportIssue
        {
            Id = Guid.CreateVersion7(),
            ImportId = importId,
            ShopifyProductId = shopifyProductId,
            Title = SnapshotText.Fit((title ?? string.Empty).Trim(), TitleMaxLength),
            Code = trimmedCode.Length > ShopifyProductCodes.CodeMaxLength ? trimmedCode[..ShopifyProductCodes.CodeMaxLength] : trimmedCode,
            CreatedAt = now,
        };
    }
}
