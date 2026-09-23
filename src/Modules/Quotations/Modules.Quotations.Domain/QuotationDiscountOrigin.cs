namespace Modules.Quotations.Domain;

/// <summary>
/// De dónde salió el descuento de una línea. Reemplaza al bool <c>Grouped</c>, que sólo sabía
/// distinguir dos de los tres casos y que además nunca llegó a viajar a la respuesta aunque su
/// documentación dijera que sí.
///
/// Viaja a la pantalla porque "te lo ganaste sola", "te lo dieron entre todas" y "te lo dio el
/// descuento global que eligió el asesor" no son lo mismo para quien lee una cotización, y
/// desde afuera no hay manera de reconstruir cuál fue.
/// </summary>
public enum QuotationDiscountOrigin
{
    /// <summary>La cantidad de la línea cayó sola en un tramo del producto.</summary>
    Own,

    /// <summary>El tramo se lo dio la suma del grupo y no su propia cantidad.</summary>
    Group,

    /// <summary>El tramo se lo dio el piso global que el asesor eligió para la cotización.</summary>
    GlobalFloor
}
