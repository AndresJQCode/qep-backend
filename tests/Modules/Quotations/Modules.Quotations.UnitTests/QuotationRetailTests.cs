using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La cotizacion detal sobre el agregado: que se guarde, que sea una edicion, que apagarla y
/// prenderla no negocie con el piso de escala global, y que la cotizacion ya convertida solo la
/// acepte por la puerta del pedido.
///
/// El par de mutadores es el mismo de <see cref="Quotation.SetGlobalScaleFloor"/> y su gemelo
/// <c>AfterConversion</c>, por el mismo motivo: el pedido pendiente edita una cotizacion que
/// <c>EnsureEditable</c> ya no deja tocar.
/// </summary>
public sealed class QuotationRetailTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly ValidUntil = new(2026, 10, 31);

    private static readonly QuotationBillingAccount BillingAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
    };

    private static Quotation NewQuotation(QuotationBillingAccount? billingAccount = null) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            ValidUntil,
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            billingAccount,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static Quotation ConvertedQuotation()
    {
        var quotation = NewQuotation(BillingAccount);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        quotation.ConvertToOrder(AdvisorId, Now.AddHours(2));

        return quotation;
    }

    [Fact]
    public void ANewQuotationIsNotRetail()
    {
        Assert.False(NewQuotation().IsRetail);
    }

    [Fact]
    public void TurningRetailOnIsAnEditAndBumpsTheVersion()
    {
        var quotation = NewQuotation();
        var versionBefore = quotation.Version;
        var editor = new MemberId(Guid.CreateVersion7());
        var editedAt = Now.AddHours(1);

        quotation.SetIsRetail(true, editor, editedAt);

        Assert.True(quotation.IsRetail);
        Assert.Equal(versionBefore + 1, quotation.Version);
        Assert.Equal(editor, quotation.UpdatedBy);
        Assert.Equal(editedAt, quotation.UpdatedAt);
    }

    // Detal y piso global son excluyentes: en detal ninguna linea recibe descuento, asi que un
    // piso guardado no describiria nada de lo que la pantalla muestra. Se limpia al prender en
    // vez de quedar dormido para que la respuesta no publique un piso que no esta aplicando.
    [Fact]
    public void TurningRetailOnClearsTheGlobalScaleFloor()
    {
        var quotation = NewQuotation();
        quotation.SetGlobalScaleFloor(6, AdvisorId, Now.AddHours(1));

        quotation.SetIsRetail(true, AdvisorId, Now.AddHours(2));

        Assert.Null(quotation.GlobalScaleFloor);
    }

    // Y no vuelve al apagar: el piso es una eleccion del asesor, no un estado derivado. Volver a
    // ponerlo solo es volver a elegirlo.
    [Fact]
    public void TurningRetailOffDoesNotRestoreTheGlobalScaleFloor()
    {
        var quotation = NewQuotation();
        quotation.SetGlobalScaleFloor(6, AdvisorId, Now.AddHours(1));
        quotation.SetIsRetail(true, AdvisorId, Now.AddHours(2));

        quotation.SetIsRetail(false, AdvisorId, Now.AddHours(3));

        Assert.False(quotation.IsRetail);
        Assert.Null(quotation.GlobalScaleFloor);
    }

    [Fact]
    public void WithRetailOnSettingAGlobalScaleFloorIsRejected()
    {
        var quotation = NewQuotation();
        quotation.SetIsRetail(true, AdvisorId, Now.AddHours(1));

        var error = Assert.Throws<QuotationsDomainException>(
            () => quotation.SetGlobalScaleFloor(6, AdvisorId, Now.AddHours(2)));

        Assert.Equal("quotation.retail.floor_not_allowed", error.Code);
    }

    // Quitarlo si: null no pide descuento, y rechazarlo obligaria a apagar el detal primero para
    // poder limpiar un piso que el propio detal ya dejo en null.
    [Fact]
    public void WithRetailOnClearingTheGlobalScaleFloorIsStillAllowed()
    {
        var quotation = NewQuotation();
        quotation.SetIsRetail(true, AdvisorId, Now.AddHours(1));

        quotation.SetGlobalScaleFloor(null, AdvisorId, Now.AddHours(2));

        Assert.Null(quotation.GlobalScaleFloor);
    }

    [Fact]
    public void AConvertedQuotationRejectsTheOrdinaryRetailSetter()
    {
        var quotation = ConvertedQuotation();

        var error = Assert.Throws<QuotationsDomainException>(
            () => quotation.SetIsRetail(true, AdvisorId, Now.AddHours(3)));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }

    [Fact]
    public void AConvertedQuotationAcceptsTheAfterConversionRetailSetter()
    {
        var quotation = ConvertedQuotation();

        quotation.SetIsRetailAfterConversion(true, AdvisorId, Now.AddHours(3));

        Assert.True(quotation.IsRetail);
    }
    // Desde el 2026-09-23 el detal no tiene endpoint propio: viaja en el cuerpo del guardado, al
    // lado del piso global, y UpdateDetails lo aplica. Mismo contrato que el piso en 400773f.
    private static void SaveHeader(Quotation quotation, bool isRetail, int? globalScaleFloor) =>
        quotation.UpdateDetails(
            validUntil: ValidUntil,
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            repricing: null,
            isRetail,
            globalScaleFloor,
            AdvisorId,
            Now.AddHours(3));

    // Prender detal en el guardado limpia el piso aunque el cuerpo no lo mande: la pantalla manda
    // null en el mismo cuerpo, y el agregado no puede quedar con los dos a la vez.
    [Fact]
    public void SavingTheHeaderWithRetailOnTurnsItOnAndClearsTheFloor()
    {
        var quotation = NewQuotation();
        quotation.SetGlobalScaleFloor(6, AdvisorId, Now.AddHours(1));

        SaveHeader(quotation, isRetail: true, globalScaleFloor: null);

        Assert.True(quotation.IsRetail);
        Assert.Null(quotation.GlobalScaleFloor);
    }

    // El cuerpo contradictorio: prender detal y elegir un piso a la vez. Lo resuelve el dominio
    // con el mismo código que el mutador suelto, aunque la cotización guardada no fuera detal:
    // el guard mira el detal que va a quedar, no el que había.
    [Fact]
    public void SavingTheHeaderWithRetailOnAndAFloorIsRejected()
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(
            () => SaveHeader(quotation, isRetail: true, globalScaleFloor: 6));

        Assert.Equal("quotation.retail.floor_not_allowed", error.Code);
        // Rechazado antes de asignar nada: el agregado queda como estaba.
        Assert.False(quotation.IsRetail);
        Assert.Null(quotation.GlobalScaleFloor);
    }

    // Detal se aplica antes que el piso: apagarlo y elegir un piso en el mismo guardado funciona,
    // aunque la cotización guardada viniera con detal prendido.
    [Fact]
    public void SavingTheHeaderWithRetailOffAcceptsAFloorComingFromRetail()
    {
        var quotation = NewQuotation();
        quotation.SetIsRetail(true, AdvisorId, Now.AddHours(1));

        SaveHeader(quotation, isRetail: false, globalScaleFloor: 6);

        Assert.False(quotation.IsRetail);
        Assert.Equal(6, quotation.GlobalScaleFloor);
    }

    // Mandar el mismo detal que ya estaba no es un cambio: la foto del encabezado no se mueve y
    // el historial no gana una fila.
    [Fact]
    public void SavingTheSameRetailFlagIsNotAHeaderChange()
    {
        var quotation = NewQuotation();
        quotation.SetIsRetail(true, AdvisorId, Now.AddHours(1));
        var before = Modules.Quotations.Application.QuotationHeaderSnapshot.Of(quotation);

        SaveHeader(quotation, isRetail: true, globalScaleFloor: null);

        Assert.Null(Modules.Quotations.Application.QuotationChangeSummary.HeaderChanged(
            before, Modules.Quotations.Application.QuotationHeaderSnapshot.Of(quotation)));
    }
}
