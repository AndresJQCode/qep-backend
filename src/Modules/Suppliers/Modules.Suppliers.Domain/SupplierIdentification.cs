using BuildingBlocks.Domain.Identification;

namespace Modules.Suppliers.Domain;

/// <summary>
/// El documento con el que se identifica al proveedor: tipo y número, juntos, porque el par es
/// lo que se muestra y se busca, y separarlos permite armar un proveedor con el tipo de uno y el
/// número de otro sin queja del compilador. Como en Customers, el par es la **clave de unicidad** del
/// proveedor dentro del tenant, también entre inactivos (spec D-5): la arbitra el índice
/// <c>IX_suppliers_tenant_identification</c> y la traduce el UnitOfWork a
/// <c>suppliers.supplier.identification_taken</c>, no este record.
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
        if (trimmed.Length > NumberMaxLength)
        {
            throw new SuppliersDomainException(
                "suppliers.supplier.identification_number_too_long",
                $"The supplier identification number cannot exceed {NumberMaxLength} characters.");
        }

        // NIT, CC y CE solo llevan dígitos (con punto, guion y espacio). Se bloquea siempre, también un
        // número viejo que se reenvía sin cambios: vive solo en el alta y la edición, nunca al leer del
        // repositorio, así que un proveedor ya guardado con letras se sigue leyendo.
        if (!DocumentNumberFormat.IsValidFor(type.ToWire(), trimmed))
        {
            throw new SuppliersDomainException(
                "suppliers.supplier.identification_number_not_numeric",
                DocumentNumberFormat.NumericTypesMessage);
        }

        return new SupplierIdentification(type, trimmed);
    }
}
