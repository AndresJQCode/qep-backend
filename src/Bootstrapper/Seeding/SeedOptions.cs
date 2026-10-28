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
    /// el arranque. Un token de Azure DevOps sin reemplazar (<c>#{SEED_OPERATOR_OWNER_EMAIL}#</c>,
    /// ver <see cref="IsUnreplacedPipelineToken"/>) cuenta como sin valor. Si no, tiene que ser un
    /// email válido.
    /// </summary>
    public string? OperatorOwnerEmail { get; set; }

    /// <summary>
    /// true si el valor es un token de reemplazo de Azure DevOps que llegó literal —
    /// <c>^#\{[A-Z0-9_]+\}#$</c> después de recortar espacios—, o sea, la variable del pipeline no
    /// existe. El ConfigMap de producción declara <c>Seed__OperatorOwnerEmail</c> así, y hasta que
    /// alguien cree la variable el valor tiene que valer lo mismo que ausente: el validador no tumba
    /// el arranque y la semilla salta QCode con una advertencia. Un único lugar para la regla, que
    /// usan los dos. Sólo se aplica a <see cref="OperatorOwnerEmail"/>: <see cref="OwnerEmail"/>
    /// sigue exigiendo un email real.
    ///
    /// Es un método estático y no una propiedad a propósito: <c>ConfigurationExampleTests</c> trata
    /// toda propiedad pública como una clave que el ejemplo tiene que documentar.
    /// </summary>
    public static bool IsUnreplacedPipelineToken(string? value)
    {
        var trimmed = value?.Trim();
        if (trimmed is not { Length: > 4 }
            || !trimmed.StartsWith("#{", StringComparison.Ordinal)
            || !trimmed.EndsWith("}#", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in trimmed.AsSpan(2, trimmed.Length - 4))
        {
            if (character is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// La carga sintética de la exportación. También le concede admin a <see cref="OwnerEmail"/>,
    /// sobre su propio tenant, así que prenderla exige el email igual que <see cref="Enabled"/>.
    /// </summary>
    public ExportLoadSeedOptions ExportLoad { get; set; } = new();
}
