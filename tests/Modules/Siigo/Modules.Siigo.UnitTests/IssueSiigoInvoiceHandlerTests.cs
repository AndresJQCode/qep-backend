using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Audit.Domain;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Spec 2026-11-24 §3: qué deja <c>IssueSiigoInvoiceHandler</c> según lo que contesta Siigo al reenviar una <c>Pending</c>. Se siembran
/// las filas, así que va por el camino de reanudación y no se arma ninguna factura.
/// </summary>
public sealed class IssueSiigoInvoiceHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    // El caso de producción del 2026-11-24.
    [Fact]
    public async Task ATimeoutThenADocumentsServiceOnTheResendStaysPendingWithTheSameKey()
    {
        var row = PendingRow();
        var harness = new Harness([row], Transient("timeout"), Rejected("documents_service"));
        var second = Now.AddMinutes(1);

        await harness.Issue();
        harness.Clock.UtcNow = second;
        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal(Now, row.UnansweredAt);
        Assert.Equal(second, row.LastUnansweredAt);
        Assert.Equal(("siigo.invoice.siigo_service", "documents_service"), (row.ErrorCode, row.ErrorDetail));
        Assert.Equal([row.IdempotencyKey, row.IdempotencyKey], harness.Api.Keys);
        Assert.Equal(["siigo.invoice.unanswered"], harness.Audit.Actions);
    }

    [Fact]
    public async Task ATimeoutThenADataRejectionIsNotRejected()
    {
        var row = PendingRow();
        var harness = new Harness([row], Transient("timeout"), Rejected("invalid_reference"));

        await harness.Issue();
        harness.Clock.UtcNow = Now.AddMinutes(1);
        var result = await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal("Pending", Assert.Single(result).Status);
        Assert.Equal(("siigo.invoice.rejected", "invalid_reference"), (row.ErrorCode, row.ErrorDetail));
        Assert.Equal(Now, row.LastUnansweredAt);
        Assert.Null(row.SendingSince);
        Assert.Equal(["siigo.invoice.unanswered", "siigo.invoice.unanswered"], harness.Audit.Actions);
    }

    [Fact]
    public async Task ADataRejectionOnTheFirstAttemptIsRejected()
    {
        var row = PendingRow();
        var harness = new Harness([row], Rejected("invalid_reference"));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Rejected, row.Status);
        Assert.Null(row.UnansweredAt);
        Assert.Equal([SiigoAuditActions.InvoiceRejected], harness.Audit.Actions);
    }

    [Fact]
    public async Task AServiceOutageOnTheFirstAttemptAwaitsConfirmation()
    {
        var row = PendingRow();
        var harness = new Harness([row], Rejected("documents_service"));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal(Now, row.UnansweredAt);
        Assert.Equal(("siigo.invoice.siigo_service", "documents_service"), (row.ErrorCode, row.ErrorDetail));
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered], harness.Audit.Actions);
    }

    [Fact]
    public async Task ATooManyRequestsAwaitsConfirmation()
    {
        var row = PendingRow();
        var harness = new Harness([row], Transient("http_429"));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal(Now, row.UnansweredAt);
        Assert.Equal("siigo.invoice.rate_limited", row.ErrorCode);
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered], harness.Audit.Actions);
    }

    // D-2 (owner, 2026-11-24): en estos dos la factura nunca salió de QEP.
    [Theory]
    [InlineData("partner_id_missing")]
    [InlineData("auth_without_token")]
    public async Task AMissingPartnerIdIsNotAnUnansweredAttempt(string reason)
    {
        var row = PendingRow();
        var harness = new Harness([row], Transient(reason));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Null(row.UnansweredAt);
        Assert.Null(row.SendingSince);
        Assert.Equal("siigo.invoice.unavailable", row.ErrorCode);
        Assert.Empty(harness.Audit.Actions);
    }

    [Fact]
    public async Task AResumeOfASendThatDiedIsMarkedUnansweredBeforeSendingAgain()
    {
        var row = PendingRow();
        row.BeginSend(Now.AddMinutes(-9));
        var harness = new Harness([row], Created());

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Created, row.Status);
        Assert.Equal(Now, row.UnansweredAt);
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered, SiigoAuditActions.InvoiceCreated], harness.Audit.Actions);
    }

    // Una fila que la versión anterior dejó sin unanswered_at durante el despliegue: el rechazo del reenvío no la libera.
    [Fact]
    public async Task ADataRejectionOnARowThePreviousVersionLeftUnansweredStaysPending()
    {
        var row = PendingRow();
        row.BeginSend(Now.AddMinutes(-1));
        row.MarkTransient("siigo.invoice.timeout", Now.AddMinutes(-1));
        var harness = new Harness([row], Rejected("invalid_reference"));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal(Now.AddMinutes(-1), row.UnansweredAt);
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered, SiigoAuditActions.InvoiceUnanswered], harness.Audit.Actions);
    }

    [Fact]
    public async Task TheFreightIsDiscardedOnlyWhenTheOrderInvoiceIsReallyRejected()
    {
        var unansweredOrder = PendingRow();
        unansweredOrder.BeginSend(Now.AddMinutes(-1));
        unansweredOrder.MarkUnanswered("siigo.invoice.timeout", null, Now.AddMinutes(-1));
        var waitingFreight = PendingRow(SiigoInvoiceKind.Freight);

        await new Harness([unansweredOrder, waitingFreight], Rejected("invalid_reference")).Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, unansweredOrder.Status);
        Assert.Equal(SiigoInvoiceStatus.Pending, waitingFreight.Status);

        var rejectedOrder = PendingRow();
        var discardedFreight = PendingRow(SiigoInvoiceKind.Freight);

        await new Harness([rejectedOrder, discardedFreight], Rejected("invalid_reference")).Issue();

        Assert.Equal(SiigoInvoiceStatus.Rejected, rejectedOrder.Status);
        Assert.Equal(SiigoInvoiceStatus.Failed, discardedFreight.Status);
        Assert.Equal("siigo.invoice.order_rejected", discardedFreight.ErrorCode);
    }

    // La de flete que esperaba nunca salió: si se queda con el reclamo, el reenvío la tomaría por un envío cortado.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFreightThatWaitedForTheOrderReleasesItsClaimAndIsNeverUnanswered(bool serviceError)
    {
        var order = PendingRow();
        var freight = PendingRow(SiigoInvoiceKind.Freight);
        SiigoInvoiceOutcome first = serviceError ? Rejected("documents_service") : Transient("timeout");
        var harness = new Harness([order, freight], first, Created(), Created());

        await harness.Issue();

        Assert.Null(freight.SendingSince);
        Assert.Null(freight.UnansweredAt);

        harness.Clock.UtcNow = Now.AddMinutes(9);
        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Created, freight.Status);
        Assert.Null(freight.UnansweredAt);
        Assert.Equal(
            [order.IdempotencyKey, order.IdempotencyKey, freight.IdempotencyKey], harness.Api.Keys);
    }

    // Un redespliegue corta el envío de la del pedido con las dos filas reclamadas. La de flete sólo sale después de que la del pedido
    // existe, así que nunca salió: su reclamo vencido no es un envío sin respuesta.
    [Fact]
    public async Task AResumeAfterACutOrderSendLeavesTheFreightWithoutUnansweredAt()
    {
        var order = PendingRow();
        var freight = PendingRow(SiigoInvoiceKind.Freight);
        order.BeginSend(Now.AddMinutes(-9));
        freight.BeginSend(Now.AddMinutes(-9));
        var harness = new Harness([order, freight], Transient("timeout"));

        await harness.Issue();

        Assert.Equal(Now, order.UnansweredAt);
        Assert.Null(freight.UnansweredAt);
        Assert.Null(freight.SendingSince);
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered], harness.Audit.Actions);
    }

    [Fact]
    public async Task AFreightWhoseOrderSendWasCutGoesOutWithoutEverHavingBeenUnanswered()
    {
        var order = PendingRow();
        var freight = PendingRow(SiigoInvoiceKind.Freight);
        order.BeginSend(Now.AddMinutes(-9));
        freight.BeginSend(Now.AddMinutes(-9));

        await new Harness([order, freight], Created(), Created()).Issue();

        Assert.Equal(SiigoInvoiceStatus.Created, freight.Status);
        Assert.Null(freight.UnansweredAt);
    }

    // Con la del pedido ya creada, la de flete sí pudo salir: su reclamo vencido sigue siendo un envío sin respuesta.
    [Fact]
    public async Task ADeadFreightSendOfAnInvoicedOrderIsStillUnanswered()
    {
        var order = PendingRow();
        order.BeginSend(Now.AddMinutes(-9));
        order.MarkCreated("siigo-1", "FV-1-123", 100m, null, Now.AddMinutes(-9));
        var freight = PendingRow(SiigoInvoiceKind.Freight);
        freight.BeginSend(Now.AddMinutes(-9));
        var harness = new Harness([order, freight], Transient("timeout"));

        await harness.Issue();

        Assert.Equal(Now, freight.UnansweredAt);
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered], harness.Audit.Actions);
    }

    // El reintento de un ítem que salió fallido por la de flete (decidido el 2026-11-25): otra tanda del pedido manda sólo la de flete,
    // con su clave, nunca una segunda factura del pedido, y vuelve a marcar el pedido por si la marca se había perdido.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResendingAnInvoicedOrderSendsOnlyItsPendingFreightAndMarksTheOrderAgain(bool unanswered)
    {
        var order = PendingRow();
        order.BeginSend(Now.AddMinutes(-11));
        order.MarkCreated("siigo-1", "FV-1-123", 100m, null, Now.AddMinutes(-11));
        var freight = unanswered
            ? Unanswered(SiigoInvoiceKind.Freight, Now.AddMinutes(-11))
            : PendingRow(SiigoInvoiceKind.Freight);
        var orderKey = order.IdempotencyKey;
        var harness = new Harness([order, freight], Created());

        await harness.Issue();

        Assert.Equal([freight.IdempotencyKey], harness.Api.Keys);
        Assert.Equal(SiigoInvoiceStatus.Created, freight.Status);
        Assert.Equal((1, orderKey), (order.Attempts, order.IdempotencyKey));
        Assert.Equal(["FV-1-123"], harness.Orders.Marks);
    }

    // Si se declara que la del pedido no existe, la de flete nunca salió, tenga lo que tenga anotado (por ejemplo, el relleno de la
    // migración sobre un reclamo viejo). Viva, dejaría el pedido bloqueado.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclaringTheOrderMissingDiscardsItsFreightEvenIfItIsMarkedUnanswered(bool expired)
    {
        var sentAt = expired ? Now.AddHours(-25) : Now.AddMinutes(-11);
        var order = Unanswered(SiigoInvoiceKind.Order, sentAt);
        var freight = Unanswered(SiigoInvoiceKind.Freight, sentAt);
        Assert.Equal(expired, order.ExpireIfStale(Now));
        Assert.Equal(expired, freight.ExpireIfStale(Now));

        await new Harness([order, freight]).Resolve("missing");

        Assert.Equal(SiigoInvoiceStatus.Failed, order.Status);
        Assert.Equal(SiigoInvoiceStatus.Failed, freight.Status);
        Assert.Equal("siigo.invoice.order_rejected", freight.ErrorCode);
    }

    [Fact]
    public async Task DeclaringThatTheOrderInvoiceExistsLeavesItsFreightAlone()
    {
        var order = Unanswered(SiigoInvoiceKind.Order, Now.AddMinutes(-11));
        var freight = Unanswered(SiigoInvoiceKind.Freight, Now.AddMinutes(-11));

        await new Harness([order, freight]).Resolve("exists", "FV-1-777");

        Assert.Equal(SiigoInvoiceStatus.Created, order.Status);
        Assert.Equal(SiigoInvoiceStatus.Pending, freight.Status);
    }

    [Fact]
    public async Task TheFreightOfAnInvoicedOrderIsNotDiscarded()
    {
        var order = PendingRow();
        order.BeginSend(Now.AddMinutes(-11));
        order.MarkCreated("siigo-1", "FV-1-123", 100m, null, Now.AddMinutes(-11));
        var freight = Unanswered(SiigoInvoiceKind.Freight, Now.AddMinutes(-11));
        var harness = new Harness([order, freight]);

        await Assert.ThrowsAsync<SiigoDomainException>(() => harness.Resolve("missing"));

        Assert.Equal(SiigoInvoiceStatus.Pending, freight.Status);
    }

    // La respuesta de resolver trae el mismo aviso que la lectura del pedido: sin confirmar la marca, se avisa.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TheResolutionSaysWhetherTheOrderCarriesTheInvoice(bool marked, bool warns)
    {
        var harness = new Harness([Unanswered(SiigoInvoiceKind.Order, Now.AddMinutes(-11))]);
        harness.Orders.Marked = marked;

        var resolved = await harness.Resolve("exists", "FV-1-777");

        Assert.Equal(warns, resolved.OrderMarkFailed);
    }

    // La de flete nunca salió mientras la del pedido siga abierta: no hay nada que mirar en Siigo, ni para decir que existe.
    [Theory]
    [InlineData("missing", false)]
    [InlineData("exists", false)]
    [InlineData("missing", true)]
    [InlineData("exists", true)]
    public async Task TheFreightCannotBeResolvedWhileTheOrderInvoiceIsOpen(string outcome, bool expired)
    {
        var sentAt = expired ? Now.AddHours(-25) : Now.AddMinutes(-11);
        var order = Unanswered(SiigoInvoiceKind.Order, sentAt);
        var freight = Unanswered(SiigoInvoiceKind.Freight, sentAt);
        var harness = new Harness([order, freight]);

        var exception = await Assert.ThrowsAsync<SiigoDomainException>(
            () => harness.Resolve(outcome, "FV-1-777", "Freight"));

        Assert.Equal(SiigoErrorCodes.InvoiceStateInvalid, exception.Code);
        Assert.True(freight.IsOpen);
        Assert.Empty(harness.Audit.Actions);
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Rejected")]
    [InlineData("Failed")]
    public async Task ALiveFreightCanBeResolvedOnceTheOrderInvoiceIsSettled(string orderStatus)
    {
        var order = PendingRow();
        switch (orderStatus)
        {
            case "Created":
                order.MarkCreated("siigo-1", "FV-1-123", 100m, null, Now.AddMinutes(-11));
                break;
            case "Rejected":
                order.MarkRejected("siigo.invoice.rejected", "invalid_reference", null, Now.AddMinutes(-11));
                break;
            default:
                order.MarkFailed("siigo.invoice.order_rejected", "Discarded.", Now.AddMinutes(-11));
                break;
        }

        var freight = Unanswered(SiigoInvoiceKind.Freight, Now.AddMinutes(-11));
        Assert.Equal(orderStatus, order.Status.ToString());

        await new Harness([order, freight]).Resolve("missing", kind: "Freight");

        Assert.Equal(SiigoInvoiceStatus.Failed, freight.Status);
    }

    // POB959 (2026-11-25): Siigo calculó un centavo menos que QEP y ningún redondeo conocido lo predice.
    [Theory]
    [InlineData("38519158.60")]
    [InlineData("38519158.62")]
    [InlineData("38519157.61")]
    public async Task ARoundingDifferenceIsResentOnceWithSiigosTotalAndANewKey(string siigoTotal)
    {
        var expected = Amount(siigoTotal);
        var row = PendingRow(requestJson: Payments(38_519_158.61m));
        var firstKey = row.IdempotencyKey;
        var harness = new Harness([row], Mismatch(expected), Created());

        var result = await harness.Issue();

        Assert.Equal("Created", Assert.Single(result).Status);
        Assert.Equal([expected], PaymentValues(harness.Api.Bodies[1]));
        Assert.Equal(harness.Api.Bodies[1], row.RequestJson);
        Assert.Equal([firstKey, row.IdempotencyKey], harness.Api.Keys);
        Assert.NotEqual(firstKey, row.IdempotencyKey);
        Assert.Equal(2, row.Attempts);
        Assert.Equal(["siigo.invoice.payments_adjusted", SiigoAuditActions.InvoiceCreated], harness.Audit.Actions);
    }

    [Fact]
    public async Task OnlyTheLastPaymentAbsorbsTheDifference()
    {
        var row = PendingRow(requestJson: Payments(30_000_000m, 11_290_811.07m));
        var harness = new Harness([row], Mismatch(41_290_811.09m), Created());

        await harness.Issue();

        Assert.Equal([30_000_000m, 11_290_811.09m], PaymentValues(row.RequestJson));
        Assert.Contains("\"observations\":\"Pedido POB893\"", row.RequestJson, StringComparison.Ordinal);
    }

    // El reenvío sale con la petición ya guardada: si el proceso muere en medio, la reanudación manda estos bytes con esta clave.
    [Fact]
    public async Task TheAdjustedRequestIsSavedBeforeItIsSent()
    {
        var row = PendingRow(requestJson: Payments(100.01m));
        var harness = new Harness([row], Mismatch(100m), Created());
        var savedKeys = new List<string>();
        var keysSavedAtEachCall = new List<string[]>();
        harness.UnitOfWork.OnSave = () => savedKeys.Add(row.IdempotencyKey);
        harness.Api.OnCall = () => keysSavedAtEachCall.Add([.. savedKeys]);

        await harness.Issue();

        Assert.Equal(2, harness.Api.Keys.Count);
        Assert.Contains(harness.Api.Keys[1], keysSavedAtEachCall[1]);
    }

    [Theory]
    [InlineData("38519157.60")]
    [InlineData("38519159.62")]
    [InlineData("38519158.61")]
    [InlineData("38519158.605")]
    public async Task ADifferenceThatIsNotRoundingStaysRejected(string siigoTotal)
    {
        var expected = Amount(siigoTotal);
        var json = Payments(38_519_158.61m);
        var row = PendingRow(requestJson: json);
        var harness = new Harness([row], Mismatch(expected));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Rejected, row.Status);
        Assert.Equal(expected, row.ExpectedPaymentsTotal);
        Assert.Equal(json, row.RequestJson);
        Assert.Single(harness.Api.Keys);
    }

    [Fact]
    public async Task APaymentThatWouldEndAtZeroIsNotAdjusted()
    {
        var row = PendingRow(requestJson: Payments(100m, 0.50m));
        var harness = new Harness([row], Mismatch(100m));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Rejected, row.Status);
        Assert.Single(harness.Api.Keys);
    }

    [Fact]
    public async Task AMismatchAlongWithAnotherErrorIsNotAdjusted()
    {
        var row = PendingRow(requestJson: Payments(100.01m));
        var outcome = new SiigoInvoiceOutcome.Rejected(
            [new SiigoError("invalid_total_payments", "message"), new SiigoError("invalid_reference", "message")], 100m);
        var harness = new Harness([row], outcome);

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Rejected, row.Status);
        Assert.Single(harness.Api.Keys);
    }

    [Fact]
    public async Task ASecondMismatchIsNotAdjustedAgain()
    {
        var row = PendingRow(requestJson: Payments(100.01m));
        var harness = new Harness([row], Mismatch(100m), Mismatch(99.99m));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Rejected, row.Status);
        Assert.Equal(99.99m, row.ExpectedPaymentsTotal);
        Assert.Equal(2, harness.Api.Keys.Count);
        Assert.Equal(["siigo.invoice.payments_adjusted", SiigoAuditActions.InvoiceRejected], harness.Audit.Actions);
    }

    // Con un envío sin respuesta Siigo pudo haber creado la primera: cambiar la clave la duplicaría.
    [Fact]
    public async Task AnUnansweredInvoiceIsNeverAdjusted()
    {
        var json = Payments(100.01m);
        var row = PendingRow(requestJson: json);
        row.BeginSend(Now.AddMinutes(-11));
        row.MarkUnanswered("siigo.invoice.timeout", null, Now.AddMinutes(-11));
        var firstKey = row.IdempotencyKey;
        var harness = new Harness([row], Mismatch(100m));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal((json, firstKey), (row.RequestJson, row.IdempotencyKey));
        Assert.Single(harness.Api.Keys);
    }

    [Fact]
    public async Task ATimeoutOnTheAdjustedSendAwaitsConfirmationWithTheNewKey()
    {
        var row = PendingRow(requestJson: Payments(100.01m));
        var harness = new Harness([row], Mismatch(100m), Transient("timeout"));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal(Now, row.UnansweredAt);
        Assert.Equal(2, harness.Api.Keys.Count);
        Assert.Equal(harness.Api.Keys[1], row.IdempotencyKey);
        Assert.Equal([100m], PaymentValues(row.RequestJson));
    }

    [Fact]
    public async Task TheFreightGoesOutAfterItsOrderInvoiceWasAdjusted()
    {
        var order = PendingRow(requestJson: Payments(100.01m));
        var freight = PendingRow(SiigoInvoiceKind.Freight);
        var harness = new Harness([order, freight], Mismatch(100m), Created(), Created());

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Created, order.Status);
        Assert.Equal(SiigoInvoiceStatus.Created, freight.Status);
    }

    // El reenvío alarga la emisión: la de flete que espera su turno sigue reclamada contando desde ahí.
    [Fact]
    public async Task AResendRenewsTheClaimOfTheInvoiceStillWaiting()
    {
        var order = PendingRow(requestJson: Payments(100.01m));
        var freight = PendingRow(SiigoInvoiceKind.Freight);
        var harness = new Harness([order, freight], Mismatch(100m), Created(), Created());
        var freightClaims = new List<DateTimeOffset?>();
        harness.Api.OnCall = () =>
        {
            freightClaims.Add(freight.SendingSince);
            harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(2);
        };

        await harness.Issue();

        Assert.Equal([Now, Now.AddMinutes(2)], freightClaims.Take(2));
        Assert.Equal(1, freight.Attempts);
    }

    private static SiigoInvoice Unanswered(SiigoInvoiceKind kind, DateTimeOffset sentAt)
    {
        var row = PendingRow(kind, sentAt);
        row.BeginSend(sentAt);
        row.MarkUnanswered("siigo.invoice.timeout", null, sentAt);
        return row;
    }

    private static SiigoInvoice PendingRow(
        SiigoInvoiceKind kind = SiigoInvoiceKind.Order, DateTimeOffset? createdAt = null, string requestJson = """{"a":1}""") =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(), TenantId, ConnectionId, OrderId, kind, requestJson, 7, SubjectId, createdAt ?? Now.AddHours(-1));

    private static string Payments(params decimal[] values) =>
        JsonSerializer.Serialize(new
        {
            observations = "Pedido POB893",
            payments = values.Select(value => new { id = 11719, value }),
        });

    private static decimal Amount(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static decimal[] PaymentValues(string requestJson)
    {
        using var json = JsonDocument.Parse(requestJson);
        return [.. json.RootElement.GetProperty("payments").EnumerateArray().Select(payment => payment.GetProperty("value").GetDecimal())];
    }

    private static SiigoInvoiceOutcome.Rejected Mismatch(decimal expected) =>
        new([new SiigoError("invalid_total_payments", "message")], expected);

    private static SiigoInvoiceOutcome.Transient Transient(string reason) => new(reason);

    private static SiigoInvoiceOutcome.Rejected Rejected(string code) => new([new SiigoError(code, "message")], null);

    private static SiigoInvoiceOutcome.Created Created() => new("siigo-1", "FV-1-123", 100m, null);

    private sealed class Harness
    {
        private readonly IssueSiigoInvoiceHandler _handler;
        private readonly ResolveSiigoInvoiceHandler _resolver;

        public Harness(SiigoInvoice[] rows, params SiigoInvoiceOutcome[] script)
        {
            Api = ApiProxy.Create(script);
            _resolver = new ResolveSiigoInvoiceHandler(
                new ExecutionContext(),
                new AllModules(),
                new FixedMembership(),
                Orders,
                new InMemoryInvoices([.. rows]),
                new FakeUnitOfWork(),
                Audit,
                Clock,
                new ResolveSiigoInvoiceValidator(),
                new NoOpenBatches(),
                NullLogger<ResolveSiigoInvoiceHandler>.Instance);
            _handler = new IssueSiigoInvoiceHandler(
                new ExecutionContext(),
                new AllModules(),
                new FixedMembership(),
                Orders,
                // El camino de reanudación no lee la configuración.
                null!,
                new ActiveConnections(),
                Api.Instance,
                new InMemoryInvoices([.. rows]),
                UnitOfWork,
                Audit,
                Clock,
                new IssueSiigoInvoiceValidator());
        }

        public ApiProxy Api { get; }

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public OrderSource Orders { get; } = new();

        public MutableClock Clock { get; } = new() { UtcNow = Now };

        public Task<IReadOnlyList<SiigoInvoiceDto>> Issue() =>
            _handler.HandleAsync(new IssueSiigoInvoiceCommand(TenantId, OrderId, null), TestContext.Current.CancellationToken);

        /// <summary>Una persona resuelve una factura: la del pedido si no dice cuál.</summary>
        public Task<SiigoInvoiceDto> Resolve(string outcome, string? number = null, string? kind = null) =>
            _resolver.HandleAsync(
                new ResolveSiigoInvoiceCommand(TenantId, OrderId, kind, outcome, number), TestContext.Current.CancellationToken);
    }

    private sealed class ExecutionContext : IExecutionContext
    {
        public Guid SubjectId => IssueSiigoInvoiceHandlerTests.SubjectId;

        public TenantId TenantId { get; } = new(IssueSiigoInvoiceHandlerTests.TenantId);

        public bool HasPermission(string permission) => permission == SiigoPermissions.InvoiceIssue;
    }

    private sealed class AllModules : ITenantModules
    {
        // `null` = el tenant no tiene fila: el guard lo deja pasar (como el stub de desarrollo).
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(null);
    }

    private sealed class FixedMembership : IMembershipDirectory
    {
        private readonly Guid _membershipId = Guid.CreateVersion7();

        public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(_membershipId);

        public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class OrderSource : ISiigoOrderSource
    {
        /// <summary>Lo que contesta la lectura de la marca del pedido.</summary>
        public bool Marked { get; set; } = true;

        public Task<SiigoInvoiceInput?> GetAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(7);

        /// <summary>Los números con que se pidió marcar el pedido.</summary>
        public List<string> Marks { get; } = [];

        public Task MarkInvoicedAsync(
            Guid tenantId, Guid orderId, string number, string siigoInvoiceId, Guid memberId, CancellationToken cancellationToken)
        {
            Marks.Add(number);
            return Task.CompletedTask;
        }

        public Task<bool> IsMarkedInvoicedAsync(Guid tenantId, Guid orderId, string number, CancellationToken cancellationToken) =>
            Task.FromResult(Marked);

        public Task<bool> UnmarkInvoicedAsync(
            Guid tenantId, Guid orderId, string number, Guid memberId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SiigoOrderLabel>> ListLabelsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ActiveConnections : ISiigoConnectionDirectory
    {
        public Task<SiigoConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(new SiigoConnectionResolution(new SiigoCredentials(connectionId, "user", "key"), null));

        public Task<SiigoConnectionInfo?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SiigoConnectionResolution> ResolveForSetupAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SiigoConnectionInfo>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryInvoices(List<SiigoInvoice> rows) : ISiigoInvoiceRepository
    {
        public Task<IReadOnlyList<SiigoInvoice>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoInvoice>>([.. rows.Where(row => row.TenantId == tenantId && row.OrderId == orderId)]);

        public Task<IReadOnlyList<SiigoInvoice>> ListFreightAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SiigoInvoice>> ListByIdsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(SiigoInvoice invoice) => throw new NotSupportedException();
    }

    /// <summary>Ninguna tanda tiene el pedido.</summary>
    private sealed class NoOpenBatches : ISiigoInvoiceBatchRepository
    {
        public Task<Guid?> FindOpenBatchIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(null);

        public void Add(SiigoInvoiceBatch batch) => throw new NotSupportedException();

        public Task<SiigoInvoiceBatch?> FindAsync(Guid tenantId, Guid batchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListOpenOrderIdsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<SiigoInvoiceBatch> Items, int Total)> SearchAsync(
            Guid tenantId, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    public sealed class FakeUnitOfWork : ISiigoUnitOfWork
    {
        public Action? OnSave { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            OnSave?.Invoke();
            return Task.FromResult(1);
        }

        public Task<ISiigoTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ISiigoTransaction>(new FakeTransaction());

        public Task LockOrderAsync(Guid orderId, CancellationToken cancellationToken) => Task.CompletedTask;

        private sealed class FakeTransaction : ISiigoTransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAudit : ISiigoAuditRecorder
    {
        public List<string> Actions { get; } = [];

        public void Record(
            Guid tenantId, Guid actorId, AuditActorType actorType, string action, string resourceType, string resourceId,
            string changedFieldsJson, DateTimeOffset occurredAt) =>
            Actions.Add(action);
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    /// <summary>Un <see cref="ISiigoApi"/> que sólo sabe crear facturas: contesta el guion en orden y anota las claves que recibe.</summary>
    public class ApiProxy : DispatchProxy
    {
        private Queue<SiigoInvoiceOutcome> _script = new();

        public List<string> Keys { get; } = [];

        public List<string> Bodies { get; } = [];

        public Action? OnCall { get; set; }

        public ISiigoApi Instance => (ISiigoApi)(object)this;

        public static ApiProxy Create(IEnumerable<SiigoInvoiceOutcome> script)
        {
            var proxy = (ApiProxy)(object)DispatchProxy.Create<ISiigoApi, ApiProxy>();
            proxy._script = new Queue<SiigoInvoiceOutcome>(script);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ISiigoApi.CreateInvoiceAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            Bodies.Add(((SiigoInvoiceRequest)args![1]!).Json);
            Keys.Add((string)args[2]!);
            OnCall?.Invoke();
            return Task.FromResult(_script.Dequeue());
        }
    }
}
