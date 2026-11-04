using System.Net;
using System.Text.Json.Nodes;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// La bandeja «Desde la tienda»: listas, contadores, candidatos y descarte. Necesitan Docker; se corren una vez en la tanda final.
/// </summary>
public sealed class StoreInboxApiTests
{
    private static readonly object NewCustomerBody = new { newCustomer = new { classificationId = (Guid?)null } };

    private static IEnumerable<string> Ids(JsonNode page) =>
        page["items"]!.AsArray().Select(item => (string)item!["id"]!);

    [Fact]
    public async Task AHugePageIsClampedInsteadOfOverflowingTheOffset()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        await rig.SubmitAsync();

        var page = await rig.GetJsonAsync("?view=All&page=2147483647&pageSize=100");

        Assert.Empty(page["items"]!.AsArray());
        Assert.Equal(1, (int)page["total"]!);
        Assert.Equal(10_000, (int)page["page"]!);
    }

    [Fact]
    public async Task TheViewsSplitUntakenMineAndAll()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var taken = await rig.SubmitAsync();
        var discarded = await rig.SubmitAsync();
        var untaken = await rig.SubmitAsync();
        await rig.TakeOkAsync(taken, NewCustomerBody);
        var discard = await rig.DiscardAsync(discarded, new { reason = "Spam" });
        Assert.Equal(HttpStatusCode.OK, discard.StatusCode);

        var defaultView = await rig.GetJsonAsync(string.Empty);
        var untakenView = await rig.GetJsonAsync("?view=Untaken");
        var mine = await rig.GetJsonAsync("?view=TakenByMe");
        var all = await rig.GetJsonAsync("?view=All&pageSize=100");

        Assert.Equal([untaken.ToString()], Ids(defaultView));
        Assert.Equal([untaken.ToString()], Ids(untakenView));
        Assert.Equal([taken.ToString()], Ids(mine));
        Assert.Equal(3, (int)all["total"]!);
        Assert.Equal(25, (int)defaultView["pageSize"]!);
    }

    [Fact]
    public async Task ThePageSizeIsBounded()
    {
        await using var rig = await StoreInboxRig.OpenAsync();

        await StoreInboxRig.AssertCodeAsync(
            await rig.GetAsync("?pageSize=101"), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await StoreInboxRig.AssertCodeAsync(
            await rig.GetAsync("?view=Everything"), HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task TheSummaryCountsTodayInTheTenantCalendar()
    {
        // 15:00 UTC del 3 de noviembre son las 10:00 en Bogotá.
        await using var rig = await StoreInboxRig.OpenAsync();
        var lastNight = await rig.SubmitAsync();
        var thisMorning = await rig.SubmitAsync();
        await rig.ExecuteAsync(
            "UPDATE store.order_requests SET received_at = @at WHERE id = @id",
            ("at", new DateTimeOffset(2026, 11, 3, 4, 30, 0, TimeSpan.Zero)),
            ("id", lastNight));
        await rig.ExecuteAsync(
            "UPDATE store.order_requests SET received_at = @at WHERE id = @id",
            ("at", new DateTimeOffset(2026, 11, 3, 13, 0, 0, TimeSpan.Zero)),
            ("id", thisMorning));

        var summary = await rig.GetJsonAsync("/summary");

        // 04:30 UTC es la noche anterior (23:30) en Bogotá: no cuenta, aunque la fecha UTC sea la de hoy.
        Assert.Equal(1, (int)summary["receivedToday"]!);
        Assert.Equal(2, (int)summary["untaken"]!);
        Assert.Equal(0, (int)summary["takenByMe"]!);
    }

    [Fact]
    public async Task KnownCustomerIsFlaggedByPhoneOrDocument()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        await rig.CreateCompleteCustomerAsync("Cliente Uno", "999", "3001234567");
        var byPhone = await rig.SubmitAsync(billing => billing["identificationNumber"] = "111");
        var byDocument = await rig.SubmitAsync(billing =>
        {
            billing["identificationNumber"] = "999";
            billing["phone"] = "3109999999";
        });
        var stranger = await rig.SubmitAsync(billing =>
        {
            billing["identificationNumber"] = "555";
            billing["phone"] = "3208888888";
        });

        var rows = (await rig.GetJsonAsync("?view=All"))["items"]!.AsArray()
            .ToDictionary(item => (string)item!["id"]!, item => (bool)item!["isKnownCustomer"]!);

        Assert.True(rows[byPhone.ToString()]);
        Assert.True(rows[byDocument.ToString()]);
        Assert.False(rows[stranger.ToString()]);
    }

    [Fact]
    public async Task CandidatesSuggestOnlyAnUnambiguousMatch()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var byPhone = await rig.CreateCompleteCustomerAsync("Dueño del teléfono", "111", "3001234567");
        var byDocument = await rig.CreateCompleteCustomerAsync("Dueño del documento", "222", "3112223344");
        var ambiguous = await rig.SubmitAsync(billing => billing["identificationNumber"] = "222");
        var clear = await rig.SubmitAsync(billing => billing["identificationNumber"] = "111");

        var split = await rig.GetJsonAsync($"/{ambiguous}/customer-candidates");
        var single = await rig.GetJsonAsync($"/{clear}/customer-candidates");

        Assert.Equal(2, split["candidates"]!.AsArray().Count);
        Assert.Null(split["suggestedCustomerId"]);
        var phoneCandidate = split["candidates"]!.AsArray().Single(item => (Guid)item!["id"]! == byPhone);
        var documentCandidate = split["candidates"]!.AsArray().Single(item => (Guid)item!["id"]! == byDocument);
        Assert.Equal(["Phone"], phoneCandidate!["matchedBy"]!.AsArray().Select(value => (string)value!));
        Assert.Equal(["Document"], documentCandidate!["matchedBy"]!.AsArray().Select(value => (string)value!));
        Assert.Equal(byPhone, (Guid)single["suggestedCustomerId"]!);
        // Order() es ordinal: "Document" va antes que "Phone".
        Assert.Equal(["Document", "Phone"], single["candidates"]![0]!["matchedBy"]!.AsArray().Select(value => (string)value!).Order());
        Assert.Equal("Ana Pérez", (string)single["newCustomer"]!["name"]!);
    }

    [Fact]
    public async Task CandidatesExcludeInactiveAndMergedCustomers()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var inactive = await rig.CreateCompleteCustomerAsync("Inactivo", "301", "3001234567");
        var merged = await rig.CreateCompleteCustomerAsync("Fusionado", "302", "3001234567");
        var survivor = await rig.CreateCompleteCustomerAsync("Sobreviviente", "303", "3115550000");
        await DeactivateCustomerAsync(rig.Factory, inactive);
        await rig.MergeAsync(merged, survivor);
        var request = await rig.SubmitAsync(billing => billing["identificationNumber"] = "999");

        var candidates = await rig.GetJsonAsync($"/{request}/customer-candidates");

        Assert.Empty(candidates["candidates"]!.AsArray());
        Assert.Null(candidates["suggestedCustomerId"]);
    }

    [Fact]
    public async Task DiscardingAConvertedRequestIs409()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();
        await rig.TakeOkAsync(request, NewCustomerBody);

        await StoreInboxRig.AssertCodeAsync(
            await rig.DiscardAsync(request, new { reason = "Duplicate" }), HttpStatusCode.Conflict, "store.order_request.not_new");
    }

    [Fact]
    public async Task DiscardWithOtherWithoutNoteIs422()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();

        await StoreInboxRig.AssertCodeAsync(
            await rig.DiscardAsync(request, new { reason = "Other" }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        var discarded = await rig.DiscardAsync(request, new { reason = "Other", note = "Era una prueba" });

        Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);
        var row = await PublicStoreFixture.JsonAsync(discarded);
        Assert.Equal("Discarded", (string)row["status"]!);
        Assert.Equal("Other", (string)row["discardReason"]!);
        Assert.Equal("Quien atiende", (string)row["discardedBy"]!["displayName"]!);
        Assert.Equal(1, await CountAuditEntriesAsync(rig.Factory, "store.order_request.discarded", request.ToString()));
    }

    [Fact]
    public async Task WithTheStoreModuleOffTheInboxIs403()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        await rig.ExecuteAsync(
            "DELETE FROM tenancy.tenant_modules WHERE tenant_id = @tenant AND module_key = 'store'",
            ("tenant", StoreSettingsFixture.Tenant));

        await StoreInboxRig.AssertCodeAsync(
            await rig.GetAsync(string.Empty), HttpStatusCode.Forbidden, "tenancy.module_not_enabled");
        await StoreInboxRig.AssertCodeAsync(
            await rig.GetAsync("/summary"), HttpStatusCode.Forbidden, "tenancy.module_not_enabled");
    }

    [Fact]
    public async Task AConvertedRowCarriesItsCustomerId()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync();
        var before = await rig.GetJsonAsync($"/{request}");
        Assert.Null(before["customerId"]);

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);
        var after = await rig.GetJsonAsync($"/{request}");
        var order = await rig.OrderAsync((Guid)taken["orderId"]!);

        Assert.Equal((Guid)order["order"]!["clientId"]!, (Guid)after["customerId"]!);
        Assert.Equal((Guid)taken["customerId"]!, (Guid)after["customerId"]!);
        Assert.Equal("Quien atiende", (string)after["convertedBy"]!["displayName"]!);
        Assert.False((bool)after["isBeingTaken"]!);
    }

    [Fact]
    public async Task EachRowCarriesItsOwnCurrency()
    {
        await using var rig = await StoreInboxRig.OpenAsync(PublicStoreFixture.TwoCurrencies, eurPrice: true);
        var cop = await rig.SubmitAsync(currency: "COP");
        var eur = await rig.SubmitAsync(currency: "EUR");

        var rows = (await rig.GetJsonAsync("?view=All"))["items"]!.AsArray()
            .ToDictionary(item => (string)item!["id"]!, item => item!);

        Assert.Equal("COP", (string)rows[cop.ToString()]["currency"]!);
        Assert.Equal(20_000m, (decimal)rows[cop.ToString()]["total"]!);
        Assert.Equal("EUR", (string)rows[eur.ToString()]["currency"]!);
        Assert.Equal(8m, (decimal)rows[eur.ToString()]["total"]!);
    }
}
