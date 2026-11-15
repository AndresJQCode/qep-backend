namespace Modules.Support.Domain;

/// <summary>Cómo se resolvió un entrante sin clasificar (plan E5 y E8). Se persiste por nombre; mientras está pendiente es null.
/// <see cref="Reassigned"/> es el que pasó solo al único ticket candidato que quedó, sin que nadie lo eligiera.</summary>
public enum SupportUnclassifiedResolution
{
    Assigned,
    Dismissed,
    Reassigned,
}
