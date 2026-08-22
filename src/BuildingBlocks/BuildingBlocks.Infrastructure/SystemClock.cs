using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
