using static Modules.Store.IntegrationTests.PublicOrderBuyerAccountApiTests;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// Spec 2026-11-15 §3.2: iniciar sesión en una tienda enlaza los pedidos de ese tenant que salieron sin cuenta y cuyo correo de
/// facturación es el de la cuenta. Cada prueba levanta su propio host porque reescribe filas de pedidos (las mueve de tenant, les
/// cambia el origen). Con la API real y Postgres; necesitan Docker.
/// </summary>
public sealed class BuyerLoginOrderLinkApiTests
{
    private const string Password = "una-contrasena-larga";

    /// <summary>Un pedido de invitado con ese correo de facturación, hecho mientras la cuenta sigue sin verificar: queda sin cuenta.</summary>
    private static async Task<Guid> GuestOrderAsync(Rig rig, string billingEmail = "ana@test.co")
    {
        var receipt = await CreatedAsync(await SubmitAsync(rig, Body(rig, body => body["billing"] = Billing(rig, billingEmail))));
        var orderId = (Guid)receipt["id"]!;
        Assert.Null(await OrderBuyerAccountAsync(rig, orderId));
        return orderId;
    }

    /// <summary>Registra la cuenta sin verificar, corre <paramref name="placeOrders"/> y después la verifica.</summary>
    private static async Task<T> WithAccountVerifiedAfterAsync<T>(Rig rig, Func<Task<T>> placeOrders, string email = "ana@test.co")
    {
        var token = await rig.RegisterAsync(email, Password);
        var result = await placeOrders();
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await rig.PostBuyerAsync("/email/verify", new { token })).StatusCode);
        return result;
    }

    [Fact]
    public async Task LoginLinksOnlyUnlinkedStoreOrdersOfThatTenantWithTheBillingEmail()
    {
        await using var rig = await OpenRigAsync();
        var (own, otherTenant, manual, otherAccount, otherEmail) = await WithAccountVerifiedAfterAsync(rig, async () => (
            await GuestOrderAsync(rig),
            await GuestOrderAsync(rig),
            await GuestOrderAsync(rig),
            await GuestOrderAsync(rig),
            await GuestOrderAsync(rig, "otra@test.co")));
        var anotherAccount = Guid.CreateVersion7();
        await rig.ExecuteAsync($"UPDATE quotations.orders SET tenant_id = '{Guid.CreateVersion7()}' WHERE id = '{otherTenant}'");
        await rig.ExecuteAsync($"UPDATE quotations.orders SET source = 'Manual' WHERE id = '{manual}'");
        await rig.ExecuteAsync($"UPDATE quotations.orders SET buyer_account_id = '{anotherAccount}' WHERE id = '{otherAccount}'");

        await LoginAsync(rig, password: Password);

        var account = await AccountIdAsync(rig, "ana@test.co");
        Assert.Equal(account, await OrderBuyerAccountAsync(rig, own));
        Assert.Null(await OrderBuyerAccountAsync(rig, otherTenant));
        Assert.Null(await OrderBuyerAccountAsync(rig, manual));
        Assert.Equal(anotherAccount.ToString(), await OrderBuyerAccountAsync(rig, otherAccount));
        Assert.Null(await OrderBuyerAccountAsync(rig, otherEmail));
    }

    [Fact]
    public async Task LoginMatchesTheBillingEmailIgnoringCaseAndSpaces()
    {
        await using var rig = await OpenRigAsync();
        var orderId = await WithAccountVerifiedAfterAsync(rig, () => GuestOrderAsync(rig));
        // El checkout guarda el correo como lo escribió el comprador: se fuerza uno con mayúsculas y espacios.
        await rig.ExecuteAsync(
            $"UPDATE quotations.order_parties SET email = '  ANA@Test.CO ' WHERE order_id = '{orderId}' AND role = 'Billing'");

        await LoginAsync(rig, password: Password);

        Assert.Equal(await AccountIdAsync(rig, "ana@test.co"), await OrderBuyerAccountAsync(rig, orderId));
    }

    [Fact]
    public async Task LoginDoesNotRelinkAnOrderAMemberUnlinked()
    {
        await using var rig = await OpenRigAsync();
        var orderId = await WithAccountVerifiedAfterAsync(rig, () => GuestOrderAsync(rig));
        await rig.ExecuteAsync($"UPDATE quotations.orders SET buyer_account_set_by_member = true WHERE id = '{orderId}'");

        await LoginAsync(rig, password: Password);

        Assert.Null(await OrderBuyerAccountAsync(rig, orderId));
    }

    [Fact]
    public async Task LoginDoesNotBumpTheOrderVersion()
    {
        await using var rig = await OpenRigAsync();
        var orderId = await WithAccountVerifiedAfterAsync(rig, () => GuestOrderAsync(rig));
        var before = await rig.ScalarTextAsync($"SELECT version::text FROM quotations.orders WHERE id = '{orderId}'");

        await LoginAsync(rig, password: Password);

        Assert.Equal(await AccountIdAsync(rig, "ana@test.co"), await OrderBuyerAccountAsync(rig, orderId));
        Assert.Equal(before, await rig.ScalarTextAsync($"SELECT version::text FROM quotations.orders WHERE id = '{orderId}'"));
    }
}
