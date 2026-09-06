namespace Bootstrapper.Seeding;

/// <summary>
/// La semilla de arranque del ambiente desplegado. No es sólo cargar datos: crea un tenant y
/// le otorga el rol admin a un email, así que es un mecanismo que concede privilegios.
///
/// El truco fail-closed del stub de auth —abortar si lo prenden fuera de Development— acá no
/// sirve: el ambiente objetivo corre con ASPNETCORE_ENVIRONMENT=Production. La defensa es que
/// <see cref="Enabled"/> nace apagado y que prenderlo exige declarar a quién se le da admin.
/// </summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public bool Enabled { get; set; }

    /// <summary>
    /// Email que recibe la membresía admin. Sin valor por defecto y sin hardcodear: dejar una
    /// persona fija en un archivo versionado haría del repositorio la autoridad sobre quién
    /// administra un tenant, y eso es una decisión del ambiente.
    /// </summary>
    public string? OwnerEmail { get; set; }
}
