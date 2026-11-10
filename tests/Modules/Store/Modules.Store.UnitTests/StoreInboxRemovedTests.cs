using System.Reflection;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.UnitTests;

/// <summary>
/// La bandeja «Desde la tienda» se quitó (spec 2026-11-09, D6): el checkout crea el pedido directo. Estas pruebas fijan que no quede
/// ni su entidad ni su manejo, ni el permiso prestado ni la opción del candado de «Tomar».
/// </summary>
public sealed class StoreInboxRemovedTests
{
    private static readonly Assembly Domain = typeof(OnlineStore).Assembly;

    private static readonly Assembly Application = typeof(StoreOptions).Assembly;

    [Theory]
    [InlineData("Modules.Store.Domain.StoreOrderRequest")]
    [InlineData("Modules.Store.Domain.StoreOrderRequestLine")]
    [InlineData("Modules.Store.Domain.StoreOrderRequestStatus")]
    [InlineData("Modules.Store.Domain.StoreDiscardReason")]
    public void TheRequestEntityIsGone(string typeName) => Assert.Null(Domain.GetType(typeName));

    [Theory]
    [InlineData("Modules.Store.Application.IStoreOrderRequestRepository")]
    [InlineData("Modules.Store.Application.IStoreRequestNumberGenerator")]
    [InlineData("Modules.Store.Application.ListOrderRequestsHandler")]
    [InlineData("Modules.Store.Application.GetOrderRequestSummaryHandler")]
    [InlineData("Modules.Store.Application.GetOrderRequestHandler")]
    [InlineData("Modules.Store.Application.GetCustomerCandidatesHandler")]
    [InlineData("Modules.Store.Application.TakeOrderRequestHandler")]
    [InlineData("Modules.Store.Application.DiscardOrderRequestHandler")]
    [InlineData("Modules.Store.Application.OrderRequestRowComposer")]
    public void TheInboxHandlersAreGone(string typeName) => Assert.Null(Application.GetType(typeName));

    [Fact]
    public void TheBorrowedOrderPermissionIsGone() =>
        Assert.Null(typeof(StorePermissions).GetField("OrderManage"));

    [Fact]
    public void TheConversionLeaseOptionIsGone() =>
        Assert.Null(typeof(StoreOptions).GetProperty("ConversionLease"));

    [Theory]
    [InlineData("OrderRequestNotFound")]
    [InlineData("NotNew")]
    [InlineData("BeingTaken")]
    [InlineData("OrderRequestConflict")]
    [InlineData("DuplicateClientRequest")]
    [InlineData("CustomerNotFound")]
    [InlineData("CustomerDocumentTaken")]
    public void TheInboxErrorCodesAreGone(string field) => Assert.Null(typeof(StoreErrorCodes).GetField(field));
}
