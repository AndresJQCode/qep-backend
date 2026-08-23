namespace Modules.Customers.Domain;

/// <summary>
/// Los datos comerciales del cliente: como esta clasificado y si se le aplica retencion.
///
/// Van juntos por la misma razon que los de contacto, y ademas porque cambian por la misma razon:
/// los dos los define el area comercial, no quien carga el alta.
///
/// Las listas de precio del cliente **no** viven aca: a diferencia de la clasificacion (1:1), un
/// cliente puede tener varias listas a la vez, asi que es una relacion N:N propia
/// (<c>CustomerPriceList</c>), no un campo de este value object. Ver CustomerPriceList.cs.
/// </summary>
public sealed record CustomerCommercialInfo
{
    /// <summary>
    /// FK obligatoria a <see cref="ClientClassification"/>. Reemplaza al enum fijo
    /// <c>CustomerClassification</c> (Pequeno/Mediano/Grande): ese catalogo no tenia ninguna
    /// relacion con este agregado y ya no tiene consumidores. Un cliente sin clasificacion tampoco
    /// tiene de donde salir el prefijo de su CUC, asi que dejo de ser opcional.
    /// </summary>
    public ClientClassificationId ClassificationId { get; init; }

    /// <summary>
    /// Si al cliente se le aplica retencion de impuestos.
    ///
    /// <c>bool</c> y no <c>bool?</c>: el formulario lo presenta como un interruptor con dos
    /// estados y ningun "sin definir". Un tercer estado nulo obligaria a cada consumidor a
    /// decidir que hacer con el, y ese consumidor —cotizaciones— todavia no existe para opinar.
    /// </summary>
    public bool WithRetention { get; init; }

    /// <summary>
    /// **No usar para <c>Customer.Create</c>/<c>Update</c>.** Deja <c>ClassificationId</c> en su
    /// default (<c>Guid.Empty</c>), que <c>Customer</c> rechaza — la clasificacion es obligatoria.
    /// Sigue existiendo para pruebas que arman un <c>CustomerCommercialInfo</c> parcial y
    /// sobreescriben el campo que les importa con un <c>with</c> o un inicializador propio.
    /// </summary>
    public static CustomerCommercialInfo Empty { get; } = new();
}
