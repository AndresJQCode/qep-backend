namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-15 §3.1–§3.2: un motivo del catálogo de resolución del tenant. Lo crea el resolver cuando el nombre no existe (D-R4);
/// renombrar y archivar es de <c>messaging.resolution_reason.manage</c>. Archivar no borra la fila (D-R5): el historial guarda el id y
/// resuelve el nombre al leer.
///
/// La unicidad del nombre (sin distinguir mayúsculas, entre los vigentes) no la arbitra esta clase sino
/// <c>IX_resolution_reasons_tenant_name</c>, y <c>MessagingUnitOfWork</c> la traduce a <c>messaging.resolution_reason.name_taken</c>.
/// </summary>
public sealed class ResolutionReason
{
    public const int NameMaxLength = 60;

    /// <summary>D-R9: el tope de motivos vigentes por tenant. Contarlos es del caso de uso; acá vive el número y su error.</summary>
    public const int MaxPerTenant = 100;

    // EF Core materializa por acá; Create es el único punto de entrada.
    private ResolutionReason()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public long Version { get; private set; }

    public bool IsArchived => DeletedAt is not null;

    /// <summary>El id lo pone quien llama: el resolver lo necesita para el evento antes de saber si la conversación se puede resolver.</summary>
    public static ResolutionReason Create(Guid id, Guid tenantId, string? name, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = NormalizeName(name),
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1,
    };

    /// <summary>Valida antes de asignar: un rechazo deja el motivo como estaba.</summary>
    public void Rename(string? name, DateTimeOffset now)
    {
        Name = NormalizeName(name);
        UpdatedAt = now;
        Version++;
    }

    public void Archive(DateTimeOffset now)
    {
        DeletedAt = now;
        UpdatedAt = now;
        Version++;
    }

    /// <summary>Se llama antes de crear: <paramref name="activeCount"/> son los vigentes; el que se intenta crear sería el número <c>MaxPerTenant + 1</c>.</summary>
    public static void EnsureTenantCapacity(int activeCount)
    {
        if (activeCount >= MaxPerTenant)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.ResolutionReasonLimitReached, $"A tenant cannot have more than {MaxPerTenant} resolution reasons.");
        }
    }

    // Recortar es parte del invariante: el índice único compara nombres ya recortados. Los espacios de adentro se conservan (spec §3.2: sólo Trim).
    public static string TrimName(string? name) => (name ?? string.Empty).Trim();

    public static bool IsValidName(string trimmedName)
    {
        ArgumentNullException.ThrowIfNull(trimmedName);
        return trimmedName.Length is > 0 and <= NameMaxLength;
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = TrimName(name);
        if (trimmed.Length == 0)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ResolutionReasonNameRequired, "The resolution reason name is required.");
        }

        return trimmed.Length > NameMaxLength
            ? throw new MessagingDomainException(
                MessagingErrorCodes.ResolutionReasonNameTooLong, $"The resolution reason name cannot exceed {NameMaxLength} characters.")
            : trimmed;
    }
}
