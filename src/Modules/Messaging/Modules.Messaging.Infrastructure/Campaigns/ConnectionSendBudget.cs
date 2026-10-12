using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Campaigns;

/// <summary>
/// Decisión del owner 2026-10-12 (P3 y P5): el token bucket por conexión, en <c>messaging.connection_send_budgets</c>. Todo el estado vive en
/// la fila, así que cualquier réplica ve el mismo cubo; las dos operaciones son una sentencia cada una (más el <c>INSERT … ON CONFLICT DO
/// NOTHING</c> del primer uso), con el candado de la fila como único punto de sincronía.
/// <list type="bullet">
/// <item>Capacidad = ritmo: un segundo de ráfaga. El relleno es <c>LEAST(ritmo, tokens + transcurrido × ritmo)</c> y se cuenta desde el final
/// de la pausa, de modo que parar 30 s no acumula una ráfaga que Meta rechazaría de nuevo.</item>
/// <item>130429: ritmo = <c>max(1, ritmo / 2)</c> y pausa hasta <c>now + 30 s</c>. Sólo baja si no hay una pausa vigente: varias respuestas del
/// mismo golpe cuentan una vez.</item>
/// <item>Subida: <c>updated_at</c> es el último cambio del ritmo; pasada la pausa, cada 30 s sin 130429 suma 1 mps hasta el objetivo
/// (<c>MessagesPerSecond</c>, acotado por <c>MaxMessagesPerSecond</c>). Si el objetivo baja en la configuración, el ritmo lo sigue de inmediato.</item>
/// </list>
/// El reloj es el <c>now</c> que llega por parámetro, no el de Postgres, para que las réplicas y las pruebas hablen del mismo tiempo.
/// </summary>
internal sealed class ConnectionSendBudget(MessagingDbContext dbContext, IOptions<MessagingCampaignOptions> options) : IConnectionSendBudget
{
    /// <summary>Cuánto dura la pausa tras un 130429, y cada cuánto sube un mps.</summary>
    public const int StepSeconds = 30;

    public async Task<SendGrant> AcquireAsync(Guid tenantId, Guid connectionId, int requested, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requested, 1);
        var cap = Cap();
        var step = (double)StepSeconds;

        // Primer uso: un cubo lleno al ritmo objetivo. Dos réplicas a la vez: una inserta y la otra no hace nada.
        _ = await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO messaging.connection_send_budgets (connection_id, tenant_id, tokens, rate, refilled_at, throttled_until, updated_at)
            VALUES ({connectionId}, {tenantId}, {cap}, {cap}, {now}, NULL, {now})
            ON CONFLICT (connection_id) DO NOTHING
            """, cancellationToken);

        var rows = await dbContext.Database.SqlQuery<GrantRow>(
            $"""
            WITH cur AS (
                SELECT connection_id, tokens, rate, refilled_at, throttled_until, updated_at,
                       COALESCE(throttled_until > {now}, false) AS paused,
                       GREATEST(updated_at, COALESCE(throttled_until, updated_at)) AS anchor
                  FROM messaging.connection_send_budgets
                 WHERE connection_id = {connectionId}
                 FOR UPDATE
            ), climb AS (
                SELECT *,
                       CASE WHEN paused OR rate >= {cap}::double precision THEN 0::double precision
                            ELSE FLOOR(GREATEST(0, EXTRACT(EPOCH FROM ({now} - anchor))) / {step}::double precision) END AS climbed
                  FROM cur
            ), calc AS (
                SELECT *,
                       LEAST({cap}::double precision, rate + climbed) AS new_rate
                  FROM climb
            ), decided AS (
                SELECT *,
                       LEAST(new_rate, tokens + GREATEST(0, EXTRACT(EPOCH FROM ({now} - GREATEST(refilled_at, COALESCE(throttled_until, refilled_at))))) * rate) AS available
                  FROM calc
            ), grant_row AS (
                SELECT *,
                       CASE WHEN paused THEN 0 ELSE LEAST({requested}::double precision, FLOOR(available + 1e-9))::int END AS granted
                  FROM decided
            )
            UPDATE messaging.connection_send_budgets AS b
               SET tokens = GREATEST(0, g.available - g.granted),
                   rate = g.new_rate,
                   refilled_at = GREATEST(b.refilled_at, {now}),
                   updated_at = CASE WHEN g.climbed > 0 THEN g.anchor + (g.climbed * {step}::double precision) * interval '1 second' ELSE b.updated_at END
              FROM grant_row AS g
             WHERE b.connection_id = g.connection_id
            RETURNING g.granted AS "Granted",
                      CASE WHEN g.granted >= {requested} THEN 0::double precision
                           WHEN g.paused THEN EXTRACT(EPOCH FROM (g.throttled_until - {now}))::double precision
                           ELSE (1 - (g.available - g.granted)) / g.new_rate END AS "RetryAfterSeconds",
                      g.new_rate AS "Rate"
            """).ToListAsync(cancellationToken);

        var row = rows.Single();
        return new SendGrant(row.Granted, TimeSpan.FromTicks((long)Math.Round(Math.Max(0, row.RetryAfterSeconds) * TimeSpan.TicksPerSecond)), row.Rate);
    }

    public async Task ThrottleAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cap = Cap();
        var step = (double)StepSeconds;
        var until = now.AddSeconds(StepSeconds);

        // Sobre una conexión sin fila (el 130429 llegó antes que cualquier concesión) arranca ya en la mitad y en pausa. Sobre una con fila, la
        // mitad del ritmo que ya tendría con la subida pendiente aplicada; y sólo si no hay una pausa vigente.
        _ = await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO messaging.connection_send_budgets AS b (connection_id, tenant_id, tokens, rate, refilled_at, throttled_until, updated_at)
            VALUES ({connectionId}, {tenantId}, 0, GREATEST(1, {cap}::double precision / 2), {now}, {until}, {now})
            ON CONFLICT (connection_id) DO UPDATE SET
                rate = GREATEST(1, LEAST({cap}::double precision, b.rate + CASE WHEN b.rate >= {cap}::double precision THEN 0::double precision
                    ELSE FLOOR(GREATEST(0, EXTRACT(EPOCH FROM ({now} - GREATEST(b.updated_at, COALESCE(b.throttled_until, b.updated_at))))) / {step}::double precision) END) / 2),
                tokens = 0,
                refilled_at = GREATEST(b.refilled_at, {now}),
                throttled_until = {until},
                updated_at = {now}
             WHERE b.throttled_until IS NULL OR b.throttled_until <= {now}
            """, cancellationToken);
    }

    /// <summary>El techo del ritmo: el objetivo, sin pasar nunca del máximo (el validador ya lo exige; esto cubre una prueba que arme las
    /// opciones a mano).</summary>
    private double Cap() => Math.Min(options.Value.MessagesPerSecond, options.Value.MaxMessagesPerSecond);

    private sealed record GrantRow(int Granted, double RetryAfterSeconds, double Rate);
}
