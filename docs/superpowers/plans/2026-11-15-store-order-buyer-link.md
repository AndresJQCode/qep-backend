# Enlace de pedidos de la tienda con la cuenta del comprador — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** enlazar un pedido de la tienda a la cuenta verificada de su correo de facturación (en el checkout y al iniciar sesión), y
darle al asesor ver, quitar y cambiar esa cuenta.

**Architecture:** la marca `buyer_account_set_by_member` en el agregado `Order` decide si un camino automático puede tocar la
cuenta. Store y Buyers llegan a Quotations por puertos con adaptador en Bootstrapper (Store y Buyers no ven Quotations; Quotations
no ve Buyers). La consola agrega dos escrituras al pedido y la cuenta al detalle.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-15-store-order-buyer-link-design.md`

## Global Constraints

- Worktree `../qep-backend-worktrees/store-order-buyer-link`, rama `feature/store-order-buyer-link`. Rutas absolutas del worktree.
- **No se corren pruebas** (owner, 2026-11-15): se escriben; RED = `dotnet build` del proyecto de pruebas falla por compilación;
  GREEN = `dotnet build` con 0 errores. El owner corre las pruebas al final.
- Commits conventional, sin atribución de IA (sin `Co-Authored-By`).
- Sólo cuentas **verificadas**; sólo pedidos `source = 'Store'`.
- El correo se compara normalizado: `BuyerEmail.TryNormalize` en el lado de la cuenta; `lower(trim(email))` en la parte de
  facturación del pedido. `NULL` o vacío nunca coincide.
- El enlace automático nunca hace fallar el checkout ni el inicio de sesión; los logs llevan ids, nunca correos.
- Migración aditiva: `buyer_account_set_by_member boolean NOT NULL DEFAULT false`.
- Códigos nuevos: `order.buyer_account.not_found` (422). Acción de auditoría nueva: `quotation.order.buyer_account_changed`.
- Español colombiano tuteando en comentarios nuevos.

## Review Focus

1. Correo con mayúsculas o espacios en la parte de facturación (`" Ana@X.com "`) → enlaza igual. Test en Task 3.
2. Pedido de otro tenant con el mismo correo → el inicio de sesión en la tienda X no lo toca. Test en Task 3.
3. Asesor quita la cuenta y el comprador vuelve a iniciar sesión → sigue sin cuenta. Test en Task 3 (repositorio) y Task 1 (dominio).
4. Checkout con sesión de la cuenta A y correo de facturación de la cuenta B → queda A. Test en Task 2.
5. Cuenta borrada después del enlace → el detalle responde `buyerAccount: null`, no 500. Test en Task 4.

---

## Bloque A (un implementador): Tasks 1–3

### Task 1: la marca en el agregado y la migración

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (propiedad junto a `BuyerAccountId` ~L139; `LinkBuyerAccount` ~L1324)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (junto a `buyer_account_id` ~L587)
- Create: migración `AddOrderBuyerAccountSetByMember` (comando del `CLAUDE.md`, `--context QuotationsDbContext`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderTests.cs`

**Interfaces:**
- Produces:
  - `public bool BuyerAccountSetByMember { get; private set; }`
  - `LinkBuyerAccount(Guid)`: devuelve `false` también si `BuyerAccountSetByMember`.
  - `public bool SetBuyerAccountByMember(Guid? buyerAccountId, DateTimeOffset occurredAt)`: `Source != Store` → `QuotationsDomainException(ExternalOrderErrors.SourceInvalid)`. Sin cambio (misma cuenta y marca ya `true`) → `false`. Si no: asigna, marca `true`, `Version++`, `UpdatedAt = occurredAt`, devuelve `true`.

- [ ] **Step 1: tests (RED)**

```csharp
[Fact] public void LinkBuyerAccountIsRefusedAfterAMemberDecision()          // SetBuyerAccountByMember(null) → LinkBuyerAccount(x) == false, BuyerAccountId null
[Fact] public void SetBuyerAccountByMemberReplacesMarksAndBumpsVersion()     // true; BuyerAccountId == b; BuyerAccountSetByMember; Version + 1
[Fact] public void RemovingFromAnOrderWithoutAccountStillMarks()             // SetBuyerAccountByMember(null) en pedido sin cuenta → true, marca true
[Fact] public void SetBuyerAccountByMemberWithTheSameAccountTwiceChangesNothing() // segunda llamada → false; Version igual
[Fact] public void SetBuyerAccountByMemberOnANonStoreOrderIsSourceInvalid()  // Code == "order.order.source_invalid"
```

- [ ] **Step 2:** `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests` → `CS1061` (`SetBuyerAccountByMember`).
- [ ] **Step 3:** implementa dominio, mapeo `HasColumnName("buyer_account_set_by_member")` y migración; revisa que el `Up` sea sólo el
  `AddColumn` con `defaultValue: false`.
- [ ] **Step 4:** `dotnet build` de la solución → 0 errores.
- [ ] **Step 5:** commit `feat(orders): let a member decision fix the order buyer account`.

### Task 2: enlace en el checkout

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Application/IStoreBuyerAccounts.cs`, `src/Bootstrapper/StoreBuyerAccounts.cs`
- Modify: `src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs` (`ResolveBuyerAccountAsync` ~L464 y su llamada ~L294)
- Test: `tests/Modules/Store/Modules.Store.IntegrationTests/PublicOrderBuyerAccountApiTests.cs`

**Interfaces:**
- Produces: `Task<Guid?> IStoreBuyerAccounts.FindVerifiedAccountIdAsync(string email, CancellationToken)`: normaliza con
  `BuyerEmail.TryNormalize`; `null` si no normaliza, no existe o no está verificada.

`ResolveBuyerAccountAsync` pasa a recibir también `billing.Email`: sesión válida → su cuenta (sin cambio); si no → la verificada del
correo. Una excepción de la búsqueda por correo se loguea (nuevo `LoggerMessage`, sin correo) y devuelve `null`.

- [ ] **Step 1: tests (RED; la RED de compilación es el doble del puerto en las pruebas unitarias de Store si lo implementa, si no,
  el build pasa y la RED queda descrita en el reporte)**

```csharp
GuestCheckoutWithTheEmailOfAVerifiedAccountLinksTheOrder()        // buyer_account_id == cuenta
GuestCheckoutWithTheEmailOfAnUnverifiedAccountLeavesItUnlinked()  // NULL
CheckoutWithASessionKeepsTheSessionAccountOverTheBillingEmail()   // sesión A, correo de B → A
CreateAccountWithAnAlreadyVerifiedEmailLinksTheOrder()            // caso PED-2026-0005: enlazado; outbox con el aviso «ya tienes cuenta»
GuestCheckoutMatchesTheEmailIgnoringCaseAndSpaces()               // " ANA@x.com " → enlazado
```

- [ ] **Step 2:** `dotnet build` → falla por `FindVerifiedAccountIdAsync` en dobles existentes de `IStoreBuyerAccounts` (búscalos con
  `rg "IStoreBuyerAccounts" tests`).
- [ ] **Step 3:** implementa puerto, adaptador y checkout.
- [ ] **Step 4:** `dotnet build` → 0 errores.
- [ ] **Step 5:** commit `feat(store): link a guest order to the verified account of its billing email`.

### Task 3: enlace al iniciar sesión

**Files:**
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/Ports.cs` (`IBuyerOrderLinker`), `LoginBuyer.cs`
- Modify: `src/Bootstrapper/BuyerOrderLinker.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IBuyerOrderGateway.cs`, `IOrderRepository` y
  `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs`
- Test: `tests/Modules/Buyers/Modules.Buyers.UnitTests/LoginBuyerHandlerTests.cs`,
  `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerOrderLinkerTests.cs`,
  nuevo `tests/Modules/Store/Modules.Store.IntegrationTests/BuyerLoginOrderLinkApiTests.cs` (propio host: escribe filas de pedidos
  de varios tenants)

**Interfaces:**
- Produces:
  - `Task<int> IBuyerOrderLinker.LinkByEmailAsync(Guid tenantId, Guid accountId, string normalizedEmail, CancellationToken)`
  - `Task<int> IBuyerOrderGateway.LinkByEmailAsync(Guid tenantId, Guid buyerAccountId, string normalizedEmail, CancellationToken)`
  - `Task<int> IOrderRepository.LinkStoreOrdersByBillingEmailAsync(Guid tenantId, Guid buyerAccountId, string normalizedEmail, CancellationToken)`:
    una sola sentencia `UPDATE quotations.orders o SET buyer_account_id = @account FROM quotations.order_parties p WHERE …`
    con `o.tenant_id`, `o.source = 'Store'`, `o.buyer_account_id IS NULL`, `NOT o.buyer_account_set_by_member`, `p.order_id = o.id`,
    rol facturación (el valor guardado del enum de rol: léelo del mapeo de `order_parties`) y `lower(trim(p.email)) = @email`. No
    toca `version`. Devuelve filas afectadas.

`LoginBuyerHandler`: después de `RetryOnceAsync`, llama al linker con `storefront.TenantId`, la cuenta y su correo; atrapa toda
excepción salvo cancelación, la loguea con el id de la cuenta (`LoggerMessage` nuevo; agrega `ILogger<LoginBuyerHandler>`) y responde
la sesión igual. El handler necesita el id y el correo de la cuenta: haz que `AttemptAsync` los devuelva junto a la sesión.

- [ ] **Step 1: tests (RED)**

```csharp
// LoginBuyerHandlerTests
LoginLinksTheStoreOrdersOfTheTenantByEmail()        // linker llamado una vez con (storefront.TenantId, account.Id, account.Email)
LoginSucceedsWhenTheLinkFails()                     // linker lanza → sesión devuelta
FailedLoginDoesNotLink()                            // 401 → linker no llamado
// BuyerOrderLinkerTests
LinkByEmailResolvesTheGatewayInItsOwnScope()
// BuyerLoginOrderLinkApiTests (integración)
LoginLinksOnlyUnlinkedStoreOrdersOfThatTenantWithTheBillingEmail() // enlaza el propio; no el de otro tenant, no uno Manual, no uno con otra cuenta
LoginMatchesTheBillingEmailIgnoringCaseAndSpaces()
LoginDoesNotRelinkAnOrderAMemberUnlinked()          // marca true → sigue NULL
LoginDoesNotBumpTheOrderVersion()
```

- [ ] **Step 2:** `dotnet build` de `Modules.Buyers.UnitTests` y `Bootstrapper.UnitTests` → falla por `LinkByEmailAsync`.
- [ ] **Step 3:** implementa puertos, repositorio, gateway, adaptador y handler; actualiza todo doble de `IBuyerOrderLinker`,
  `IBuyerOrderGateway` e `IOrderRepository` (`rg` en `tests`).
- [ ] **Step 4:** `dotnet build` de la solución → 0 errores.
- [ ] **Step 5:** commit `feat(buyers): link the store orders of the buyer email on login`.

---

## Bloque B (un implementador): Task 4

### Task 4: la cuenta en la consola

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationBuyerAccountLookup.cs`,
  `ChangeOrderBuyerAccount.cs` (comandos y handlers), `src/Bootstrapper/QuotationBuyerAccountLookup.cs`
- Modify: `OrderResponseComposer.cs` (dependencia nueva y campo), `OrdersDtos.cs`/el record `OrderDetailResponse` (último
  parámetro `BuyerAccountResponse? BuyerAccount`), `OrderEndpoints.cs`, registro DI de Quotations y Bootstrapper
  (`QepServiceCollectionExtensions`), constante de auditoría junto a las demás de pedidos (`OrderAutoArchive.AuditAction` muestra
  dónde viven), `CLAUDE.md` (gotcha nuevo, ver abajo)
- Test: nuevo `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderBuyerAccountApiTests.cs`, unitarias del composer si
  existen dobles (`rg "IOrderResponseComposer|OrderResponseComposer\(" tests`)

**Interfaces:**
- Produces:
  - `public sealed record QuotationBuyerAccountRef(Guid Id, string Email);`
  - `IQuotationBuyerAccountLookup.FindAsync(Guid accountId, CancellationToken) → Task<QuotationBuyerAccountRef?>` (`null` si
    borrada) y `FindVerifiedByEmailAsync(string email, CancellationToken) → Task<QuotationBuyerAccountRef?>` (normaliza con
    `BuyerEmail`; `null` si no existe o no está verificada).
  - `public sealed record BuyerAccountResponse(Guid Id, string Email);`
  - `SetOrderBuyerAccountCommand(Guid TenantId, Guid OrderId, long ExpectedVersion, string Email) : ICommand<OrderDetailDto>` y
    `RemoveOrderBuyerAccountCommand(Guid TenantId, Guid OrderId, long ExpectedVersion) : ICommand<OrderDetailDto>` (el tipo de la
    versión, el que usa `SaveOrderEdits`).
  - `PUT  /api/v1/tenants/{tenantId}/orders/{orderId}/buyer-account` cuerpo `{ "email": "…" }` (validador FluentValidation:
    `NotEmpty`, `EmailAddress`, máx. `BuyerEmail.MaxLength`) y `DELETE` misma ruta; `If-Match` como `SaveOrderEditsAsync`
    (`OrderEndpoints.cs` ~L409); `RequireAuthorization(OrdersPermissions.OrderManage)`; responden `OrderDetailResponse` vía composer.

Handlers: carga y revalidación de tenant y permiso con `OrderStateAction.LoadAsync(..., OrdersPermissions.OrderManage, ...)`;
versión distinta → `RequestConcurrencyException` (412, como `SaveOrderEdits`); `PUT` sin cuenta verificada →
`QuotationsDomainException("order.buyer_account.not_found", …)` (422); si `SetBuyerAccountByMember` devuelve `true`, audita
`quotation.order.buyer_account_changed` con `{ previousBuyerAccountId, buyerAccountId }` (sólo ids) y guarda.

Composer: si `order.BuyerAccountId` no es `null`, `FindAsync`; `null` → `BuyerAccount = null`. El listado no cambia.

`CLAUDE.md`, sección de gotchas: entrada «El enlace del pedido de la tienda con la cuenta del comprador lo deciden el correo de
facturación y la marca `buyer_account_set_by_member`»: el checkout y el inicio de sesión enlazan por correo verificado, sólo origen
`Store`; con la marca en `true` ningún camino automático (`LinkBuyerAccount`, `LinkStoreOrdersByBillingEmailAsync`) toca la cuenta;
un camino automático nuevo respeta la marca o deshace la decisión del asesor en el siguiente inicio de sesión.

- [ ] **Step 1: tests (RED)**

```csharp
DetailShowsTheBuyerAccountEmail()
DetailOfAnOrderWhoseAccountWasDeletedShowsNoAccount()        // Review Focus 5
PutLinksToAVerifiedAccountMarksAuditsAndBumpsVersion()
PutWithAnUnverifiedOrUnknownEmailIs422BuyerAccountNotFound()
PutWithTheSameAccountTwiceDoesNotBumpTheSecondTime()
DeleteUnlinksMarksAndAudits()
DeleteOnAnOrderWithoutAccountStillMarks()
PutOnAManualOrderIs422SourceInvalid()
WithoutManagePermissionIs403() / OtherTenantIs403()
StaleIfMatchIs412()
```

- [ ] **Step 2:** `dotnet build` de `Modules.Quotations.IntegrationTests` → falla por los tipos nuevos.
- [ ] **Step 3:** implementa.
- [ ] **Step 4:** `dotnet build` de la solución → 0 errores (incluye `ArchitectureTests`: Quotations sigue sin referenciar Buyers).
- [ ] **Step 5:** commit `feat(orders): let a member see, change and remove the order buyer account`.

---

## Cierre

- Revisión fuerte de toda la rama una sola vez al final.
- Antes de mergear a `develop`: el owner corre `Modules.Quotations.*`, `Modules.Store.*`, `Modules.Buyers.*`,
  `Bootstrapper.UnitTests` y `ArchitectureTests`, uno a la vez.
- `docs/estado.md` se actualiza en `develop` al mergear, no en la rama.
