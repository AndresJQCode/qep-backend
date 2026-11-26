namespace Bootstrapper.Valkey;

/// <summary>
/// Sección <c>Valkey</c> (spec 2026-11-13, D-1). Todo nace apagado: con <c>Enabled = false</c> el cliente no conecta, y
/// cada área se prende por separado. Un área apagada registra exactamente las clases de antes (V3 del plan).
/// </summary>
public sealed class ValkeyOptions
{
    public const string SectionName = "Valkey";

    /// <summary>Interruptor general: apagado, el cliente no conecta aunque haya cadena.</summary>
    public bool Enabled { get; set; }

    public ValkeyAreaOptions Areas { get; set; } = new();

    /// <summary>Tope de cada operación contra Valkey; pasado, el área vuelve a su respaldo local.</summary>
    public int OperationTimeoutMs { get; set; } = 100;
}

/// <summary>Un interruptor por área del spec; cada uno sólo cuenta con <see cref="ValkeyOptions.Enabled"/> prendido.</summary>
public sealed class ValkeyAreaOptions
{
    public bool Presence { get; set; }

    public bool RateLimiting { get; set; }

    public bool Sessions { get; set; }

    public bool Permissions { get; set; }

    public bool SendBudget { get; set; }

    public bool Doorbells { get; set; }

    public bool ApiKeyTouch { get; set; }

    /// <summary>La tabla de valores legales de Hr, entera, bajo una clave versionada (spec 2026-11-22, «Caché»).</summary>
    public bool LegalParameters { get; set; }
}
