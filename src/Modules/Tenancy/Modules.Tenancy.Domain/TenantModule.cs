namespace Modules.Tenancy.Domain;

/// <summary>
/// Un módulo contratado por un tenant: la presencia de la fila **es** el entitlement, y apagar es
/// borrarla (spec 2026-10-07). <see cref="Source"/> no se valida acá: el código sólo escribe
/// <see cref="TenantModuleSources"/>, <c>backfill</c> y <c>manual</c> sólo los escribe SQL, y el
/// <c>CHECK</c> de la tabla es la única validación.
/// </summary>
public sealed class TenantModule
{
    public const int SourceMaxLength = 16;
    public const int NoteMaxLength = 300;

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
    }

    public TenantId TenantId { get; private set; }

    public TenantModuleKey ModuleKey { get; private set; }

    public DateTimeOffset EnabledAt { get; private set; }

    public string Source { get; private set; }

    public string? Note { get; private set; }

    public static TenantModule Create(
        TenantId tenantId, TenantModuleKey moduleKey, string source, DateTimeOffset enabledAt, string? note) =>
        new(tenantId, moduleKey, source, enabledAt, note);
}

/// <summary>Los orígenes que escribe el código. <c>backfill</c> y <c>manual</c> los escribe SQL.</summary>
public static class TenantModuleSources
{
    public const string Signup = "signup";
    public const string Seed = "seed";
}
