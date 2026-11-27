using BuildingBlocks.Domain.Identification;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// La regla de formato del número de documento de un empleado: CC, CE, PEP y PPT solo admiten dígitos, punto y
/// guion (sin espacios: el número de Hr nunca los lleva); PA, CD y SC son alfanuméricos y no se tocan. La comparten
/// el validador del alta/edición y la importación del Excel. Es la versión de Hr de <see cref="DocumentNumberFormat"/>,
/// que cubre NIT, CC y CE: Hr tiene sus propios tipos (PEP, PPT) y no admite espacios.
/// </summary>
internal static class HrDocumentNumberRules
{
    public const string NumericTypesMessage = "El número de documento solo puede tener números para CC, CE, PEP y PPT.";

    public static bool RequiresDigits(HrDocumentType type) =>
        type is HrDocumentType.CC or HrDocumentType.CE or HrDocumentType.PEP or HrDocumentType.PPT;

    public static bool IsValidFor(HrDocumentType type, string? number) =>
        !RequiresDigits(type) || DocumentNumberFormat.IsNumeric(number, allowSpaces: false);
}
