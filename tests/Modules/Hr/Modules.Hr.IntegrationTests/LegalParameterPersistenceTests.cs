using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// La migración <c>AddLegalParameterChanges</c> (spec 2026-11-22, «Datos») y el repositorio de la consola del operador contra
/// Postgres real: las filas sembradas siguen sin autor, y el historial guarda y pagina sus <c>jsonb</c>.
/// </summary>
public sealed class LegalParameterPersistenceTests : IClassFixture<LegalParameterPersistenceTests.World>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly World _world;

    public LegalParameterPersistenceTests(World world) => _world = world;

    public sealed class World : IAsyncLifetime
    {
        private PostgreSqlContainer? _database;

        internal QepApiFactory Factory { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            _database = await StartDatabaseAsync();
            Factory = new QepApiFactory(_database.GetConnectionString());
            await SeedTenantAsync(Factory);
        }

        public async ValueTask DisposeAsync()
        {
            Factory?.Dispose();
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _world.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private async Task SaveInScopeAsync(Action<ILegalParameterRepository> write)
    {
        await using var scope = _world.Factory.Services.CreateAsyncScope();
        write(scope.ServiceProvider.GetRequiredService<ILegalParameterRepository>());
        await scope.ServiceProvider.GetRequiredService<IHrUnitOfWork>().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheMigrationKeepsSeededRowsWithoutAuthor()
    {
        var (values, brackets) = await InScopeAsync(async provider =>
        {
            var repository = provider.GetRequiredService<ILegalParameterRepository>();
            return (await repository.ListValuesAsync(Ct), await repository.ListBracketsAsync(Ct));
        });

        Assert.NotEmpty(values);
        Assert.NotEmpty(brackets);
        Assert.All(values, row =>
        {
            Assert.Null(row.UpdatedAt);
            Assert.Null(row.UpdatedBy);
        });
        Assert.All(brackets, row =>
        {
            Assert.Null(row.UpdatedAt);
            Assert.Null(row.UpdatedBy);
        });
        Assert.Equal(
            1_750_905m,
            values.Single(row => row.Key == LegalParameterKey.Smmlv && row.EffectiveFrom == new DateOnly(2026, 1, 1)).Value);
    }

    [Fact]
    public async Task AWrittenRowKeepsItsAuthorAndAReplaceUpdatesIt()
    {
        var author = Guid.NewGuid();
        var other = Guid.NewGuid();
        var at = new DateTimeOffset(2090, 5, 1, 10, 0, 0, TimeSpan.Zero);
        var later = at.AddDays(1);
        var date = new DateOnly(2090, 1, 1);

        await SaveInScopeAsync(repository =>
        {
            repository.Add(LegalParameterValue.Create(LegalParameterKey.Smmlv, date, 2_000_000m, author, at));
            repository.AddBrackets(
            [
                LegalParameterBracket.Create(LegalBracketKey.SolidarityFund, date, new LegalBracketInput(4m, 16m, 0.01m), author, at),
            ]);
        });

        await InScopeAsync<object?>(async provider =>
        {
            var repository = provider.GetRequiredService<ILegalParameterRepository>();
            var row = (await repository.ListValuesAsync(Ct)).Single(r => r.Key == LegalParameterKey.Smmlv && r.EffectiveFrom == date);
            row.Replace(2_100_000m, other, later);
            await provider.GetRequiredService<IHrUnitOfWork>().SaveChangesAsync(Ct);
            return null;
        });

        var (value, bracket) = await InScopeAsync(async provider =>
        {
            var repository = provider.GetRequiredService<ILegalParameterRepository>();
            return (
                (await repository.ListValuesAsync(Ct)).Single(r => r.Key == LegalParameterKey.Smmlv && r.EffectiveFrom == date),
                (await repository.ListBracketsAsync(Ct)).Single(r => r.EffectiveFrom == date));
        });

        Assert.Equal(2_100_000m, value.Value);
        Assert.Equal(later, value.UpdatedAt);
        Assert.Equal(other, value.UpdatedBy);
        Assert.Equal(at, bracket.UpdatedAt);
        Assert.Equal(author, bracket.UpdatedBy);
    }

    // El escritor reemplaza un conjunto de tramos borrando sus filas y escribiendo las nuevas en el mismo guardado; la llave primaria
    // incluye el límite inferior, así que un tramo que conserva su inicio repite llave con el que se borra.
    [Fact]
    public async Task ReplacingABracketSetThatKeepsALowerBoundSavesInOneTransaction()
    {
        var author = Guid.NewGuid();
        var at = new DateTimeOffset(2092, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var date = new DateOnly(2092, 1, 1);
        await SaveInScopeAsync(repository => repository.AddBrackets(
        [
            LegalParameterBracket.Create(LegalBracketKey.SolidarityFund, date, new LegalBracketInput(4m, 16m, 0.01m), author, at),
            LegalParameterBracket.Create(LegalBracketKey.SolidarityFund, date, new LegalBracketInput(16m, 25m, 0.02m), author, at),
        ]));

        await InScopeAsync<object?>(async provider =>
        {
            var repository = provider.GetRequiredService<ILegalParameterRepository>();
            var old = (await repository.ListBracketsAsync(Ct)).Where(row => row.EffectiveFrom == date).ToList();
            repository.RemoveBrackets(old);
            repository.AddBrackets(
            [
                LegalParameterBracket.Create(LegalBracketKey.SolidarityFund, date, new LegalBracketInput(4m, 18m, 0.015m), author, at),
                LegalParameterBracket.Create(LegalBracketKey.SolidarityFund, date, new LegalBracketInput(18m, null, 0.025m), author, at),
            ]);
            await provider.GetRequiredService<IHrUnitOfWork>().SaveChangesAsync(Ct);
            return null;
        });

        var rows = await InScopeAsync(async provider =>
            (await provider.GetRequiredService<ILegalParameterRepository>().ListBracketsAsync(Ct))
            .Where(row => row.EffectiveFrom == date)
            .OrderBy(row => row.LowerBound)
            .ToList());

        Assert.Equal([4m, 18m], rows.Select(row => row.LowerBound));
        Assert.Equal([18m, (decimal?)null], rows.Select(row => row.UpperBound));
        Assert.Equal([0.015m, 0.025m], rows.Select(row => row.Rate));
    }

    [Fact]
    public async Task AChangeRoundTripsWithJson()
    {
        const string newValue = "[{\"lowerBound\":4,\"upperBound\":16,\"rate\":0.01},{\"lowerBound\":16,\"upperBound\":null,\"rate\":0.012}]";
        var by = Guid.NewGuid();
        var batch = Guid.NewGuid();
        var at = new DateTimeOffset(2090, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var change = LegalParameterChange.Record(
            "SolidarityFund", new DateOnly(2090, 1, 1), LegalParameterChangeAction.Created, null, newValue,
            by, "Operadora", at, LegalParameterChangeSource.Excel, batch, "enero.xlsx");

        await SaveInScopeAsync(repository => repository.AddChange(change));

        var (items, _) = await InScopeAsync(provider =>
            provider.GetRequiredService<ILegalParameterRepository>().ListChangesAsync(1, 50, Ct));
        var stored = items.Single(item => item.Id == change.Id);

        Assert.Equal("SolidarityFund", stored.Key);
        Assert.Equal(new DateOnly(2090, 1, 1), stored.EffectiveFrom);
        Assert.Equal(LegalParameterChangeAction.Created, stored.Action);
        Assert.Null(stored.OldValue);
        // jsonb reordena las propiedades y normaliza los espacios: se compara el contenido, no el texto.
        var roundTripped = JsonDocument.Parse(stored.NewValue!).RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, roundTripped.Length);
        Assert.Equal(4m, roundTripped[0].GetProperty("lowerBound").GetDecimal());
        Assert.Equal(16m, roundTripped[0].GetProperty("upperBound").GetDecimal());
        Assert.Equal(0.01m, roundTripped[0].GetProperty("rate").GetDecimal());
        Assert.Equal(JsonValueKind.Null, roundTripped[1].GetProperty("upperBound").ValueKind);
        Assert.Equal(0.012m, roundTripped[1].GetProperty("rate").GetDecimal());
        Assert.Equal(by, stored.ChangedBy);
        Assert.Equal("Operadora", stored.ChangedByName);
        Assert.Equal(at, stored.ChangedAt);
        Assert.Equal(LegalParameterChangeSource.Excel, stored.Source);
        Assert.Equal(batch, stored.BatchId);
        Assert.Equal("enero.xlsx", stored.FileName);
    }

    [Fact]
    public async Task ChangesPageNewestFirst()
    {
        var by = Guid.NewGuid();
        var changes = Enumerable.Range(1, 3)
            .Select(day => LegalParameterChange.Record(
                "Smmlv", new DateOnly(2095, 1, 1), LegalParameterChangeAction.Updated, "1", "2", by, "Operadora",
                new DateTimeOffset(2095, 1, day, 9, 0, 0, TimeSpan.Zero), LegalParameterChangeSource.Manual, null, null))
            .ToList();

        await SaveInScopeAsync(repository =>
        {
            foreach (var change in changes)
            {
                repository.AddChange(change);
            }
        });

        var (first, total) = await InScopeAsync(provider =>
            provider.GetRequiredService<ILegalParameterRepository>().ListChangesAsync(1, 2, Ct));
        var (second, _) = await InScopeAsync(provider =>
            provider.GetRequiredService<ILegalParameterRepository>().ListChangesAsync(2, 2, Ct));

        Assert.True(total >= 3);
        Assert.Equal([changes[2].Id, changes[1].Id], first.Select(item => item.Id));
        Assert.Equal(changes[0].Id, second[0].Id);
    }
}
