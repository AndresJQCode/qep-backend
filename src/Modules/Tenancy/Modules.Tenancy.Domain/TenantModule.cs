namespace Modules.Tenancy.Domain;

/// <summary>
/// Un módulo de un tenant y su estado (spec 2026-10-08 §3): la fila es el entitlement y su
/// <see cref="Status"/> dice si está contratado. Desactivar ya no es borrar: la fila queda con su estado
/// y el historial (<see cref="TenantChange"/>) guarda quién, cuándo y por qué. <see cref="EnabledAt"/> es
/// cuándo se creó la fila por primera vez; <see cref="StatusChangedAt"/>, el último cambio de estado.
/// <see cref="Source"/> no se valida acá: el código sólo escribe <see cref="TenantModuleSources"/>,
/// <c>backfill</c> y <c>manual</c> sólo los escribe SQL, y el <c>CHECK</c> de la tabla es la única
/// validación.
/// </summary>
public sealed class TenantModule
{
    public const int SourceMaxLength = 16;
    public const int NoteMaxLength = 300;
    public const int StatusMaxLength = 16;

    private TenantModule()
    {
        ModuleKey = null!;
        Source = string.Empty;
    }

    private TenantModule(
        TenantId tenantId, TenantModuleKey moduleKey, string source, DateTimeOffset enabledAt, string? note)
    {
        TenantId = tenantId;
        ModuleKey = moduleKey;
        Source = source;
        EnabledAt = enabledAt;
        Note = note;
        Status = TenantModuleStatus.Active;
        StatusChangedAt = enabledAt;
    }

    public TenantId TenantId { get; private set; }

    public TenantModuleKey ModuleKey { get; private set; }

    public DateTimeOffset EnabledAt { get; private set; }

    public string Source { get; private set; }

    public string? Note { get; private set; }

    public TenantModuleStatus Status { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    public static TenantModule Create(
        TenantId tenantId, TenantModuleKey moduleKey, string source, DateTimeOffset enabledAt, string? note) =>
        new(tenantId, moduleKey, source, enabledAt, note);

    public void Activate(DateTimeOffset occurredAt) => ChangeTo(TenantModuleStatus.Active, occurredAt);

    public void Deactivate(DateTimeOffset occurredAt) => ChangeTo(TenantModuleStatus.Inactive, occurredAt);

    // Sin cambios vacíos (§3): el lote ya lo filtra, pero el agregado no confía en quien lo llama.
    private void ChangeTo(TenantModuleStatus next, DateTimeOffset occurredAt)
    {
        if (Status == next)
        {
            throw new TenantDomainException(
                TenantModuleChangeBatch.NoChangesCode,
                $"The '{ModuleKey.Value}' module is already {TenantChangeVocabulary.ToText(next)}.");
        }

        Status = next;
        StatusChangedAt = occurredAt;
    }
}

/// <summary>Los orígenes que escribe el código. <c>backfill</c> y <c>manual</c> los escribe SQL.</summary>
public static class TenantModuleSources
{
    public const string Signup = "signup";
    public const string Seed = "seed";

    /// <summary>Spec 2026-10-08 §3: la fila la creó la consola (el módulo nunca había existido).</summary>
    public const string Operator = "operator";
}
