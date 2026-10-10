using Modules.Messaging.Infrastructure.Media;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.6 y P15: el lease es también el tiempo que tiene una copia para terminar. Una
/// copia puede durar hasta <see cref="MediaTransfer.DefaultCopyTimeout"/>; con un lease más corto otra réplica
/// la retomaría a mitad de camino.</summary>
public sealed class MediaCopyWorkerTests
{
    [Fact]
    public void TheLeasesKeepTheCurveAndOutlastACopy()
    {
        Assert.Equal(
            [TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)],
            MediaCopyWorker.Leases);
        Assert.All(MediaCopyWorker.Leases, lease => Assert.True(lease > MediaTransfer.DefaultCopyTimeout));
    }
}
