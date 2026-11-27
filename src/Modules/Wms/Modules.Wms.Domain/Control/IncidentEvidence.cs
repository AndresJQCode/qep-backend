namespace Modules.Wms.Domain.Control;

/// <summary>Un archivo o escaneo que respalda una novedad (spec §8.1). El archivo vive en Storage y se nombra por su id, sin FK.</summary>
public sealed class IncidentEvidence
{
    public const int RawValueMaxLength = 200;

    private IncidentEvidence()
    {
    }

    public Guid Id { get; private set; }

    public Guid FileId { get; private set; }

    public IncidentEvidenceKind Kind { get; private set; }

    /// <summary>Lo que se escaneó, si la evidencia es un escaneo.</summary>
    public string? RawValue { get; private set; }

    public int Position { get; private set; }

    public static IncidentEvidence Create(Guid fileId, IncidentEvidenceKind kind, string? rawValue)
    {
        if (fileId == Guid.Empty)
        {
            throw new WmsDomainException(WmsErrorCodes.IncidentEvidenceInvalid, "La evidencia necesita un archivo.");
        }

        var trimmed = string.IsNullOrWhiteSpace(rawValue) ? null : rawValue.Trim();
        return trimmed?.Length > RawValueMaxLength
            ? throw new WmsDomainException(
                WmsErrorCodes.IncidentEvidenceInvalid, $"El valor escaneado tiene a lo sumo {RawValueMaxLength} caracteres.")
            : new IncidentEvidence { Id = Guid.CreateVersion7(), FileId = fileId, Kind = kind, RawValue = trimmed };
    }

    internal void Place(int position) => Position = position;
}
