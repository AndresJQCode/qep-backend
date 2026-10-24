namespace Modules.Suppliers.Domain;

/// <summary>
/// El documento con el que se identifica al proveedor: tipo y número, juntos, porque el par es
/// lo que se muestra y se busca, y separarlos permite armar un proveedor con el tipo de uno y el
/// número de otro sin queja del compilador. A diferencia de Customers, el par **no** es único por
/// tenant: dos fichas del mismo proveedor no rompen nada.
/// </summary>
public sealed record SupplierIdentification(IdentificationType Type, string Number)
{
    public const int NumberMaxLength = 32;

    public static SupplierIdentification Create(IdentificationType type, string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new SuppliersDomainException(
                "suppliers.supplier.identification_number_required",
                "The supplier identification number is required.");
        }

        // Sólo se recorta: los puntos y guiones de un NIT ("900.123.456-7") son como la persona
        // lo escribe y como lo va a buscar.
        var trimmed = number.Trim();
        return trimmed.Length > NumberMaxLength
            ? throw new SuppliersDomainException(
                "suppliers.supplier.identification_number_too_long",
                $"The supplier identification number cannot exceed {NumberMaxLength} characters.")
            : new SupplierIdentification(type, trimmed);
    }
}
