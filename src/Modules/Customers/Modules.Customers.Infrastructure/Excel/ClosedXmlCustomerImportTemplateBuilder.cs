using ClosedXML.Excel;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Excel;

/// <summary>
/// Arma la plantilla de importacion con ClosedXML: la primera hoja con las once columnas de
/// <see cref="CustomerImportColumns"/> en la cabecera, una segunda hoja de referencia con los
/// nombres de departamento, de clasificacion y los tipos de identificacion validos, y desplegables
/// en las cuatro columnas que tienen un catalogo cerrado — para que quien llena el Excel no tenga
/// que adivinarlos ni pedirlos por otro canal, y no pueda tipear un valor que la importacion vaya a
/// rechazar despues.
///
/// Ciudad es la excepcion al desplegable plano: son 1122 municipios y cada uno pertenece a un solo
/// departamento, asi que un unico listado de 1122 opciones sin filtrar dejaria elegir una ciudad
/// que no corresponde al departamento de esa fila. En cambio, cada columna de
/// <see cref="CityListsSheetName"/> es la lista de ciudades de un departamento, con un rango con
/// nombre por departamento (<see cref="DepartmentNamedRangeKey"/>); la validacion de Ciudad de cada
/// fila resuelve ese rango con <c>INDIRECT(VLOOKUP(...))</c> contra el departamento que esa misma
/// fila declaro en su columna Departamento — el mismo patron de "desplegable en cascada" que usa
/// cualquier plantilla de Excel con dos catalogos dependientes.
/// </summary>
internal sealed class ClosedXmlCustomerImportTemplateBuilder : ICustomerImportTemplateBuilder
{
    private const string DataSheetName = "Clientes";

    private const string ReferenceSheetName = "Referencia";

    private const string CityListsSheetName = "ListasCiudades";

    // `AdjustToContents()` ajusta al ancho exacto del texto, sin margen: la cabecera queda pegada
    // al borde de la celda siguiente y es incomoda de leer. Este piso deja aire aunque el nombre
    // de la columna sea corto (ej. "Ciudad").
    private const double MinimumColumnWidth = 14;

    private const int IdentificationTypeColumn = 3;

    private const int DepartmentColumn = 8;

    private const int CityColumn = 9;

    private const int ClassificationColumn = 10;

    // Piso de filas con desplegable ademas de las que ya trae la fila en BuildWithRows: nadie
    // llena a mano un archivo de miles de filas, y una validacion sobre toda la columna (sin
    // limite) es mas pesada para Excel sin ninguna ventaja practica aca.
    private const int MinimumValidationRows = 500;

    public byte[] Build(
        IReadOnlyCollection<CustomerImportDepartmentOption> departments,
        IReadOnlyCollection<string> classificationNames,
        IReadOnlyCollection<string> identificationTypeValues,
        CancellationToken cancellationToken) =>
        BuildWithRows(
            departments, classificationNames, identificationTypeValues, [], cancellationToken);

    public byte[] BuildWithRows(
        IReadOnlyCollection<CustomerImportDepartmentOption> departments,
        IReadOnlyCollection<string> classificationNames,
        IReadOnlyCollection<string> identificationTypeValues,
        IReadOnlyList<CustomerImportRowData> rows,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();

        var dataSheet = workbook.Worksheets.Add(DataSheetName);
        for (var column = 0; column < CustomerImportColumns.Ordered.Count; column++)
        {
            dataSheet.Cell(1, column + 1).Value = CustomerImportColumns.Ordered[column];
        }

        for (var index = 0; index < rows.Count; index++)
        {
            WriteRow(dataSheet, index + 2, rows[index]);
        }

        dataSheet.SheetView.FreezeRows(1);
        dataSheet.Row(1).Style.Font.Bold = true;
        var dataColumns = dataSheet.Columns(1, CustomerImportColumns.Ordered.Count);
        dataColumns.AdjustToContents();
        ApplyMinimumWidth(dataColumns);

        cancellationToken.ThrowIfCancellationRequested();

        var sortedDepartments = departments
            .OrderBy(department => department.Name, StringComparer.Ordinal)
            .ToArray();
        var classifications = classificationNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var identificationTypes = identificationTypeValues
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        BuildReferenceSheet(workbook, sortedDepartments, classifications, identificationTypes);
        BuildCityListsSheet(workbook, sortedDepartments);

        var lastDataRow = Math.Max(rows.Count, MinimumValidationRows) + 1;
        ApplyDropdowns(
            dataSheet,
            lastDataRow,
            sortedDepartments.Length,
            classifications.Length,
            identificationTypes.Length);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    // El mismo orden que `CustomerImportColumns.Ordered` — una fila desalineada de su cabecera
    // sería peor que no tener datos precargados.
    private static void WriteRow(IXLWorksheet sheet, int excelRow, CustomerImportRowData row)
    {
        sheet.Cell(excelRow, 1).Value = row.Cuc ?? string.Empty;
        sheet.Cell(excelRow, 2).Value = row.Name;
        sheet.Cell(excelRow, 3).Value = row.IdentificationType;
        sheet.Cell(excelRow, 4).Value = row.IdentificationNumber;
        sheet.Cell(excelRow, 5).Value = row.Phone ?? string.Empty;
        sheet.Cell(excelRow, 6).Value = row.Email ?? string.Empty;
        sheet.Cell(excelRow, 7).Value = row.Address ?? string.Empty;
        sheet.Cell(excelRow, 8).Value = row.Department;
        sheet.Cell(excelRow, 9).Value = row.City;
        sheet.Cell(excelRow, 10).Value = row.Classification;
        sheet.Cell(excelRow, 11).Value = row.WithRetention ?? string.Empty;
    }

    private static void BuildReferenceSheet(
        XLWorkbook workbook,
        CustomerImportDepartmentOption[] sortedDepartments,
        string[] classifications,
        string[] identificationTypes)
    {
        var reference = workbook.Worksheets.Add(ReferenceSheetName);
        reference.Cell(1, 1).Value = "Departamentos validos";
        reference.Cell(1, 2).Value = "Tamanos validos";
        reference.Cell(1, 3).Value = "Tipos de identificacion validos";
        reference.Cell(1, 4).Value = "Clave interna (no editar)";
        reference.Row(1).Style.Font.Bold = true;

        var rowCount = Math.Max(
            sortedDepartments.Length, Math.Max(classifications.Length, identificationTypes.Length));
        for (var index = 0; index < rowCount; index++)
        {
            var row = index + 2;
            if (index < sortedDepartments.Length)
            {
                var department = sortedDepartments[index];
                reference.Cell(row, 1).Value = department.Name;
                reference.Cell(row, 4).Value = DepartmentNamedRangeKey(department.DivipolaCode);
            }

            if (index < classifications.Length)
            {
                reference.Cell(row, 2).Value = classifications[index];
            }

            if (index < identificationTypes.Length)
            {
                reference.Cell(row, 3).Value = identificationTypes[index];
            }
        }

        var referenceColumns = reference.Columns(1, 3);
        referenceColumns.AdjustToContents();
        ApplyMinimumWidth(referenceColumns);

        // La columna D solo existe para que la validacion de Ciudad la resuelva con VLOOKUP: no
        // es informacion que la persona que llena el Excel necesite ver.
        reference.Column(4).Hide();
    }

    // Una columna por departamento, con sus ciudades debajo, mas un rango con nombre por columna
    // — el mecanismo que INDIRECT usa desde la validacion de Ciudad. La hoja entera queda
    // "veryHidden": a diferencia de ocultar una hoja desde el menu contextual, esa opcion no
    // aparece hasta que alguien la muestra por codigo, asi nadie la borra ni la edita por error
    // pensando que es una hoja de trabajo vacia.
    private static void BuildCityListsSheet(
        XLWorkbook workbook, CustomerImportDepartmentOption[] sortedDepartments)
    {
        var lists = workbook.Worksheets.Add(CityListsSheetName);

        for (var column = 0; column < sortedDepartments.Length; column++)
        {
            var department = sortedDepartments[column];
            var cities = department.CityNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (cities.Length == 0)
            {
                continue;
            }

            for (var row = 0; row < cities.Length; row++)
            {
                lists.Cell(row + 1, column + 1).Value = cities[row];
            }

            var cityRange = lists.Range(1, column + 1, cities.Length, column + 1);
            workbook.DefinedNames.Add(DepartmentNamedRangeKey(department.DivipolaCode), cityRange);
        }

        lists.Visibility = XLWorksheetVisibility.VeryHidden;
    }

    private static void ApplyDropdowns(
        IXLWorksheet dataSheet,
        int lastDataRow,
        int departmentCount,
        int classificationCount,
        int identificationTypeCount)
    {
        if (identificationTypeCount > 0)
        {
            AddListValidation(
                dataSheet,
                IdentificationTypeColumn,
                lastDataRow,
                $"Referencia!$C$2:$C${identificationTypeCount + 1}");
        }

        if (departmentCount > 0)
        {
            AddListValidation(
                dataSheet, DepartmentColumn, lastDataRow, $"Referencia!$A$2:$A${departmentCount + 1}");
        }

        if (classificationCount > 0)
        {
            AddListValidation(
                dataSheet,
                ClassificationColumn,
                lastDataRow,
                $"Referencia!$B$2:$B${classificationCount + 1}");
        }

        // Ciudad no tiene una lista plana: cada fila solo puede elegir una ciudad del
        // departamento que esa misma fila declaro en su columna Departamento (ver
        // BuildCityListsSheet). La referencia a esa columna ($<letra>2) es deliberadamente
        // relativa: Excel la desplaza por fila para un rango de mas de una celda, igual que hace
        // con cualquier formula de validacion o de formato condicional.
        var departmentColumnLetter = dataSheet.Cell(1, DepartmentColumn).Address.ColumnLetter;
        var cityRange = dataSheet.Range(2, CityColumn, lastDataRow, CityColumn);
        var cityValidation = cityRange.CreateDataValidation();
        cityValidation.List(
            $"INDIRECT(VLOOKUP(${departmentColumnLetter}2,Referencia!$A:$D,4,FALSE))", true);
        cityValidation.IgnoreBlanks = true;
    }

    private static void AddListValidation(
        IXLWorksheet dataSheet, int column, int lastDataRow, string formula)
    {
        var range = dataSheet.Range(2, column, lastDataRow, column);
        var validation = range.CreateDataValidation();
        validation.List(formula, true);
        validation.IgnoreBlanks = true;
    }

    // El nombre de rango de Excel no admite tildes, comas ni puntos ("Bogotá, D.C." los tiene
    // todos), y ademas dos departamentos nunca comparten DivipolaCode — a diferencia del nombre,
    // que si tendria que sanearse sin garantia de que el resultado siga siendo unico.
    private static string DepartmentNamedRangeKey(string divipolaCode) =>
        "Dept_" + new string(divipolaCode.Where(char.IsLetterOrDigit).ToArray());

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
