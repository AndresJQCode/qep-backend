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

    /// <summary>
    /// Email de QCode que recibe la membresía admin del tenant operador (<c>qcode</c>). Es una clave
    /// aparte y no se reutiliza <see cref="OwnerEmail"/> a propósito: en producción ése puede ser el
    /// email del cliente, y reutilizarlo lo convertiría en operador de toda la plataforma.
    ///
    /// Opcional: sin valor la semilla no crea el tenant operador y lo advierte en el log, sin tumbar
    /// el arranque. Si viene, tiene que ser un email válido.
    /// </summary>
    public string? OperatorOwnerEmail { get; set; }

    /// <summary>
    /// La carga sintética de la exportación. También le concede admin a <see cref="OwnerEmail"/>,
    /// sobre su propio tenant, así que prenderla exige el email igual que <see cref="Enabled"/>.
    /// </summary>
    public ExportLoadSeedOptions ExportLoad { get; set; } = new();
}
