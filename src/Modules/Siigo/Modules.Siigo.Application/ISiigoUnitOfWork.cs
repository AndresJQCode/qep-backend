namespace Modules.Siigo.Application;

/// <summary>Guarda la configuración, las facturas y su auditoría en una sola transacción. La implementación traduce los índices
/// únicos por nombre y la concurrencia, en Infrastructure (Application no referencia EF Core ni Npgsql).</summary>
public interface ISiigoUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Abre una transacción explícita sobre la conexión del módulo; <see cref="SaveChangesAsync"/> se suma a ella. Hace
    /// falta para <see cref="LockOrderAsync"/>.</summary>
    Task<ISiigoTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Spec 2026-11-18 §5.4: <c>pg_advisory_xact_lock(20261119, hashtext('{orderId}'))</c>. Exige una transacción abierta con
    /// <see cref="BeginTransactionAsync"/>: el candado se suelta con ella. Serializa dos «Generar factura» del mismo pedido: el
    /// segundo lee la factura que dejó el primero y no manda otra a Siigo.
    /// </summary>
    Task LockOrderAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <summary>La clave de dos partes del candado de factura (20261118 ya es de <c>ShopifyProductLock</c>).</summary>
public static class SiigoInvoiceLock
{
    public const int Namespace = 20261119;

    /// <summary>El texto que entra en <c>hashtext</c>: el uuid como lo formatea Postgres.</summary>
    public static string Key(Guid orderId) => orderId.ToString("D");
}

/// <summary>Se confirma con <see cref="CommitAsync"/>; disponerla sin commit hace rollback, que además suelta los candados que haya
/// tomado.</summary>
public interface ISiigoTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
