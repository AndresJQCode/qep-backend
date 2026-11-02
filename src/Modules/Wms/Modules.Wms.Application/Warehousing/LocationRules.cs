using System.Globalization;
using FluentValidation;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>
/// Parseo y validación de los campos de una ubicación que comparten el alta y la edición. Los enums viajan por nombre y se
/// parsean <b>sin</b> <c>ignoreCase</c>: <c>Enum.TryParse</c> aceptaría también «1» y un número fuera de rango, y el contrato es el
/// nombre exacto.
/// </summary>
internal static class LocationRules
{
    public static bool TryParse<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        return value is not null
            && Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal)
            && Enum.TryParse(value, ignoreCase: false, out result);
    }

    public static TEnum Parse<TEnum>(string value)
        where TEnum : struct, Enum =>
        TryParse<TEnum>(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"'{value}' is not a {typeof(TEnum).Name}; the validator should have rejected it.");

    public static LocationCapacity Capacity(decimal? maxWeightKg, decimal? maxVolumeM3, int? maxUnits) =>
        new(maxWeightKg, maxVolumeM3, maxUnits);

    public static IReadOnlyList<string> Classes(IReadOnlyList<string>? storageClasses) =>
        storageClasses?.Select(storageClass => storageClass.Trim()).ToList() ?? [];

    public static IRuleBuilderOptions<T, string> MustBeALocationCode<T>(this IRuleBuilder<T, string> rule) =>
        // El formato (P-4) lo valida el dominio y da wms.location.code_invalid; acá sólo lo que el dominio lanzaría como 500.
        rule.Must(code => !string.IsNullOrWhiteSpace(code) && code.Trim().Length <= LocationCodes.MaxLength)
            .WithMessage($"The code is required and has at most {LocationCodes.MaxLength} characters.");

    public static IRuleBuilderOptions<T, string> MustBeAnEnumName<T, TEnum>(this IRuleBuilder<T, string> rule)
        where TEnum : struct, Enum =>
        rule.Must(value => TryParse<TEnum>(value, out _))
            .WithMessage($"Must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");

    /// <summary>Tope superior por la precisión de la columna (peso <c>numeric(12,3)</c>); sin él, un número que no cabe es 22003 y
    /// 500. Lo negativo sigue siendo del dominio, que da <c>wms.location.capacity_invalid</c>.</summary>
    public static IRuleBuilderOptions<T, decimal?> MustFitTheWeightColumn<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.LessThanOrEqualTo(MaxWeightKg).WithMessage($"The weight cannot exceed {MaxWeightKg}.");

    /// <summary>Ídem para el volumen (<c>numeric(12,6)</c>).</summary>
    public static IRuleBuilderOptions<T, decimal?> MustFitTheVolumeColumn<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.LessThanOrEqualTo(MaxVolumeM3).WithMessage($"The volume cannot exceed {MaxVolumeM3}.");

    private const decimal MaxWeightKg = 999_999_999.999m;
    private const decimal MaxVolumeM3 = 999_999.999999m;

    public static IRuleBuilderOptions<T, int> MustBeAPutawayPriority<T>(this IRuleBuilder<T, int> rule) =>
        rule.GreaterThanOrEqualTo(0);

    public static IRuleBuilderOptions<T, string> MustBeAStorageClass<T>(this IRuleBuilderInitialCollection<T, string> rule) =>
        rule.Must(storageClass => !string.IsNullOrWhiteSpace(storageClass) && storageClass.Trim().Length <= LocationCodes.MaxLength)
            .WithMessage($"A storage class is required and has at most {LocationCodes.MaxLength} characters.");

    /// <summary>Los campos editables como texto, para auditar sólo los que cambiaron (el reporte pregunta «qué campo»).</summary>
    public static Dictionary<string, string?> Snapshot(Location location) => new()
    {
        ["parentId"] = location.ParentId?.Value.ToString(),
        ["code"] = location.Code,
        ["path"] = location.Path,
        ["kind"] = location.Kind.ToString(),
        ["role"] = location.Role.ToString(),
        ["allowsStorage"] = location.AllowsStorage ? "true" : "false",
        ["allowsPicking"] = location.AllowsPicking ? "true" : "false",
        ["maxWeightKg"] = location.Capacity.MaxWeightKg?.ToString(CultureInfo.InvariantCulture),
        ["maxVolumeM3"] = location.Capacity.MaxVolumeM3?.ToString(CultureInfo.InvariantCulture),
        ["maxUnits"] = location.Capacity.MaxUnits?.ToString(CultureInfo.InvariantCulture),
        ["putawayPriority"] = location.PutawayPriority.ToString(CultureInfo.InvariantCulture),
        ["storageClasses"] = string.Join(',', location.StorageClasses),
        ["status"] = location.Status.ToString(),
    };

    public static Dictionary<string, string?> Diff(Dictionary<string, string?> before, Dictionary<string, string?> after) =>
        after.Where(field => before[field.Key] != field.Value).ToDictionary(field => field.Key, field => field.Value);
}
