using System.Globalization;
using ClosedXML.Excel;
using Modules.Customers.Application;
using Modules.Tenancy.Application;

namespace Modules.Customers.Infrastructure.Excel;

/// <summary>
/// Arma el Excel del padron con ClosedXML.
///
/// Las columnas de <see cref="CustomerImportColumns"/> van primero y en su orden exacto (el CUC
/// incluido), y las cuatro propias al final. Esa forma es deliberada: el importador ubica las
/// columnas **por nombre de cabecera** (ver <c>ClosedXmlCustomerImporter</c>), asi que un archivo
/// exportado se puede corregir y volver a importar sin editarle la estructura, y las columnas de
/// mas no le molestan.
///
/// Cada celda se escribe **por el nombre de su cabecera** (<see cref="RowValues"/>), no por un
/// indice fijo: desde <c>a054cfd</c> la lista de importacion crecio (CUC, razon social, excedente de
/// IVA) y los indices fijos dejaron las cabeceras corridas respecto a los datos.
/// </summary>
internal sealed class ClosedXmlCustomerExportBuilder : ICustomerExportBuilder
{
    private const string DataSheetName = "Clientes";

    // Mismo piso que la plantilla de importacion: `AdjustToContents()` ajusta al ancho exacto del
    // texto y deja la cabecera pegada al borde de la celda siguiente.
    private const double MinimumColumnWidth = 14;

    private const string ActiveColumn = "Activo";
    private const string CreatedAtColumn = "Creado";
    private const string UpdatedAtColumn = "Actualizado";
    private const string CompletenessColumn = "Estado de la ficha";

    private static readonly IReadOnlyList<string> Columns =
    [
        .. CustomerImportColumns.Ordered,
        ActiveColumn,
        CreatedAtColumn,
        UpdatedAtColumn,
        // Spec 2026-10-10 §5.2: al final, no en medio.
        CompletenessColumn
    ];

    public CustomerExportFile Build(
        IReadOnlyList<CustomerDto> customers,
        TenantCalendar calendar,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(DataSheetName);

        for (var column = 0; column < Columns.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = Columns[column];
        }

        for (var index = 0; index < customers.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteRow(sheet, index + 2, customers[index], calendar);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Row(1).Style.Font.Bold = true;
        var columns = sheet.Columns(1, Columns.Count);
        columns.AdjustToContents();
        ApplyMinimumWidth(columns);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        // En la hora del tenant (spec 2026-09-17, punto 8a): es la hora que la persona ve en su reloj.
        var generatedAtLocal = calendar.ToLocal(calendar.UtcNow);
        return new CustomerExportFile(
            stream.ToArray(),
            $"clientes-{generatedAtLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.xlsx");
    }

    // Cada valor va en la columna de su cabecera, en el orden de `Columns`. Una cabecera sin valor
    // es un error de programacion (alguien sumo una columna a la importacion y no al export): falla
    // aca y no deja un archivo con las columnas corridas.
    private static void WriteRow(IXLWorksheet sheet, int excelRow, CustomerDto customer, TenantCalendar calendar)
    {
        var values = RowValues(customer, calendar);
        for (var column = 0; column < Columns.Count; column++)
        {
            sheet.Cell(excelRow, column + 1).Value = values.TryGetValue(Columns[column], out var value)
                ? value
                : throw new InvalidOperationException($"The export has no value for column '{Columns[column]}'.");
        }
    }

    // Los textos de las columnas compartidas son los que el importador espera leer: el nombre de la
    // clasificacion y del departamento/ciudad, no sus ids. Un incompleto deja vacio lo que no tiene
    // (spec 2026-10-10 §5.2).
    private static Dictionary<string, string> RowValues(CustomerDto customer, TenantCalendar calendar) => new(StringComparer.Ordinal)
    {
        [CustomerImportColumns.Cuc] = customer.Cuc ?? string.Empty,
        [CustomerImportColumns.Name] = customer.Name,
        [CustomerImportColumns.BusinessName] = customer.BusinessName ?? string.Empty,
        [CustomerImportColumns.IdentificationType] = customer.IdentificationType ?? string.Empty,
        [CustomerImportColumns.IdentificationNumber] = customer.IdentificationNumber ?? string.Empty,
        [CustomerImportColumns.Phone] = customer.Phone ?? string.Empty,
        [CustomerImportColumns.Email] = customer.Email ?? string.Empty,
        [CustomerImportColumns.Address] = customer.Address ?? string.Empty,
        // Un cliente de afuera no tiene departamento DIVIPOLA —la columna queda vacia— y su ciudad
        // es la escrita a mano. Se exporta igual que uno colombiano a proposito: el archivo es
        // para leerlo, y una fila con la ciudad en blanco esconderia al cliente. Que el importador
        // no pueda volver a subir esa fila es otro asunto, y esta dicho en ImportCustomers: el
        // alta de un cliente de afuera es por formulario.
        [CustomerImportColumns.Department] = customer.Department?.Name ?? string.Empty,
        [CustomerImportColumns.City] = customer.City?.Name ?? customer.CityName ?? string.Empty,
        [CustomerImportColumns.Classification] = customer.Classification?.Name ?? string.Empty,
        // "Si"/"No" y no true/false: es el vocabulario que el importador lee y el que ve la persona
        // que abre el archivo.
        [CustomerImportColumns.WithRetention] = customer.WithRetention ? "Si" : "No",
        [CustomerImportColumns.VatSurplus] = customer.VatSurplus ? "Si" : "No",
        [ActiveColumn] = customer.IsActive ? "Si" : "No",
        // Como texto y no como fecha de Excel: una celda de fecha se muestra según la configuración
        // regional de quien abre el archivo, y ahí 03/04 deja de ser una fecha sola. En la hora del
        // tenant, al minuto y sin offset (spec 2026-09-17, punto 8a).
        [CreatedAtColumn] = calendar.ToLocal(customer.CreatedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        [UpdatedAtColumn] = calendar.ToLocal(customer.UpdatedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        [CompletenessColumn] = customer.IsComplete ? "Completa" : "Incompleta",
    };

    private static void ApplyMinimumWidth(IXLColumns columns)
    {
        foreach (var column in columns)
        {
            if (column.Width < MinimumColumnWidth)
            {
                column.Width = MinimumColumnWidth;
            }
        }
    }
}
