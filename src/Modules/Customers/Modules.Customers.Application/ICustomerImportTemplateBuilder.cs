namespace Modules.Customers.Application;

/// <summary>
/// Arma el archivo <c>.xlsx</c> de la plantilla de importacion (Fase 6): las once columnas de
/// <see cref="CustomerImportColumns"/> en la primera hoja, y una hoja de referencia con los
/// nombres de departamento y de clasificacion validos, para que quien llena el Excel no tenga que
/// adivinarlos.
///
/// Puerto en Application, adaptador en Infrastructure (<c>ClosedXmlCustomerImportTemplateBuilder</c>)
/// — mismo criterio que <see cref="IExcelCustomerImporter"/>: la libreria concreta es un detalle
/// de infraestructura. No conoce ningun repositorio: recibe los nombres ya resueltos, que arma
/// <c>GetCustomerImportTemplateHandler</c>.
/// </summary>
public interface ICustomerImportTemplateBuilder
{
    byte[] Build(
        IReadOnlyCollection<CustomerImportDepartmentOption> departments,
        IReadOnlyCollection<string> classificationNames,
        IReadOnlyCollection<string> identificationTypeValues,
        CancellationToken cancellationToken);

    /// <summary>
    /// La misma plantilla, con filas ya cargadas — para "descargá un Excel con las filas que
    /// fallaron" del modal de errores de importación: la persona corrige ahí las celdas
    /// marcadas y reimporta ese archivo más chico, en vez del original completo (que volvería a
    /// duplicar las filas que ya se guardaron bien).
    /// </summary>
    byte[] BuildWithRows(
        IReadOnlyCollection<CustomerImportDepartmentOption> departments,
        IReadOnlyCollection<string> classificationNames,
        IReadOnlyCollection<string> identificationTypeValues,
        IReadOnlyList<CustomerImportRowData> rows,
        CancellationToken cancellationToken);
}

/// <summary>
/// Un departamento y los nombres de sus ciudades, tal como los necesita el builder para armar el
/// desplegable de Ciudad en cascada de la columna Departamento de esa misma fila.
/// <c>DivipolaCode</c> es la clave estable que arma el nombre del rango con nombre de Excel — el
/// nombre del departamento no sirve para eso: trae comas, puntos y tildes ("Bogotá, D.C.") que un
/// nombre de rango de Excel no acepta.
/// </summary>
public sealed record CustomerImportDepartmentOption(
    string Name, string DivipolaCode, IReadOnlyList<string> CityNames);
