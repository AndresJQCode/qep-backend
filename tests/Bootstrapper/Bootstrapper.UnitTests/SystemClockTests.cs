using BuildingBlocks.Infrastructure;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Pin del invariante documentado en <see cref="SystemClock"/>: todo valor tiene que sobrevivir
/// un viaje de ida y vuelta por <c>timestamptz</c> de Postgres, que sólo guarda microsegundos.
/// </summary>
public sealed class SystemClockTests
{
    [Fact]
    public void UtcNowIsTruncatedToMicroseconds()
    {
        var now = new SystemClock().UtcNow;

        Assert.Equal(0, now.Ticks % TimeSpan.TicksPerMicrosecond);
    }
}
