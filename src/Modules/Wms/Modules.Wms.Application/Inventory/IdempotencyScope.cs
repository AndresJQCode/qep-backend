namespace Modules.Wms.Application;

public enum IdempotencyKeySource
{
    /// <summary>Ruta IK: la clave viene en el header <c>Idempotency-Key</c>.</summary>
    Header,

    /// <summary>Ruta SK: el servidor deriva la clave del documento y su versión; el header se ignora.</summary>
    Server,
}

public static class ServerIdempotencyKeys
{
    public static string For(string document, Guid id, string action, long version) => $"{document}:{id:D}:{action}:{version}";
}

/// <summary>
/// La clave de la petición en curso, que el filtro fija antes del handler y <see cref="IOperationReceipts.Record{TResponse}"/>
/// consume. Scoped: vive lo que la petición. Sin <see cref="Key"/> el scope está inactivo, y grabar un recibo entonces es un error
/// de cableado (una ruta idempotente sin filtro).
/// </summary>
public sealed class IdempotencyScope
{
    public Guid TenantId { get; set; }

    public string? Key { get; set; }

    public string? RequestHash { get; set; }

    public IdempotencyKeySource Source { get; set; }

    public bool IsActive => Key is not null;
}
