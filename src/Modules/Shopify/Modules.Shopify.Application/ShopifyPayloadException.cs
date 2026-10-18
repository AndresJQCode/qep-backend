namespace Modules.Shopify.Application;

/// <summary>
/// Un pedido de Shopify que QEP no sabe leer: falta un campo sin el cual no hay fila del espejo (<c>id</c>, <c>name</c>,
/// <c>updated_at</c>…) o un campo viene con un tipo que no es el documentado. Es un fallo permanente: reintentar el mismo cuerpo
/// da lo mismo, así que el worker lo registra en <c>last_error</c> y marca la entrega como procesada (Review Focus 2). No es una
/// <c>DomainException</c>: nunca llega a una respuesta HTTP.
/// </summary>
public sealed class ShopifyPayloadException : Exception
{
    public ShopifyPayloadException(string field, string message)
        : base(message)
    {
        Field = field;
    }

    public ShopifyPayloadException()
        : this(string.Empty, "The Shopify order payload is invalid.")
    {
    }

    public ShopifyPayloadException(string message)
        : this(string.Empty, message)
    {
    }

    public ShopifyPayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
        Field = string.Empty;
    }

    /// <summary>La ruta del campo en el payload (<c>line_items[0].quantity</c>), para el <c>last_error</c>.</summary>
    public string Field { get; }
}
