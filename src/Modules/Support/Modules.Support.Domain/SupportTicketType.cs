namespace Modules.Support.Domain;

/// <summary>Un tipo de ticket del tenant (spec §7.5): su término en días hábiles, si es legal y si exige respuesta formal. Un ticket
/// copia estos valores al crearse o al cambiar de tipo, así que editar un tipo no mueve vencimientos ya calculados. Un tipo usado no
/// se borra: se desactiva.</summary>
public sealed class SupportTicketType
{
    public const int NameMaxLength = 80;

    public const int MaxTermBusinessDays = 365;

    // EF Core materializa por acá; Create es el único punto de entrada de código.
    private SupportTicketType()
    {
        Name = string.Empty;
    }

    public SupportTicketTypeId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Null: sin término, sin vencimiento (p. ej. «Soporte técnico»).</summary>
    public int? TermBusinessDays { get; private set; }

    public bool TermIsLegal { get; private set; }

    public bool RequiresFormalResponse { get; private set; }

    public bool IsPublished { get; private set; }

    public bool IsActive { get; private set; }

    public int Position { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static SupportTicketType Create(
        SupportTicketTypeId id,
        Guid tenantId,
        string name,
        int? termBusinessDays,
        bool termIsLegal,
        bool requiresFormalResponse,
        bool isPublished,
        int position,
        DateTimeOffset now)
    {
        var normalized = NormalizeName(name);
        EnsureTerm(termBusinessDays, termIsLegal);

        return new SupportTicketType
        {
            Id = id,
            TenantId = tenantId,
            Name = normalized,
            TermBusinessDays = termBusinessDays,
            TermIsLegal = termIsLegal,
            RequiresFormalResponse = requiresFormalResponse,
            IsPublished = isPublished,
            IsActive = true,
            Position = position,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Con los mismos valores no cambia nada ni sube la versión.</summary>
    public void Update(
        string name,
        int? termBusinessDays,
        bool termIsLegal,
        bool requiresFormalResponse,
        bool isPublished,
        bool isActive,
        DateTimeOffset now)
    {
        var normalized = NormalizeName(name);
        EnsureTerm(termBusinessDays, termIsLegal);

        if (normalized == Name && termBusinessDays == TermBusinessDays && termIsLegal == TermIsLegal &&
            requiresFormalResponse == RequiresFormalResponse && isPublished == IsPublished && isActive == IsActive)
        {
            return;
        }

        Name = normalized;
        TermBusinessDays = termBusinessDays;
        TermIsLegal = termIsLegal;
        RequiresFormalResponse = requiresFormalResponse;
        IsPublished = isPublished;
        IsActive = isActive;
        Touch(now);
    }

    public void MoveTo(int position, DateTimeOffset now)
    {
        if (position == Position)
        {
            return;
        }

        Position = position;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("A ticket type needs a name.", nameof(name));
        }

        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"A ticket type name has at most {NameMaxLength} characters.", nameof(name));
        }

        return trimmed;
    }

    // Spec §7.5: un tipo sin término no tiene vencimiento, así que no puede prometer un plazo legal.
    private static void EnsureTerm(int? termBusinessDays, bool termIsLegal)
    {
        if (termBusinessDays is { } term && (term < 1 || term > MaxTermBusinessDays))
        {
            throw new ArgumentOutOfRangeException(
                nameof(termBusinessDays), term, $"The term is between 1 and {MaxTermBusinessDays} business days.");
        }

        if (termIsLegal && termBusinessDays is null)
        {
            throw new ArgumentException("A ticket type without term cannot be legal.", nameof(termIsLegal));
        }
    }
}
