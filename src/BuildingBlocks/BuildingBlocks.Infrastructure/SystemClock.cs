using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure;

public sealed class SystemClock : IClock
{
    // Truncado a microsegundos (hallazgo 2026-09-16, slice CANCEL-01 backend): .NET mide en
    // ticks de 100ns, pero `timestamptz` de Postgres sólo guarda microsegundos -- Npgsql trunca
    // ese último dígito al persistir. Sin este truncado, un valor devuelto en memoria (por
    // ejemplo, la respuesta de un POST) nunca es exactamente igual al mismo valor releído de la
    // base (por ejemplo, un GET posterior), y una comparación exacta como
    // `Assert.Equal(order.CancelledAt, fetched.CancelledAt)` falla de forma intermitente.
    public DateTimeOffset UtcNow
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        }
    }
}
