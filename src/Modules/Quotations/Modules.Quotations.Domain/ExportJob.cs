namespace Modules.Quotations.Domain;

/// <summary>
/// Una exportación pedida y todavía no entregada, o ya terminada (spec 2026-09-12, D2).
///
/// No es un agregado de negocio sino el estado de un trabajo en segundo plano: vive en el dominio
/// porque sus reglas —cuánto dura el lease, cuántas veces se reintenta y cuánto se espera entre
/// intentos— son las que soporte tiene que poder leer en un solo lugar.
///
/// La toma exclusiva no la hace esta clase sino un UPDATE con SKIP LOCKED (ExportJobQueue); el
/// método <see cref="Claim"/> es ese mismo cambio, para quien no tiene SQL y como especificación
/// de lo que el UPDATE tiene que dejar.
/// </summary>
public sealed class ExportJob
{
    /// <summary>
    /// D11: cuatro intentos —el primero y un reintento por cada espera de
    /// <see cref="RetryDelays"/>—; al cuarto fallido, <see cref="ExportJobStatus.Failed"/>. El
    /// archivo llega por correo y nadie mira la pantalla: unos 21 minutos de ventana que se
    /// recuperan de una caída corta de R2 o de la base cuestan menos que un correo de fallo.
    /// </summary>
    public const int MaxAttempts = 4;

    public const int LastErrorMaxLength = 2_000;

    public const int FileNameMaxLength = 200;

    /// <summary>D6: cuánto tiempo es de un worker. Si muere, otro lo retoma al vencer.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>D13: los terminados se borran pasado este tiempo.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>
    /// D11: la espera después del intento fallido número n es <c>RetryDelays[n - 1]</c>. Tres
    /// esperas para <see cref="MaxAttempts"/> intentos: el último fallido no espera, termina. Si
    /// cambia una de las dos cosas, cambia la otra (ExportJobTests lo fija).
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    // Para EF.
    private ExportJob()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>El sujeto que la pidió: a quien va el correo y contra quien se cuenta el límite
    /// de pendientes.</summary>
    public Guid RequestedBy { get; private set; }

    public ExportJobKind Kind { get; private set; }

    /// <summary>Los filtros ya validados, en JSON. Se guardan crudos —el NIT o el CUC como texto,
    /// no los ids que resolvían al pedir— para que el archivo refleje los datos al generarse.</summary>
    public string Filters { get; private set; } = string.Empty;

    public ExportJobStatus Status { get; private set; }

    /// <summary>Intentos consumidos. La toma lo suma antes de procesar, así que un worker que
    /// muere también gasta el suyo.</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public string? LastError { get; private set; }

    public string? FileName { get; private set; }

    public int? RowCount { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    /// <summary>Cuándo terminó, bien o mal. La retención cuenta desde acá.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Tomado más veces de las permitidas: el anterior murió en el último intento y el
    /// lease se venció. Se cierra sin procesar.</summary>
    public bool HasExceededAttempts => Attempts > MaxAttempts;

    public static ExportJob Enqueue(
        Guid id,
        Guid tenantId,
        Guid requestedBy,
        ExportJobKind kind,
        string filters,
        DateTimeOffset requestedAt) => new()
        {
            Id = id,
            TenantId = tenantId,
            RequestedBy = requestedBy,
            Kind = kind,
            Filters = filters,
            Status = ExportJobStatus.Pending,
            Attempts = 0,
            NextAttemptAt = requestedAt,
            RequestedAt = requestedAt,
        };

    public bool IsClaimable(DateTimeOffset now) =>
        (Status == ExportJobStatus.Pending && NextAttemptAt <= now)
        || (Status == ExportJobStatus.Processing && LockedUntil < now);

    public void Claim(DateTimeOffset now)
    {
        if (!IsClaimable(now))
        {
            throw new InvalidOperationException($"Export job '{Id}' is not claimable at {now:O}.");
        }

        Status = ExportJobStatus.Processing;
        Attempts++;
        LockedUntil = now.Add(LeaseDuration);
    }

    public void Complete(string fileName, int rowCount, DateTimeOffset now)
    {
        EnsureProcessing();
        Status = ExportJobStatus.Completed;
        FileName = fileName;
        RowCount = rowCount;
        CompletedAt = now;
        LockedUntil = null;
        LastError = null;
    }

    /// <summary>Un fallo que puede no repetirse (R2, base, timeout). Devuelve <c>true</c> si ya
    /// no quedaban intentos y el job terminó en <see cref="ExportJobStatus.Failed"/>.</summary>
    public bool RecordTransientFailure(string error, DateTimeOffset now)
    {
        EnsureProcessing();
        if (Attempts >= MaxAttempts)
        {
            Fail(error, now);
            return true;
        }

        Status = ExportJobStatus.Pending;
        NextAttemptAt = now.Add(RetryDelays[Attempts - 1]);
        LockedUntil = null;
        LastError = Truncate(error);
        return false;
    }

    /// <summary>Un fallo que no se arregla reintentando: filtros ilegibles o cero filas.</summary>
    public void Fail(string error, DateTimeOffset now)
    {
        EnsureProcessing();
        Status = ExportJobStatus.Failed;
        CompletedAt = now;
        LockedUntil = null;
        LastError = Truncate(error);
    }

    // Una transición desde otro estado es un error de programación del runner, no una entrada
    // de usuario: por eso InvalidOperationException y no un código de dominio.
    private void EnsureProcessing()
    {
        if (Status != ExportJobStatus.Processing)
        {
            throw new InvalidOperationException(
                $"Export job '{Id}' is {Status}; only a Processing job can finish.");
        }
    }

    private static string Truncate(string error) =>
        error.Length <= LastErrorMaxLength ? error : error[..LastErrorMaxLength];
}
