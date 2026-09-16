using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Las copias públicas de los comprobantes de un request (spec 2026-09-15, P4 y P7): qué se publica,
/// con qué clave llega al dominio, y qué borra el rollback cuando el request falla después de copiar.
/// </summary>
public sealed class PaymentProofCopiesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    // Cada comprobante nuevo sale con la clave de su copia, en el orden del request.
    [Fact]
    public async Task PublishesEachProofAndHandsItsKeyToTheDomain()
    {
        var publisher = new RecordingPaymentProofPublisher();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var inputs = await new PaymentProofCopies(publisher).PublishAsync(
            TenantId, [new(first, 60_000m), new(second, 40_000m)], TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new OrderPaymentProofInput(first, 60_000m, RecordingPaymentProofPublisher.KeyFor(first)),
                new OrderPaymentProofInput(second, 40_000m, RecordingPaymentProofPublisher.KeyFor(second)),
            ],
            inputs);
    }

    // P1: apagada, no hay copia, el comprobante queda privado y el rollback no tiene nada que borrar.
    [Fact]
    public async Task WithTheOptionOffTheProofsGoWithoutKeyAndTheRollbackDeletesNothing()
    {
        var publisher = new RecordingPaymentProofPublisher(enabled: false);
        var copies = new PaymentProofCopies(publisher);

        var inputs = await copies.PublishAsync(
            TenantId, [new(Guid.CreateVersion7(), 1m)], TestContext.Current.CancellationToken);
        await copies.RollbackAsync();

        Assert.Null(Assert.Single(inputs).PublicStorageKey);
        Assert.Empty(publisher.DeletedKeys);
    }

    // P7: si el request falla después de copiar, el rollback borra cada copia hecha, sin el token del
    // request, que puede ser justo el que se canceló.
    [Fact]
    public async Task RollbackDeletesEveryCopyMadeWithoutTheRequestToken()
    {
        var publisher = new RecordingPaymentProofPublisher();
        var copies = new PaymentProofCopies(publisher);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await copies.PublishAsync(TenantId, [new(first, 1m), new(second, 2m)], request.Token);

        await copies.RollbackAsync();

        Assert.Equal(
            [RecordingPaymentProofPublisher.KeyFor(first), RecordingPaymentProofPublisher.KeyFor(second)],
            publisher.DeletedKeys);
        Assert.All(publisher.DeleteTokens, token => Assert.False(token.CanBeCanceled));
    }

    // P7: una copia que falla sube su excepción, y las anteriores quedan anotadas para el rollback.
    [Fact]
    public async Task ACopyThatFailsLeavesTheEarlierCopiesToTheRollback()
    {
        var publisher = new RecordingPaymentProofPublisher { FailingPublishCall = 2 };
        var copies = new PaymentProofCopies(publisher);
        var first = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() => copies.PublishAsync(
            TenantId, [new(first, 1m), new(Guid.CreateVersion7(), 2m)], TestContext.Current.CancellationToken));
        await copies.RollbackAsync();

        Assert.Equal([RecordingPaymentProofPublisher.KeyFor(first)], publisher.DeletedKeys);
    }

    // Best-effort: un borrado que falla no deja las demás copias sin borrar, y RollbackAsync no lanza
    // —si lanzara, taparía la excepción original del request—.
    [Fact]
    public async Task ADeleteThatFailsDoesNotStopTheOthers()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var publisher = new RecordingPaymentProofPublisher
        {
            FailingDeleteKey = RecordingPaymentProofPublisher.KeyFor(first),
        };
        var copies = new PaymentProofCopies(publisher);
        await copies.PublishAsync(
            TenantId, [new(first, 1m), new(second, 2m)], TestContext.Current.CancellationToken);

        await copies.RollbackAsync();

        Assert.Equal([RecordingPaymentProofPublisher.KeyFor(second)], publisher.DeletedKeys);
    }

    // D9 (spec 2026-09-16): sólo un comprobante con copia pública tiene algo que mover.
    [Fact]
    public void OnlyTheProofsWithAPublicKeyAreAttached()
    {
        var withKey = Guid.CreateVersion7();
        var withoutKey = Guid.CreateVersion7();

        var attached = PaymentProofCopies.AttachedFrom(
        [
            new OrderPaymentProofInput(withKey, 10_000m, "payment-proofs/abc.webp"),
            new OrderPaymentProofInput(withoutKey, 5_000m),
        ]);

        Assert.Equal([new AttachedPaymentProof(withKey, "payment-proofs/abc.webp")], attached);
    }

    // D9 y D19: de las correcciones, sólo un archivo de reemplazo con copia pública tiene algo que mover.
    // Corregir sólo el monto no cambia el archivo.
    [Fact]
    public void OnlyTheReplacementFilesWithAPublicKeyAreAttached()
    {
        var replacedWithKey = Guid.CreateVersion7();
        var replacedWithoutKey = Guid.CreateVersion7();

        var attached = PaymentProofCopies.AttachedFromReplacements(
        [
            new OrderPaymentProofAmountUpdate(OrderPaymentProofId.New(), 10_000m, replacedWithKey, "payment-proofs/def.webp"),
            new OrderPaymentProofAmountUpdate(OrderPaymentProofId.New(), 5_000m, replacedWithoutKey),
            new OrderPaymentProofAmountUpdate(OrderPaymentProofId.New(), 7_000m),
        ]);

        Assert.Equal([new AttachedPaymentProof(replacedWithKey, "payment-proofs/def.webp")], attached);
    }

    // D19 (spec 2026-09-16): se suelta sólo el archivo que el pedido dejó de usar. Si otro comprobante
    // del mismo pedido lo sigue usando, no hay nada que borrar.
    [Fact]
    public void OnlyTheFilesTheOrderNoLongerUsesAreDetached()
    {
        var replaced = new DetachedPaymentProof(Guid.CreateVersion7(), "payment-proofs/abc.webp");
        var stillUsed = new DetachedPaymentProof(Guid.CreateVersion7(), "payment-proofs/def.webp");
        var withoutKey = new DetachedPaymentProof(Guid.CreateVersion7(), null);

        var detached = PaymentProofCopies.DetachedFrom(
            [replaced, stillUsed, withoutKey],
            [stillUsed.FileId, Guid.CreateVersion7()]);

        Assert.Equal([replaced, withoutKey], detached);
    }
}
