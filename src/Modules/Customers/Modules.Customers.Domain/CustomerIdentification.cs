namespace Modules.Customers.Domain;

/// <summary>
/// El documento con el que se identifica al cliente: tipo y numero, juntos.
///
/// Van juntos y no como dos parametros sueltos por la misma razon por la que existe
/// <c>CompanyContactInfo</c> en empresas: el par es la **clave de unicidad** del cliente dentro
/// del tenant (<c>IX_customers_tenant_identification</c>), y separarlos en la firma permite
/// construir un cliente con el tipo de uno y el numero de otro sin una sola queja del compilador.
/// </summary>
public sealed record CustomerIdentification
{
    public required IdentificationType Type { get; init; }

    public required string Number { get; init; }

    // Espeja el ancho de columna, igual que el resto del modulo: un valor demasiado largo falla
    // como 422 con codigo de dominio en vez de llegar a PostgreSQL y volver como 500. Sale del
    // schema del formulario que ya existe (customer-form.schema.ts).
    public const int NumberMaxLength = 32;

    /// <summary>
    /// Normaliza y hace cumplir los invariantes. Lo llama <see cref="Customer"/>; no es punto de
    /// entrada publico, del mismo modo que <c>Customer.Create</c> es el unico que construye el
    /// agregado.
    /// </summary>
    internal CustomerIdentification Normalized()
    {
        if (string.IsNullOrWhiteSpace(Number))
        {
            throw new CustomersDomainException(
                "customers.customer.identification_number_required",
                "The customer identification number is required.");
        }

        // Se recorta pero no se normaliza mas alla de eso: los puntos y guiones de un NIT
        // colombiano ("900.123.456-1") son como la persona lo escribe y como lo va a buscar.
        // Quitarlos aca haria que el numero guardado no coincida con el que el usuario tipea en
        // el buscador, y ese es el unico lugar donde vuelve a verlo.
        var trimmed = Number.Trim();
        return trimmed.Length > NumberMaxLength
            ? throw new CustomersDomainException(
                "customers.customer.identification_number_too_long",
                $"The customer identification number cannot exceed {NumberMaxLength} characters.")
            : this with { Number = trimmed };
    }
}
