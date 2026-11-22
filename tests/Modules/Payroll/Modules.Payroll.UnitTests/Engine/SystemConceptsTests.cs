using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class SystemConceptsTests
{
    [Fact]
    public void TheTwelveSystemConceptsHaveTheContractIds()
    {
        (string Id, SystemConceptKey Key, ConceptKind Kind, bool? Constitutive)[] expected =
        [
            ("00000000-0000-0000-0001-000000000001", SystemConceptKey.ExtraDay, ConceptKind.Overtime, null),
            ("00000000-0000-0000-0001-000000000002", SystemConceptKey.ExtraNight, ConceptKind.Overtime, null),
            ("00000000-0000-0000-0001-000000000003", SystemConceptKey.NightSurcharge, ConceptKind.Overtime, null),
            ("00000000-0000-0000-0001-000000000004", SystemConceptKey.SundaySurcharge, ConceptKind.Overtime, null),
            ("00000000-0000-0000-0001-000000000005", SystemConceptKey.ExtraSundayDay, ConceptKind.Overtime, null),
            ("00000000-0000-0000-0001-000000000006", SystemConceptKey.ExtraSundayNight, ConceptKind.Overtime, null),
            ("00000000-0000-0000-0001-000000000007", SystemConceptKey.NightSundaySurcharge, ConceptKind.Overtime, null),
            ("00000000-0000-0000-0002-000000000001", SystemConceptKey.Commissions, ConceptKind.Income, true),
            ("00000000-0000-0000-0002-000000000002", SystemConceptKey.PrestationalBonus, ConceptKind.Income, true),
            ("00000000-0000-0000-0002-000000000003", SystemConceptKey.SalaryAdvance, ConceptKind.Income, null),
            ("00000000-0000-0000-0002-000000000004", SystemConceptKey.OtherIncome, ConceptKind.Income, null),
            ("00000000-0000-0000-0003-000000000001", SystemConceptKey.OtherDeduction, ConceptKind.Deduction, null),
        ];

        Assert.Equal(expected.Length, SystemConcepts.All.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var concept = SystemConcepts.All[index];
            Assert.Equal(Guid.Parse(expected[index].Id), concept.Id);
            Assert.Equal(expected[index].Key, concept.Key);
            Assert.Equal(expected[index].Kind, concept.Kind);
            Assert.Equal(expected[index].Constitutive, concept.DefaultSalaryConstitutive);
            Assert.Same(concept, SystemConcepts.Find(concept.Id));
        }
    }

    [Fact]
    public void IdsAreUnique() =>
        Assert.Equal(SystemConcepts.All.Count, SystemConcepts.All.Select(concept => concept.Id).Distinct().Count());

    [Fact]
    public void AnUnknownIdIsNotASystemConcept() => Assert.Null(SystemConcepts.Find(Guid.NewGuid()));
}
