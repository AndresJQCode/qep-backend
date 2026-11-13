# Shopify: líneas personalizadas (backend) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> Las reglas de «Presupuesto de pruebas y ejecución de planes» del `CLAUDE.md` del workspace mandan sobre el skill: **un implementador por bloque**, no por tarea; cada tarea conserva su commit; una revisión por bloque y una revisión fuerte de toda la rama al final.

**Goal:** Que un pedido de Shopify con una línea personalizada (sin producto en Shopify) se importe con la variante de Catalog que la tienda asoció a ese nombre exacto, y que, sin asociación, espere en `product_unmatched` diciendo qué nombres faltan para que la bandeja ofrezca «Asociar a un producto».

**Architecture:** `ShopifyLine` suma `Title` e `IsCustom` con default (los snapshots ya guardados siguen deserializando). Los dos parsers marcan `IsCustom` cuando la línea trae **explícitamente** los dos ids en `null`. Una tabla nueva `shopify.custom_line_mappings` (`connection_id + title → variant_id`) vive en el módulo Shopify, con su repositorio en Infrastructure y la violación del índice único traducida por nombre a `409`. El importador resuelve primero las líneas personalizadas contra las asociaciones de la conexión y la variante por id (método nuevo del puerto `IShopifyProductLookup`), y el resto por SKU como hoy; `wait_detail` pasa a `{"skus":[…],"customTitles":[…]}`. Tres rutas nuevas (`GET`/`POST`/`DELETE`) bajo la tienda, con los permisos de la bandeja, en archivos nuevos para no chocar con la rama paralela.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-13-shopify-lineas-personalizadas-design.md`](../specs/2026-11-13-shopify-lineas-personalizadas-design.md)

---

## Antes de empezar

- Checkout: `C:/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend`, rama `feature/shopify-lineas-personalizadas`. Antes de **cada** commit, `git -C $W branch --show-current` tiene que decir esa rama (la rama es un estado, no lo que leíste al abrir la sesión).
- Comandos en PowerShell, uno por línea (`A; if ($?) { B }`). Si `Api.exe` (o `dotnet Api.dll`) está corriendo, el build falla por archivo bloqueado: avisa y espera, no lo detengas.
- **RED de compilación:** la evidencia es la salida de `dotnet build <proyecto de pruebas>`, sin contenedores.
- **Integración** sólo en GREEN, con `$env:Seed__Enabled = "false"` en la sesión, filtrada por nombre (`--filter "FullyQualifiedName~<Prueba>"`) y `-- xUnit.MaxParallelThreads=4`. La clase completa, una vez al final del bloque. Pruebas siempre en primer plano. Antes de una corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra corrida (de otra sesión), espera: nunca dos corridas de integración a la vez y nunca detengas una ajena.
- Atajos: `$W` = la ruta del checkout; `$SU` = `$W/tests/Modules/Shopify/Modules.Shopify.UnitTests`; `$SI` = `$W/tests/Modules/Shopify/Modules.Shopify.IntegrationTests`; `$BU` = `$W/tests/Bootstrapper/Bootstrapper.UnitTests`; `$AT` = `$W/tests/ArchitectureTests/ArchitectureTests`.
- **Sesión paralela:** `feature/shopify-document-sources` (worktree `..\qep-backend-worktrees\shopify-document-sources`) toca `ShopifyEndpoints.cs`, `ShopifyDtos.cs`, `ShopifyRepositories.cs` (Application e Infrastructure), `src/Bootstrapper/QepServiceCollectionExtensions.cs` y `ShopifyWebhookTestDoubles.cs`, sin migración. Por eso este plan **no edita** `ShopifyEndpoints.cs`, `ShopifyDtos.cs` ni los dos `ShopifyRepositories.cs`, y en `QepServiceCollectionExtensions.cs` sólo **agrega una línea** al final del bloque de Shopify, sin reordenar nada. Rutas, DTOs, handlers y repositorio van en archivos nuevos `ShopifyCustomLineMapping*.cs`.
- `docs/estado.md` **no se toca** en esta rama.

## Global Constraints

- Tabla `shopify.custom_line_mappings`, columnas exactas (contrato con `lulo-crm-migrator`, que siembra por SQL): `id uuid PK` (GUID v7), `tenant_id uuid`, `connection_id uuid`, `title varchar(255)`, `variant_id uuid` (sin FK), `created_at timestamptz`, `created_by uuid`. Todas `NOT NULL`.
- Índice único `IX_custom_line_mappings_connection_title` sobre `(connection_id, title)`; su violación se traduce **por nombre de índice**, en Infrastructure, a `409 shopify.custom_line.already_mapped` (`ResourceConflictException`).
- `title` se guarda y se compara con `Trim()`, ordinal: sensible a mayúsculas y emojis, sin otra normalización. ≤ 255 caracteres tras el recorte.
- Códigos de error: `shopify.custom_line.already_mapped` (409), `shopify.custom_line.variant_not_found` (422), `shopify.custom_line.variant_inactive` (422).
- Permisos: `quotations.order.read` para `GET`, `quotations.order.manage` para `POST` y `DELETE` (`ShopifyPermissions.OrderRead`/`OrderManage`, ya existen con política: no se registra ninguna política nueva).
- El handler revalida con `ShopifyAuthorization.EnsureAsync` y la conexión con `ShopifyStoreSupport.ConnectionAsync`: **403, nunca 404**.
- Rutas: `/api/v1/tenants/{tenantId:guid}/shopify/stores/{connectionId:guid}/custom-line-mappings` (`GET`, `POST`) y `/…/custom-line-mappings/{id:guid}` (`DELETE` → 204).
- `wait_detail` de `product_unmatched` = `{"skus":[…],"customTitles":[…]}`, en ese orden, **las dos llaves siempre presentes** (arreglos vacíos si no aplica). `skus`: SKU recortados de líneas no personalizadas, `null` para una sin SKU, sin repetir. `customTitles`: nombres recortados de líneas personalizadas sin asociación, con variante inexistente o inactiva, sin repetir (ordinal), en orden de aparición. Una línea personalizada sin `Title` entra en `skus` como `null`.
- Una línea **no** personalizada nunca casa por nombre (anti-objetivo). Una asociación nunca aplica fuera de su `connection_id` (anti-objetivo).
- `Modules.Shopify.*` no referencia Catalog (`ShopifyLayerTests`): la variante entra por `IShopifyProductLookup` y su adaptador en Bootstrapper. `Modules.Shopify.Application` no referencia EF Core ni Npgsql.
- Commits: conventional commits en inglés, **sin `Co-Authored-By` ni ninguna atribución de IA** (aunque el harness lo sugiera).

## Decisiones del plan (lo que el spec deja al builder y lo que el código obligó)

Todas son **decisión mía (planner)**; el owner las puede revertir.

| # | Decisión | Por qué |
| --- | --- | --- |
| D1 | `IsCustom` exige que la línea traiga las **dos llaves presentes y en `null`**: REST `product_id` y `variant_id`; GraphQL `variant` y `product`. Una llave **ausente** no cuenta como `null`. | `ShopifyJson.Find` devuelve `null` también para una llave ausente, y las fixtures REST y GraphQL actuales (`Fixtures/rest/*.json`, `Fixtures/graphql/*.json`) no traen esas llaves: con «ausente = nulo» todas sus líneas pasarían a personalizadas, sin `Title`, y todo el flujo de importación quedaría en espera. Shopify real siempre manda las llaves (backup de Lulo: `product_id: null`). |
| D2 | El contrato de la consulta GraphQL se prueba mandando `variant` y `product` en `null` en `OrderQueryRequestsEveryFieldTheParserReads`: con D1, si la consulta deja de pedirlos, `IsCustom` sale `false` y la prueba se pone roja. | `AssertPopulated` exige `true` en todo `bool`; es la forma de que la prueba vigile esos dos campos. |
| D3 | Dos puertos de lectura de Catalog: `IShopifyProductLookup.FindByVariantIdsAsync` (el importador y el `POST`, mismo `ShopifyProductRef`) y uno aparte, `IShopifyVariantLabels.DescribeAsync`, para el `GET` (nombre visible y código). | `ShopifyProductRef` no trae nombre (spec, «Queda al builder»). Sumarle el nombre obligaría a `FindByCodesAsync` a cargar opciones en cada pedido y a cambiar el ctor de `ShopifyProductLookup` y sus pruebas. El adaptador nuevo usa `FindVariantsByIdsAsync` + `CatalogVariantReads` como `QuotationProductLookup`. |
| D4 | `productName` del DTO es el **nombre visible de la variante** (`CatalogVariantReads.DisplayNameOf`: producto + valores de opción). | Es lo que el asesor reconoce en la bandeja; para la variante por defecto es el nombre del producto. |
| D5 | **Variante borrada en Catalog** (no hay FK): el `GET` devuelve igual la fila, con `productName = null`, `variantCode = null`, `isActive = false`. Los dos campos de texto del DTO son nullable. El frontend la muestra como «Variante eliminada». | Pedido del coordinador; sin esto la fila desaparecería y no se podría borrar desde la pantalla. Prueba en la Tarea 5. |
| D6 | `created_by` = **id de membresía** activa del actor (`IMembershipDirectory.FindActiveMembershipIdAsync`), como `shopify.stores.updated_by`. Sin membresía activa → 403. | Consistencia con la tienda. **El migrador tiene que sembrar el id de membresía del dueño, no su id de usuario**: hay que avisarlo en el spec del migrador. |
| D7 | `DELETE` de un id que no existe en `(tenant, conexión)` responde **204 sin hacer nada y sin auditar** (idempotente). | El spec prohíbe 404 y no da código; un 403 ante un doble clic diría «no tienes permiso». Un id de otro tenant tampoco revela nada: mismo 204, nada borrado. |
| D8 | Auditoría con `IShopifyAuditRecorder` (atómica, como `shopify.store.updated`; el spec dice «publicador del módulo», que en el código es éste). Acciones `shopify.custom_line_mapping.created` / `shopify.custom_line_mapping.deleted`, recurso `shopify_custom_line_mapping`, id de la asociación; detalle `{"connectionId":"…","title":"…","variantId":"…"}`. | El texto exacto es del builder (spec). El título no es dato sensible. |
| D9 | Orden del `GET`: por `title` con `StringComparer.Ordinal`, en memoria. | Determinista e igual en cualquier collation de Postgres. |
| D10 | Orden de chequeos del `POST`: tenant/permiso/módulo (403) → validador (422 `validation.failed`) → conexión (403) → membresía (403) → variante (422 `variant_not_found`, luego `variant_inactive`) → guardar (409). | Igual que `UpdateShopifyStoreHandler`. |
| D11 | El 409 sale **sólo** del índice (`SaveChangesAsync`), sin consulta previa. | Regla del repo: una comprobación previa deja pasar la carrera. |
| D12 | Registro en DI: handlers, validador y adaptadores en un archivo nuevo `src/Bootstrapper/ShopifyCustomLineMappingsRegistration.cs` (`AddShopifyCustomLineMappings`), y **una línea** `services.AddShopifyCustomLineMappings();` agregada al final del bloque de Shopify de `QepServiceCollectionExtensions.cs`. El repositorio, en `ShopifyInfrastructureExtensions.cs`. Rutas en `ShopifyCustomLineMappingEndpoints.cs` (`MapShopifyCustomLineMappingEndpoints`), con una línea en `src/Api/Program.cs` justo después de `app.MapShopifyEndpoints();`. | Minimiza el choque con `feature/shopify-document-sources`. |
| D13 | Los códigos de error se agregan **al final** de `ShopifyErrorCodes` (`ShopifyDomainException.cs`) y las acciones al final de `ShopifyAuditActions.cs`. | «Los códigos del módulo, en un solo lugar»; ninguno de los dos archivos lo toca la otra rama. |
| D14 | El comentario de `ShopifyDtos.cs:59-60` (que describe `{"skus":[…]}`) se corrige **después** de mergear `develop` (Tarea 7), no en las tareas. | `ShopifyDtos.cs` lo edita la otra rama. |

## Review Focus

1. **Fixtures sin las llaves de ids vueltas personalizadas** (D1): todo pedido existente quedaría en espera. Tarea 1: `ALineWithoutTheIdKeysIsNotCustom` (REST y GraphQL) y `RestAndGraphQlSameOrderProduceEqualSnapshots` sigue verde; fin del bloque A corre `ShopifyImportFlowTests` completa.
2. **La consulta GraphQL deja de pedir `variant`/`product`/`title`**: la conciliación nunca vería una línea personalizada, sin error. Tarea 1: `OrderQueryRequestsEveryFieldTheParserReads` ampliada (D2) y `ParseGraphQlCustomLineReadsTitleAndIsCustom`.
3. **Una línea de producto real sin SKU casa por nombre** (anti-objetivo). Tarea 4: `ARealProductLineWithoutSkuNeverMatchesByTitle`.
4. **La violación del índice sale como 500 o 412** en vez de `409 shopify.custom_line.already_mapped` (el `ShopifyUnitOfWork` actual traduce los otros índices a `concurrency.conflict`). Tarea 2: `DuplicateTitleOnTheSameConnectionIsAlreadyMapped` (incluye `" X "` vs `"X"`); Tarea 5: `PostTheSameTitleTwiceIs409`.
5. **Una asociación de otra tienda del mismo tenant aplica** (anti-objetivo). Tarea 2: `FindVariantIdsByTitlesOnlyReadsItsConnection`; Tarea 4: `AMappingOfAnotherStoreDoesNotApply`.

---

## Bloque A — lectura de la línea y tabla de asociaciones (Tareas 1–2, un implementador)

### Tarea 1: `Title` e `IsCustom` en el snapshot y los parsers

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderSnapshot.cs:52` (`ShopifyLine` y su comentario)
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Parsing/RestOrderParser.cs:60-73`
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Parsing/GraphQlOrderParser.cs:67-80`
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Parsing/ShopifyJson.cs` (helper de llave presente y nula)
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Admin/ShopifyGraphQlClient.cs:42-43` (`LineFields`)
- Test: `$SU/OrderParsersTests.cs`, `$SU/ShopifyGraphQlClientTests.cs` (`ShopifyGraphQlQueryContractTests`, `:405-500`)

**Interfaces — Produces:**
```csharp
public sealed record ShopifyLine(
    string? Sku, decimal Quantity, decimal OriginalUnitPrice, decimal DiscountAmount,
    string? Title = null, bool IsCustom = false);

// ShopifyJson
/// La llave existe y vale null. Ausente → false (D1).
public static bool IsExplicitNull(JsonElement element, string name);
```
- REST: `Title = OptionalString(line, "title", $"{field}.title")`; `IsCustom = IsExplicitNull(line, "product_id") && IsExplicitNull(line, "variant_id")`. No se lee `product_exists`.
- GraphQL: `Title = OptionalString(line, "title", …)`; `IsCustom = IsExplicitNull(line, "variant") && IsExplicitNull(line, "product")`.
- `LineFields` = `"sku title quantity variant { id } product { id } originalUnitPriceSet { … } discountAllocations { … }"` (sin alias ni fragmentos; vale para `OrderQuery` y `OrderLinesQuery`).

- [ ] **RED.** En `OrderParsersTests` (JSON inline, con `Json(...)`):
  ```csharp
  [Fact] public void ParseRestCustomLineReadsTitleAndIsCustom()
  // line_items: [{"title":"Garantía por 1 año + Envío Prioritario🚛","product_id":null,"variant_id":null,"product_exists":false,"sku":null,"quantity":1,"price":"5000.00","discount_allocations":[]}]
  // Assert.Equal(new ShopifyLine(null, 1m, 5_000m, 0m, "Garantía por 1 año + Envío Prioritario🚛", true), Assert.Single(snapshot.Lines));
  [Fact] public void ParseGraphQlCustomLineReadsTitleAndIsCustom()
  // lineItems.nodes: [{"title":"Garantía…🚛","variant":null,"product":null,"sku":null,"quantity":1,"originalUnitPriceSet":{"shopMoney":{"amount":"5000.0"}},"discountAllocations":[]}]
  // mismo ShopifyLine esperado
  [Theory, InlineData("rest"), InlineData("graphql")]
  public void ALineOfADeletedProductIsNotCustom(string format)
  // REST: "product_id":632910392,"variant_id":808950810,"product_exists":false,"sku":"CAM-001","title":"Camiseta"
  // GraphQL: "variant":{"id":"gid://shopify/ProductVariant/1"},"product":null
  // Assert.False(line.IsCustom); Assert.Equal(("CAM-001", "Camiseta"), (line.Sku, line.Title))  (GraphQL: Sku "CAM-001")
  [Theory, InlineData("rest"), InlineData("graphql")]
  public void ALineWithoutTheIdKeysIsNotCustom(string format)   // Review Focus 1: Fixture(format, "taxes-included") → todas las líneas IsCustom == false
  [Fact] public void AnOldPayloadWithoutTitleReadsDefaults()
  // var json = """{… snapshot serializado por WritePayload, con "lines":[{"sku":"CAM-001","quantity":2,"originalUnitPrice":50000,"discountAmount":0}] …}""";
  // ShopifyOrderIngestion.ReadPayload(json).Lines[0] == new ShopifyLine("CAM-001", 2m, 50_000m, 0m) (Title null, IsCustom false)
  ```
  Arma el JSON viejo de `AnOldPayloadWithoutTitleReadsDefaults` serializando un snapshot de prueba con `WritePayload` y **quitándole** `title`/`isCustom` de cada línea con `JsonNode` antes de `ReadPayload` (así no depende de escribir a mano el snapshot entero).
  En `ShopifyGraphQlQueryContractTests`: agrega a `Leaf` `("id", "variant") => "gid://shopify/ProductVariant/1"` y `("id", "product") => "gid://shopify/Product/1"`; en `OrderQueryRequestsEveryFieldTheParserReads`, antes de parsear, pon `variant` y `product` en `null` en cada nodo de `lineItems.nodes` (D2). Agrega `[Fact] public void LineQueryAsksForTitleVariantAndProduct()` → `Assert.Contains("title", …)`, `"variant { id }"`, `"product { id }"` en `OrderQuery` **y** en `OrderLinesQuery`.
- [ ] Corre `dotnet build $SU` → falla con `CS1739`/`CS1729` (`ShopifyLine` no tiene `Title`/`IsCustom`).
- [ ] **GREEN.** Implementa las firmas. Actualiza el comentario de `ShopifyLine`: qué es una línea personalizada, D1 y que los defaults existen para los payloads guardados antes del cambio.
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~OrderParsersTests|FullyQualifiedName~ShopifyGraphQlClientTests|FullyQualifiedName~ShopifyGraphQlQueryContractTests"` → verde.
- [ ] Commit: `feat(shopify): read title and custom flag of order lines`

### Tarea 2: tabla, entidad y repositorio de asociaciones

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyCustomLineMapping.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyDomainException.cs` (al final de `ShopifyErrorCodes`, D13)
- Create: `src/Modules/Shopify/Modules.Shopify.Application/IShopifyCustomLineMappingRepository.cs`
- Create: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyCustomLineMappingRepository.cs`
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyDbContext.cs` (constante del índice, `DbSet`, `ConfigureCustomLineMapping`)
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/ShopifyUnitOfWork.cs` (nuevo `catch`)
- Modify: `src/Modules/Shopify/Modules.Shopify.Infrastructure/ShopifyInfrastructureExtensions.cs` (registro scoped del repositorio)
- Create: migración `AddCustomLineMappings` en `src/Modules/Shopify/Modules.Shopify.Infrastructure/Persistence/Migrations/` (la última hoy es `20261026211954_AddCityCorrectionJobs`)
- Test: nuevo `$SU/ShopifyCustomLineMappingTests.cs`; `$SI/ShopifyPersistenceTests.cs`

**Interfaces — Produces:**
```csharp
// Domain
public sealed class ShopifyCustomLineMapping
{
    public const int TitleMaxLength = 255;
    public Guid Id { get; private set; }            // Guid.CreateVersion7()
    public Guid TenantId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string Title { get; private set; }       // recortado
    public Guid VariantId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    /// Recorta; vacío, > TitleMaxLength o Guid.Empty en ids → ArgumentException (el validador lo frena antes).
    public static ShopifyCustomLineMapping Create(Guid tenantId, Guid connectionId, string title, Guid variantId, Guid createdBy, DateTimeOffset now);
    /// Trim; null o en blanco → null. La única regla de comparación: la usan el dominio, el validador y el importador.
    public static string? NormalizeTitle(string? title);
}

// ShopifyErrorCodes
public const string CustomLineAlreadyMapped = "shopify.custom_line.already_mapped";     // 409
public const string CustomLineVariantNotFound = "shopify.custom_line.variant_not_found"; // 422
public const string CustomLineVariantInactive = "shopify.custom_line.variant_inactive"; // 422

// Application
public interface IShopifyCustomLineMappingRepository
{
    /// Claves ordinales = título recortado; sólo filas de (tenantId, connectionId). Una consulta para todos los títulos.
    Task<IReadOnlyDictionary<string, Guid>> FindVariantIdsByTitlesAsync(
        Guid tenantId, Guid connectionId, IReadOnlyCollection<string> titles, CancellationToken cancellationToken);
    Task<IReadOnlyList<ShopifyCustomLineMapping>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ShopifyCustomLineMapping?> FindAsync(Guid tenantId, Guid connectionId, Guid id, CancellationToken cancellationToken);
    void Add(ShopifyCustomLineMapping mapping);
    void Remove(ShopifyCustomLineMapping mapping);
}

// ShopifyDbContext
public const string CustomLineMappingConnectionTitleIndex = "IX_custom_line_mappings_connection_title";
public DbSet<ShopifyCustomLineMapping> CustomLineMappings => Set<ShopifyCustomLineMapping>();
```
Mapeo: tabla `custom_line_mappings` en `Schema`, columnas snake_case de las Global Constraints, `title` con `HasMaxLength(ShopifyCustomLineMapping.TitleMaxLength)`, índice único `(ConnectionId, Title)` con `HasDatabaseName(CustomLineMappingConnectionTitleIndex)`. En `ShopifyUnitOfWork.SaveChangesAsync`, antes de los demás `catch (DbUpdateException …)`: `IsUniqueViolation(exception, ShopifyDbContext.CustomLineMappingConnectionTitleIndex)` → `throw new ResourceConflictException(ShopifyErrorCodes.CustomLineAlreadyMapped, "…", exception)` (revisa la firma real de `ResourceConflictException` en `BuildingBlocks.Application/ApplicationExceptions.cs`).

- [ ] **RED.** En `ShopifyCustomLineMappingTests`:
  ```csharp
  [Fact] public void CreateTrimsTheTitleAndKeepsCaseAndEmoji()
  // Create(..., "  Garantía … en este Pedido🚛  ", ...) → Title == "Garantía … en este Pedido🚛"; Id != Guid.Empty
  [Theory, InlineData(""), InlineData("   ")] public void CreateWithoutTitleThrows(string title)   // ArgumentException
  [Fact] public void CreateWithATitleOf256Throws()        // new string('a', 256) → ArgumentException; 255 + espacios alrededor → ok
  [Fact] public void NormalizeTitleReturnsNullForBlank()  // null, "", "  " → null; " X " → "X"
  ```
  En `ShopifyPersistenceTests` (contenedor, migraciones reales):
  ```csharp
  [Fact] public async Task CustomLineMappingRoundTripsWithTheContractColumns()
  // guarda una; SELECT id, tenant_id, connection_id, title, variant_id, created_at, created_by FROM shopify.custom_line_mappings → los mismos valores
  [Fact] public async Task DuplicateTitleOnTheSameConnectionIsAlreadyMapped()      // Review Focus 4
  // "Garantía🚛" y luego " Garantía🚛 " en la misma conexión → ResourceConflictException con Code == "shopify.custom_line.already_mapped"
  // "garantía🚛" (otra mayúscula) en la misma conexión → se guarda
  [Fact] public async Task FindVariantIdsByTitlesOnlyReadsItsConnection()           // Review Focus 5
  // mismo título en conexión A (variante 1) y B (variante 2) del mismo tenant: FindVariantIdsByTitlesAsync(tenant, A, ["Garantía🚛","Otra"]) == { "Garantía🚛": variante 1 }
  ```
- [ ] Corre `dotnet build $SU` y `dotnet build $SI` → `CS0246 The type or namespace name 'ShopifyCustomLineMapping' could not be found`.
- [ ] **GREEN.** Implementa. Genera la migración desde `$W`:
  ```powershell
  dotnet ef migrations add AddCustomLineMappings --project src/Modules/Shopify/Modules.Shopify.Infrastructure --context ShopifyDbContext -o Persistence/Migrations
  ```
  Revísala antes del commit: sólo crea la tabla y el índice; el snapshot sólo suma esa entidad.
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~ShopifyCustomLineMappingTests"` → verde. Integración: `dotnet test $SI --filter "FullyQualifiedName~CustomLineMapping|FullyQualifiedName~DuplicateTitleOnTheSameConnection|FullyQualifiedName~FindVariantIdsByTitlesOnlyReadsItsConnection" -- xUnit.MaxParallelThreads=4` → verde.
- [ ] Commit: `feat(shopify): add custom line mappings table and repository`
- [ ] **Fin del bloque A:** `dotnet test $SU`; `dotnet test $SI --filter "FullyQualifiedName~ShopifyPersistenceTests|FullyQualifiedName~ShopifyImportFlowTests|FullyQualifiedName~ShopifyReconcileWorkerTests" -- xUnit.MaxParallelThreads=4` (Review Focus 1: el flujo existente sigue importando). Revisión del bloque.

---

## Bloque B — puertos e importador (Tareas 3–4, un implementador)

Depende del bloque A (`ShopifyLine.IsCustom`, el repositorio).

### Tarea 3: la variante por id y su nombre, por puertos

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyPorts.cs:186-196` (método nuevo; record y puerto nuevos debajo)
- Modify: `src/Bootstrapper/ShopifyProductLookup.cs`
- Create: `src/Bootstrapper/ShopifyVariantLabels.cs`
- Create: `src/Bootstrapper/ShopifyCustomLineMappingsRegistration.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` — **sólo** agregar `services.AddShopifyCustomLineMappings();` después de la última línea del bloque de Shopify (D12)
- Test: `$BU/ShopifyAdaptersTests.cs` (sección «Productos»), `$SU/ShopifyImporterTestDoubles.cs` (`FakeProductLookup`)

**Interfaces — Produces:**
```csharp
// IShopifyProductLookup
/// Por id de variante; uno que no existe en el tenant no aparece. Inactivas incluidas, marcadas con IsActive = false
/// (CatalogVariantReads.IsSellable). La tasa, con la misma regla que FindByCodesAsync.
Task<IReadOnlyDictionary<Guid, ShopifyProductRef>> FindByVariantIdsAsync(
    Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

/// D3/D4: lo que pinta la lista de asociaciones. DisplayName = CatalogVariantReads.DisplayNameOf.
public sealed record ShopifyVariantLabel(Guid VariantId, string DisplayName, string Code, bool IsActive);

public interface IShopifyVariantLabels
{
    /// Una variante inexistente o de otro tenant no aparece (el handler la pinta como borrada, D5).
    Task<IReadOnlyDictionary<Guid, ShopifyVariantLabel>> DescribeAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

// Bootstrapper
internal sealed class ShopifyVariantLabels(IProductRepository products, IOptionDefinitionRepository options) : IShopifyVariantLabels;
public static class ShopifyCustomLineMappingsRegistration
{
    public static IServiceCollection AddShopifyCustomLineMappings(this IServiceCollection services); // aquí: IShopifyVariantLabels (scoped); la Tarea 5 suma handlers y validador
}
```
`FindByVariantIdsAsync` usa `products.FindVariantsByIdsAsync` + `CatalogVariantReads.Select` (una consulta), y la tasa con el mismo caché por `TaxRateId` que `FindByCodesAsync` (extrae un helper privado; no cambies el ctor de `ShopifyProductLookup`). `FakeProductLookup` gana `ByVariant` (`Dictionary<Guid, ShopifyProductRef>`), `VariantRequests` y `WithVariant(Guid variantId, int taxPercentage = 19, bool active = true)`.

- [ ] **RED.** En `ShopifyAdaptersTests`:
  ```csharp
  [Fact] public async Task ProductLookupFindsByVariantIdWithItsTaxAndActivity()
  // VariantWorld con largeIsActive: false, tasa 19: FindByVariantIdsAsync(TenantId, [Medium.Id, Large.Id, Guid.CreateVersion7()])
  // [Medium] == new ShopifyProductRef(Medium.Id.Value, Product.Id.Value, "CAM-M", true, 19); [Large].IsActive == false; Count == 2
  [Fact] public async Task ProductLookupByVariantIdIgnoresAnotherTenant()   // VariantWorld de otro tenant → vacío
  [Fact] public async Task VariantLabelsCarryDisplayNameCodeAndActivity()
  // new ShopifyVariantLabels(new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(<opciones de VariantWorld>))
  // [Medium] == new ShopifyVariantLabel(Medium.Id.Value, <DisplayNameOf esperado, p. ej. "Camiseta M">, "CAM-M", true); id inexistente no aparece
  ```
  Revisa en `CatalogVariantTestDoubles.cs`/`VariantWorld` cómo se arman las opciones y escribe el nombre esperado literal.
- [ ] Corre `dotnet build $BU` → `CS1061 'ShopifyProductLookup' does not contain a definition for 'FindByVariantIdsAsync'` y `CS0246 'ShopifyVariantLabels'`.
- [ ] **GREEN.** Implementa, incluido `FakeProductLookup` (si no, `$SU` no compila).
- [ ] Corre `dotnet test $BU --filter "FullyQualifiedName~ShopifyAdaptersTests|FullyQualifiedName~CompositionRootTests"` → verde; `dotnet build $SU` → sin errores.
- [ ] Commit: `feat(shopify): look up catalog variants by id for custom lines`

### Tarea 4: el importador casa las líneas personalizadas

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyOrderImporter.cs` (ctor; `:141` el `Wait` de `product_unmatched`; `ResolveLinesAsync` `:236-280` y su comentario)
- Test: `$SU/ShopifyImporterTestDoubles.cs` (`FakeCustomLineMappings`, `ImporterWorld`), `$SU/ShopifyPreconditionsTests.cs:108,151,164`, nuevo `$SU/ShopifyCustomLineImportTests.cs`

**Interfaces — Consumes:** `IShopifyCustomLineMappingRepository.FindVariantIdsByTitlesAsync` (T2), `IShopifyProductLookup.FindByVariantIdsAsync` (T3), `ShopifyCustomLineMapping.NormalizeTitle` (T2).

**Interfaces — Produces:**
```csharp
public sealed class ShopifyOrderImporter(
    IShopifyMemberDirectory members, IShopifyBillingAccounts billingAccounts, ShopifyCustomerResolver customers,
    IShopifyProductLookup products, IShopifyCustomLineMappingRepository customLines, IShopifyShippingLookup shipping, …);

private async Task<(IReadOnlyList<ShopifyOrderLineDraft> Lines, IReadOnlyList<string?> UnmatchedSkus, IReadOnlyList<string> UnmatchedTitles)>
    ResolveLinesAsync(Guid tenantId, Guid connectionId, ShopifyOrderSnapshot snapshot, string currency, CancellationToken cancellationToken);
// el Wait: new { skus = unmatchedSkus, customTitles = unmatchedTitles }, cuando cualquiera de los dos tiene elementos
```
Algoritmo (lo que el implementador no puede deducir):
1. Personalizada = `line.IsCustom && NormalizeTitle(line.Title) is { } title`. Una `IsCustom` sin título va por el camino del SKU (su SKU es `null` → `skus: [null]`).
2. Títulos personalizados distintos (ordinal) → **una** llamada a `FindVariantIdsByTitlesAsync(tenantId, connectionId, …)` sólo si hay alguno; variantes encontradas → **una** llamada a `FindByVariantIdsAsync` sólo si hay alguna.
3. Personalizada con asociación, variante presente e `IsActive` → `ShopifyOrderLineDraft` con el **mismo** cálculo que una línea por SKU (`ShopifyPricing.UnitPriceWithVat`, `DiscountPercentage`, IVA de la variante). Si no → su título a `customTitles` (sin repetir).
4. Las no personalizadas, por SKU exactamente como hoy (nunca miran asociaciones).
5. `FindByCodesAsync` se sigue llamando sólo con SKU de líneas no personalizadas.

`FakeCustomLineMappings : IShopifyCustomLineMappingRepository` guarda filas por `(connectionId, title)`, anota `Requests` (`(Guid ConnectionId, string[] Titles)`) y ofrece `Map(Guid connectionId, string title, Guid variantId)`; `ImporterWorld` lo expone como `CustomLines` y lo pasa al importador. Los métodos de escritura del doble lanzan `NotSupportedException`.

- [ ] **RED.** Actualiza las tres aserciones existentes de `ShopifyPreconditionsTests` a `{"skus":[…],"customTitles":[]}`. En `ShopifyCustomLineImportTests` (constante `Garantia = "Garantía por 1 año + Envío Prioritario🚛"`, línea `new ShopifyLine(null, 1m, 5_000m, 0m, Garantia, true)`):
  ```csharp
  [Fact] public async Task ACustomLineWithAMappingImportsWithTheMappedVariant()
  // world.Products.WithVariant(service, taxPercentage: 19); world.CustomLines.Map(ConnectionId, Garantia, service)
  // pedido CAM-001 + Garantia → Imported; Writer.Creates[0].Draft.Lines tiene 2; la de Garantia: VariantId == service, Quantity 1,
  //   UnitPrice == ShopifyPricing.UnitPriceWithVat(5_000m, true, 19, "COP"), TaxPercentage 19
  // Assert.Equal((ConnectionId, new[] { Garantia }), Assert.Single(world.CustomLines.Requests)); Products.Requests == [["CAM-001"]]
  [Fact] public async Task ACustomLineWithoutMappingWaitsWithItsTitle()
  // AssertWaiting(... ProductUnmatched, """{"skus":[],"customTitles":["Garantía por 1 año + Envío Prioritario🚛"]}"""); Writer.Creates vacío
  [Fact] public async Task ACustomLineMappedToAnInactiveVariantWaits()      // WithVariant(service, active: false) → mismo wait con customTitles
  [Fact] public async Task ACustomLineMappedToADeletedVariantWaits()        // Map sin WithVariant → mismo wait
  [Fact] public async Task ARealProductLineWithoutSkuNeverMatchesByTitle()  // Review Focus 3
  // Map(ConnectionId, "Camiseta", service); línea new ShopifyLine(null, 1m, 50_000m, 0m, "Camiseta", false)
  // → """{"skus":[null],"customTitles":[]}"""; CustomLines.Requests vacío
  [Fact] public async Task AMappingOfAnotherStoreDoesNotApply()             // Review Focus 5: Map(otraConexión, Garantia, service) → espera con customTitles
  [Fact] public async Task MissingSkuAndMissingTitleAreBothListed()
  // líneas "X-9", Garantia, Garantia (repetida), " Otra " personalizada → """{"skus":["X-9"],"customTitles":["Garantía por 1 año + Envío Prioritario🚛","Otra"]}"""
  [Fact] public async Task ACustomLineWithoutTitleIsListedAsANullSku()      // new ShopifyLine(null, 1m, 5_000m, 0m, null, true) → """{"skus":[null],"customTitles":[]}"""
  [Fact] public async Task AnOrderWithoutCustomLinesDoesNotReadMappings()   // pedido base → Imported; CustomLines.Requests vacío
  ```
- [ ] Corre `dotnet build $SU` → `CS0103 The name 'CustomLines'`/`CS1061` en `ImporterWorld`.
- [ ] **GREEN.** Implementa. Actualiza el `<summary>` de `ResolveLinesAsync` (la forma nueva de `wait_detail`, el anti-objetivo y D1). El contenedor real ya resuelve `IShopifyCustomLineMappingRepository` (T2): no hay registro nuevo.
- [ ] Corre `dotnet test $SU --filter "FullyQualifiedName~ShopifyCustomLineImportTests|FullyQualifiedName~ShopifyPreconditionsTests|FullyQualifiedName~ShopifyImportResultTests"` → verde.
- [ ] Commit: `feat(shopify): import custom lines through store mappings`
- [ ] **Fin del bloque B:** `dotnet test $SU`; `dotnet test $BU --filter "FullyQualifiedName~Shopify|FullyQualifiedName~CompositionRootTests"`; `dotnet test $AT --filter "FullyQualifiedName~ShopifyLayerTests"`; `dotnet test $SI --filter "FullyQualifiedName~ShopifyImportFlowTests|FullyQualifiedName~ShopifyInboxApiTests" -- xUnit.MaxParallelThreads=4`. Revisión del bloque.

---

## Bloque C — API, flujo completo y cierre (Tareas 5–7, un implementador)

Depende de los bloques A y B.

### Tarea 5: `GET`/`POST`/`DELETE` de asociaciones

**Files:**
- Create: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyCustomLineMappings.cs` (DTOs, request, comandos, consulta, validador, handlers)
- Modify: `src/Modules/Shopify/Modules.Shopify.Application/ShopifyAuditActions.cs` (al final, D8/D13)
- Create: `src/Modules/Shopify/Modules.Shopify.Api/ShopifyCustomLineMappingEndpoints.cs`
- Modify: `src/Api/Program.cs` — una línea `app.MapShopifyCustomLineMappingEndpoints();` después de `app.MapShopifyEndpoints();` (D12)
- Modify: `src/Bootstrapper/ShopifyCustomLineMappingsRegistration.cs` (handlers y validador)
- Test: nuevo `$SI/ShopifyCustomLineMappingsApiTests.cs`

**Interfaces — Produces:**
```csharp
/// D5: ProductName y VariantCode null e IsActive false cuando la variante ya no existe en Catalog (sin FK). BFF: nombre y
/// código viajan para que la pantalla no consulte Catalog por fila.
public sealed record ShopifyCustomLineMappingDto(Guid Id, string Title, Guid VariantId, string? ProductName, string? VariantCode, bool IsActive);
public sealed record CreateShopifyCustomLineMappingRequest(string? Title, Guid? VariantId);

public sealed record ListShopifyCustomLineMappingsQuery(Guid TenantId, Guid ConnectionId) : IQuery<IReadOnlyList<ShopifyCustomLineMappingDto>>;
public sealed record CreateShopifyCustomLineMappingCommand(Guid TenantId, Guid ConnectionId, string? Title, Guid VariantId) : ICommand<ShopifyCustomLineMappingDto>;
public sealed record DeleteShopifyCustomLineMappingCommand(Guid TenantId, Guid ConnectionId, Guid MappingId) : ICommand<bool>;

public sealed class CreateShopifyCustomLineMappingValidator : AbstractValidator<CreateShopifyCustomLineMappingCommand>;
// "title": NormalizeTitle no nulo y Length <= 255; "variantId": NotEmpty. OverridePropertyName en camelCase.

public sealed class ListShopifyCustomLineMappingsHandler(IExecutionContext, ITenantModules, IShopifyConnectionDirectory,
    IShopifyCustomLineMappingRepository, IShopifyVariantLabels);
public sealed class CreateShopifyCustomLineMappingHandler(IExecutionContext, ITenantModules, IShopifyConnectionDirectory,
    IShopifyCustomLineMappingRepository, IShopifyProductLookup, IShopifyVariantLabels, IMembershipDirectory,
    IShopifyUnitOfWork, IShopifyAuditRecorder, IClock, IValidator<CreateShopifyCustomLineMappingCommand>);
public sealed class DeleteShopifyCustomLineMappingHandler(IExecutionContext, ITenantModules, IShopifyConnectionDirectory,
    IShopifyCustomLineMappingRepository, IShopifyUnitOfWork, IShopifyAuditRecorder, IClock);

// ShopifyAuditActions
public const string CustomLineMappingCreated = "shopify.custom_line_mapping.created";
public const string CustomLineMappingDeleted = "shopify.custom_line_mapping.deleted";
public const string CustomLineMappingResourceType = "shopify_custom_line_mapping";

public static IEndpointRouteBuilder MapShopifyCustomLineMappingEndpoints(this IEndpointRouteBuilder endpoints);
// grupo "/api/v1/tenants/{tenantId:guid}/shopify/stores/{connectionId:guid}/custom-line-mappings", WithTags("Shopify")
// GET  → RequireAuthorization(ShopifyPermissions.OrderRead),   200
// POST → RequireAuthorization(ShopifyPermissions.OrderManage), 201 Created con la fila (Location: …/custom-line-mappings/{id})
// DELETE "/{id:guid}" → RequireAuthorization(ShopifyPermissions.OrderManage), 204
// request.VariantId ?? Guid.Empty al armar el comando (el validador da el 422 con el campo)
```
Orden de chequeos del `POST`: D10. La respuesta del `POST` usa `IShopifyVariantLabels` para llenar nombre y código. Auditoría: D8, en la misma unidad de trabajo, antes de `SaveChangesAsync` (si el 409 gana, no queda auditoría). `DELETE`: D7.

- [ ] **RED.** En `ShopifyCustomLineMappingsApiTests` (usa `ShopifyWorld.ArrangeAsync`, `CreateClient` con `OrdersPermissions.*` reales; `world.Gorra` como variante de servicio — la variante por defecto lleva el id del producto):
  ```csharp
  [Fact] public async Task PostCreatesListsAndDeletes()
  // POST {title:"  Garantía por 1 año + Envío Prioritario🚛 ", variantId: world.Gorra} → 201;
  //   body: title == "Garantía por 1 año + Envío Prioritario🚛", variantId, productName == "Gorra", variantCode == "GOR-002", isActive == true
  // GET → un elemento igual; DELETE /{id} → 204; GET → []
  // audit: SELECT action FROM shopify.audit_entries (o la tabla real que mapea AuditEntries) → created y deleted
  [Fact] public async Task GetIsOrderedByTitleOrdinal()                    // "b", "B", "a" → ["B","a","b"]
  [Fact] public async Task GetShowsADeletedVariantAsNullWithInactive()     // D5 (pedido del coordinador)
  // INSERT directo en shopify.custom_line_mappings con variant_id = Guid.CreateVersion7() (no existe en Catalog)
  // GET → la fila con productName null, variantCode null, isActive false (JsonValueKind.Null en el JSON, no ausente)
  [Fact] public async Task PostTheSameTitleTwiceIs409()                    // Review Focus 4: segundo POST con espacios alrededor → 409, code "shopify.custom_line.already_mapped"
  [Fact] public async Task PostWithAnUnknownVariantIs422()                 // variantId nuevo → 422 "shopify.custom_line.variant_not_found"
  [Fact] public async Task PostWithAnInactiveVariantIs422()                // desactiva el producto por su API → 422 "shopify.custom_line.variant_inactive"
  [Theory] public async Task PostWithoutTitleOrVariantIs422WithTheField(string body, string field)
  // {"title":"   ","variantId":"<gorra>"} → "title"; {"title":"x"} → "variantId"; title de 256 → "title"; code "validation.failed" y errors[field]
  [Fact] public async Task AnotherTenantGets403OnEveryVerb()               // cliente del tenant B sobre la ruta del tenant A: GET, POST, DELETE → 403 authorization.denied
  [Fact] public async Task AConnectionOfAnotherTenantIs403()               // tenant B con su ruta y el connectionId de A → 403
  [Fact] public async Task WithoutOrderManagePostAndDeleteAre403()         // sólo OrderRead: GET 200, POST 403, DELETE 403
  [Fact] public async Task DeletingAnUnknownIdIs204AndAuditsNothing()      // D7
  ```
  Revisa en `ShopifyStoresApiTests` cómo se arma el segundo tenant y cómo se lee la auditoría del módulo, y reúsalo.
- [ ] Corre `dotnet build $SI` → `CS0246 'ShopifyCustomLineMappingDto'` (o el primer tipo que la prueba nombre).
- [ ] **GREEN.** Implementa y registra (handlers, `IValidator` por `AddValidatorsFromAssemblyContaining` ya cubre el ensamblado: verifica que el validador nuevo quede registrado una sola vez).
- [ ] Corre `dotnet test $SI --filter "FullyQualifiedName~ShopifyCustomLineMappingsApiTests.PostCreatesListsAndDeletes" -- xUnit.MaxParallelThreads=4`, después cada prueba por nombre mientras iteras; `dotnet test $BU --filter "FullyQualifiedName~CompositionRootTests"` → verde.
- [ ] Commit: `feat(shopify): add custom line mapping endpoints`

### Tarea 6: flujo completo webhook → espera → asociar → reintentar

**Files:**
- Test: `$SI/ShopifyImportFlowTests.cs` (dos pruebas nuevas; reusa `Flow`, `Fixture`, `Renumber`)

Sin código de producción: si algo falla acá, el arreglo vuelve a la tarea dueña (T1–T5) con su propio commit.

- [ ] **RED (de comportamiento).** Helper privado en la clase: `static void AddCustomLine(JsonObject order, string title)` que agrega a `line_items` `{"id":9001,"title":title,"product_id":null,"variant_id":null,"product_exists":false,"sku":null,"quantity":1,"price":"5000.00","price_set":{"shop_money":{"amount":"5000.00","currency_code":"COP"}},"total_discount":"0.00","discount_allocations":[]}` y sube `total_price` en 5.000 (y su `total_price_set`) para no disparar `total_difference`.
  ```csharp
  [Fact] public async Task CustomLineWaitsThenMappingAndRetryImportsWithTheMappedVariant()
  // DeliverAsync("orders/create","wh-1", Fixture("taxes-included", o => AddCustomLine(o, Garantia)))
  // MirrorAsync(5550000000001, "import_state") == "Waiting"; "wait_reason" == "product_unmatched";
  // "wait_detail::text" == {"skus": [], "customTitles": ["Garantía por 1 año + Envío Prioritario🚛"]} (compara parseando el JSON, jsonb reordena espacios)
  // POST custom-line-mappings {title: Garantia, variantId: World.Gorra} → 201; POST /shopify/orders/{rowId}/retry → 200
  // import_state == "Imported"; SELECT variant_id FROM quotations.order_items WHERE order_id = <qep_order_id> contiene World.Gorra y World.Camiseta
  [Fact] public async Task ASecondOrderWithTheMappedTitleImportsWithoutWaiting()
  // tras la asociación, DeliverAsync("orders/create","wh-2", Fixture(..., o => { Renumber(o, 5550000000501, "#1501"); AddCustomLine(o, Garantia); }))
  // import_state == "Imported" al primer intento (attempts == 1)
  ```
- [ ] Corre `dotnet test $SI --filter "FullyQualifiedName~CustomLineWaitsThenMappingAndRetry|FullyQualifiedName~ASecondOrderWithTheMappedTitle" -- xUnit.MaxParallelThreads=4` → verde. (Si sale rojo, es un defecto de T1–T5: arréglalo allá, con RED en su prueba unitaria.)
- [ ] Commit: `test(shopify): cover the custom line flow from webhook to retry`

### Tarea 7: cierre — documentación, merge de `develop` y suite

**Files:**
- Modify: `CLAUDE.md` (gotcha nuevo, cerca de las demás entradas de Shopify)
- Modify: `README.md`, § Integración con Shopify (`:4340`): un párrafo
- Modify (después del merge): `src/Modules/Shopify/Modules.Shopify.Application/ShopifyDtos.cs:59-60` (D14)

- [ ] Gotcha en `CLAUDE.md`, con este contenido (tuteo): **`shopify.custom_line_mappings` es contrato con `lulo-crm-migrator`, que la siembra por SQL** (`INSERT … ON CONFLICT (connection_id, title) DO NOTHING`, spec 2026-11-13). Renombrar una columna (`id`, `tenant_id`, `connection_id`, `title`, `variant_id`, `created_at`, `created_by`) o el índice `IX_custom_line_mappings_connection_title` compila, pasa las pruebas de este repo y rompe la siembra de la migración de Lulo v1; se cambia junto con el spec del migrador. `title` va recortado y `created_by` es id de **membresía** (D6). Y `IsCustom` exige las llaves de ids presentes en `null`: un fixture sin `product_id`/`variant_id` no es una línea personalizada.
- [ ] Párrafo en el README: qué es una línea personalizada, cómo se asocia desde la bandeja (`POST` + `retry`), la forma de `wait_detail` y que los pedidos en espera de antes del despliegue no traen el nombre hasta que Shopify los vuelva a mandar.
- [ ] Commit: `docs(shopify): document custom line mappings`
- [ ] **Merge de `develop` en la rama** (antes de mergear a `develop`): `git -C $W fetch`; `git -C $W merge develop`. Si choca en `QepServiceCollectionExtensions.cs` o `Program.cs`, conserva las líneas de los dos lados, sin reordenar. **No regeneres** el snapshot de EF salvo conflicto en `ShopifyDbContextModelSnapshot.cs`; si para entonces `develop` trae una migración de Shopify posterior a la nuestra (por ejemplo de `feature/shopify-document-sources`), borra `AddCustomLineMappings` y regenérala encima con el mismo comando de la Tarea 2, y corre otra vez las pruebas de la Tarea 2.
- [ ] Con `develop` ya adentro, corrige el comentario de `ShopifyDtos.cs:59-60` a `{"skus":[…],"customTitles":[…]}` (D14). Commit: `docs(shopify): describe the custom titles in wait detail`.
- [ ] **Suite antes de mergear a `develop`**, uno a la vez, en primer plano, revisando antes `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`:
  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test $SU
  dotnet test $SI -- xUnit.MaxParallelThreads=4
  dotnet test $BU
  dotnet test $AT
  ```
  No corras la solución entera: el cambio no es transversal (una línea en `Program.cs` y otra en el registro).
- [ ] Revisión fuerte de toda la rama (modelo más capaz). Después, el merge a `develop` lo decide quien coordina; `docs/estado.md` se actualiza en `develop`, no acá.

---

## Autorrevisión

- **Cobertura del spec.** «Lectura de la línea» (REST, GraphQL, consulta, sin `product_exists`, snapshot viejo) → T1. «Asociación» (tabla, columnas, índice, 409 por nombre, `Trim` ordinal, atada a la conexión) → T2. «Importador» (asociación por conexión, variante por id, inactiva/inexistente no casa, SKU como hoy, mismo precio e IVA, `wait_detail` con las dos llaves) → T3, T4. «Endpoints» (ruta, permisos, 403, `GET` BFF ordenado, `POST` con validador y 422/409, `DELETE` 204, auditoría) → T5. Objetivo 3 y «un segundo pedido entra sin espera» → T6. «Pedidos en espera de antes» → README (T7), sin código. «Migración Lulo CRM v1» → Global Constraints y gotcha (T7). `ShopifyLayerTests` → fin de bloque B y suite.
- **Tipos coherentes.** `ShopifyLine(…, Title, IsCustom)`, `ShopifyCustomLineMapping.NormalizeTitle`, `IShopifyCustomLineMappingRepository.FindVariantIdsByTitlesAsync`, `IShopifyProductLookup.FindByVariantIdsAsync`, `IShopifyVariantLabels.DescribeAsync`/`ShopifyVariantLabel`, `ShopifyCustomLineMappingDto(…, string? ProductName, string? VariantCode, bool IsActive)` y los tres códigos se usan igual en todas las tareas.
- **Review Focus.** Los cinco tienen prueba nombrada en su tarea dueña.
- **Proporción.** Siete tareas en tres bloques; sólo el algoritmo de `ResolveLinesAsync` y el orden de chequeos del `POST` van descritos, el resto son firmas y aserciones.
