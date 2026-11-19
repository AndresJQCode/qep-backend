# Chatbot «Acción de CRM» block — backend

Date: 2026-11-19. Pair spec (editor, canvas, simulator):
`qep-frontend/docs/superpowers/specs/2026-11-19-chatbot-crm-action-design.md`.
Origin: competitive review of the chatbot builder, wave P1. Owner's standing rule: when there is a choice,
take the most professional option; what does not exist in the code is marked as an owner decision, never
invented.

## Intent

A chatbot flow today talks, asks, branches, calls an external API, hands off to an advisor and delegates to
an AI agent, but it cannot **do anything to the tenant's own CRM**. A sales bot that qualifies a lead cannot
tag the customer or move its deal to the next stage, so a person has to do it by hand afterwards. Standalone
builders (ManyChat, Typebot) reach the CRM only through integrations; QEP owns the CRM, so the bot can act on
it natively. This is the block that makes that real. The original builder spec left it out on purpose
(`2026-10-21-constructor-de-chatbots-design.md` §13: «Etiquetar cliente», «Completar ficha», «abrir negocio»).

## What exists in the code today (verified on `develop`, 2026-11-19)

- **Customer tags exist**: a tenant catalog and per-customer assignments (`Customers.Domain/Tag.cs`,
  `Customers.Application/CustomerTags.cs`). The only write is `SetCustomerTagsCommand`, which **replaces the
  whole set** and requires `CustomerManage`.
- **Pipeline deals exist**: `Deal.MoveTo` (`Pipeline.Domain/Deal.cs`) needs an open deal and a target stage of
  kind `Open`; moving to the same stage is a no-op. `MoveDealCommand` requires `DealManage`, a member and an
  optimistic `Version`. The system actor pattern exists (`DealActor.ForSystem`, used by `QuotationEventApplier`).
- **Not in the product**: customer **custom fields** and a customer **advisor/owner**. A deal has
  `OwnerMemberId`; a conversation has an assignee; the customer has neither.
- **`Customer.Update` is all-or-nothing** and calls `EnsureComplete()` (throws, not a domain error); the customer the
  bot creates from a conversation is `Incomplete`, so a single-field write fails for the bot's main customer. The only
  partial mutator is `ReplacePhoneFallbackName`.
- **One pipeline per tenant** (unique index `IX_pipelines_tenant`); Pipeline depends on Customers.
- **Audit has no «detail» field**: `PublishBySystem` and `IPipelineAuditRecorder.Record` carry `changedFields` and `source`.
- **Closest precedent in Automations**: the `dataConsent` block writes to Customers through a port
  (`IAutomationDataConsents`) with a Bootstrapper adapter that resolves the conversation's customer, creating and
  linking one if missing (`CustomerOfAsync`), and audits as the system (`ICustomersAuditPublisher.PublishBySystem`).
- **Layering**: Automations may not reference Customers or Pipeline (`AutomationsLayerTests`); it defines a port
  and the adapter lives in `src/Bootstrapper/`.

## Goals

1. A new block **«Acción de CRM»** with one operation per block, chosen from:
   - **Etiquetar** — add one catalog tag to the conversation's customer.
   - **Quitar etiqueta** — remove one tag.
   - **Mover negocio** — move the customer's open deal to a chosen open stage.
2. Three outputs: **ok** (done, or already in the desired state), **skipped** (nothing to act on) and **error**.
3. Everything the bot does shows in the audit trail of Customers / Pipeline as done by the system on behalf of
   the chatbot, never by a member.
4. The simulator never writes to the CRM.
5. A flow can only reference tags and stages of its own tenant; the editor offers only what the tenant has.

## Non-goals (and owner decisions pending)

- **Custom fields of the customer** and **assigning an advisor to the customer**: they do not exist.
  Pending owner decision whether either should exist as product concepts. Until then no operation for them.
- **«Actualizar dato» (writing a customer field)**: left out of this version. `Customer.Update` needs a complete record
  and the bot's customer is incomplete, so it would fail in the main case; it needs partial-write domain methods in
  Customers first (name, businessName, email, address) and a decision on `Incomplete` customers. Its own spec later.
- Assigning the **conversation** to an advisor without ending the flow (today `handoff` does it and ends the run):
  a separate small block if the owner wants it.
- Several actions in one block (chain blocks instead), creating deals, closing/winning/losing deals, editing
  identification fields (duplicate rules), creating tags from the flow, bulk operations.
- The AI agent calling CRM actions (explicitly out of scope in `2026-10-29-agentes-ia-design.md`).

## Anti-goals

- Moving a deal that is not the one the person means (several open deals).
- A flow replay or a retried unit applying the action twice with a visible effect.
- The simulator or a draft test changing real customer data.
- A bot able to change data the tenant's own permission model would forbid for that tenant (module off).

## Approaches considered

| Approach | Trade-off | Verdict |
|---|---|---|
| **A. One block, one operation, effect + port, system actor (chosen)** | Same shape as `http` / `dataConsent`: engine emits an effect, a port in Automations, an adapter in Bootstrapper calls new system-actor entry points in Customers and Pipeline. Operations are naturally idempotent. Small blocks compose in the flow. | **Chosen** |
| B. One block with a list of actions | Fewer blocks on the canvas, but partial-failure semantics (action 2 of 3 fails) and one `error` output for different causes. | Rejected |
| C. Reuse the existing member commands with a technical "bot member" | No new entry points, but invents an identity, fakes permissions and pollutes audit. | Rejected |

## Design

### Flow contract (schema stays 1, additive)

New node kind `crmAction`:

```json
{ "kind": "crmAction", "operation": "addTag" | "removeTag" | "moveDeal",
  "tagId": "<guid>" | null, "stageId": "<guid>" | null,
  "saveTo": "<prefix>", "outputs": { "ok": null, "skipped": null, "error": null } }
```

- Only the fields of the chosen operation are meaningful; the normalizer clears the others (as for other
  kinds). `saveTo` defaults like the AI block (`crm`, `crm2`, …) and writes `{saveTo}.result` with one of
  `added`, `already_tagged`, `removed`, `not_tagged`, `moved`, `already_in_stage`, `no_open_deal`,
  `several_open_deals`, `no_customer` (skipped), or an error code (`customer_unavailable`, `tag_limit`,
  `deal_conflict`, `stage_not_open`, `module_off`, `failed`). Older frontends ignore the kind only until
  they read a flow that has it: the version check forces a reload.
- Registered in `FlowNodeKinds` (const, `All`, `OutputsOf` → `[ok, skipped, error]`), `FlowDefinition`
  (`CrmActionNode`), `FlowJson` (read/write), `FlowNormalizer`, `FlowValidator`, `FlowIssueCodes`.

### Validation (`FlowValidator`, through `FlowValidationContext`)

`ChatbotEditorBuilder` hands the validator the tenant's CRM data (new context fields: tags, open stages,
which modules are on), as it does for members and agents.

- **Error** `crm.tag_missing` — `tagId` empty or not in the tenant's catalog (also after an import into another tenant).
- **Error** `crm.stage_missing` — `stageId` empty, not in the tenant's pipeline, or of a closed kind.
- **Error** `crm.module_off` — the operation needs a module the tenant does not have (tags: Customers; deals: Pipeline).
- The validator also registers `{saveTo}.result` as a written variable (`WrittenVariables` / `KnownWrittenInOrder`), validates
  `saveTo` with `CheckName`, and treats the unconnected `skipped` / `error` exits with the same severity as other blocks'
  secondary exits.
- Blocks block publishing (errors); warnings do not.

### Execution

- New effect `Effect.CrmAction(NodeId, Operation, …interpolated value)`; `FlowEngine.Enter` emits it and stops
  `Running/Pending` like `http`; the result re-enters as `EngineInput.CrmResult(nodeId, code, ok|skipped|error)` and
  `walk.Leave(node, output)`. `RunEffects.ExternalAsync` maps it, and `PendingInputAsync` re-requests it when a
  unit died while `Running` (as for `http`). It counts for the re-entry limit.
- Port `IAutomationCrm` in `Automations.Application` with `AddTagAsync`, `RemoveTagAsync` and
  `MoveDealAsync`; each returns a result code, never throws a domain error to the engine (a `DomainException`
  becomes the `error` output with its code in `{saveTo}.result`). Adapter in `src/Bootstrapper/` (like
  `AutomationDataConsents`) over the conversation, then over new **system-actor application services**:
  - Customers: add-one and remove-one tag (read the set, apply, write the whole set — `SetCustomerTagsCommand`
    semantics — with the `MaxPerCustomer` limit → `error` `tag_limit`). It must work on an **Incomplete** customer (the
    one the bot creates): the first test of the plan. It skips `IExecutionContext` and permissions like
    `ICustomerDataConsents` and audits through `PublishBySystem`.
  - Pipeline: a system service in the style of `QuotationEventApplier` (module enabled check, repositories,
    `Deal.MoveTo` with `DealActor.ForSystem`, audit `bySystem`) that, unlike that applier, **saves in its own unit of
    work**: it loads the deal fresh (the engine passes no `Version`), and on a concurrency conflict re-reads once and tries
    again, then answers `error` `deal_conflict`.
- **Which customer:** for `addTag` / `removeTag`, the conversation's customer; if the conversation has no user →
  `skipped` `no_customer`; if none is linked it is resolved or created and linked as `CustomerOfAsync` does for
  `dataConsent`, and a creation failure → `error` `customer_unavailable` (`CustomerOfAsync` today returns null for both
  cases and swallows the exception: split them into a result). `moveDeal` never creates a customer: without a linked
  customer and no deal from this conversation → `skipped` `no_open_deal`.
- **Which deal (moveDeal):** the open deal that originated from this conversation (a new `IDealRepository` find method;
  `HasOpenDealFromConversationAsync` only answers a bool) **if it belongs to the conversation's customer**; else the
  customer's only open deal; none → `skipped` `no_open_deal`; several → `skipped` `several_open_deals` (never move a
  guess, never another customer's deal). The target stage must be open (`stage_not_open`, `error`); there is one pipeline
  per tenant, so no pipeline match is needed.
- **Idempotency:** add/remove tag and move to the same stage are no-ops that return `ok` with `already_*` / `not_tagged`; audit
  is written only when something changed.
- **Audit:** the audit of Customers and Pipeline records the change with a new `Chatbot` source value (both publishers get it:
  neither has a detail field) and never a member as the actor. The link to the run lives in Automations' own audit
  (`IAutomationsAuditRecorder`, `bySystem: true`): one entry per applied or skipped action with the chatbot id, run id,
  node id, operation and result code.

### Editor response and simulator

- `ChatbotEditorDto` gains `crm: { tags: [{id,name}], stages: [{id,name}] (open stages, in pipeline order), customersEnabled, pipelineEnabled }`,
  read only when the flow has a `crmAction` block or the editor asks for the palette (modules on), as `AiOfAsync` does
  for agents. Older frontends ignore it.
- `Simulations.cs`: the `CrmAction` effect never calls the port. It emits an existing **system line** (`lines.System`, which
  the frontend already renders) with the text «Simulación: se etiquetaría al cliente con «Interesado»» (no new DTO
  field), writes `{saveTo}.result` with the operation's success code (`added`, `removed` or `moved`, so downstream
  conditions can be tested) and leaves by `ok`.

### Duplicate, templates, export / import

Duplicate and «guardar como plantilla» in the same tenant keep the ids. Export keeps them; an import or paste into
another tenant fails `crm.tag_missing` / `crm.stage_missing` until the person picks the new tenant's tag or stage
(the editor shows it as an error on the block). `TemplateParameters.Apply` must carry the new node through, and
`Templates.cs` must treat `tagId` / `stageId` like it treats the HTTP block's `ConnectionId` (tenant-specific ids); `Chatbots.cs`
import/duplicate remapping needs nothing unless ids get remapped.

### Where a new node kind must be handled (checklist)

`FlowNodeKinds` (const, `All`, `OutputsOf`), `FlowDefinition`, `FlowJson` (read and write), `FlowNormalizer`, `FlowValidator`
(per-kind case, written variables, text fields, `CheckName`), `FlowIssueCodes`, `Effect` / `EngineInput`, `FlowEngine.Enter`
and the result handler, `RunEffects` (`ExternalAsync`, `PendingInputAsync`, the re-entry counter), `ChatbotEditorBuilder` and
`FlowValidationContext`, `AutomationsDtos`, `Simulations.cs`, `Templates.cs`, `SystemTemplateCatalog` (only if it switches on kind).

## Constraints

- TDD, RED before GREEN with literal evidence. `Modules.Automations.Application` references no EF Core or Npgsql and
  no other module; the adapter and the new system entry points keep `ArchitectureTests` green (a new
  `<Modulo>LayerTests` is not needed: no new module).
- Integration tests filtered by name while iterating; the touched projects once before merging.
- Deploy backend before frontend: every field is additive.

## Calls made by the designer (check them)

- One operation per block, with `ok` / `skipped` / `error`, and a `{saveTo}.result` code for branching on the cause.
- «Actualizar dato» is out of this version (Customers has no partial write; the bot's customer is incomplete).
- Several open deals → `skipped`, never a guess; a deal from this conversation wins.
- A customer missing for the conversation is created and linked for tags, as `dataConsent` does; `moveDeal` never creates one.
- Tag add/remove are built on the replace-all command inside a system service, since there is no add-one command.
- The simulator never writes and answers with a system line and the operation's success code.
- The Customers and Pipeline audit gets a `Chatbot` source; the run link lives in Automations' audit.

## Left to the builder

- Names of the port, adapter and system services.
- Whether Pipeline's system entry point is a service or a `MoveDealForSystemCommand`.
- The exact result-code strings beyond the ones listed, as long as they are stable and documented in the frontend spec.
