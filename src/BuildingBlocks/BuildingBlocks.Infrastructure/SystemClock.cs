using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure;

public sealed class SystemClock : IClock
{
    // Invariante: todo valor de IClock tiene que sobrevivir sin cambios un viaje de ida y vuelta
    // por `timestamptz` de Postgres. .NET mide en ticks de 100ns, pero `timestamptz` sólo guarda
    // microsegundos -- Npgsql trunca ese último dígito al persistir. Sin este truncado acá, un
    // valor devuelto en memoria nunca es exactamente igual al mismo valor releído de la base, y
    // una comparación exacta entre ambos falla de forma intermitente.
    public DateTimeOffset UtcNow
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        }
    }
}
