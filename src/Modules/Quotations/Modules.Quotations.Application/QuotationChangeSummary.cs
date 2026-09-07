using System.Globalization;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Redacta el texto corto que queda en <c>quotation_history.details</c>: <b>qué</b> cambió en cada
/// operación, no sólo que hubo una.
///
/// La línea de tiempo ya guardaba quién y cuándo; sin esto, cinco ediciones seguidas son cinco
/// filas idénticas que dicen "Edited" y no responden la única pregunta que se le hace a un
/// historial —"¿qué tocaron?"—. El texto es para leer, no para parsear: quien necesite el dato
/// estructurado tiene la cotización y la auditoría transversal.
///
/// En español rioplatense como el resto del copy que ve una persona, y **resumido**: la columna
/// admite 500 caracteres y una fila de historial se lee de un vistazo, así que se enumera qué
/// campos se tocaron y no sus valores viejos y nuevos, salvo donde el valor <em>es</em> la noticia
/// (el cliente, la cantidad de una línea, la moneda).
///
/// <b>Nunca un id ni un dato sensible.</b> Lo lee una persona, no un sistema: un GUID no le dice
/// nada a quien audita y un número de cuenta completo no tiene por qué quedar repetido en cada
/// fila del historial. Las referencias se resuelven a nombre antes de llegar acá, y la cuenta se
/// enmascara (<see cref="MaskAccount"/>).
/// </summary>
public static class QuotationChangeSummary
{
    /// <summary>El ancho de <c>quotation_history.details</c>. Un resumen que se pase se recorta
    /// acá y no en PostgreSQL, que respondería con un 500 en vez de guardar la fila.</summary>
    public const int MaxLength = 500;

    public static string Created(string? clientName, QuotationCurrency currency) =>
        Trim(clientName is null
            ? $"Cotización creada en {currency.ToCode()}."
            : $"Cotización creada para {clientName}, en {currency.ToCode()}.");

    public static string ItemAdded(string productName, decimal quantity) =>
        Trim($"Agregó {productName} x{Number(quantity)}.");

    public static string ItemQuantityChanged(
        string productName, decimal from, decimal to) =>
        Trim($"Cambió la cantidad de {productName}: {Number(from)} → {Number(to)}.");

    public static string ItemRemoved(string productName) =>
        Trim($"Quitó {productName}.");

    public static string ClientChanged(string? from, string? to) =>
        Trim($"Cambió el cliente: {from ?? "sin cliente"} → {to ?? "sin cliente"}. " +
             "Se borraron los datos propios de facturación y envío.");

    public static string Sent() => "Enviada al cliente con su PDF.";

    public static string Resent() => "Reenviada al cliente con su PDF.";

    public static string Voided() => "Anulada.";

    public static string Expired() => "Vencida automáticamente al pasar su vigencia.";

    public static string ConvertedToSale(string saleNumber) =>
        Trim($"Convertida en la venta {saleNumber}.");

    /// <summary>
    /// Qué campos del encabezado cambiaron entre dos fotos del mismo agregado. Devuelve
    /// <c>null</c> cuando no cambió nada: el <c>PATCH</c> del editor se manda entero en cada
    /// guardado, y una fila "no cambió nada" por cada vez que alguien apretó Guardar convierte el
    /// historial en ruido.
    /// </summary>
    public static string? HeaderChanged(
        QuotationHeaderSnapshot before, QuotationHeaderSnapshot after)
    {
        var changes = new List<string>();

        if (before.ValidUntil != after.ValidUntil)
        {
            // "vigencia (sin vigencia)" se leia como un error de redaccion. Borrarla es un gesto
            // propio y se dice como tal.
            changes.Add(after.ValidUntil is null
                ? "quitó la vigencia"
                : $"vigencia ({Date(after.ValidUntil)})");
        }

        if (before.PaymentMethod != after.PaymentMethod)
        {
            changes.Add($"forma de pago ({after.PaymentMethod ?? "sin especificar"})");
        }

        if (before.Notes != after.Notes)
        {
            changes.Add(after.Notes is null ? "nota (borrada)" : "nota");
        }

        // "Propios" y "los del cliente" es la distinción que hace la pantalla (el switch), así que
        // es la que entiende quien lee esto — más que "se creó/borró una fila en parties".
        AppendParty(changes, "facturación", before.Billing, after.Billing);
        AppendParty(changes, "envío", before.Shipping, after.Shipping);

        if (before.BillingAccount != after.BillingAccount)
        {
            changes.Add(after.BillingAccount is null
                ? "quitó la cuenta de cobro"
                : $"cuenta de cobro ({MaskAccount(after.BillingAccount)})");
        }

        // Va al final y aparte: cambiar de moneda revalorizó todas las líneas, que es una
        // consecuencia mucho más grande que el resto de la lista.
        if (before.Currency != after.Currency)
        {
            changes.Add(
                $"moneda ({before.Currency.ToCode()} → {after.Currency.ToCode()}), " +
                "con los precios de todas las líneas revalorizados");
        }

        return changes.Count == 0 ? null : Trim($"Editó {string.Join(", ", changes)}.");
    }

    private static void AppendParty(
        List<string> changes, string label, string? before, string? after)
    {
        if (before == after) return;

        changes.Add(after is null
            ? $"{label} (vuelve a los datos del cliente)"
            : before is null
                ? $"{label} (ahora con datos propios)"
                : $"datos de {label}");
    }

    /// <summary>
    /// El banco y los últimos cuatro dígitos, no el número entero: alcanza para distinguir dos
    /// cuentas del mismo banco, que es lo único que el historial necesita decir, sin dejar el
    /// número completo repetido en cada fila.
    /// </summary>
    private static string MaskAccount(QuotationBillingAccountSummary account) =>
        account.AccountNumber.Length <= 4
            ? $"{account.BankName} {account.Currency}"
            : $"{account.BankName} ···{account.AccountNumber[^4..]} {account.Currency}";

    private static string Date(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    // Sin ceros a la derecha: "x3" y no "x3,00". La cantidad es decimal porque hay productos que
    // se venden fraccionados, pero la mayoría son enteros y el historial se lee mejor así.
    private static string Number(decimal value) =>
        value == decimal.Truncate(value)
            ? decimal.Truncate(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Trim(string summary) =>
        summary.Length <= MaxLength ? summary : summary[..(MaxLength - 1)] + "…";
}

/// <summary>
/// Una foto del encabezado de la cotización, para comparar antes y después de
/// <c>Quotation.UpdateDetails</c>. Las partes se reducen a un texto porque lo que el historial
/// necesita saber es si esa parte tiene datos propios y cuáles a grandes rasgos, no cada campo.
/// </summary>
public sealed record QuotationHeaderSnapshot(
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    string? Billing,
    string? Shipping,
    QuotationBillingAccountSummary? BillingAccount,
    QuotationCurrency Currency)
{
    public static QuotationHeaderSnapshot Of(Quotation quotation) => new(
        quotation.ValidUntil,
        quotation.PaymentMethod,
        quotation.Notes,
        Describe(quotation.Billing),
        Describe(quotation.Shipping),
        quotation.BillingAccount is { } account
            ? new QuotationBillingAccountSummary(
                account.BankName, account.AccountNumber, account.Currency)
            : null,
        quotation.Currency);

    private static string? Describe(QuotationParty? party) =>
        party is null
            ? null
            : string.Join(
                '|',
                party.Name,
                party.Phone,
                party.Email,
                party.Address,
                party.DepartmentId,
                party.CityId);
}

/// <summary>La cuenta de cobro dentro de una foto del encabezado. Es un record para que la
/// comparación entre antes y después sea por valor, y para que el enmascarado tenga sus partes
/// separadas en vez de un texto ya armado.</summary>
public sealed record QuotationBillingAccountSummary(
    string BankName,
    string AccountNumber,
    string Currency);
