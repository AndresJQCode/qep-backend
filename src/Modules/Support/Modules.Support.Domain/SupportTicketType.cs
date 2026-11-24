namespace Modules.Support.Domain;

/// <summary>Un tipo de ticket del tenant (spec §7.5): su término en días hábiles, si es legal y si exige respuesta formal. Un ticket
/// copia estos valores al crearse o al cambiar de tipo, así que editar un tipo no mueve vencimientos ya calculados. Un tipo usado no
/// se borra: se desactiva.</summary>
public sealed class SupportTicketType
{
    public const int NameMaxLength = 80;

    public const int MaxTermBusinessDays = 365;

    /// <summary>Fase 4b (H6): el «siguiente paso» de la respuesta formal, en texto plano.</summary>
    public const int FormalNextStepMaxLength = SupportFormalNextStep.MaxLength;

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

    /// <summary>Fase 4b (H6): la sección 7 del PDF de la respuesta formal; pisa la del tenant. Null: usa la del tenant.</summary>
    public string? FormalNextStep { get; private set; }

    public bool IsPublished { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Spec 2026-11-22: el tipo de las tareas internas. Uno por tenant, sin término, sin publicar y siempre activo; no se
    /// edita ni se borra (<see cref="SupportErrorCodes.InternalTypeLocked"/>), sólo se mueve.</summary>
    public bool IsInternal { get; private set; }

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
        DateTimeOffset now,
        string? formalNextStep = null)
    {
        var normalized = NormalizeName(name);
        EnsureTerm(termBusinessDays, termIsLegal);
        var nextStep = SupportFormalNextStep.Normalize(formalNextStep);

        return new SupportTicketType
        {
            Id = id,
            TenantId = tenantId,
            Name = normalized,
            TermBusinessDays = termBusinessDays,
            TermIsLegal = termIsLegal,
            RequiresFormalResponse = requiresFormalResponse,
            FormalNextStep = nextStep,
            IsPublished = isPublished,
            IsActive = true,
            Position = position,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>El tipo de las tareas internas: sin término ni respuesta formal, sin publicar, activo y bloqueado.</summary>
    public static SupportTicketType CreateInternal(SupportTicketTypeId id, Guid tenantId, int position, DateTimeOffset now)
    {
        var type = Create(
            id, tenantId, SupportTicketTypeDefaults.InternalTaskName, termBusinessDays: null, termIsLegal: false,
            requiresFormalResponse: false, isPublished: false, position, now);
        type.IsInternal = true;
        return type;
    }

    /// <summary>Reemplaza el tipo entero: <paramref name="formalNextStep"/> en null borra el «siguiente paso». Con los mismos valores
    /// no cambia nada ni sube la versión.</summary>
    public void Update(
        string name,
        int? termBusinessDays,
        bool termIsLegal,
        bool requiresFormalResponse,
        bool isPublished,
        bool isActive,
        string? formalNextStep,
        DateTimeOffset now)
    {
        if (IsInternal)
        {
            // Se compara antes de normalizar: con sus propios valores no hace nada; cualquier diferencia se rechaza.
            if (name == Name && termBusinessDays == TermBusinessDays && termIsLegal == TermIsLegal &&
                requiresFormalResponse == RequiresFormalResponse && isPublished == IsPublished && isActive == IsActive &&
                formalNextStep == FormalNextStep)
            {
                return;
            }

            throw new SupportDomainException(SupportErrorCodes.InternalTypeLocked, "The internal ticket type cannot be edited.");
        }

        var normalized = NormalizeName(name);
        EnsureTerm(termBusinessDays, termIsLegal);
        var nextStep = SupportFormalNextStep.Normalize(formalNextStep);

        if (normalized == Name && termBusinessDays == TermBusinessDays && termIsLegal == TermIsLegal &&
            requiresFormalResponse == RequiresFormalResponse && isPublished == IsPublished && isActive == IsActive &&
            nextStep == FormalNextStep)
        {
            return;
        }

        Name = normalized;
        TermBusinessDays = termBusinessDays;
        TermIsLegal = termIsLegal;
        RequiresFormalResponse = requiresFormalResponse;
        FormalNextStep = nextStep;
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
