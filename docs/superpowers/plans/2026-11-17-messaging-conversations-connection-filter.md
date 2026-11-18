# Messaging: filter conversations by connection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `GET /api/v1/tenants/{tenantId}/messaging/conversations` accepts an optional `connectionId` and returns only the conversations that arrived through that connection.

**Architecture:** One more optional parameter threaded through the existing listing pipeline: endpoint → `ListConversationsQuery` → `ConversationListFilter` → `ConversationQueries.ListAsync`. No new permission, no validator rule (the Guid binding rejects a malformed value, as it does for `tagId`), counts untouched.

**Tech Stack:** .NET 10 minimal APIs, EF Core, xUnit, Testcontainers (`Modules.Messaging.IntegrationTests`).

**Spec:** `docs/superpowers/specs/2026-11-17-messaging-conversations-connection-filter-design.md`

## Global Constraints

- Branch: work in a worktree under `qep-backend-worktrees/`, branch `feature/messaging-connection-filter` from `develop`. Never on `main`.
- Conventional commits, no AI attribution.
- Integration test runs: one at a time on the machine; check `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` before launching. Filter by test name while iterating (`--filter "FullyQualifiedName~..."`).
- `Api.exe` running blocks `dotnet build`: stop only if you started it; otherwise ask.

## Review Focus

1. `connectionId` of another tenant → empty page, never a 403/404 and never another tenant's rows (test in Task 1).
2. `connectionId` combined with `status=Resolved` keeps working on the resolved tab (test in Task 1).
3. `counts` unchanged by the filter (§5.4), like every other filter (test in Task 1).
4. Cursor paging with the filter applied: the second page honours the same `connectionId` (covered by the existing keyset design; the filter is applied before the cursor `Where`, same as `Channel`).
5. Omitting the parameter changes nothing: the whole existing `ConversationListFiltersApiTests` class stays green (Task 1 step 6).

---

### Task 1: `connectionId` on the conversations listing

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs:374-378`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ListConversations.cs:8-11, 93-94`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationQueries.cs:60-71`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationQueries.cs:33-38`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationListFiltersApiTests.cs`

**Interfaces:**
- Produces: `ListConversationsQuery(..., string? Automation = null, Guid? ConnectionId = null)`; `ConversationListFilter(..., AutomationFilter? Automation = null, Guid? ConnectionId = null)`; query string `connectionId=<guid>`.

- [ ] **Step 1: Write the failing integration test**

Add to `ConversationListFiltersApiTests` (the fixture already seeds «Ventas» with phone number id `"111"`; `SeedWhatsAppConnectionAsync` returns the connection's `Guid`, and `MetaPayloads.Inbound(phoneNumberId, ...)` routes by it):

```csharp
// Spec qep-frontend 2026-11-17 (chip y filtro por conexión): connectionId acota la lista a un número; los counts no se mueven.
[Fact]
public async Task TheConnectionFilterKeepsOnlyConversationsOfThatNumberInBothTabsAndTheCountsDoNotMove()
{
    await using var database = await StartDatabaseAsync();
    var f = await ArrangeAsync(database);
    using var _ = f.Factory;
    var ventas = await ScalarAsync<Guid>(f.ConnectionString,
        "SELECT id FROM integrations.connections WHERE tenant_id = @t AND name = 'Ventas'", ("t", f.Tenant.TenantId));
    var soporte = await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Soporte", "333", "444");
    var (laura, _) = await IngestAsync(f, "CO.L", "w1", 1760000100, "Laura");
    using (var anonymous = f.Factory.CreateClient())
    {
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("333", "CO.M", null, "w2", 1760000200, "hola", profileName: "Mateo"));
        await DrainDeliveriesAsync(f.Factory);
    }
    var mateo = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations WHERE user_id = 'CO.M'");
    await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved' WHERE id = @id", ("id", mateo));

    var all = await ListAsync(f, "");
    var onlyVentas = await ListAsync(f, $"?connectionId={ventas}");
    var resolvedSoporte = await ListAsync(f, $"?status=Resolved&connectionId={soporte}");
    var resolvedVentas = await ListAsync(f, $"?status=Resolved&connectionId={ventas}");
    var foreign = await ListAsync(f, $"?connectionId={Guid.CreateVersion7()}");

    Assert.Equal([laura], Ids(all));
    Assert.Equal([laura], Ids(onlyVentas));
    Assert.Equal([mateo], Ids(resolvedSoporte));
    Assert.Empty(Ids(resolvedVentas));
    Assert.Empty(Ids(foreign));
    Assert.Equal(all.GetProperty("counts").GetRawText(), onlyVentas.GetProperty("counts").GetRawText());
}
```

If the `integrations.connections` table or column names differ, read `SeedWhatsAppConnectionAsync` (`MessagingApiHarness.cs:422`) and use what it writes; do not guess.

- [ ] **Step 2: Run it to verify it fails**

Run (PowerShell, from the worktree root):
```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~TheConnectionFilterKeepsOnlyConversationsOfThatNumber"
```
Expected: FAIL — `onlyVentas` still returns `[laura]` **and** `resolvedVentas` returns `[mateo]` (the parameter is ignored), or the assertion on `resolvedVentas` fails.

- [ ] **Step 3: Thread the parameter**

- `MessagingEndpoints.cs:374-378`: add `Guid? connectionId` after `string? automation` in `ListConversationsAsync` and pass it as the last argument of `ListConversationsQuery`.
- `ListConversations.cs:8-11`: add `Guid? ConnectionId = null` as the last record parameter.
- `ListConversations.cs:93-94`: pass `query.ConnectionId` as the last argument of `new ConversationListFilter(...)`.
- `IConversationQueries.cs:60-71`: add `Guid? ConnectionId = null` as the last record parameter, with a one-line `///` comment citing spec qep-frontend 2026-11-17.
- `ConversationQueries.cs:38`: right after the `Channel` block, `if (filter.ConnectionId is { } connectionId) query = query.Where(conversation => conversation.ConnectionId == connectionId);`.

- [ ] **Step 4: Run the test to verify it passes**

Same command as step 2. Expected: PASS.

- [ ] **Step 5: Run the validator unit tests (positional record change)**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessagingValidatorsTests"
```
Expected: PASS (the new parameter has a default, so existing `new ListConversationsQuery(...)` calls compile unchanged).

- [ ] **Step 6: Run the whole filters class once**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationListFiltersApiTests"
```
Expected: PASS, every test.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationListFiltersApiTests.cs docs/superpowers/specs/2026-11-17-messaging-conversations-connection-filter-design.md docs/superpowers/plans/2026-11-17-messaging-conversations-connection-filter.md
git commit -m "feat(messaging): filter the conversations list by connectionId"
```
