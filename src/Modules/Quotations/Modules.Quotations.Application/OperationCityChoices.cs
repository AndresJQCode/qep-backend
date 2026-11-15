using System.Text.Json;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Lee el <c>operationCityId</c> de un cuerpo (plan P1, spec ReteICA por empresa §9.4): ausente es <c>Keep</c>, <c>null</c>
/// es <c>Company</c> («el de la empresa») y un GUID es <c>Explicit</c>. Cualquier otra cosa —un texto que no es GUID, un
/// número, un objeto— es <c>{prefijo}.billing.operation_city_invalid</c>, el mismo código que un municipio que no existe.
/// </summary>
public static class OperationCityChoices
{
    /// <param name="codePrefix"><c>quotation</c> u <c>order</c>.</param>
    public static OperationCityChoice From(JsonElement value, string codePrefix) => value.ValueKind switch
    {
        JsonValueKind.Undefined => OperationCityChoice.Keep,
        JsonValueKind.Null => OperationCityChoice.Company,
        JsonValueKind.String when value.TryGetGuid(out var cityId) && cityId != Guid.Empty =>
            OperationCityChoice.Explicit(cityId),
        _ => throw new QuotationsDomainException(
            $"{codePrefix}.billing.operation_city_invalid",
            "The operation municipality must be a municipality id or null."),
    };
}
