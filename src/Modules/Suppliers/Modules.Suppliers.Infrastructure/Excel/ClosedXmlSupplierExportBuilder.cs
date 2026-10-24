using System.Globalization;
using ClosedXML.Excel;
using Modules.Suppliers.Application;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Infrastructure.Excel;

/// <summary>
/// Arma el Excel del directorio con ClosedXML (spec 2026-10-23 §6.4). Cuentas y contactos son una
/// sola celda con una línea por elemento, para que la fila siga siendo un proveedor.
/// </summary>
internal sealed class ClosedXmlSupplierExportBuilder : ISupplierExportBuilder
{
    private const string DataSheetName = "Proveedores";

    // `AdjustToContents()` ajusta al ancho exacto del texto y deja la cabecera pegada a la celda
    // siguiente; mismo piso que el export de clientes.
    private const double MinimumColumnWidth = 14;

    private const int BankAccountsColumn = 13;

    private const int ContactsColumn = 14;

    private static readonly string[] Columns =
    [
        "Nombre",
        "Razón social",
        "Tipo de documento",
        "Documento",
        "Teléfono",
        "Correo",
        "Dirección",
        "País",
        "Departamento",
        "Ciudad",
        "Categoría",
        "Plazo (días)",
        "Cuentas",
        "Contactos",
        "Estado"
    ];

    public SupplierExportFile Build(
        IReadOnlyList<SupplierResponse> suppliers,
        TenantCalendar calendar,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(DataSheetName);

        for (var column = 0; column < Columns.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = Columns[column];
        }

        for (var index = 0; index < suppliers.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteRow(sheet, index + 2, suppliers[index]);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Row(1).Style.Font.Bold = true;
        var columns = sheet.Columns(1, Columns.Length);
        columns.AdjustToContents();
        foreach (var column in columns)
        {
            if (column.Width < MinimumColumnWidth)
            {
                column.Width = MinimumColumnWidth;
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        // En la hora del tenant: es la que la persona ve en su reloj.
        var generatedAtLocal = calendar.ToLocal(calendar.UtcNow);
        return new SupplierExportFile(
            stream.ToArray(),
            $"proveedores-{generatedAtLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.xlsx");
    }

    private static void WriteRow(IXLWorksheet sheet, int row, SupplierResponse supplier)
    {
        string[] values =
        [
            supplier.Name,
            supplier.BusinessName ?? string.Empty,
            supplier.IdentificationType,
            supplier.IdentificationNumber,
            supplier.Phone ?? string.Empty,
            supplier.Email ?? string.Empty,
            supplier.Address ?? string.Empty,
            supplier.Country,
            // Fuera de Colombia no hay departamento DIVIPOLA y la ciudad es la escrita a mano; se
            // exporta igual a propósito: una fila con la ciudad en blanco escondería al proveedor.
            supplier.Department?.Name ?? string.Empty,
            supplier.City?.Name ?? supplier.CityName ?? string.Empty,
            supplier.Category ?? string.Empty,
            // Como texto, igual que el resto de celdas: el archivo es para leerlo.
            supplier.PaymentTermDays.ToString(CultureInfo.InvariantCulture),
            string.Join('\n', supplier.BankAccounts.Select(account =>
                $"{account.BankName} – {account.AccountNumber} ({account.Currency})")),
            string.Join('\n', supplier.Contacts.Select(ContactLine)),
            supplier.IsActive ? "Activo" : "Inactivo"
        ];

        for (var column = 0; column < values.Length; column++)
        {
            sheet.Cell(row, column + 1).Value = values[column];
        }

        sheet.Cell(row, BankAccountsColumn).Style.Alignment.WrapText = true;
        sheet.Cell(row, ContactsColumn).Style.Alignment.WrapText = true;
    }

    // «Nombre, Cargo, Teléfono, Correo» omitiendo los vacíos: sin comas colgando.
    private static string ContactLine(SupplierContactPayload contact) =>
        string.Join(
            ", ",
            new[] { contact.Name, contact.Role, contact.Phone, contact.Email }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
}
