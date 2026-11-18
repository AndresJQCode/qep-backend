using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Siigo.Application;
using Modules.Siigo.Infrastructure.Persistence;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-18 §5.4: el candado de factura usa un espacio propio (20261118 ya es de Shopify) y la clave es el uuid
/// del pedido tal como lo formatea Postgres, así un candado tomado desde SQL crudo con <c>order_id::text</c> es el mismo.</summary>
public sealed class SiigoInvoiceLockTests
{
    [Fact]
    public void TheNamespaceIsItsOwn() => Assert.Equal(20261119, SiigoInvoiceLock.Namespace);

    [Fact]
    public void TheKeyIsThePostgresTextOfTheOrderId()
    {
        var orderId = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

        Assert.Equal("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", SiigoInvoiceLock.Key(orderId));
    }

    // Antes que DbUpdateException, de la que hereda: al revés saldría como 500 en vez de 412.
    [Fact]
    public void AConcurrencyLossIsAConcurrencyConflict()
    {
        var translated = SiigoUnitOfWork.Translate(new DbUpdateConcurrencyException("lost"));

        Assert.Equal("concurrency.conflict", Assert.IsType<RequestConcurrencyException>(translated).Code);
    }

    [Fact]
    public void AnythingElseIsNotOurs() => Assert.Null(SiigoUnitOfWork.Translate(new InvalidOperationException("other")));
}
