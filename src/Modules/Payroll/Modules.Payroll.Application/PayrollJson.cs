using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modules.Payroll.Application;

/// <summary>
/// Cómo se guarda el JSON de nómina (resultados, foto legal, excluidos): camelCase y enums por nombre, igual que la API. Un solo
/// lugar para que lo escrito al liquidar se lea igual al componer el detalle.
/// </summary>
public static class PayrollJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Los excluidos de la última liquidación, tal como se guardan en <c>payroll.periods.excluded</c> y se muestran en Aprobado y Cerrado.</summary>
public static class PeriodExcluded
{
    public static string ToJson(IReadOnlyList<ExcludedEmployeeDto> excluded) =>
        JsonSerializer.Serialize(excluded, PayrollJson.Options);

    public static IReadOnlyList<ExcludedEmployeeDto> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<ExcludedEmployeeDto>>(json, PayrollJson.Options) ?? [];
}
