using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

/// <summary>
/// La huella (R-16): SHA-256 en hexadecimal minúscula de lo que el motor lee de un empleado, sin la foto legal (que queda fija
/// aunque Hr cambie) ni los nombres de conceptos y centros de trabajo (son etiquetas, no insumos). Si lo que hay hoy en Hr, las capturas o la configuración da otra huella que la guardada al liquidar, el
/// período trae <c>sourceChanged</c>. Los decimales se normalizan: <c>100</c> y <c>100.00</c> son la misma huella.
/// </summary>
public static class InputFingerprint
{
    // Decision 4 (2026-11-22): names are labels. Ids, hours, amounts, caps and the nature of each concept (kind, system key,
    // multiplier, mode, salary-constitutive) stay in; the concept and workplace names leave.
    private static readonly string[] ListsWithConceptNames = ["overtime", "incomes", "deductions", "recurring"];

    public static string Of(EngineInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var node = JsonSerializer.SerializeToNode(input, PayrollJson.Options)!.AsObject();
        node.Remove("legal");

        // An input without vacations must hash exactly as it did before EngineInput gained the list: otherwise every period
        // liquidated at deploy time would report a false «sourceChanged».
        if (node["vacations"] is JsonArray { Count: 0 })
        {
            node.Remove("vacations");
        }

        foreach (var list in ListsWithConceptNames)
        {
            foreach (var item in node[list]!.AsArray())
            {
                item!.AsObject().Remove("conceptName");
            }
        }

        node["workplace"]!.AsObject().Remove("name");
        return Hash(Canonical(node));
    }

    /// <summary>La huella del conjunto: las de los liquidables y los motivos de los excluidos y bloqueados, ordenados por id.</summary>
    public static string OfPeriod(
        IEnumerable<(Guid EmployeeId, string Fingerprint)> liquidable,
        IEnumerable<(Guid EmployeeId, string Reason)> excludedOrBlocked)
    {
        ArgumentNullException.ThrowIfNull(liquidable);
        ArgumentNullException.ThrowIfNull(excludedOrBlocked);

        var text = new StringBuilder();
        foreach (var (employeeId, fingerprint) in liquidable.OrderBy(item => item.EmployeeId))
        {
            text.Append("L:").Append(employeeId).Append(':').Append(fingerprint).Append('\n');
        }

        foreach (var (employeeId, reason) in excludedOrBlocked.OrderBy(item => item.EmployeeId))
        {
            text.Append("X:").Append(employeeId).Append(':').Append(reason).Append('\n');
        }

        return Hash(text.ToString());
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Numbers are written with their canonical decimal text (no trailing zeros); everything else keeps the serializer's order,
    // which follows the record's declaration order and is therefore stable.
    private static string Canonical(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node, builder);
        return builder.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (name, child) in obj)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(name)).Append(':');
                    Write(child, builder);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    Write(array[index], builder);
                }

                builder.Append(']');
                break;
            case JsonValue value:
                WriteValue(value, builder);
                break;
        }
    }

    private static void WriteValue(JsonValue value, StringBuilder builder)
    {
        if (value.TryGetValue<decimal>(out var number))
        {
            builder.Append(Normalize(number));
            return;
        }

        builder.Append(value.ToJsonString());
    }

    private static string Normalize(decimal number) =>
        number.ToString("G29", CultureInfo.InvariantCulture);
}
