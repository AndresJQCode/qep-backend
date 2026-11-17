using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Owner, 2026-11-14 (revisión final, I1): en el alta manual con cliente vinculado, el correo y el teléfono del cliente llenan los
// campos de contacto que el solicitante dejó vacíos. Lo que trae el solicitante siempre gana. Owner, 2026-11-15 (P3): el teléfono del
// cliente puede ser fijo, así que va al fijo de la foto y nunca al celular: una plantilla de WhatsApp a un fijo la rechaza Meta.
public sealed class TicketRequesterCustomerContactTests
{
    private static readonly TicketRequester Ana = new("Ana", "Gómez", null, null, null, null, null, null, "CO", null);

    private static SupportCustomerContact Customer(string? email, string? phone) =>
        new(Guid.CreateVersion7(), "Novasteel SAS", true, email, phone);

    [Fact]
    public void WithoutRequesterTheSnapshotIsJustTheCustomerContactInTheGivenCountry()
    {
        var merged = TicketRequesterNormalizer.WithCustomerContact(null, Customer("compras@novasteel.co", "+573001234567"), "CO");

        Assert.Equal(new TicketRequester(null, null, null, null, null, "compras@novasteel.co", null, "+573001234567", "CO", null), merged);
    }

    [Fact]
    public void WithoutRequesterAndWithoutCustomerContactThereIsNoSnapshot() =>
        Assert.Null(TicketRequesterNormalizer.WithCustomerContact(null, Customer(null, "  "), "CO"));

    [Fact]
    public void ARequesterWithoutContactKeepsItsNamesAndTakesTheCustomerContact()
    {
        var merged = TicketRequesterNormalizer.WithCustomerContact(Ana, Customer(" compras@novasteel.co ", "+573001234567"), "PE");

        Assert.Equal(Ana with { Email = "compras@novasteel.co", LandlinePhone = "+573001234567" }, merged);
        Assert.Null(merged!.MobilePhone);
    }

    [Fact]
    public void TheRequesterContactAlwaysWins()
    {
        var requester = Ana with { Email = "ana@example.com", MobilePhone = "+573109876543", LandlinePhone = "+5716012345" };

        var merged = TicketRequesterNormalizer.WithCustomerContact(requester, Customer("compras@novasteel.co", "+573001234567"), "CO");

        Assert.Equal(requester, merged);
    }

    [Fact]
    public void EachFieldFallsBackOnItsOwn()
    {
        var merged = TicketRequesterNormalizer.WithCustomerContact(
            Ana with { Email = "ana@example.com" }, Customer("compras@novasteel.co", "+573001234567"), "CO");

        Assert.Equal(Ana with { Email = "ana@example.com", LandlinePhone = "+573001234567" }, merged);
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

    // Revisión final, FU2/FU4: el correo del cliente pasa por la misma regla de formato que el del solicitante; uno que no la
    // cumple no se copia, igual que uno demasiado largo.
    [Fact]
    public void ACustomerEmailWithoutTheRequesterFormatIsNotCopied()
    {
        var merged = TicketRequesterNormalizer.WithCustomerContact(Ana, Customer("compras-novasteel.co", "+573001234567"), "CO");

        Assert.Equal(Ana with { LandlinePhone = "+573001234567" }, merged);
    }

    // P3: con celular del solicitante y sin fijo, el teléfono del cliente llena el fijo y el celular del solicitante se queda.
    [Fact]
    public void TheCustomerPhoneNeverReplacesOrFillsTheMobile()
    {
        var withMobile = Ana with { MobilePhone = "+573109876543" };

        var merged = TicketRequesterNormalizer.WithCustomerContact(withMobile, Customer(null, "+5716012345"), "CO");

        Assert.Equal(withMobile with { LandlinePhone = "+5716012345" }, merged);
    }
}
