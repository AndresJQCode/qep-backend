namespace Modules.Scheduling.Domain;

public enum BlockKind
{
    /// <summary>Se repite por día de la semana, con vigencia opcional.</summary>
    Weekly,

    /// <summary>Una sola fecha (una jornada extendida, un sábado de diciembre).</summary>
    OneOff,
}
