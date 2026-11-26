using System.Globalization;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.UnitTests;

public sealed class LegalParameterImportAnalyzerTests
{
    private static readonly TenantId OperatorTenant = new(Guid.Parse("01900000-0000-7000-8000-000000000001"));
    private static readonly DateOnly Jan2026 = new(2026, 1, 1);
    private static readonly DateOnly Jan2027 = new(2027, 1, 1);
    private static readonly DateTimeOffset Today = new(2026, 6, 15, 15, 0, 0, TimeSpan.Zero);

    private const string Jan2027Text = "2027-01-01";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal ValueOf(LegalParameterKey key) => LegalParameterLabels.Of(key).Unit switch
    {
        LegalParameterUnit.Fraction => key == LegalParameterKey.HealthEmployeeRate ? 0.04m : 0.05m,
        LegalParameterUnit.Currency => 1_750_905m,
        LegalParameterUnit.Hours => 42m,
        LegalParameterUnit.Flag => 1m,
        _ => 25m,
    };

    private static readonly LegalBracketInput[] CurrentBrackets =
    [
        new(4m, 16m, 0.01m),
        new(16m, 17m, 0.02m),
        new(17m, 18m, 0.025m),
        new(18m, 19m, 0.03m),
        new(19m, 20m, 0.035m),
        new(20m, null, 0.04m),
    ];

    private sealed class World
    {
        public readonly List<LegalParameterValue> Values = Enum.GetValues<LegalParameterKey>()
            .Select(key => new LegalParameterValue(key, Jan2026, ValueOf(key)))
            .ToList();

        public readonly List<LegalParameterBracket> Brackets = CurrentBrackets
            .Select(input => new LegalParameterBracket(LegalBracketKey.SolidarityFund, Jan2026, input.LowerBound, input.UpperBound, input.Rate))
            .ToList();

        public List<CalculatedPeriodStamp> Stamps = [];
        public LegalParameterWorkbook Workbook = Template();
        public int StampReads;

        public LegalParameterImportAnalyzer Analyzer() =>
            new(new FakeImporter(this), new FakeRepository(this), new FakeUsage(this), new FakeClock());

        public Task<LegalParameterImportAnalysis> AnalyzeAsync(
            string? fileName = "carga.xlsx", long size = 1024) =>
            Analyzer().AnalyzeAsync(OperatorTenant, fileName, size, Stream.Null, Ct);
    }

    private sealed class FakeImporter(World world) : IExcelLegalParameterImporter
    {
        public Task<LegalParameterWorkbook> ReadAsync(Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(world.Workbook);
    }

    private sealed class FakeRepository(World world) : ILegalParameterRepository
    {
        public Task<IReadOnlyList<LegalParameterValue>> ListValuesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LegalParameterValue>>(world.Values.ToArray());

        public Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LegalParameterBracket>>(world.Brackets.ToArray());

        public void Add(LegalParameterValue value) => throw new NotSupportedException();

        public void Remove(LegalParameterValue value) => throw new NotSupportedException();

        public void AddBrackets(IEnumerable<LegalParameterBracket> brackets) => throw new NotSupportedException();

        public void RemoveBrackets(IEnumerable<LegalParameterBracket> brackets) => throw new NotSupportedException();

        public void AddChange(LegalParameterChange change) => throw new NotSupportedException();

        public Task<(IReadOnlyList<LegalParameterChange> Items, int Total)> ListChangesAsync(
            int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeUsage(World world) : ILegalParameterUsage
    {
        public Task<IReadOnlyList<CalculatedPeriodStamp>> ListCalculatedPeriodsOfAllTenantsAsync(CancellationToken cancellationToken)
        {
            world.StampReads++;
            return Task.FromResult<IReadOnlyList<CalculatedPeriodStamp>>(world.Stamps.ToArray());
        }
    }

    private sealed class FakeClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(Today, TimeZoneInfo.Utc));
    }

    /// <summary>Lo que traería la plantilla sin tocar: las 27 claves anuales prellenadas, las tres que no son anuales vacías y el conjunto.</summary>
    private static LegalParameterWorkbook Template()
    {
        var row = 1;
        var parameters = Enum.GetValues<LegalParameterKey>()
            .Select(key =>
            {
                row++;
                return LegalParameterLabels.Of(key).Yearly
                    ? new ExcelLegalParameterRow(row, key.ToString(), Jan2027Text, Text(ValueOf(key)))
                    : new ExcelLegalParameterRow(row, key.ToString(), null, null);
            })
            .ToList();
        var brackets = CurrentBrackets
            .Select((input, index) => new ExcelLegalBracketRow(
                index + 2,
                "SolidarityFund",
                Jan2027Text,
                Text(input.LowerBound),
                input.UpperBound is { } upper ? Text(upper) : null,
                Text(input.Rate)))
            .ToList();
        return new LegalParameterWorkbook(parameters, brackets);
    }

    private static LegalParameterWorkbook WithParameters(
        World world, Func<ExcelLegalParameterRow, ExcelLegalParameterRow?> change) =>
        world.Workbook with
        {
            Parameters = world.Workbook.Parameters.Select(change).OfType<ExcelLegalParameterRow>().ToList(),
        };

    private static LegalParameterImportItemDto ItemOf(LegalParameterImportAnalysis analysis, string key) =>
        analysis.Preview.Items.Single(item => item.Key == key);

    [Fact]
    public async Task AnUntouchedTemplateIsAllCarryAndOneSet()
    {
        var world = new World();

        var analysis = await world.AnalyzeAsync();

        Assert.Equal(new LegalParameterImportSummaryDto(0, 28, 0, 0, 0), analysis.Preview.Summary);
        Assert.True(analysis.Preview.CanApply);
        Assert.Empty(analysis.Preview.Warnings);
        Assert.Equal(28, analysis.Preview.Items.Count);
        Assert.Equal(28, analysis.Carried);
        Assert.Equal(27, analysis.ParameterEdits.Count);
        Assert.Single(analysis.BracketEdits);
        Assert.All(analysis.Preview.Items, item => Assert.Equal(LegalParameterImportActions.Carry, item.Action));
        var set = analysis.Preview.Items.Single(item => item.Kind == "Brackets");
        Assert.Equal("Tramos", set.Sheet);
        Assert.Equal(6, set.NewBrackets!.Count);
        Assert.Equal(set.OldBrackets!.Select(b => b.Rate), set.NewBrackets.Select(b => b.Rate));
    }

    [Fact]
    public async Task OneChangedValueIsCreate()
    {
        var world = new World();
        world.Workbook = WithParameters(
            world, row => row.Key == "Smmlv" ? row with { NewValue = "2000000" } : row);

        var analysis = await world.AnalyzeAsync();

        var item = ItemOf(analysis, "Smmlv");
        Assert.Equal(LegalParameterImportActions.Create, item.Action);
        Assert.Equal(1_750_905m, item.OldValue);
        Assert.Equal(2_000_000m, item.NewValue);
        Assert.Equal(Jan2027, item.EffectiveFrom);
        Assert.Equal("Parameter", item.Kind);
        Assert.Equal("Parámetros", item.Sheet);
        Assert.Equal("Salario mínimo mensual legal vigente", item.Name);
        Assert.Equal(new LegalParameterImportSummaryDto(1, 27, 0, 0, 0), analysis.Preview.Summary);
    }

    [Fact]
    public async Task AnExistingRowWithANewValueIsUpdate()
    {
        var world = new World();
        world.Workbook = WithParameters(
            world, row => row.Key == "Smmlv" ? row with { EffectiveFrom = "2026-01-01", NewValue = "1800000" } : row);

        var analysis = await world.AnalyzeAsync();

        var item = ItemOf(analysis, "Smmlv");
        Assert.Equal(LegalParameterImportActions.Update, item.Action);
        Assert.Equal(1_750_905m, item.OldValue);
        Assert.Equal(1_800_000m, item.NewValue);
        Assert.Contains(analysis.ParameterEdits, edit => edit.Key == LegalParameterKey.Smmlv && edit.Value == 1_800_000m);
    }

    [Fact]
    public async Task AnExistingRowWithTheSameValueIsUnchanged()
    {
        var world = new World();
        world.Workbook = WithParameters(
            world, row => row.Key == "Smmlv" ? row with { EffectiveFrom = "2026-01-01" } : row);

        var analysis = await world.AnalyzeAsync();

        Assert.Equal(LegalParameterImportActions.Unchanged, ItemOf(analysis, "Smmlv").Action);
        Assert.Equal(1, analysis.Preview.Summary.Unchanged);
        Assert.DoesNotContain(analysis.ParameterEdits, edit => edit.Key == LegalParameterKey.Smmlv);
    }

    [Fact]
    public async Task RateTwelveIsAnErrorWithTheExactMessage()
    {
        var world = new World();
        world.Workbook = WithParameters(
            world, row => row.Key == "HealthEmployeeRate" ? row with { NewValue = "12" } : row);

        var analysis = await world.AnalyzeAsync();

        var item = ItemOf(analysis, "HealthEmployeeRate");
        Assert.Equal(LegalParameterImportActions.Error, item.Action);
        Assert.Equal(HrErrorCodes.LegalParameterImportRow.ValueInvalid, item.Code);
        Assert.Equal(
            "La tasa tiene que estar entre 0 % y 100 %. ¿Escribiste 12 en una celda sin formato de porcentaje?", item.Message);
        Assert.False(analysis.Preview.CanApply);
        Assert.Equal(1, analysis.Preview.Summary.Error);
    }

    [Fact]
    public async Task ABracketRateTwelveUsesTheSameMessage()
    {
        var world = new World();
        world.Workbook = world.Workbook with
        {
            Brackets = world.Workbook.Brackets.Select(row => row.RowNumber == 3 ? row with { Rate = "2" } : row).ToList(),
        };

        var analysis = await world.AnalyzeAsync();

        var set = analysis.Preview.Items.Single(item => item.Kind == "Brackets");
        Assert.Equal(HrErrorCodes.LegalParameterImportRow.BracketsInvalid, set.Code);
        Assert.Equal(LegalParameterRules.RateOutOfRangeMessage, set.Message);
    }

    [Fact]
    public async Task ABracketRateZeroDoesNotSuggestAMissingPercentFormat()
    {
        var world = new World();
        world.Workbook = world.Workbook with
        {
            Brackets = world.Workbook.Brackets.Select(row => row.RowNumber == 3 ? row with { Rate = "0" } : row).ToList(),
        };

        var analysis = await world.AnalyzeAsync();

        var set = analysis.Preview.Items.Single(item => item.Kind == "Brackets");
        Assert.Equal(HrErrorCodes.LegalParameterImportRow.BracketsInvalid, set.Code);
        Assert.NotEqual(LegalParameterRules.RateOutOfRangeMessage, set.Message);
        Assert.Contains("sin incluir el 0 %", set.Message);
    }

    [Fact]
    public async Task UnknownKeyAndBadDateAreRowErrors()
    {
        var world = new World();
        world.Workbook = world.Workbook with
        {
            Parameters =
            [
                new ExcelLegalParameterRow(2, "smmlv", Jan2027Text, "2000000"),
                new ExcelLegalParameterRow(3, "Smmlv", "01/01/2027", "2000000"),
                new ExcelLegalParameterRow(4, "Smmlv", null, "2000000"),
                new ExcelLegalParameterRow(5, "IbcCapSmmlv", Jan2027Text, "abc"),
            ],
            Brackets = [],
        };

        var analysis = await world.AnalyzeAsync();

        Assert.Equal(
            [
                HrErrorCodes.LegalParameterImportRow.KeyUnknown,
                HrErrorCodes.LegalParameterImportRow.DateInvalid,
                HrErrorCodes.LegalParameterImportRow.DateInvalid,
                HrErrorCodes.LegalParameterImportRow.ValueInvalid,
            ],
            analysis.Preview.Items.Select(item => item.Code));
        Assert.Equal([[2], [3], [4], [5]], analysis.Preview.Items.Select(item => item.Rows.ToArray()));
        Assert.Equal(4, analysis.Preview.Summary.Error);
    }

    [Fact]
    public async Task TheSameKeyAndDateTwiceIsDuplicated()
    {
        var world = new World();
        world.Workbook = world.Workbook with
        {
            Parameters =
            [
                new ExcelLegalParameterRow(2, "Smmlv", Jan2027Text, "2000000"),
                new ExcelLegalParameterRow(3, "Smmlv", Jan2027Text, "2100000"),
            ],
            Brackets = [],
        };

        var analysis = await world.AnalyzeAsync();

        Assert.Equal(LegalParameterImportActions.Create, analysis.Preview.Items[0].Action);
        Assert.Equal(LegalParameterImportActions.Error, analysis.Preview.Items[1].Action);
        Assert.Equal(HrErrorCodes.LegalParameterImportRow.Duplicated, analysis.Preview.Items[1].Code);
        Assert.Equal([3], analysis.Preview.Items[1].Rows);
    }

    [Fact]
    public async Task ASetIsOneItemWithAllItsRows()
    {
        var world = new World();

        var analysis = await world.AnalyzeAsync();

        var set = Assert.Single(analysis.Preview.Items, item => item.Kind == "Brackets");
        Assert.Equal([2, 3, 4, 5, 6, 7], set.Rows);
        Assert.Equal("SolidarityFund", set.Key);
        Assert.Equal("Fondo de solidaridad pensional", set.Name);
    }

    [Fact]
    public async Task ARowSetSplitByBlankRowsIsStillOneSet()
    {
        var world = new World();
        world.Workbook = world.Workbook with
        {
            Brackets = world.Workbook.Brackets.Select(row => row with { RowNumber = row.RowNumber * 2 }).ToList(),
        };

        var analysis = await world.AnalyzeAsync();

        var set = Assert.Single(analysis.Preview.Items, item => item.Kind == "Brackets");
        Assert.Equal([4, 6, 8, 10, 12, 14], set.Rows);
    }

    [Fact]
    public async Task AGapInTheSetIsBracketsInvalid()
    {
        var world = new World();
        world.Workbook = world.Workbook with
        {
            Brackets = world.Workbook.Brackets.Where(row => row.RowNumber != 4).ToList(),
        };

        var analysis = await world.AnalyzeAsync();

        var set = analysis.Preview.Items.Single(item => item.Kind == "Brackets");
        Assert.Equal(LegalParameterImportActions.Error, set.Action);
        Assert.Equal(HrErrorCodes.LegalParameterImportRow.BracketsInvalid, set.Code);
        Assert.Contains("contiguos", set.Message);
        Assert.Equal([2, 3, 5, 6, 7], set.Rows);
    }

    [Fact]
    public async Task ARowInAUsedRangeIsInUse()
    {
        var world = new World();
        world.Stamps = [new CalculatedPeriodStamp(new DateOnly(2026, 8, 1), Guid.NewGuid())];
        world.Workbook = world.Workbook with
        {
            Parameters = [new ExcelLegalParameterRow(2, "Smmlv", "2026-07-01", "1800000")],
            Brackets = [],
        };

        var analysis = await world.AnalyzeAsync();

        var item = Assert.Single(analysis.Preview.Items);
        Assert.Equal(LegalParameterImportActions.Error, item.Action);
        Assert.Equal(HrErrorCodes.LegalParameterImportRow.InUse, item.Code);
        Assert.Contains("1 período(s)", item.Message);
        Assert.Empty(analysis.ParameterEdits);
    }

    [Fact]
    public async Task ARowAfterTheLastCalculatedPeriodIsNotInUse()
    {
        var world = new World();
        world.Stamps = [new CalculatedPeriodStamp(new DateOnly(2026, 8, 1), Guid.NewGuid())];

        var analysis = await world.AnalyzeAsync();

        Assert.True(analysis.Preview.CanApply);
        Assert.Equal(1, world.StampReads);
    }

    [Fact]
    public async Task MissingNextYearRowsAreWarnings()
    {
        var world = new World();
        world.Workbook = new LegalParameterWorkbook(
            world.Workbook.Parameters.Where(row => row.Key != "Smmlv").ToList(), []);

        var analysis = await world.AnalyzeAsync();

        Assert.True(analysis.Preview.CanApply);
        Assert.Equal(["Smmlv", "SolidarityFund"], analysis.Preview.Warnings.Select(warning => warning.Key));
        Assert.All(
            analysis.Preview.Warnings,
            warning => Assert.Equal(HrErrorCodes.LegalParameterImportNextYearMissing, warning.Code));
        Assert.Contains("2027", analysis.Preview.Warnings[0].Message);
    }

    [Fact]
    public async Task MoreThanFiveHundredRowsIsTooManyRows()
    {
        var world = new World();
        world.Workbook = new LegalParameterWorkbook(
            Enumerable.Range(2, 501).Select(row => new ExcelLegalParameterRow(row, "Smmlv", Jan2027Text, "2000000")).ToList(), []);

        var error = await Assert.ThrowsAsync<HrDomainException>(() => world.AnalyzeAsync());

        Assert.Equal(HrErrorCodes.LegalParameterImportTooManyRows, error.Code);
    }

    [Fact]
    public async Task AnEmptyNewValueIsIgnored()
    {
        var world = new World();
        world.Workbook = new LegalParameterWorkbook(
            [new ExcelLegalParameterRow(2, "Smmlv", Jan2027Text, null), new ExcelLegalParameterRow(3, "nada", null, null)], []);

        var analysis = await world.AnalyzeAsync();

        Assert.Empty(analysis.Preview.Items);
        Assert.Equal(new LegalParameterImportSummaryDto(0, 0, 0, 0, 0), analysis.Preview.Summary);
        Assert.True(analysis.Preview.CanApply);
    }

    [Fact]
    public async Task AFileWithoutRowsIsEmpty()
    {
        var world = new World();
        world.Workbook = new LegalParameterWorkbook([], []);

        var error = await Assert.ThrowsAsync<HrDomainException>(() => world.AnalyzeAsync());

        Assert.Equal(HrErrorCodes.LegalParameterImportEmpty, error.Code);
    }

    [Theory]
    [InlineData(null, 10, "hr.legal_parameter.import.file_required")]
    [InlineData("carga.xlsx", 0, "hr.legal_parameter.import.file_required")]
    [InlineData("carga.csv", 10, "hr.legal_parameter.import.extension_invalid")]
    [InlineData("carga.xlsx", 1_048_577, "hr.legal_parameter.import.too_large")]
    public async Task TheFileIsCheckedBeforeItIsRead(string? fileName, long size, string code)
    {
        var world = new World();

        var error = await Assert.ThrowsAsync<HrDomainException>(() => world.AnalyzeAsync(fileName, size));

        Assert.Equal(code, error.Code);
    }
}
