namespace Modules.Customers.Application;

/// <summary>
/// Arma el archivo <c>.xlsx</c> de la plantilla de importacion (Fase 6): las diez columnas de
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
        IReadOnlyCollection<string> departmentNames,
        IReadOnlyCollection<string> classificationNames,
        CancellationToken cancellationToken);
}
