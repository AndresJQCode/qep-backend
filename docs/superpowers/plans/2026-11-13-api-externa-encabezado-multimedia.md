# External API Media Template Header Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let `POST /api/v1/external/whatsapp/messages` send approved templates whose header is `IMAGE`, `VIDEO` or `DOCUMENT` by passing a direct `https` link, list those templates with their header type, reject contract errors as 422 before calling Meta, compare the header on replay, and expose the stored link header in the inbox thread — without making those templates sendable anywhere else.

**Architecture:** `TemplateCatalog.Parse` adds two fields to `MessageTemplateDto` (`HeaderMedia`, `SendableWithHeaderMedia`) and leaves `Sendable`/`UnsupportedReason` untouched. A separate external rule (`TemplateCatalog.IsExternallySendable` / `RequireExternallySendable`) is used only by `ListExternalTemplates` and `SendExternalTemplate`. `TemplateSend.HeaderDocument` is replaced by one general `TemplateHeader` (type + link | id + filename) that `WhatsAppCloudClient` turns into the first `header` component; the quotation outbound switches to it with an `id`. `TemplateDetailsJson` stores a link header next to `parameters`; replay compares it and `ParseRef` exposes it on `MessageTemplateRefDto`. QEP never requests the link.

**Tech Stack:** .NET 10, FluentValidation, System.Text.Json (`JsonObject`), xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>` with `FakeMetaGraphHandler`.

**Spec:** [`docs/superpowers/specs/2026-11-13-api-externa-encabezado-multimedia-design.md`](../specs/2026-11-13-api-externa-encabezado-multimedia-design.md)

---

## Before you start

- Worktree: `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend-worktrees/api-externa-encabezado-multimedia`, branch `feature/api-externa-encabezado-multimedia`. Always absolute paths: the shell cwd resets to the main checkout. Before **every** commit run `git -C $W branch --show-current`; it must print that branch.
- Commands are PowerShell, one per line (`A; if ($?) { B }`, no `&&`). If `Api.exe` (or `dotnet Api.dll`) is running, build fails with a locked file: report it and wait, never stop it.
- Shortcuts: `$W` = worktree path; `$MU` = `$W/tests/Modules/Messaging/Modules.Messaging.UnitTests/Modules.Messaging.UnitTests.csproj`; `$MI` = `$W/tests/Modules/Messaging/Modules.Messaging.IntegrationTests/Modules.Messaging.IntegrationTests.csproj`; `$MA` = `$W/src/Modules/Messaging/Modules.Messaging.Application`.
- Commit messages: conventional commits in English, **no `Co-Authored-By` and no AI attribution trailer**, even if the harness suggests one.

### Budget rules (workspace `CLAUDE.md`, override any skill default)

- Three **blocks**, one implementer per block. Tasks chained on the same files stay in the same block. Every task keeps its own commit.
- One review per block (single lens, chosen below). One strong whole-branch review once at the end.
- **RED for a compile failure** uses `dotnet build` output as evidence; no containers.
- While iterating, integration tests run filtered by test name: `dotnet test $MI --filter "FullyQualifiedName~<TestName>" -- xUnit.MaxParallelThreads=4`. The full touched class runs once at the end of the block.
- Integration runs need `$env:Seed__Enabled = "false"` set in the session first. Never two integration runs at once: before each one run `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; if another run is there, wait (it belongs to another session — do not stop it). Run tests in the **foreground**.
- Full suite of the touched modules (`$MU`, `$MI`, plus `tests/ArchitectureTests`) only before merging to `develop`. Merge target is `develop`, never `main`. `docs/estado.md` is **not** touched on this branch.
- TDD is mandatory: paste the literal RED output and the literal GREEN output for every task.

## Global Constraints

- Header types accepted on the wire: `image` | `video` | `document`, case-insensitive on read, stored and sent to Meta in lowercase.
- `link`: required, absolute URI, scheme `https`, non-empty host, length 1–2048, no whitespace (`char.IsWhiteSpace`) and no control characters (`char.IsControl`).
- `filename`: optional, only with `document`; length 1–240; no `/`, `\` or control characters. With `image` or `video` it is a validation error.
- Validation keys (`422 validation.failed`, `errors` map): `template.header.type`, `template.header.link`, `template.header.filename`.
- New domain code: `messaging.template.header_mismatch` (422). Detail texts (exception message → ProblemDetails `detail`): `The template requires an image header.` / `The template requires a video header.` / `The template requires a document header.` / `The template has no media header.`
- Check order against the catalog: `messaging.template.not_available` → `messaging.template.header_mismatch` → `messaging.template.parameters_mismatch`.
- External sendable rule: `Sendable || (SendableWithHeaderMedia && HeaderMedia is not null)`. `AUTHENTICATION`, carousel and flow still block.
- `MessageTemplateDto.Sendable` and `UnsupportedReason` keep their current value for every template (`media_header` for `IMAGE`/`VIDEO`/`DOCUMENT`/`LOCATION`).
- `HeaderMedia` values on `MessageTemplateDto`: `"Image"` | `"Video"` | `"Document"` | `null` (`LOCATION` and unknown formats → `null`, and they count as `other` for `SendableWithHeaderMedia`).
- Meta header component: `{"type":"header","parameters":[{"type":"<t>","<t>":{"link":"…"}}]}`; document adds `"filename"` only when present; quotation keeps `{"id":…,"filename":…}`. Always the first element of `components`.
- Stored detail: `details.template.header = {"type","link","filename"?}`, only for link headers. A detail without `header` reads as "no header".
- Replay (`Repeat`/`SameTemplate`) with a different header (type, link, filename, or present vs absent) → `409 external.idempotency_key_reused`. Resend of a `CanResend` message (`Failed`, not `Unconfirmed`) is not compared and goes out with the new body.
- Thread: `MessageTemplateRefDto.Header` = `{type, link, filename|null}` only when the stored type is one of the three and the link passes the same `https` rule; otherwise `null`.
- QEP never issues an HTTP request to the link; the link never appears in logs, `platform.request_failures` or `api_access.api_key_requests`.
- No migration, no configuration change. Inbox, campaigns, bot and system-template sends keep using `TemplateCatalog.RequireSendable` unchanged.

## Plan decisions (spec §7 and what the code forced)

| # | Decision | Why |
| --- | --- | --- |
| D1 | `TemplateSend.HeaderDocument` and `TemplateHeaderDocument` are **replaced** by `TemplateHeader? Header` (`MessageTemplates.cs:142-147`). The existing quotation test only changes its constructor argument; its assertions stay. | Spec §4.5: one way to build the component. Only two producers exist (`QuotationTemplateOutbound.cs:207`, `WhatsAppCloudClientTemplatesTests.cs:141`). |
| D2 | New `MessageTemplateDto` fields are appended with defaults: `string? HeaderMedia = null, bool SendableWithHeaderMedia = false`. | Only two `new MessageTemplateDto(` call sites exist (`MessageTemplates.cs:368`, `CampaignDispatcherTests.cs:83`); defaults keep the test double compiling. |
| D3 | `SendableWithHeaderMedia` = "no reason other than the media header" (so a plain sendable template is also `true`). The external rule additionally requires `HeaderMedia is not null`. | Spec §4.3 wording "sendable ignoring only the media header"; the extra guard makes the rule explicit. |
| D4 | Separate `TemplateCatalog.RequireExternallySendable`, not a flag on `RequireSendable`. Parameter-key check is extracted into a private helper shared by both. | Spec §4.3: never change the shared one; three callers (`Campaigns.cs:251`, `CampaignDispatch.cs:237`, `TemplateOutbound.cs:32`) stay untouched. |
| D5 | Header mismatch is checked **before** parameters. | Spec §4.2 table order; one fix at a time for the integrator. |
| D6 | Link/filename/type rules live in a new `ExternalMediaLink` static helper (`Application/External/ExternalMediaLink.cs`), used by the validator and by `TemplateDetailsJson.ParseRef`. | Spec §7 "validator with a reusable helper"; one definition of a valid link for input and output. |
| D7 | `SendExternalTemplateCommand` gets `ExternalTemplateHeader? Header = null` as its **last** positional parameter. | The only caller is `ExternalWhatsAppEndpoints.cs:80`; appending keeps the record readable and the default safe. |
| D8 | An empty-string `filename` is a validation error (not "absent"). | "1–240" is the only rule that is unambiguous for the integrator. **(planner's call)** |
| D9 | `QuotationTemplateProvisioning` / `OrderTemplateProvisioning` keep reading `UnsupportedReason == media_header` (`:89`, `:78`). | Spec §4.3 leaves it to the builder; untouched is zero risk. |
| D10 | `TemplateDetailsJson.Serialize` writes `header` only for link headers; an `id` header is never stored. | Spec §4.6: an `id` header never reaches the thread; the quotation path calls `BuildSend` without header anyway. |

## Review Focus

Failure modes the spec implies that ordinary happy-path tests miss. Each one has a test in its owning task.

1. **Replay with a different header** — same `idempotencyKey`, other link / other filename / header removed must be 409, while the identical body is 200 and a `Failed` resend with a new link goes out with the new link. Tests: Task 6 `TheSameIdempotencyKeyWithAnotherHeaderIs409` (theory over three variants), `RepeatingAHeaderSendAnswers200WithoutCallingMetaAgain`, `AFailedHeaderSendResendsWithTheNewLink`.
2. **Header sent to a template without media / missing / wrong type** — must be `header_mismatch` before any conversation is opened and before Meta is called. Tests: Task 4 unit theory `AMissingOrWrongHeaderIsHeaderMismatch`; Task 6 `AHeaderProblemIs422HeaderMismatchAndLeavesNoThread`.
3. **`http`, relative, over-long or whitespace links** — must be `422 validation.failed` with `template.header.link`, never reach Meta, and never be fetched. Tests: Task 5 unit theory `ALinkOutsideTheContractIsRejected` (`http://`, `/logo.png`, 2049 chars, embedded space, `\u0007`, `ftp://`), integration `ABadHeaderIs422WithItsKey`; Task 6 asserts no Meta-handler request mentions `cdn.example.com`.
4. **`filename` on an image/video, or a bad document filename** — 422 on `template.header.filename`. Tests: Task 5 integration `ABadHeaderIs422WithItsKey` rows for `image`+filename, `document`+`a/b.pdf`, `document`+241 chars.
5. **Leakage of the external rule and of the link** — an image template must stay `not_available` in inbox and campaigns; a stored `http` link must not reach the thread; the link (which may carry a token) must not appear in logs or failure tables. Tests: Task 4 `TheSharedRuleStillRejectsAnImageTemplate`; Task 3 `AStoredHeaderOutsideTheContractIsHidden`; Task 8 `AnImageHeaderTemplateIsStillNotAvailableForACampaign`, `AStoredHttpLinkIsHiddenFromTheThread`, `AHeaderLinkNeverAppearsInLogsOrFailureTables`.

---

## Block A — catalog, wire format and stored detail (unit tests only)

One implementer. Files: `MessageTemplates.cs`, `MessagingDtos.cs`, `MessagingErrorCodes.cs`, `WhatsAppCloudClient.cs`, `QuotationTemplateOutbound.cs`, and the new `External/ExternalMediaLink.cs` (Task 3 creates it with `IsValidLink` only, because `ParseRef` needs it; Task 5 adds the filename rule).
Review lens at end of block: `review-reliability`. End-of-block runs: `dotnet test $MU` (whole unit project, no containers) and once `dotnet test $MI --filter "FullyQualifiedName~QuotationTemplateOutboundTests" -- xUnit.MaxParallelThreads=4`.

### Task 1: `HeaderMedia` and `SendableWithHeaderMedia` in the catalog

**Files:**
- Modify: `$MA/MessageTemplates.cs:19-30` (`MessageTemplateDto`), `:170-178` (add `TemplateHeaderMedia`), `:311-369` (`Parse`)
- Test: `$W/tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateCatalogTests.cs` (append), `TemplateCatalogFromStoredTests.cs` (append)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record MessageTemplateDto(
      string Name, string Language, string Category, string ParameterFormat, string? Header, string Body, string? Footer,
      IReadOnlyList<TemplateButtonDto> Buttons, IReadOnlyList<string> Parameters, bool Sendable, string? UnsupportedReason,
      string? HeaderMedia = null, bool SendableWithHeaderMedia = false);

  public static class TemplateHeaderMedia
  {
      public const string Image = "Image"; public const string Video = "Video"; public const string Document = "Document";
      public static string? FromComponentFormat(string? format);   // "IMAGE"/"VIDEO"/"DOCUMENT" (ignore case) → constant; else null
      public static string? FromMetaType(string? type);            // "image"/"video"/"document" (ignore case) → constant; else null
      public static string ToMetaType(string headerMedia);         // "Image" → "image"
  }
  ```

- [ ] **Step 1: Write the failing tests** (append to `TemplateCatalogTests`; keep `AMediaHeaderIsNotSendable` untouched — it is the "no value change" guard):
  ```csharp
  [Theory]
  [InlineData("IMAGE", "Image")]
  [InlineData("VIDEO", "Video")]
  [InlineData("DOCUMENT", "Document")]
  public void AMediaHeaderKeepsItsReasonAndIsSendableWithTheHeader(string format, string expected)
  {
      var template = Single($$"""
          { "name": "aviso_logo", "language": "es", "status": "APPROVED", "category": "UTILITY",
            "components": [ { "type": "HEADER", "format": "{{format}}" }, { "type": "BODY", "text": "Tu guía {{1}}" } ] }
          """);
      Assert.False(template.Sendable);
      Assert.Equal("media_header", template.UnsupportedReason);
      Assert.Equal(expected, template.HeaderMedia);
      Assert.True(template.SendableWithHeaderMedia);
      Assert.Null(template.Header);
  }

  [Fact] public void AMediaHeaderWithAFlowButtonIsNotSendableEvenWithTheHeader()
  // IMAGE header + BUTTONS [{ "type":"FLOW","text":"Abrir" }] →
  //   UnsupportedReason == "media_header" (Worst unchanged), HeaderMedia == "Image", SendableWithHeaderMedia == false

  [Theory] [InlineData("LOCATION")] [InlineData("GIF")]
  public void ALocationOrUnknownHeaderIsNotSendableEvenWithAHeader(string format)
  //   Sendable == false, UnsupportedReason == "media_header", HeaderMedia == null, SendableWithHeaderMedia == false

  [Fact] public void AnAuthenticationTemplateWithAnImageIsNotSendableWithTheHeader()
  // category AUTHENTICATION + IMAGE → UnsupportedReason == "authentication", HeaderMedia == "Image", SendableWithHeaderMedia == false

  [Fact] public void ATextHeaderTemplateHasNoHeaderMediaAndStaysSendable()
  // Single(NamedUtility) → Sendable == true, HeaderMedia == null, SendableWithHeaderMedia == true
  ```
  Append to `TemplateCatalogFromStoredTests`:
  ```csharp
  [Theory] [InlineData("IMAGE")] [InlineData("VIDEO")] [InlineData("DOCUMENT")] [InlineData("LOCATION")]
  public void AStoredMediaHeaderNormalizesLikeTheMetaListing(string format)
  // components = $$"""[{"type":"HEADER","format":"{{format}}"},{"type":"BODY","text":"Hola {{1}}"}]"""
  // stored = TemplateCatalog.FromStored("aviso_logo","es","UTILITY","POSITIONAL", components)
  // listed = Assert.Single(TemplateCatalog.Normalize([ JsonDocument.Parse($$"""{"name":"aviso_logo","language":"es","status":"APPROVED","category":"UTILITY","components":{{components}}}""").RootElement.Clone() ]))
  // Assert.Equal((listed.Sendable, listed.UnsupportedReason, listed.HeaderMedia, listed.SendableWithHeaderMedia),
  //              (stored.Sendable, stored.UnsupportedReason, stored.HeaderMedia, stored.SendableWithHeaderMedia));
  ```
- [ ] **Step 2: Run — expect compile failure (RED evidence, no containers)**
  `dotnet build $MU` → `error CS1061: 'MessageTemplateDto' does not contain a definition for 'HeaderMedia'`.
- [ ] **Step 3: Implement**
  - Add the two DTO fields (D2) and `TemplateHeaderMedia` (next to `TemplateUnsupportedReasons`, `:170`).
  - In `Parse`: keep `reason` exactly as today. Add `string? headerMedia` and `string? otherReason` (seeded with `Authentication` when the category is `AUTHENTICATION`). Every `Worst(reason, x)` call except the media-header one also does `otherReason = Worst(otherReason, x)`. In the non-`TEXT` `HEADER` branch: `reason = Worst(reason, MediaHeader)` (unchanged), then `headerMedia = TemplateHeaderMedia.FromComponentFormat(format)`, and if that is `null` → `otherReason = Worst(otherReason, Other)`.
  - Construct with `HeaderMedia: headerMedia, SendableWithHeaderMedia: otherReason is null`.
- [ ] **Step 4: Run — expect pass**
  `dotnet test $MU --filter "FullyQualifiedName~TemplateCatalog"` → all `TemplateCatalogTests` and `TemplateCatalogFromStoredTests` green, including the untouched `AMediaHeaderIsNotSendable` and `ADocumentHeaderIsNotSendableFromTheInboxAndSaysWhy`.
- [ ] **Step 5: Commit**
  `git -C $W add src/Modules/Messaging/Modules.Messaging.Application/MessageTemplates.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateCatalogTests.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateCatalogFromStoredTests.cs`
  `git -C $W commit -m "feat(messaging): expose media header format in the template catalog"`

### Task 2: one `TemplateHeader` on `TemplateSend` and its Meta component

**Files:**
- Modify: `$MA/MessageTemplates.cs:135-147` (replace `HeaderDocument` / `TemplateHeaderDocument`)
- Modify: `$W/src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs:178-190`
- Modify: `$MA/QuotationTemplateOutbound.cs:207`
- Test: `$W/tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTemplatesTests.cs` (edit `:141`, append)

**Interfaces:**
- Consumes: `TemplateHeaderMedia` (Task 1) is **not** used here; `TemplateHeader.Type` is Meta's lowercase type.
- Produces:
  ```csharp
  public sealed record TemplateSend(
      string Name, string Language, bool Named, IReadOnlyList<TemplateParameter> Parameters,
      IReadOnlyList<TemplateUrlButton>? UrlButtons = null, TemplateHeader? Header = null);

  /// Type: "image" | "video" | "document". Exactly one of Link / MediaId.
  public sealed record TemplateHeader(string Type, string? Link, string? MediaId, string? FileName)
  {
      public static TemplateHeader ByLink(string type, string link, string? fileName);
      public static TemplateHeader DocumentById(string mediaId, string fileName);
  }
  ```

- [ ] **Step 1: Write the failing tests**
  - Edit `ADocumentHeaderTravelsAsTheFirstComponent` (`:141`) only in its argument: `Header: TemplateHeader.DocumentById("media.out", "Cotizacion-COT-1.pdf")`. Assertions unchanged.
  - Append:
  ```csharp
  [Theory] [InlineData("image")] [InlineData("video")]
  public async Task ALinkHeaderTravelsFirstWithItsType(string type)
  {
      var (_, request) = await SendAsync(new SendToPhone("573001234567"), new TemplateSend(
          "aviso_logo", "es", Named: false, [new TemplateParameter("1", "1014853")],
          Header: TemplateHeader.ByLink(type, "https://cdn.example.com/logo.png", null)));
      using var body = JsonDocument.Parse(request.Body!);
      var components = body.RootElement.GetProperty("template").GetProperty("components").EnumerateArray().ToArray();
      Assert.True(JsonNode.DeepEquals(
          JsonNode.Parse($$"""{"type":"header","parameters":[{"type":"{{type}}","{{type}}":{"link":"https://cdn.example.com/logo.png"}}]}"""),
          JsonNode.Parse(components[0].GetRawText())));
      Assert.Equal("body", components[1].GetProperty("type").GetString());
  }

  [Fact] public async Task ADocumentLinkHeaderCarriesItsFilename()
  // ByLink("document", "https://cdn.example.com/f.pdf", "Factura-1.pdf") → components[0] deep-equals
  // {"type":"header","parameters":[{"type":"document","document":{"link":"https://cdn.example.com/f.pdf","filename":"Factura-1.pdf"}}]}

  [Fact] public async Task ADocumentLinkWithoutFilenameSendsNoFilename()
  // ByLink("document", "https://cdn.example.com/f.pdf", null) → the "document" object has exactly one property, "link"
  ```
- [ ] **Step 2: Run — expect compile failure**
  `dotnet build $MU` → `error CS0246: The type or namespace name 'TemplateHeader' could not be found` (and `CS1739` for `Header:`).
- [ ] **Step 3: Implement**
  - Replace the record field and type (D1); update the XML doc of `TemplateSend`.
  - `WhatsAppCloudClient.SendTemplateAsync`: `if (send.Header is { } header)` builds `media = new JsonObject()`; `media["link"] = header.Link` when not null, else `media["id"] = header.MediaId`; `media["filename"] = header.FileName` only when not null; component `{"type":"header","parameters":[{"type":header.Type, [header.Type]: media}]}`, still added first. Keep the existing comment about order.
  - `QuotationTemplateOutbound.cs:207`: `built.Send with { Header = TemplateHeader.DocumentById(mediaId, send.DocumentFileName) }`.
- [ ] **Step 4: Run — expect pass**
  `dotnet test $MU --filter "FullyQualifiedName~WhatsAppCloudClientTemplatesTests"` → all green, including the unchanged quotation assertions.
- [ ] **Step 5: Commit**
  `git -C $W add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTemplatesTests.cs`
  `git -C $W commit -m "refactor(messaging): build template media headers from one header type"`

### Task 3: header in the stored detail, in `BuildSend` and in the thread DTO

**Files:**
- Create: `$MA/External/ExternalMediaLink.cs` (only `MaxLinkLength` and `IsValidLink` in this task)
- Modify: `$MA/MessageTemplates.cs:32-119` (`StoredTemplateSend`, `TemplateDetailsJson`), `:277-290` (`BuildSend`)
- Modify: `$MA/MessagingDtos.cs:249-251` (`MessageTemplateRefDto` + new `MessageTemplateHeaderDto`)
- Test: `TemplateDetailsJsonTests.cs`, `TemplateMessageMappingTests.cs`, `TemplateCatalogTests.cs` (append), plus new `ExternalMediaLinkTests.cs` (link part only)

**Interfaces:**
- Consumes: `TemplateHeader` (Task 2).
- Produces:
  ```csharp
  public sealed record StoredTemplateSend(string Name, string Language, IReadOnlyDictionary<string, string> Parameters, TemplateHeader? Header = null);
  public static string TemplateDetailsJson.Serialize(string name, string language, string category,
      IReadOnlyDictionary<string, string> parameters, TemplateHeader? header = null);   // writes "header" only when header.Link is not null
  public static BuiltTemplate TemplateCatalog.BuildSend(MessageTemplateDto template, IReadOnlyDictionary<string, string> values, TemplateHeader? header = null);
  public sealed record MessageTemplateRefDto(string Name, string Language, MessageTemplateHeaderDto? Header = null);
  public sealed record MessageTemplateHeaderDto(string Type, string Link, string? Filename);
  public static class ExternalMediaLink { public const int MaxLinkLength = 2048; public static bool IsValidLink(string? link); }
  ```

- [ ] **Step 1: Write the failing tests**
  ```csharp
  // ExternalMediaLinkTests (new)
  [Theory]
  [InlineData("http://cdn.example.com/logo.png")] [InlineData("/logo.png")] [InlineData("cdn.example.com/logo.png")]
  [InlineData("ftp://cdn.example.com/logo.png")] [InlineData("https://cdn.example.com/lo go.png")] [InlineData("https://cdn.example.com/\u0007.png")]
  [InlineData("")] [InlineData(null)]
  public void ALinkOutsideTheContractIsRejected(string? link) => Assert.False(ExternalMediaLink.IsValidLink(link));
  [Fact] public void ALinkOf2049CharactersIsRejectedAnd2048IsAccepted()
  // "https://cdn.example.com/" + new string('a', 2048 - 24) → true; one more 'a' → false
  [Fact] public void AnHttpsLinkWithAQueryIsAccepted() => Assert.True(ExternalMediaLink.IsValidLink("https://cdn.example.com/logo.png?token=abc"));

  // TemplateDetailsJsonTests
  [Fact] public void ParseSendReadsTheHeaderSerializeWrote()
  // header = TemplateHeader.ByLink("document", "https://cdn.example.com/f.pdf", "Factura-1.pdf");
  // Assert.Equal(header, TemplateDetailsJson.ParseSend(Serialize("pedido","es_CO","UTILITY", values, header))!.Header);
  [Fact] public void ADetailWithoutHeaderReadsAsNoHeader()       // existing Serialize → ParseSend(...).Header is null
  [Fact] public void AMediaIdHeaderIsNeverStored()               // Serialize(..., TemplateHeader.DocumentById("m","a.pdf")) does not contain "\"header\""

  // TemplateMessageMappingTests
  [Fact] public void ATemplateMessageExposesItsLinkHeader()
  // Map(Row(Template, Serialize("aviso_logo","es","UTILITY", {}, TemplateHeader.ByLink("image","https://cdn.example.com/logo.png", null)))).Template
  //   == new MessageTemplateRefDto("aviso_logo", "es", new MessageTemplateHeaderDto("image", "https://cdn.example.com/logo.png", null))
  [Theory]
  [InlineData("""{"template":{"name":"x","language":"es","header":{"type":"image","link":"http://cdn.example.com/logo.png"}}}""")]
  [InlineData("""{"template":{"name":"x","language":"es","header":{"type":"image","link":"/logo.png"}}}""")]
  [InlineData("""{"template":{"name":"x","language":"es","header":{"type":"location","link":"https://cdn.example.com/a"}}}""")]
  [InlineData("""{"template":{"name":"x","language":"es","header":{"type":"image"}}}""")]
  [InlineData("""{"template":{"name":"x","language":"es","header":"x"}}""")]
  public void AStoredHeaderOutsideTheContractIsHidden(string details) =>
      Assert.Equal(new MessageTemplateRefDto("x", "es"), Map(Row(MessageKind.Template, details)).Template);

  // TemplateCatalogTests
  [Fact] public void BuildSendCarriesTheHeaderToMetaAndToTheDetail()
  // header = ByLink("image","https://cdn.example.com/logo.png",null); built = BuildSend(template, values, header)
  // Assert.Equal(header, built.Send.Header); Assert.Equal(header, TemplateDetailsJson.ParseSend(built.DetailsJson)!.Header);
  // and BuildSend(template, values) (no header) → built.Send.Header is null and ParseSend(...).Header is null
  ```
- [ ] **Step 2: Run — expect compile failure**
  `dotnet build $MU` → `CS0103`/`CS0246` for `ExternalMediaLink`, `MessageTemplateHeaderDto`, and `CS1061` for `StoredTemplateSend.Header`.
- [ ] **Step 3: Implement**
  - `ExternalMediaLink.IsValidLink`: not null; `1 <= Length <= 2048`; no char with `char.IsWhiteSpace || char.IsControl`; `Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0`.
  - `Serialize` adds `["header"] = { "type", "link", "filename"? }` only for `header?.Link is not null`.
  - `ParseSend` reads `header` when it is an object with string `type` and `link` → `TemplateHeader(type, link, null, filename-or-null)`; anything else → `null`.
  - `ParseRef` reads the same object and returns `MessageTemplateHeaderDto` only when `TemplateHeaderMedia.FromMetaType(type)` is not null **and** `ExternalMediaLink.IsValidLink(link)`; `Type` is emitted lowercase (`ToMetaType`).
  - `BuildSend` passes `header` to `TemplateSend.Header` and to `Serialize`.
- [ ] **Step 4: Run — expect pass**
  `dotnet test $MU --filter "FullyQualifiedName~TemplateDetailsJsonTests|FullyQualifiedName~TemplateMessageMappingTests|FullyQualifiedName~TemplateCatalogTests|FullyQualifiedName~ExternalMediaLinkTests|FullyQualifiedName~SendTemplateMessageHandlerTests"` → green (the `SendTemplateMessageHandlerTests` equality asserts on `new MessageTemplateRefDto("hola","es")` still hold).
- [ ] **Step 5: Commit**
  `git -C $W add src/Modules/Messaging/Modules.Messaging.Application tests/Modules/Messaging/Modules.Messaging.UnitTests`
  `git -C $W commit -m "feat(messaging): store and expose the link header of a template send"`

### Task 4: external sendable rule and `header_mismatch`

**Files:**
- Modify: `$W/src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs:69` (add after `TemplateParametersMismatch`)
- Modify: `$MA/MessageTemplates.cs:246-270` (extract parameter check; add the two members)
- Test: create `$W/tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateCatalogExternalTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  /// Spec 2026-11-13 §4.2
  public const string TemplateHeaderMismatch = "messaging.template.header_mismatch";

  public static bool TemplateCatalog.IsExternallySendable(MessageTemplateDto template);   // Sendable || (SendableWithHeaderMedia && HeaderMedia is not null)
  public static MessageTemplateDto TemplateCatalog.RequireExternallySendable(
      IReadOnlyList<MessageTemplateDto> catalog, string name, string language, string? headerMedia, IEnumerable<string> parameterKeys);
  // headerMedia is a TemplateHeaderMedia constant or null (no header sent)
  ```

- [ ] **Step 1: Write the failing tests** — catalog built with `TemplateCatalog.Normalize` from: `aviso` (BODY `Tu guía {{1}}`), `aviso_logo` (IMAGE + same body), `promo_flujo` (IMAGE + body + FLOW button), `otp` (AUTHENTICATION + IMAGE + body), all `es`, `APPROVED`.
  ```csharp
  [Fact] public void AnImageTemplateIsExternallySendableWithAnImageHeader()
  // RequireExternallySendable(catalog, "aviso_logo", "es", TemplateHeaderMedia.Image, ["1"]).Name == "aviso_logo"
  [Theory]
  [InlineData("aviso_logo", null, "The template requires an image header.")]
  [InlineData("aviso_logo", "Video", "The template requires an image header.")]
  [InlineData("aviso", "Image", "The template has no media header.")]
  public void AMissingOrWrongHeaderIsHeaderMismatch(string name, string? header, string detail)
  // ex = Assert.Throws<MessagingDomainException>(...); Assert.Equal(("messaging.template.header_mismatch", detail), (ex.Code, ex.Message));
  [Theory] [InlineData("promo_flujo")] [InlineData("otp")] [InlineData("noexiste")]
  public void ATemplateNotSendableEvenWithTheHeaderIsNotAvailable(string name)   // code "messaging.template.not_available", header "Image"
  [Fact] public void TheHeaderIsCheckedBeforeTheParameters()   // aviso_logo, header null, keys ["2"] → header_mismatch
  [Fact] public void TheParametersStillMustMatch()             // aviso_logo, "Image", keys ["2"] → parameters_mismatch
  [Fact] public void TheSharedRuleStillRejectsAnImageTemplate()// RequireSendable(catalog, "aviso_logo", "es", ["1"]) → not_available
  [Fact] public void OnlyTheExternallySendableAreListed()      // catalog.Where(IsExternallySendable).Select(Name) == ["aviso", "aviso_logo"]
  ```
- [ ] **Step 2: Run — expect compile failure**
  `dotnet build $MU` → `CS0117: 'TemplateCatalog' does not contain a definition for 'RequireExternallySendable'`.
- [ ] **Step 3: Implement** — lookup by name/language as `RequireSendable` does; `!IsExternallySendable` → `TemplateNotAvailable` (same message as today); `!string.Equals(template.HeaderMedia, headerMedia, StringComparison.Ordinal)` → `TemplateHeaderMismatch` with the Global Constraints detail (`a`/`an` by type; "has no media header" when `template.HeaderMedia is null`); then the shared private parameter check. `RequireSendable` behaviour is byte-for-byte the same.
- [ ] **Step 4: Run — expect pass**
  `dotnet test $MU --filter "FullyQualifiedName~TemplateCatalog"` → green. Then end of block: `dotnet test $MU` (whole project) green; then integration once: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; `$env:Seed__Enabled = "false"`; `dotnet test $MI --filter "FullyQualifiedName~QuotationTemplateOutboundTests" -- xUnit.MaxParallelThreads=4` green.
- [ ] **Step 5: Commit**
  `git -C $W add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateCatalogExternalTests.cs`
  `git -C $W commit -m "feat(messaging): add the external template sendable rule and header_mismatch"`

---

## Block B — external API contract, send and listing

One implementer. Files: `ExternalWhatsAppEndpoints.cs`, `External/SendExternalTemplate.cs`, `External/ExternalMediaLink.cs`, `External/ListExternalTemplates.cs`, `ExternalSendApiTests.cs`, `ExternalApiAuthTests.cs`.
Review lens at end of block: `review-risk` (public contract of the external API and a user-supplied URL). End-of-block runs, one at a time: full `ExternalSendApiTests` class, then full `ExternalApiAuthTests` class, both with `-- xUnit.MaxParallelThreads=4`.

Shared test fixture change (first step of Task 5): append to `ExternalSendApiTests.Catalog` (`ExternalSendApiTests.cs:22-31`):
```
{"id":"4","name":"aviso_logo","language":"es_CO","status":"APPROVED","category":"UTILITY",
 "components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Tu guía {{1}} está {{2}}."}]},
{"id":"5","name":"factura_pdf","language":"es_CO","status":"APPROVED","category":"UTILITY",
 "components":[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{1}}, adjuntamos tu factura {{2}}."}]},
{"id":"6","name":"qep_cotizacion","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
 "components":[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"¡Hola, {{nombre_contacto}}! Te compartimos la cotización *{{numero_cotizacion}}*, por *{{valor_total}}*, vigente hasta el *{{vigencia}}*."}]}
```
and a helper `HeaderBody(Guid idempotencyKey, Guid connectionId, string name, object? header, Dictionary<string,string>? parameters = null)` that builds `{ idempotencyKey, connectionId, to = Phone, template = new { name, language, header, parameters } }` (language `es` for `qep_cotizacion`, `Language` otherwise). Default parameters for `aviso_logo`/`factura_pdf`: `{"1":"1014853","2":"Pendiente"}`.

### Task 5: request contract and shape validation

**Files:**
- Modify: `$W/src/Modules/Messaging/Modules.Messaging.Api/ExternalWhatsAppEndpoints.cs:80` (map header), `:107` (record)
- Modify: `$MA/External/SendExternalTemplate.cs:11-13` (command), `:20-46` (validator)
- Modify: `$MA/External/ExternalMediaLink.cs` (add type/filename rules)
- Test: `ExternalMediaLinkTests.cs` (append), `ExternalSendApiTests.cs` (append)

**Interfaces:**
- Produces:
  ```csharp
  // Api
  public sealed record ExternalTemplateRequest(string? Name, string? Language, ExternalTemplateHeaderRequest? Header, IReadOnlyDictionary<string, string?>? Parameters);
  public sealed record ExternalTemplateHeaderRequest(string? Type, string? Link, string? Filename);
  // Application
  public sealed record ExternalTemplateHeader(string? Type, string? Link, string? Filename);
  public sealed record SendExternalTemplateCommand(string? IdempotencyKey, string? ConnectionId, string? To, string? TemplateName,
      string? TemplateLanguage, IReadOnlyDictionary<string, string?>? Parameters, ExternalTemplateHeader? Header = null) : ICommand<ExternalMessageResult>;
  public static class ExternalMediaLink
  {
      public const int MaxLinkLength = 2048; public const int MaxFileNameLength = 240;
      public static bool IsValidLink(string? link);
      public static bool IsValidFileName(string? fileName);   // 1..240, no '/', '\\', char.IsControl
  }
  ```

- [ ] **Step 1: Write the failing tests**
  ```csharp
  // ExternalMediaLinkTests
  [Theory] [InlineData("a/b.pdf")] [InlineData("a\\b.pdf")] [InlineData("fac\u0001.pdf")] [InlineData("")] [InlineData(null)]
  public void AFilenameOutsideTheContractIsRejected(string? name) => Assert.False(ExternalMediaLink.IsValidFileName(name));
  [Fact] public void AFilenameOf241IsRejectedAnd240IsAccepted()

  // ExternalSendApiTests
  public static TheoryData<string, string> BadHeaders => new()
  {
      { """{"type":"gif","link":"https://cdn.example.com/a.gif"}""",                     "template.header.type" },
      { """{"type":"image","link":"http://cdn.example.com/logo.png"}""",                 "template.header.link" },
      { """{"type":"image","link":"/logo.png"}""",                                       "template.header.link" },
      { $$"""{"type":"image","link":"https://cdn.example.com/{{new string('a', 2025)}}"}""", "template.header.link" },   // 2049 chars
      { """{"type":"image","link":"https://cdn.example.com/logo.png","filename":"logo.png"}""", "template.header.filename" },
      { """{"type":"document","link":"https://cdn.example.com/f.pdf","filename":"a/b.pdf"}""",  "template.header.filename" },
      { $$"""{"type":"document","link":"https://cdn.example.com/f.pdf","filename":"{{new string('a', 241)}}"}""", "template.header.filename" },
  };
  [Theory] [MemberData(nameof(BadHeaders))]
  public async Task ABadHeaderIs422WithItsKey(string header, string key)
  // PostRawAsync with aviso_logo + raw header JSON → AssertValidationAsync(response, key); SendCalls(f) == 0; ConversationsAsync(f) == 0
  [Fact] public async Task AHeaderTypeIsReadIgnoringCase()   // "IMAGE" + valid link on aviso_logo → 201 (lands GREEN only after Task 6)
  ```
  Build the 2049-char literal in code and `Assert.Equal(2049, link.Length)` inside the test so the boundary is explicit.
- [ ] **Step 2: Run — RED**
  `dotnet build $MU` → `CS0117` for `MaxFileNameLength`/`IsValidFileName` (compile evidence). Then `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; `$env:Seed__Enabled = "false"`; `dotnet test $MI --filter "FullyQualifiedName~ExternalSendApiTests.ABadHeaderIs422WithItsKey" -- xUnit.MaxParallelThreads=4` → fails: today the binder ignores `header`, so the request is 201/`not_available` instead of `validation.failed`.
- [ ] **Step 3: Implement**
  - Endpoint `:80` passes `template?.Header is { } h ? new ExternalTemplateHeader(h.Type, h.Link, h.Filename) : null`.
  - Validator, `When(command => command.Header is not null)`: `Header.Type` → `TemplateHeaderMedia.FromMetaType(type) is not null`, message `Envía image, video o document.`, key `template.header.type`; `Header.Link` → `ExternalMediaLink.IsValidLink`, message `Envía una URL https completa, de hasta 2048 caracteres y sin espacios.`, key `template.header.link`; `Header.Filename` when not null → if type is `image`/`video` (ignore case): `Envía filename sólo con document.`; else `ExternalMediaLink.IsValidFileName`, message `Envía un nombre de hasta 240 caracteres, sin / ni \.`; key `template.header.filename`. Messages never interpolate the value.
- [ ] **Step 4: Run — expect pass**
  `dotnet test $MU --filter "FullyQualifiedName~ExternalMediaLinkTests"` green; `dotnet test $MI --filter "FullyQualifiedName~ExternalSendApiTests.ABadHeaderIs422WithItsKey" -- xUnit.MaxParallelThreads=4` green (7 cases).
- [ ] **Step 5: Commit**
  `git -C $W add src/Modules/Messaging tests/Modules/Messaging`
  `git -C $W commit -m "feat(messaging): accept and validate a media header on external sends"`

### Task 6: send with header, `header_mismatch`, replay and resend

**Files:**
- Modify: `$MA/External/SendExternalTemplate.cs:113-116` (rule + build), `:230-242` (`SameTemplate`)
- Test: `ExternalSendApiTests.cs` (append)

**Interfaces:**
- Consumes: `RequireExternallySendable`, `TemplateHeaderMedia`, `TemplateHeader.ByLink`, `BuildSend(…, header)`, `StoredTemplateSend.Header`.

- [ ] **Step 1: Write the failing tests**
  ```csharp
  [Fact] public async Task ASendWithAnImageHeaderReachesMetaWithTheLink()
  // HeaderBody(aviso_logo, new { type = "image", link = "https://cdn.example.com/logo.png" }) → 201
  // Meta send body: template.components[0] deep-equals {"type":"header","parameters":[{"type":"image","image":{"link":"https://cdn.example.com/logo.png"}}]}
  // components[1].type == "body"
  // DB: details->'template'->'header'->>'type' || '|' || ->>'link' == "image|https://cdn.example.com/logo.png"
  // messages.text == "Tu guía 1014853 está Pendiente." (no header in the text)
  // Assert.DoesNotContain(f.Factory.MetaHandler.Requests, r => r.Uri!.Host.Contains("cdn.example.com", StringComparison.Ordinal))
  [Fact] public async Task ADocumentHeaderTravelsWithItsFilename()
  // factura_pdf + { type="document", link="https://cdn.example.com/f.pdf", filename="Factura-1.pdf" } → 201; component document.filename == "Factura-1.pdf"
  [Fact] public async Task TheQuotationSystemTemplateIsSentWithALink()
  // qep_cotizacion (language "es") + document link + 4 named parameters → 201; components[0].parameters[0].document.link == link
  [Theory]
  [InlineData("aviso_logo", null, "The template requires an image header.")]
  [InlineData("aviso_logo", "video", "The template requires an image header.")]
  [InlineData("aviso_despacho", "image", "The template has no media header.")]
  public async Task AHeaderProblemIs422HeaderMismatchAndLeavesNoThread(string name, string? type, string detail)
  // header = type is null ? null : new { type, link = "https://cdn.example.com/x" }
  // 422, code "messaging.template.header_mismatch", body.detail == detail, SendCalls == 0, ConversationsAsync == 0
  [Fact] public async Task AnImageTemplateWithoutParametersMatchStillSaysParametersMismatch()
  // aviso_logo + valid image header + parameters {"1":"x"} → parameters_mismatch
  [Fact] public async Task RepeatingAHeaderSendAnswers200WithoutCallingMetaAgain()  // same body twice → 201 then 200, SendCalls == 1
  public static TheoryData<string> OtherHeaders => new()
  {
      """{"type":"image","link":"https://cdn.example.com/otro.png"}""",
      "null",
  };
  [Theory] [MemberData(nameof(OtherHeaders))]
  public async Task TheSameIdempotencyKeyWithAnotherHeaderIs409(string otherHeader)
  // first: aviso_logo with logo.png → 201; second: same key, raw JSON with otherHeader → 409 external.idempotency_key_reused, SendCalls == 1
  [Fact] public async Task TheSameIdempotencyKeyWithAnotherFilenameIs409()
  // factura_pdf: filename "Factura-1.pdf" then "Factura-2.pdf" → 409
  [Fact] public async Task TheSameIdempotencyKeyAddingAHeaderToAPlainSendIs409()
  // aviso_despacho without header → 201; same key, aviso_despacho with an image header → 409 (replay compares before the catalog)
  [Fact] public async Task AFailedHeaderSendResendsWithTheNewLink()
  // pattern of ACertainRejectionIs422AndTheSameIdempotencyKeyResends (ExternalSendApiTests.cs:633):
  // Meta 400 GraphError(132015) on link A → 422 external.message_rejected; Reset(); ScriptMetaOk; same key, link B → 201 same messageId;
  // last Meta send has link B; details header link == B; OutboundMessagesAsync == 1
  ```
- [ ] **Step 2: Run — RED (runtime)**
  `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; `dotnet test $MI --filter "FullyQualifiedName~ExternalSendApiTests.ASendWithAnImageHeaderReachesMetaWithTheLink" -- xUnit.MaxParallelThreads=4` → `422 messaging.template.not_available` instead of 201. Same for `AHeaderProblemIs422HeaderMismatchAndLeavesNoThread` (`not_available` / `201` instead of `header_mismatch`) and `TheSameIdempotencyKeyWithAnotherHeaderIs409` (200 instead of 409).
- [ ] **Step 3: Implement**
  - `:115`: `var headerMedia = command.Header is null ? null : TemplateHeaderMedia.FromMetaType(command.Header.Type);` → `TemplateCatalog.RequireExternallySendable(catalog, name, language, headerMedia, values.Keys)`.
  - `:116`: `TemplateCatalog.BuildSend(template, values, headerMedia is null ? null : TemplateHeader.ByLink(TemplateHeaderMedia.ToMetaType(headerMedia), command.Header!.Link!, command.Header.Filename))`.
  - `SameTemplate`: after name/language/parameters, compare `stored.Header` with the command header: both `null` → equal; one `null` → different; otherwise `Type` equal ignoring case, `Link` and `FileName` ordinal.
  - Nothing logs the link (no new log lines in this handler).
- [ ] **Step 4: Run — expect pass** (filtered, one test name at a time while iterating), then the full `ExternalSendApiTests` class once: `dotnet test $MI --filter "FullyQualifiedName~ExternalSendApiTests" -- xUnit.MaxParallelThreads=4`. `AHeaderTypeIsReadIgnoringCase` from Task 5 must be green now.
- [ ] **Step 5: Commit**
  `git -C $W add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ExternalSendApiTests.cs`
  `git -C $W commit -m "feat(messaging): send external templates with a media header link"`

### Task 7: listing with `header`

**Files:**
- Modify: `$MA/External/ListExternalTemplates.cs:10` (DTO), `:28-31` (filter + projection)
- Test: `$W/tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ExternalApiAuthTests.cs:29-37` (catalog), `:322-341` (rewrite `TemplatesOnlyListSendableOnes`)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record ExternalTemplateDto(string Name, string Language, string Category, ExternalTemplateHeaderDto? Header, IReadOnlyList<string> Parameters);
  public sealed record ExternalTemplateHeaderDto(string Type);   // "image" | "video" | "document"
  ```

- [ ] **Step 1: Write the failing test** — add to `TemplatesPage`: `{"id":"4","name":"qep_cotizacion","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED","components":[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}"}]}` and `{"id":"5","name":"encuesta","language":"es","status":"APPROVED","category":"MARKETING","components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"x"},{"type":"BUTTONS","buttons":[{"type":"FLOW","text":"Abrir"}]}]}`. Rename the test to `TemplatesListTheExternallySendableOnesWithTheirHeader`:
  ```csharp
  // names == ["promo", "qep_cotizacion", "seguimiento"]   (encuesta and borrador absent; ordered by name)
  // promo.header.type == "image"; promo.parameters == []
  // qep_cotizacion.header.type == "document"; parameters == ["nombre_contacto"]
  // seguimiento.header.ValueKind == JsonValueKind.Null; parameters == ["first_name"]; no "body" property (kept from today)
  ```
- [ ] **Step 2: Run — RED (runtime)**: `dotnet test $MI --filter "FullyQualifiedName~ExternalApiAuthTests.TemplatesListTheExternallySendableOnesWithTheirHeader" -- xUnit.MaxParallelThreads=4` → names `["seguimiento"]` only.
- [ ] **Step 3: Implement** — `.Where(TemplateCatalog.IsExternallySendable)`; `Header = template.HeaderMedia is { } media ? new ExternalTemplateHeaderDto(TemplateHeaderMedia.ToMetaType(media)) : null`.
- [ ] **Step 4: Run — expect pass**, then the full `ExternalApiAuthTests` class once (end of Block B).
- [ ] **Step 5: Commit**
  `git -C $W add src/Modules/Messaging/Modules.Messaging.Application/External/ListExternalTemplates.cs tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ExternalApiAuthTests.cs`
  `git -C $W commit -m "feat(messaging): list external templates with their media header type"`

---

## Block C — thread, non-regression, no-leak and README

One implementer. Files: `ExternalSendApiTests.cs`, `CampaignApiTests.cs`, `ExternalApiEndToEndTests.cs`, `README.md`. Production code should not change here; if a test fails, the fix goes in the file it points to and is reported in the block review.
Review lens at end of block: `review-reliability`. End-of-block runs: the three touched classes once each, then `SendTemplateApiTests` once (inbox non-regression: `ATemplateThatIsMissingOrNotSendableIs422NotAvailable` with `promo` already covers the inbox path).

### Task 8: header in the thread, campaign non-regression, link never leaks

**Files:**
- Test: `ExternalSendApiTests.cs` (append), `CampaignApiTests.cs:15-22` (catalog) + append, `ExternalApiEndToEndTests.cs:23-28` (catalog) + append

- [ ] **Step 1: Write the tests**
  ```csharp
  // ExternalSendApiTests — reader client: CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions); GET MessagesUrl(tenantId, conversationId)
  [Fact] public async Task TheThreadShowsTheHeaderOfAnExternalSend()
  // factura_pdf + document link + filename "Factura-1.pdf" → 201; thread item kind "Template":
  //   template.header.type == "document", .link == "https://cdn.example.com/f.pdf", .filename == "Factura-1.pdf"
  // plus an aviso_logo send (no filename) in the same thread → header.filename ValueKind Null
  [Fact] public async Task AStoredHttpLinkIsHiddenFromTheThread()
  // aviso_logo send → ExecuteAsync("UPDATE messaging.messages SET details = jsonb_set(details, '{template,header,link}', '\"http://cdn.example.com/logo.png\"') WHERE direction = 2")
  //   → template.header ValueKind Null, template.name still "aviso_logo"
  // then ExecuteAsync("UPDATE messaging.messages SET details = details #- '{template,header}' WHERE direction = 2") → header Null (old-message shape)
  [Fact] public async Task ASendWithoutHeaderHasANullHeaderInTheThread()   // aviso_despacho → template.header ValueKind Null

  // CampaignApiTests — add {"id":"3","name":"promo_imagen","language":"es","status":"APPROVED","category":"MARKETING","components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Mira"}]}
  [Fact] public async Task AnImageHeaderTemplateIsStillNotAvailableForACampaign()
  // SendAsync(f.Client, Post, CampaignsUrl, Input(f, template: "promo_imagen")) → "messaging.template.not_available"; 0 rows in messaging.campaigns

  // ExternalApiEndToEndTests — add aviso_logo (IMAGE + "Tu guía {{1}} está {{2}}.") to Catalog
  [Fact] public async Task AHeaderLinkNeverAppearsInLogsOrFailureTables()
  // ArrangeAsync(database, traceLogs: true); key = CreateKeyAsync(f, "ERP Contable"); marker = "link-secreto-7Q2"
  // ok:       aviso_logo + { type="image", link=$"https://cdn.example.com/logo.png?token={marker}" } → 201
  // invalid:  same with "http://…?token={marker}" → 422 validation.failed
  // mismatch: aviso_despacho + https link with marker → 422 header_mismatch
  // positive controls: Logs.Levels contains Trace; platform.request_failures count > 0
  // for "logs", "platform.request_failures", "api_access.api_key_requests", "audit.entries": text does not contain marker
  ```
- [ ] **Step 2: Run** — these exercise behaviour built in Blocks A–B, so they are expected GREEN on first run; record the output as evidence. To prove each test can fail (RED evidence), temporarily break the guarded line and run the single test filtered: (a) drop the `IsValidLink` check in `ParseRef` → `AStoredHttpLinkIsHiddenFromTheThread` red; (b) make `CampaignDispatch`/`Campaigns` call `RequireExternallySendable` → campaign test red. Revert before committing (`git -C $W diff --stat` shows only test files).
- [ ] **Step 3: If `AHeaderLinkNeverAppearsInLogsOrFailureTables` is red for real**, find the writer (likely an exception message or a log template) and remove the link from it; do **not** add the request body to any log (spec §4.5).
- [ ] **Step 4: Run the three classes once each**, then `SendTemplateApiTests` once, all green.
- [ ] **Step 5: Commit**
  `git -C $W add tests/Modules/Messaging/Modules.Messaging.IntegrationTests`
  `git -C $W commit -m "test(messaging): cover the media header in the thread and its non-regressions"`

### Task 9: README § API externa

**Files:**
- Modify: `$W/README.md:4651` (listing row), `:4654-4664` (body example), `:4672-4676` (idempotency paragraph), `:4690-4691` (error table)

- [ ] **Step 1: Edit** (Spanish, tuteo, as the rest of the README):
  - Listing row: «Las plantillas enviables: nombre, idioma, categoría, `header` (`null` o `{ "type": "image" | "video" | "document" }`) y parámetros».
  - Body example: add `"header": { "type": "image", "link": "https://cdn.example.com/logo.png" }` to a second example for a template with header, and a short list of the rules: `type` `image`/`video`/`document`; `link` `https` absoluto, máximo 2048, sin espacios; `filename` sólo con `document`, máximo 240, sin `/` ni `\`. QEP no descarga el link.
  - Idempotency paragraph: the comparison includes `header`; a `Failed` resend goes out with the new body.
  - Error table: new row `| 422 | \`messaging.template.header_mismatch\` | Falta el encabezado, sobra o es de otro tipo; \`detail\` dice cuál pide la plantilla |` after `parameters_mismatch`.
  - Note: un link que Meta no puede bajar no da error en el `POST`: el mensaje queda `Failed` después (webhook) y se ve en `GET /messages/{id}` con `failureCode`/`failureReason`.
- [ ] **Step 2: Commit**
  `git -C $W add README.md`
  `git -C $W commit -m "docs: document the media header of the external template api"`

---

## End of branch

- [ ] Strong whole-branch review once (most capable model, `git -C $W diff develop...HEAD`), focused on the five Review Focus items.
- [ ] Before merging to `develop`: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; `$env:Seed__Enabled = "false"`; `dotnet test $MU`; `dotnet test $MI -- xUnit.MaxParallelThreads=4`; `dotnet test $W/tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj` — sequentially, foreground.
- [ ] Merge to `develop` (never `main`); `docs/estado.md` is updated on `develop` in the merge or a `docs:` commit right after, not on this branch.
