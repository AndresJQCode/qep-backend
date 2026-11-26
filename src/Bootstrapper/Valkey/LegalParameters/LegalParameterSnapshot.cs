using System.Text.Json;
using Modules.Hr.Domain;

namespace Bootstrapper.Valkey.LegalParameters;

/// <summary>
/// La tabla de valores legales entera, lo que se guarda bajo <c>qep:legalparams:{ver}</c> (spec 2026-11-22, «Caché»): se cachea la
/// tabla y no el resultado por fecha, porque <c>Resolve</c> ya filtra por <c>EffectiveFrom</c> y cruzar de año no invalida nada. Viaja
/// como registros planos y se reconstruye con los constructores públicos de las entidades: la autoría (<c>UpdatedAt</c>) no la lee
/// ningún resolver y se queda en la base.
/// </summary>
internal sealed record LegalParameterSnapshot(
    IReadOnlyList<LegalParameterValue> Values,
    IReadOnlyList<LegalParameterBracket> Brackets)
{
    public string Serialize() =>
        JsonSerializer.Serialize(new Wire(
            Values.Select(value => new WireValue(value.Key.ToString(), value.EffectiveFrom, value.Value)).ToList(),
            Brackets
                .Select(bracket => new WireBracket(
                    bracket.Key.ToString(), bracket.EffectiveFrom, bracket.LowerBound, bracket.UpperBound, bracket.Rate))
                .ToList()));

    /// <exception cref="JsonException">El texto no es una tabla (otra forma, truncado).</exception>
    /// <exception cref="ArgumentException">Una clave que este despliegue no conoce: la escribió otra versión.</exception>
    public static LegalParameterSnapshot Deserialize(string json)
    {
        var wire = JsonSerializer.Deserialize<Wire>(json) ?? throw new JsonException("The legal parameter table is empty.");
        return new LegalParameterSnapshot(
            wire.Values.Select(value => new LegalParameterValue(
                Enum.Parse<LegalParameterKey>(value.Key), value.EffectiveFrom, value.Value)).ToList(),
            wire.Brackets.Select(bracket => new LegalParameterBracket(
                Enum.Parse<LegalBracketKey>(bracket.Key), bracket.EffectiveFrom, bracket.LowerBound, bracket.UpperBound, bracket.Rate))
                .ToList());
    }

    private sealed record Wire(List<WireValue> Values, List<WireBracket> Brackets);

    private sealed record WireValue(string Key, DateOnly EffectiveFrom, decimal Value);

    private sealed record WireBracket(string Key, DateOnly EffectiveFrom, decimal LowerBound, decimal? UpperBound, decimal Rate);
}
