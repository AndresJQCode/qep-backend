using System.Text.Json.Nodes;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>
/// Owner, 2026-11-25: cuando Siigo rechaza con <c>invalid_total_payments</c> por no más de <see cref="SiigoInvoiceBuilder.Tolerance"/>, la
/// diferencia es de su redondeo, que no siempre se puede predecir (POB959: un centavo con IVA y ReteFuente; ningún cálculo por línea ni
/// sobre el total lo da). El último pago la absorbe. Más que eso es otra base, tasa o configuración y se deja rechazada a la vista.
/// </summary>
internal static class SiigoPaymentRounding
{
    /// <returns>La petición con el último pago ajustado, o <c>null</c> si el rechazo no es sólo de redondeo.</returns>
    public static string? Adjust(string requestJson, SiigoInvoiceOutcome.Rejected rejected)
    {
        if (rejected.ExpectedPaymentsTotal is not { } expected
            || decimal.Round(expected, 2) != expected
            || rejected.Errors.Count == 0
            || !rejected.Errors.All(error => string.Equals(error.Code, SiigoApiErrors.InvalidTotalPayments, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (JsonNode.Parse(requestJson) is not JsonObject body || body["payments"] is not JsonArray { Count: > 0 } payments)
        {
            return null;
        }

        var values = payments.Select(payment => payment!["value"]!.GetValue<decimal>()).ToList();
        var difference = expected - values.Sum();
        var last = values[^1] + difference;
        if (difference == 0m || Math.Abs(difference) > SiigoInvoiceBuilder.Tolerance || last <= 0m)
        {
            return null;
        }

        payments[^1]!["value"] = last;
        return body.ToJsonString();
    }
}
