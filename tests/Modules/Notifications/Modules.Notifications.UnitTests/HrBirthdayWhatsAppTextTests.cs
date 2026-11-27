using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>HR birthday notices, spec 2026-11-26 §5.4: the text of a name list inside a WhatsApp variable. Meta rejects empty variables and line
/// breaks, and a variable has a length limit, so the list folds to «Ana, Luis y 3 más».</summary>
public sealed class HrBirthdayWhatsAppTextTests
{
    private const int Max = 200;

    [Fact]
    public void EmptyIsNadie()
    {
        Assert.Equal("nadie", HrBirthdayWhatsAppText.Names([], 0, Max));
        Assert.Equal("nadie", HrBirthdayWhatsAppText.Names([], 0, 10));
    }

    [Fact]
    public void FewNamesAreJoinedWithCommas()
    {
        Assert.Equal("Ana Pérez", HrBirthdayWhatsAppText.Names(["Ana Pérez"], 1, Max));
        Assert.Equal("Ana, Luis, Marta", HrBirthdayWhatsAppText.Names(["Ana", "Luis", "Marta"], 3, Max));
    }

    [Fact]
    public void ManyAreFoldedWithYNMas()
    {
        var names = new[] { "Ana", "Luis", "Marta", "Pedro" };

        var text = HrBirthdayWhatsAppText.Names(names, 4, 20);

        Assert.Equal("Ana, Luis y 2 más", text);
        Assert.True(text.Length <= 20);
    }

    [Fact]
    public void TotalAboveShownAddsYNMas() =>
        Assert.Equal("Ana, Luis y 3 más", HrBirthdayWhatsAppText.Names(["Ana", "Luis"], 5, Max));

    [Fact]
    public void ANameThatAloneDoesNotFitStillGivesTheCount() =>
        Assert.Equal("3 personas", HrBirthdayWhatsAppText.Names([new string('x', 60), "Luis"], 3, 30));

    [Fact]
    public void TheDateIsSpanishWithoutYearAndNullIsADash()
    {
        Assert.Equal("viernes 4 de diciembre", HrBirthdayWhatsAppText.Date(new DateOnly(2026, 12, 4)));
        Assert.Equal("lunes 1 de marzo", HrBirthdayWhatsAppText.Date(new DateOnly(2027, 3, 1)));
        Assert.Equal("—", HrBirthdayWhatsAppText.Date(null));
    }
}
