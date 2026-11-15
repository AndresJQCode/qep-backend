using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Owner, 2026-11-14 (revisión final, I1): en el alta manual con cliente vinculado, el correo y el celular del cliente llenan los
// campos de contacto que el solicitante dejó vacíos. Lo que trae el solicitante siempre gana.
public sealed class TicketRequesterCustomerContactTests
{
    private static readonly TicketRequester Ana = new("Ana", "Gómez", null, null, null, null, null, null, "CO", null);

    private static SupportCustomerContact Customer(string? email, string? mobilePhone) =>
        new(Guid.CreateVersion7(), "Novasteel SAS", true, email, mobilePhone);

    [Fact]
    public void WithoutRequesterTheSnapshotIsJustTheCustomerContactInTheGivenCountry()
    {
        var merged = TicketRequesterNormalizer.WithCustomerContact(null, Customer("compras@novasteel.co", "+573001234567"), "CO");

        Assert.Equal(new TicketRequester(null, null, null, null, null, "compras@novasteel.co", "+573001234567", null, "CO", null), merged);
    }

    [Fact]
    public void WithoutRequesterAndWithoutCustomerContactThereIsNoSnapshot() =>
        Assert.Null(TicketRequesterNormalizer.WithCustomerContact(null, Customer(null, "  "), "CO"));

    [Fact]
    public void ARequesterWithoutContactKeepsItsNamesAndTakesTheCustomerContact()
    {
        var merged = TicketRequesterNormalizer.WithCustomerContact(Ana, Customer(" compras@novasteel.co ", "+573001234567"), "PE");

        Assert.Equal(Ana with { Email = "compras@novasteel.co", MobilePhone = "+573001234567" }, merged);
    }

    [Fact]
    public void TheRequesterContactAlwaysWins()
    {
        var requester = Ana with { Email = "ana@example.com", MobilePhone = "+573109876543" };

        var merged = TicketRequesterNormalizer.WithCustomerContact(requester, Customer("compras@novasteel.co", "+573001234567"), "CO");

        Assert.Equal(requester, merged);
    }

    [Fact]
    public void EachFieldFallsBackOnItsOwn()
    {
        var merged = TicketRequesterNormalizer.WithCustomerContact(
            Ana with { Email = "ana@example.com" }, Customer("compras@novasteel.co", "+573001234567"), "CO");

        Assert.Equal(Ana with { Email = "ana@example.com", MobilePhone = "+573001234567" }, merged);
    }

    // La foto tiene los topes de sus columnas y el celular va en E.164: un contacto del cliente que no cabe no se recorta ni se
    // guarda a medias, simplemente no se copia.
    [Fact]
    public void ACustomerContactThatDoesNotFitTheSnapshotIsNotCopied()
    {
        var tooLong = new string('a', TicketRequester.EmailMaxLength - "@x.co".Length + 1) + "@x.co";

        var merged = TicketRequesterNormalizer.WithCustomerContact(Ana, Customer(tooLong, "3001234567"), "CO");

        Assert.Equal(Ana, merged);
    }
}
