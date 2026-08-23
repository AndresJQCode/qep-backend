namespace Modules.Customers.Application;

/// <summary>
/// Las diez columnas que la importacion masiva de clientes (Fase 5) espera y que la plantilla
/// descargable (Fase 6) genera — el contrato exacto entre las dos fases. En espanol y en este
/// orden: es el mismo vocabulario que ya usa el resto del sistema, y el orden es el que la
/// plantilla escribe.
/// </summary>
public static class CustomerImportColumns
{
    public const string Name = "Nombre";

    public const string IdentificationType = "TipoIdentificacion";

    public const string IdentificationNumber = "NumeroIdentificacion";

    public const string Phone = "Telefono";

    public const string Email = "Email";

    public const string Address = "Direccion";

    public const string Department = "Departamento";

    public const string City = "Ciudad";

    public const string Classification = "Clasificacion";

    public const string WithRetention = "ConRetencion";

    public static readonly IReadOnlyList<string> Ordered =
    [
        Name,
        IdentificationType,
        IdentificationNumber,
        Phone,
        Email,
        Address,
        Department,
        City,
        Classification,
        WithRetention
    ];
}

/// <summary>
/// Una fila del Excel de importacion, **sin ninguna validacion de negocio todavia**: cada celda
/// llega como el texto crudo que la persona tipeo (recortado y vacio-a-null, nada mas), y
/// <c>RowNumber</c> es el numero de fila real del Excel (arranca en 2: la fila 1 es la cabecera),
/// para que un error se pueda reportar con el mismo numero que la persona ve al abrir el archivo.
///
/// Decidir si una fila es valida —campos obligatorios, formato, si el departamento/ciudad/
/// clasificacion existen— es una regla de negocio y vive en Application
/// (<c>ExcelCustomerRowRules</c> y <c>ImportCustomersHandler</c>), no aca.
/// </summary>
public sealed record ExcelCustomerRow(
    int RowNumber,
    string? Name,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Department,
    string? City,
    string? Classification,
    string? WithRetention);

/// <summary>
/// El resultado de parsear un Excel: si la cabecera trae las diez columnas esperadas y, si las
/// trae, las filas de datos ya leidas. Cuando <see cref="HasExpectedColumns"/> es falso,
/// <see cref="Rows"/> viene vacio — no tiene sentido leer datos de columnas que no se pudieron
/// ubicar, y el llamador trata eso como un archivo estructuralmente invalido, no como un reporte
/// por fila.
/// </summary>
public sealed record ExcelCustomerImportFile(bool HasExpectedColumns, IReadOnlyList<ExcelCustomerRow> Rows);

/// <summary>
/// Lee un Excel de clientes y devuelve sus filas en crudo. Puerto en Application, adaptador en
/// Infrastructure (<c>ClosedXmlCustomerImporter</c>) — el mecanismo de lectura (que libreria, que
/// formato de archivo) es un detalle de infraestructura, igual que <c>ICucGenerator</c> separa la
/// concurrencia del contador de las reglas que lo usan.
///
/// Solo hace parseo **estructural**: ¿tiene las columnas esperadas la cabecera? ¿esta vacio el
/// archivo? Ninguna regla de negocio (formato de email, si la ciudad existe, duplicados) vive aca.
/// </summary>
public interface IExcelCustomerImporter
{
    ExcelCustomerImportFile Parse(Stream content, CancellationToken cancellationToken);
}
