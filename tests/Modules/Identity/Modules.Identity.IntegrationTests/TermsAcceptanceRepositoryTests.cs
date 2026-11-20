using Microsoft.EntityFrameworkCore;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using static Modules.Identity.IntegrationTests.PasswordAuthApiHarness;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// Spec 2026-11-19 (onboarding por link): la aceptación de términos es una fila por usuario y versión
/// (<c>identity.terms_acceptances</c>, única en <c>(user_id, version)</c>). Registrar dos veces es idempotente, también
/// contra la base real; cada llamada va en su propio contexto, como en producción.
/// </summary>
public sealed class TermsAcceptanceRepositoryTests
{
    private const string Current = "2026-11-19";

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordingTwiceStoresOneRow()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var userId = await SeedUserAsync(cs);

        await using (var first = NewIdentityContext(cs))
        {
            await NewService(first, Current).RecordAsync(userId, Current, ct);
        }

        await using (var second = NewIdentityContext(cs))
        {
            await NewService(second, Current).RecordAsync(userId, Current, ct);
        }

        await using var reading = NewIdentityContext(cs);
        var stored = await reading.TermsAcceptances.AsNoTracking().ToListAsync(ct);
        var row = Assert.Single(stored);
        Assert.Equal((userId, Current, Now), (row.UserId.Value, row.Version, row.AcceptedAt));
    }

    [Fact]
    public async Task HasAcceptedCurrentIsFalseForAnOlderVersion()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var userId = await SeedUserAsync(cs);

        await using (var older = NewIdentityContext(cs))
        {
            await NewService(older, "2026-01-01").RecordAsync(userId, "2026-01-01", ct);
        }

        await using (var reading = NewIdentityContext(cs))
        {
            Assert.False(await NewService(reading, Current).HasAcceptedCurrentAsync(userId, ct));
        }

        await using (var accepting = NewIdentityContext(cs))
        {
            await NewService(accepting, Current).RecordAsync(userId, Current, ct);
        }

        await using var again = NewIdentityContext(cs);
        Assert.True(await NewService(again, Current).HasAcceptedCurrentAsync(userId, ct));
        Assert.Equal(2, await again.TermsAcceptances.CountAsync(ct));
    }

    private static TermsAcceptanceService NewService(IdentityDbContext context, string currentVersion) =>
        new(new TermsAcceptanceRepository(context), new TermsOptions { CurrentVersion = currentVersion }, new FixedClock(Now));

    private static async Task<Guid> SeedUserAsync(string connectionString)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var migrating = NewIdentityContext(connectionString))
        {
            await migrating.Database.MigrateAsync(cancellationToken);
        }

        var user = User.CreateInvited(UserId.New(), "person@example.com", Now);
        await using var writing = NewIdentityContext(connectionString);
        new UserRepository(writing).Add(user);
        await writing.SaveChangesAsync(cancellationToken);
        return user.Id.Value;
    }

    private sealed class FixedClock(DateTimeOffset now) : BuildingBlocks.Application.IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
