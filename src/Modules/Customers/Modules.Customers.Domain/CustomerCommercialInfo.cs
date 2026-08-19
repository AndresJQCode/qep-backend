namespace Modules.Customers.Domain;

/// <summary>
/// Los datos comerciales del cliente: como esta clasificado, con que lista de precios se le cotiza
/// y si se le aplica retencion.
///
/// Van juntos por la misma razon que los de contacto, y ademas porque cambian por la misma razon:
/// los tres los define el area comercial, no quien carga el alta.
/// </summary>
public sealed record CustomerCommercialInfo
{
    public CustomerClassification? Classification { get; init; }

    /// <summary>
    /// Referencia a la lista de precios del modulo <c>pricing</c>.
    ///
    /// **Sin clave foranea, y no por olvido:** `pricing` no existe todavia en `qep-backend`, asi
    /// que no hay tabla a la que apuntar. Guardarlo igual conserva el dato que el formulario ya
    /// manda; inventar una tabla de listas de precios para tener a donde apuntar seria construir
    /// un modulo ajeno desde este slice.
    ///
    /// La consecuencia es que nadie garantiza que el id exista. El dia que `pricing` llegue, esta
    /// columna gana su FK con una migracion que primero tiene que limpiar los ids huerfanos.
    /// </summary>
    public Guid? PriceListId { get; init; }

    /// <summary>
    /// Si al cliente se le aplica retencion de impuestos.
    ///
    /// <c>bool</c> y no <c>bool?</c>: el formulario lo presenta como un interruptor con dos
    /// estados y ningun "sin definir". Un tercer estado nulo obligaria a cada consumidor a
    /// decidir que hacer con el, y ese consumidor —cotizaciones— todavia no existe para opinar.
    /// </summary>
    public bool WithRetention { get; init; }

    public static CustomerCommercialInfo Empty { get; } = new();
}
