namespace Modules.Quotations.Domain;

/// <summary>Qué listado se exporta. Viaja como texto en la tabla y en los eventos: Notifications
/// lo usa para nombrar el tipo en el correo.</summary>
public enum ExportJobKind
{
    Quotations,
    Orders,
}
