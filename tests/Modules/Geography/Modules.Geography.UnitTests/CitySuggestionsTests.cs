using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class CitySuggestionsTests
{
    private static readonly string[] TieNames = ["CALIE", "CALIC", "CALID", "CALIA", "CALIB"];

    private static CitySuggestionCandidate Candidate(string name, string? divipolaName = null) =>
        new(CityId.New(), name, divipolaName ?? name);

    [Fact]
    public void TheScoreOrdersCaliOverCalimaOverCandelaria()
    {
        Assert.Equal(80, CitySuggestions.Score("CALLI", "CALI"));
        Assert.Equal(50, CitySuggestions.Score("CALLI", "CALIMA"));
        Assert.Equal(40, CitySuggestions.Score("CALLI", "CANDELARIA"));
    }

    [Fact]
    public void TwoEmptyTextsScoreZero()
    {
        Assert.Equal(0, CitySuggestions.Score(string.Empty, string.Empty));
    }

    [Fact]
    public void RankKeepsTheFloorInclusiveAndDropsWhatIsBelow()
    {
        var cali = Candidate("CALI", "SANTIAGO DE CALI");
        var calima = Candidate("CALIMA");
        var candelaria = Candidate("CANDELARIA");
        var dagua = Candidate("DAGUA");

        var result = CitySuggestions.Rank("CALLI", [cali, calima, candelaria, dagua]);

        Assert.Equal(
            [new CitySuggestion(cali.CityId, 80), new CitySuggestion(calima.CityId, 50)],
            result);
    }

    [Fact]
    public void TheBestOfBothNamesWins()
    {
        var cali = Candidate("CALI", "SANTIAGO DE CALI");

        var result = CitySuggestions.Rank("SANTIAGO DE CALLI", [cali]);

        Assert.Equal([new CitySuggestion(cali.CityId, 94)], result);
    }

    [Fact]
    public void MedelinScores88()
    {
        Assert.Equal(88, CitySuggestions.Score("MEDELIN", "MEDELLIN"));
    }

    [Fact]
    public void NoCityPassesTheFloorGivesEmpty()
    {
        var valle = new[] { Candidate("CALI"), Candidate("DAGUA"), Candidate("YUMBO"), Candidate("JAMUNDI") };

        Assert.Empty(CitySuggestions.Rank("VEREDA EL PLACER", valle));
    }

    [Fact]
    public void AtMostThreeTiesOrderedByName()
    {
        var cities = TieNames.Select(n => Candidate(n)).ToList();

        var result = CitySuggestions.Rank("CALIX", cities);

        Assert.Equal(CitySuggestions.MaxSuggestions, result.Count);
        var expected = cities.OrderBy(c => c.Name, StringComparer.Ordinal).Take(3).Select(c => c.CityId);
        Assert.Equal(expected, result.Select(s => s.CityId));
        Assert.All(result, s => Assert.Equal(80, s.Similarity));
    }
}
