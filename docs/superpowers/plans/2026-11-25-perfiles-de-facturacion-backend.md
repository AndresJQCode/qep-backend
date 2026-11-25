# Perfiles de facturación del cliente y entrega editable del pedido — Plan de implementación del backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de ejecución de este repo y del workspace mandan sobre el proceso del skill** (tres bloques, un implementador y una revisión por bloque; ver «Global Constraints»).

**Goal:** Un cliente guarda hasta 50 perfiles de facturación (con retenciones y excedente de IVA) que la cotización y el pedido leen en su bloque `client` y copian; «Editar pedido» cambia recoger/enviar y la dirección de envío mientras el pedido no esté facturado ni preparado, en todo origen, y en un pedido de Shopify de cualquier país la dirección cambiada se empuja a Shopify.

**Architecture:** Customers gana la entidad hija `CustomerBillingProfile` del agregado `Customer`, gemela de `CustomerAddress` (libreta CLI-DIR-01), con cuatro sub-rutas que responden el cliente entero; Bootstrapper la proyecta en `QuotationCustomerRef`. Quotations gana `Order.SetDelivery` y un bloque `delivery` opcional en `PUT /orders/{id}` y en su preview, con la bandera de capacidad `deliveryEditable`. Para Shopify: Quotations publica `quotations.order.shipping-party-changed.v1` al outbox en la misma transacción, Shopify lo consume y encola el envío `ShippingAddress`; el worker aprende a armar direcciones de fuera de Colombia (`SpainProvinceCodes`), y un puerto síncrono (`IExternalOrderDeliveryCheck`) rechaza en el guardado lo que el worker no podría mandar. De paso se cierran los cuatro huecos del código postal en Shopify.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, CQ propio, outbox/inbox de plataforma, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-24-perfiles-de-facturacion-backend-handoff.md` (contrato; §3 perfiles, §10 entrega, §11 Shopify) y `docs/superpowers/specs/2026-11-24-perfiles-de-facturacion-design.md` (diseño del frontend). Ambos son copias **verbatim** del frontend (commit `25b283f3`): no se editan; donde el código los contradice, manda la sección «Fallos de los documentos frente al código» de este plan. Spec previo ya mergeado: `2026-11-24-codigo-postal-envio-backend-handoff.md`.

## Global Constraints

- **Worktree y rama:** `feature/billing-profiles` (cortada de `develop` en `6e6d8320d`), en `..\qep-backend-worktrees\billing-profiles`, con su propio índice `.codegraph/`. Nada se edita en el checkout `qep-backend`, en otros worktrees ni en el frontend. `git branch --show-current` y `git status --short` **antes de editar y antes de cada commit**. Un subagente que corra scripts hace `Set-Location` al worktree primero. Edita con las herramientas de edición, no con `Set-Content` (rompe las tildes).
- **Commits:** convencionales, uno por tarea, con rutas explícitas (`git commit -m "<msg>" -- <rutas>`), **sin** `Co-Authored-By` ni atribución de IA.
- **TDD:** RED antes que GREEN, con la salida literal de ambos en el reporte de la tarea. Un RED que falla por compilación se prueba con `dotnet build` del proyecto de pruebas, **sin** contenedores. Una prueba verde al primer intento se reporta como caracterización.
- **Bloques:** tres bloques, un implementador (Sonnet) por bloque y **una** revisión por bloque. La revisión fuerte (modelo más capaz, toda la rama) corre una sola vez, al final.
- **Integración:** mientras iteras, filtrada por nombre de prueba (`--filter "FullyQualifiedName~NombreDeLaPrueba"`); cada clase tocada completa **una vez**, al final del bloque. Antes de cada corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una de otra sesión, espera y **nunca** la detengas. Nunca dos corridas de integración a la vez. Las pruebas corren en **primer plano**. Cada corrida va precedida, en línea aparte, de `$env:Seed__Enabled = "false"`, y lleva `-- xUnit.MaxParallelThreads=4`. Si `Api.exe` está corriendo el build falla por archivo bloqueado: avisa, no lo detengas.
- **Para mergear a `develop`:** `.\scripts\test-feature.ps1` en verde, leyendo sus avisos (ver «Verificación final»). `docs/estado.md` **no** se toca en la rama: se actualiza en `develop` al mergear. Sólo merge a `develop`, sin push.
- **Comandos:** Windows PowerShell 5.1 (`A; if ($?) { B }`, nada de `&&`).
- **Idioma:** comentarios, XML doc y textos para personas en español colombiano tuteando; identificadores, códigos, enums y mensajes de commit en inglés. Los mensajes de excepción (`detail`) siguen en inglés como en el resto del módulo. Los DTO nuevos llevan en su XML doc el porqué BFF.
- **i18n del backend (verificado en `develop`, `d58445cc1`/`b7653d259`):** el idioma `es-419`/`es-ES` sólo localiza correos, PDF y Excel de cotizaciones; el historial y el `detail` de los errores quedan en `es-419` (spec i18n 2026-11-22, «Pendiente»). Esta rama no agrega correo, PDF, Excel ni plantilla: **ningún texto nuevo pasa por `SupportedLanguages`**. El único texto nuevo para personas es la fila de historial de la entrega (Tarea 7): en `es-419`, sin términos del glosario de España (no dice «cotización» ni «asesor»). El frontend traduce por `code`.
- **No inventar:** todo campo, ruta y código sale del handoff o de este plan. Lo que falte es decisión pendiente del owner (sección final).
- **Decisiones del owner del 2026-11-25** (P-1…P-4, al final del plan) mandan sobre el handoff: el código postal nunca es obligatorio para Shopify (sin 422 por código postal), los perfiles cuentan en el candado de tasas, la fusión no deduplica perfiles, y los códigos de provincia de España se confirman antes del merge.
- **Valores fijos:** `CustomerBillingProfile.MaxPerCustomer = 50`; topes del perfil: nombre 255, documento 32, teléfono 32, email 254, dirección 255, ciudad escrita 120 (`CustomerContactInfo.CityNameMaxLength`), país 2; código postal de la parte 20 (`QuotationPartyDetails.PostalCodeMaxLength`, ya existe).
- **Sin auditoría ni eventos** en las operaciones de perfil (owner; paridad con `ManageCustomerAddresses`, que no audita: la única acción `AddressAdded` la usa `CustomerProvisioning`, otro camino). Ninguna prueba de arquitectura exige auditoría por handler. Los cambios de entrega del pedido **sí** auditan (`quotation.order.delivery_changed`), como el resto de `SaveOrderEdits`.
- **Despliegue:** backend primero. Migraciones de esta rama: `AddCustomerBillingProfiles` (Customers) y `AddCityCorrectionPostalCode` (Shopify), las dos **aditivas** (sólo crean tablas o columnas nulables): sin corte.
- **Línea base heredada:** el código postal (`1dfaaf3c4`) se mergeó sin integración, sin `test-feature.ps1` y con 3 fallas en Bootstrapper/Architecture sin confirmar (`docs/estado.md`). La Tarea 0 las mide antes de tocar nada.

## Fallos de los documentos frente al código y regla que sigue el plan

Verificado contra `develop` `6e6d8320d`. Las líneas citadas por el handoff corrieron por el código postal y otros merges; aquí sólo lo que cambia una decisión.

- **R-1. `SaveOrderEdits` corta antes de llegar al agregado.** `SaveOrderEdits.cs:78` y `PreviewOrderEdits.cs:69` lanzan `order.order.not_editable` con `!order.IsEditable` antes de aplicar nada, así que «el handler aplica la entrega antes que el resto» (handoff §10.1) no alcanza para que salgan `already_invoiced`/`already_prepared`. **Regla:** con `Delivery` presente, los dos handlers llaman `order.EnsureDeliveryEditable()` **antes** de ese chequeo (facturado primero, luego preparado, luego `not_editable`).
- **R-2. `PreviewOrderEditsRequest` no hereda los parámetros nuevos solos.** Es un `record` posicional que pasa una lista explícita al constructor base (`OrdersDtos.cs:336-354`). **Regla:** `Delivery` va como propiedad `init` de `SaveOrderEditsRequest` (como `Gifts`, `OrdersDtos.cs:329`): la hereda el preview sin tocar su constructor.
- **R-3. La parte de envío no tiene validador de FluentValidation.** `QuotationPartiesRequestValidator` la ignora a propósito (`QuotationEdits.cs:107-108`), y `CreateOrder.cs:104-107` sólo valida la facturación. «Reutilizar el validador de crear pedido con la clave `Delivery.Shipping.*`» (handoff §10.2) no da ningún campo. **Regla:** las reglas de la parte las da el dominio (`QuotationPartyDetails`, con prefijo `Delivery.Shipping`: el código postal ya lleva mapa `errors`) y `PartyRegionRules.EnsureAsync(geography, party, "Delivery.Shipping", ct)` (región con mapa `errors`). No se agrega un validador nuevo.
- **R-4. El adaptador de Shopify no puede leer una parte de envío extranjera.** `ShopifyOrderWriter.Fingerprint` lanza `"A persisted shipping party always has a department."` (`ShopifyOrderWriter.cs:246-256`), y `ToShopify` lo usan `ReadStateAsync`, `ApplyEditAsync` y `ShopifyOrderCorrectionWriter`. `ExternalShippingParty` (`IExternalOrderWriter.cs:53-54`) y `ShopifyShippingFingerprint` (`ShopifyCityCorrection.cs:12-13`, `DepartmentId`/`CityId` no nulos) no tienen país, ciudad escrita ni región. Guardar una parte de España en un pedido de Shopify sin esto **rompe** la sincronización de ediciones y el worker de envíos de ese pedido. **Regla:** la Tarea 9 ensancha los dos tipos antes de que la Tarea 10 abra la puerta.
- **R-5. Shopify no consume hoy ningún evento `quotations.*`.** Su único consumidor de outbox es `CitySynonymEventsProcessor` (`shopify.city-synonym-events`). **Regla:** consumidor nuevo calcado de él (Tarea 11), con su worker en `MultiReplicaInventory` y su aserción en `CompositionRootTests`.
- **R-6. No existe `SpainProvinceCodes`** ni ninguna tabla INE → ISO. Geography siembra 52 regiones de `ES` (`Seed/Data/es-regions.json`, códigos `01`…`52`). **Regla:** la crea la Tarea 9; el spec S-4 (fuera de alcance) la reutilizará al revés.
- **R-7. La fusión de clientes no deduplica direcciones.** `CustomerMergeStore.MoveOwnedCoreAsync` mueve con `ExecuteUpdate` y pone `IsPrincipal = false`, sin comparar. El handoff §4 pide deduplicar perfiles al fusionar. Además, a diferencia de la libreta, el que queda puede no tener ningún perfil, y con `IsPrincipal = false` quedaría sin principal. **Regla (owner, P-3, 2026-11-25; reemplaza el dedupe del handoff §4):** paridad con direcciones (se mueven todos, sin dedupe, sin tope) **más** una regla: si el que queda no tenía perfiles, el movido más antiguo (por `created_at`, luego `id`) queda principal.
- **R-8. La regla de «cliente incompleto» sólo protege el alta.** En la libreta, `AddAddress`/`EnsureAddress` rechazan `customers.customer.incomplete`, pero `UpdateAddress`, `MakeAddressPrincipal` y `RemoveAddress` sólo exigen `EnsureNotMerged`/`EnsureActive` (`Customer.cs:800-851`). **Regla:** igual en perfiles. Un incompleto nunca tiene perfiles, así que es equivalente.
- **R-9. El candado de tasas del catálogo bloquea borrar y desactivar.** `TaxRateCustomerUsage` → `ICustomerRepository.AnyWithTaxRateAsync` alimenta `DeleteTaxRate` **y** `DeactivateTaxRate` (`catalog.tax_rate.in_use`). El handoff dice «a confirmar». **Regla (owner, P-2, 2026-11-25):** los perfiles de clientes sin fusionar cuentan en esa consulta. Efecto: una tasa usada sólo por un perfil ya no se puede desactivar, y la conducta del spec D-4 («una tasa del perfil desactivada no se precarga») queda como defensa para datos viejos.
- **R-10. El código de región del perfil no tiene nombre en el handoff** («una constante propia en `CustomerRegionRules`»). **Regla:** `CustomerRegionRules.BillingProfileRegionInvalid = "customers.billing_profile.region_invalid"`, calcado de `customers.address.region_invalid`. El frontend tiene que conocerlo.
- **R-11. `IQuotationCustomerLookup.FindManyAsync` sólo lo usa el Excel de pedidos** (`OrdersExportProcessor.cs:207`); el detalle de cotización y de pedido usa `FindAsync`. **Regla:** los perfiles se cargan sólo en `ICustomerRepository.FindAsync` (el que usan los handlers de libreta, el detalle del cliente y `QuotationCustomerLookup.FindAsync`), no en el listado (`CustomerRepository.cs:73`) ni en `FindManyAsync` (`:609`).
- **R-12. Nombre repetido.** `Modules.ServiceContracts.Domain.CustomerBillingProfile` existe. Tres archivos de Bootstrapper importan los dos namespaces (`ServiceContractsCustomerLookup.cs`, `ServiceContractsCustomerProvisioning.cs`, `ServiceContractsPhoneNormalizer.cs`); hoy no nombran la clase, pero un `CS0104` aparecerá si alguno la usa. **Regla:** se conserva el nombre del handoff; si aparece la ambigüedad, alias `using` en el archivo que la sufre, nunca renombrar la de ServiceContracts.

## Review Focus

1. **Reintentos y duplicados del `POST` de perfil:** el frontend lo manda después de grabar el documento, y un reintento con el número escrito con puntos, guiones o minúsculas no crea otra fila, no sube la versión y no cuenta contra el tope. Prueba en las Tareas 1 (unitaria) y 3 (integración).
2. **Pedido facturado o preparado con `delivery` en el cuerpo:** responde el código exacto (`already_invoiced` gana a `already_prepared`), también en el preview, y nunca `not_editable` (R-1). Prueba en la Tarea 7.
3. **Cuerpo de un frontend viejo** (sin `delivery`): la entrega queda igual, sin historial ni auditoría de entrega, y `client.billingProfiles` sigue presente vacío en un cliente sin perfiles. Pruebas en las Tareas 3, 5 y 7.
4. **Pedido de Shopify con una parte de España:** después de guardarla, leer el estado del pedido para la sincronización y para el worker no lanza (R-4), y una edición posterior de Shopify sobre la dirección sigue siendo conflicto. Sin código postal y en otra provincia, el guardado se acepta y el envío sale sin `zip` (nunca un 422 ni el `zip` de otra provincia, P-1). Prueba en las Tareas 9, 10 y 11.
5. **Pedido de Shopify con código postal tras una corrección de ciudad:** la recorrección por sinónimo no lo trata como `ShippingChanged` ni borra el código postal. Prueba en la Tarea 8.

---

## Tarea 0 (antes del Bloque 1, sin commit): línea base

- [ ] **Paso 1:** en el worktree, sin cambios todavía, corre (sin contenedores) las dos suites que el código postal dejó en duda y guarda la lista de fallas en el reporte del bloque. Un worktree recién creado no tiene `obj/project.assets.json`, así que `--no-restore` sin un restore previo falla con `NETSDK1004`: primero el restore (con los lock files del repo, sin `--force-evaluate`, que los reescribiría):

```powershell
dotnet restore -nologo
dotnet build --no-restore -nologo -v q
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build -nologo
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build -nologo
```

Esperado: o verde, o las 3 fallas conocidas con su nombre. Esas fallas son **línea base**: cada revisión de bloque compara contra esta lista; una falla nueva es de la rama.
- [ ] **Paso 2:** `.\scripts\test-feature.ps1 -DryRun` (el script compila con `--no-restore`: depende del restore del Paso 1) y anota los pasos y avisos que imprime con la rama vacía.

---

## Bloque 1 — Perfiles de facturación en Customers y en el bloque `client` (Tareas 1 a 5, un implementador)

### Task 1: Dominio del perfil

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Domain/CustomerBillingProfile.cs` (entidad, `CustomerBillingProfileId`, `CustomerBillingProfileDetails` con `Normalized()`), calcado de `CustomerAddress.cs`.
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (colección y cuatro métodos, junto a los de la libreta, `:728-865`).
- Test: create `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerBillingProfileTests.cs`.

**Interfaces:**
- Produces:
  - `CustomerBillingProfileDetails { string Name; string? PersonType; string IdentificationType; string IdentificationNumber; string? Phone; string? Email; string Address; string Country; Guid? CityId; string? CityName; Guid? RegionId; IReadOnlyCollection<Guid> WithholdingTaxRateIds; bool VatSurplus }` (`init`), `Normalized()` recorta, país vacío ⇒ `CO` y a mayúsculas, deduplica tasas, rechaza `Guid.Empty` en tasas con `customers.customer.withholding_tax_rate_invalid`.
  - `CustomerBillingProfile`: `Id`, `CustomerId`, los campos de arriba, `IReadOnlyCollection<Guid> WithholdingTaxRateIds` (owned `CustomerBillingProfileTaxRate(Guid TenantId, Guid TaxRateId)`), `IsPrincipal`, `CreatedAt`, `UpdatedAt`; `const int MaxPerCustomer = 50`; topes como constantes (`NameMaxLength = 255`, `IdentificationNumberMaxLength = 32`, `PhoneMaxLength = 32`, `EmailMaxLength = 254`, `AddressMaxLength = 255`).
  - En `Customer`: `IReadOnlyCollection<CustomerBillingProfile> BillingProfiles`; `(CustomerBillingProfile Profile, bool Added) EnsureBillingProfile(CustomerBillingProfileDetails details, bool isPrincipal, DateTimeOffset occurredAt)`; `CustomerBillingProfile UpdateBillingProfile(CustomerBillingProfileId id, CustomerBillingProfileDetails details, bool isPrincipal, DateTimeOffset occurredAt)`; `void RemoveBillingProfile(CustomerBillingProfileId id, DateTimeOffset occurredAt)`; `void MakeBillingProfilePrincipal(CustomerBillingProfileId id, DateTimeOffset occurredAt)`.
  - Códigos (`CustomersErrorCodes` o una clase `CustomerBillingProfileErrors` en Domain): `customers.billing_profile.{name_required,name_too_long,identification_required,identification_too_long,identification_type_invalid,document_type_not_allowed,person_type_invalid,person_type_requires_nit,phone_too_long,email_invalid,address_required,address_too_long,country_invalid,city_required,city_lane_invalid,region_lane_invalid,limit_reached,not_found,principal_not_removable}`.

Reglas (handoff §3.1-3.4): carriles de ciudad idénticos a `CustomerAddress.EnsureCityLane` (`CustomerAddress.cs:214-259`) con los códigos de perfil; tipo de documento con `DocumentTypesByCountry.IsKnown`/`IsAllowed` contra **su** país; en `CO`, `Juridica` exige `NIT` (misma regla que `Customer.cs:1477`). Dedupe del `Ensure`: mismo país, mismo tipo, número normalizado igual (sin no alfanuméricos, mayúsculas: la expresión de `CustomerIdentificationSql.NormalizedNumber` en C#), `SameText` de nombre y dirección (`Customer.cs:788-792`), misma ciudad (id en `CO`, `SameText` afuera); teléfono, email, región, tipo de persona, tasas y excedente **no** cuentan. Encontrado ⇒ se devuelve sin `Touch`. No encontrado con 50 guardados ⇒ `limit_reached`. El primero queda principal aunque llegue `false`; `isPrincipal: true` desmarca el resto; `Update` con `false` no desmarca. `Ensure` exige `EnsureNotMerged`, `EnsureActive` y completo (`customers.customer.incomplete`); los otros tres, sólo los dos primeros (R-8). Todo cambio llama `Touch`.

- [ ] **Paso 1: pruebas que fallan** (nombres y aserciones):
  - `AColombianProfileWithoutCityIdIsRejected` ⇒ `customers.billing_profile.city_required`; `AColombianProfileWithCityNameIsRejected` y `ASpanishProfileWithCityIdIsRejected` ⇒ `city_lane_invalid`; `AColombianProfileWithRegionIsRejected` ⇒ `region_lane_invalid`.
  - `NifInColombiaIsNotAllowed` ⇒ `document_type_not_allowed`; `JuridicaWithCcInColombiaRequiresNit` ⇒ `person_type_requires_nit`; `JuridicaWithNifInSpainIsAllowed`.
  - `TheFirstProfileIsPrincipalEvenWhenNotAsked`; `MarkingAnotherPrincipalUnmarksThePrevious`; `UpdatingWithFalseKeepsThePrincipal`; `ThePrincipalCannotBeRemoved` ⇒ `principal_not_removable`; `AMissingProfileIsNotFound` ⇒ `not_found`.
  - `EnsuringTheSameNumberWrittenDifferentlyReturnsTheExistingWithoutTouching` (`"900.123-456"` vs `"900123456"`, `"x"` vs `"X"`: `Added == false`, `Version` igual); `DifferentPhoneTaxesOrVatSurplusAreStillTheSameProfile`; `ADifferentAddressAddsAProfile`.
  - `TheFiftyFirstDistinctProfileHitsTheLimit` ⇒ `limit_reached`; `ADuplicateWithFiftySavedIsReturned`.
  - `IncompleteMergedOrInactiveCustomersCannotEnsureAProfile` (tres casos: `customers.customer.incomplete`, `customers.customer.merged`, `customers.customer.inactive`).
- [ ] **Paso 2: RED** — `dotnet build tests/Modules/Customers/Modules.Customers.UnitTests -nologo -v q`. Esperado: `error CS0246` / `CS1061` por `CustomerBillingProfileDetails` y `EnsureBillingProfile`.
- [ ] **Paso 3: implementa** lo del bloque Interfaces.
- [ ] **Paso 4: GREEN** — `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests -nologo --filter "FullyQualifiedName~CustomerBillingProfileTests"`; después la clase `CustomerAddressTests` y `CustomerEnsureAddressTests` completas (no deben moverse).
- [ ] **Paso 5: commit** — `feat(customers): add billing profiles to the customer aggregate`

### Task 2: Persistencia, migración y candado de tasas

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs` (nuevo `ConfigureCustomerBillingProfile`, junto a `ConfigureCustomerAddress`, `:354`), `CustomerRepository.cs` (`FindAsync` `:355` incluye `BillingProfiles`; `AnyWithTaxRateAsync` `:379` suma perfiles).
- Create: migración `AddCustomerBillingProfiles` con
  `dotnet ef migrations add AddCustomerBillingProfiles --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations` (lee antes `.claude/rules/build-y-despliegue.md`).
- Test: create `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerBillingProfilePersistenceTests.cs`; modify la prueba de Catalog que fija el candado con clientes (`tests/Modules/Catalog/Modules.Catalog.IntegrationTests/TaxRateCustomerFixtures.cs` y su clase de pruebas).

**Interfaces:**
- Produces: tabla `customers.customer_billing_profiles` (`id`, `customer_id` FK CASCADE, `name` 255, `person_type` 16 null, `identification_type` 16, `identification_number` 32, `phone` 32 null, `email` 254 null, `address` 255, `country` 2 default `'CO'`, `city_id` null, `city_name` 120 null, `region_id` null, `vat_surplus` bool default false, `is_principal`, `created_at`, `updated_at`); CHECK `CK_customer_billing_profiles_city_lane` con el texto de `CK_customer_addresses_city_lane`; índices `IX_customer_billing_profiles_customer`, `IX_customer_billing_profiles_city` y `IX_customer_billing_profiles_principal` (`customer_id, is_principal`, **no** único); FK manual a `geography.cities(id)` `ON DELETE RESTRICT` agregada a mano en la migración como en `AddCustomerAddresses`. Tabla `customers.customer_billing_profile_tax_rates` (`billing_profile_id` FK CASCADE, `tenant_id`, `tax_rate_id`; PK `(billing_profile_id, tax_rate_id)`; índice `IX_customer_billing_profile_tax_rates_tenant_tax_rate`), con `ValueGeneratedNever` en la clave como `customer_tax_rates` (`CustomersDbContext.cs:252-268`).
- `AnyWithTaxRateAsync`: `true` también si un perfil de un cliente sin fusionar tiene la tasa (R-9). El nombre del método no cambia.

- [ ] **Paso 1: pruebas que fallan:** `AProfileRoundTripsWithItsTaxRates` (guardar con `FindAsync` + unidad de trabajo, releer en otro contexto: campos, tasas, principal); `RemovingAProfileDeletesItsTaxRates`; `TheDatabaseRejectsAMixedCityLane` (SQL crudo con `country='CO'` y `city_name` ⇒ violación de `CK_customer_billing_profiles_city_lane`); en Catalog, `ARateUsedOnlyByABillingProfileCannotBeDeletedNorDeactivated` ⇒ `catalog.tax_rate.in_use` en los dos.
- [ ] **Paso 2: RED** — build del proyecto de integración: falla por compilación si la prueba usa `BillingProfiles`; si compila, corre filtrado por `CustomerBillingProfilePersistenceTests` y pega la falla.
- [ ] **Paso 3: implementa** mapeo, migración (revisa el `.cs` generado: sólo `CreateTable`, `AddForeignKey` manual de ciudad, índices; nada de `AlterColumn` sobre tablas existentes) y la consulta del candado.
- [ ] **Paso 4: GREEN** — las dos clases filtradas por nombre, en fila.
- [ ] **Paso 5: commit** — `feat(customers): persist billing profiles and count them as tax rate usage`

### Task 3: Endpoints, handlers, validador y lectura en el cliente

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/ManageCustomerBillingProfiles.cs` (cuatro comandos, `CustomerBillingProfileWriteRules`, dos validadores, cuatro handlers), calcado de `ManageCustomerAddresses.cs`.
- Modify: `CustomersDtos.cs` (`CustomerBillingProfileDto`, `CustomerDto.BillingProfiles` y `BillingProfileLimit`, lo mismo en `CustomerResponse` `:187` y en la respuesta de fusión `:659` si es otro tipo), `CustomerMapping.cs` (`ToDtoAsync`: ciudades, regiones y tasas de los perfiles en los mismos lotes), `CustomerCityIds.cs` (`OfBillingProfiles`), `CustomerRegionRules.cs` (`BillingProfileRegionInvalid`), `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` (rutas junto a `/addresses`, `:103-130`, y el mapeo DTO → respuesta `:650`), registro de handlers/validadores donde se registran los de direcciones (`QepServiceCollectionExtensions` o el escaneo del módulo; confírmalo con `codegraph callers AddCustomerAddressHandler`).
- Test: create `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerBillingProfilesApiTests.cs`, calcado de `CustomerAddressesApiTests.cs`.

**Interfaces:**
- Consumes: Task 1 (`EnsureBillingProfile`, `UpdateBillingProfile`, `RemoveBillingProfile`, `MakeBillingProfilePrincipal`).
- Produces (contrato, handoff §3):
  - `POST|PUT /api/v1/tenants/{tenantId:guid}/customers/{customerId:guid}/billing-profiles[/{profileId:guid}]`, `DELETE …/billing-profiles/{profileId:guid}`, `POST …/billing-profiles/{profileId:guid}/principal`; `RequireAuthorization(CustomersPermissions.CustomerManage)`, sin `If-Match`, 200 con el cliente entero.
  - `CustomerBillingProfileRequest(string? Name, string? PersonType, string? IdentificationType, string? IdentificationNumber, string? Phone, string? Email, string? Address, string? Country, Guid? CityId, string? CityName, Guid? RegionId, IReadOnlyList<Guid>? WithholdingTaxRateIds, bool VatSurplus, bool IsPrincipal)`.
  - `CustomerBillingProfileDto(Guid Id, string Name, string? PersonType, string IdentificationType, string IdentificationNumber, string? Phone, string? Email, string Address, string Country, Guid? CityId, string? CityName, Guid? DepartmentId, string? DepartmentName, CustomerRegionDto? Region, IReadOnlyList<CustomerWithholdingTaxRateDto> WithholdingTaxRates, bool VatSurplus, bool IsPrincipal)`; `CustomerDto.BillingProfiles` nunca `null` (patrón `WithholdingTaxRates ?? []`, `CustomersDtos.cs:123`), principal primero y luego `Name`; `CustomerDto.BillingProfileLimit = CustomerBillingProfile.MaxPerCustomer`. XML doc: el tope viaja para que la pantalla no ofrezca guardar sin escribir el 50 (BFF).
  - Una tasa que el catálogo ya no devuelve se omite de la lectura, como en las del cliente.

Handler: autorizar (`CustomersAuthorization.EnsureAuthorized`) **antes** de validar; validador con mapa `errors` (`Name`, `IdentificationNumber`, `IdentificationType`, `PersonType`, `Phone`, `Email`, `Address`, `CityId`, `CityName`, `RegionId`, `WithholdingTaxRateIds`) con los carriles de `CustomerAddressWriteRules` (`ManageCustomerAddresses.cs:57-118`); `CustomerRegionRules.ResolveRegionAsync(..., BillingProfileRegionInvalid, ...)`; `CustomerWithholdingRules.ValidateAsync(taxRateLookup, tenantId, ids, ct)` (mismos códigos `customers.customer.withholding_*`); ciudad comprobada con `CustomerAddressWorkflow.LoadAsync` (`customers.customer.city_not_found`). Tipo de persona exigido por el tenant: **no** se exige en el perfil (handoff §3.1).

- [ ] **Paso 1: pruebas que fallan** (integración):
  - `PostingAColombianProfileReturnsTheCustomerWithResolvedCityAndLimit` (`billingProfiles[0].cityName`, `departmentId`, `departmentName`, tasas con `name`, `type`, `percentage`; `billingProfileLimit == 50`).
  - `PostingASpanishProfileReturnsItsRegion`.
  - `RepeatingThePostCreatesNoRowAndKeepsTheVersion`.
  - `MunicipalInactiveAndRepeatedRatesAreRejected` (tres casos con sus códigos).
  - `PutReplacesTheProfile` (omitir `regionId` la borra; `withholdingTaxRateIds: []` las borra).
  - `DeletingThePrincipalIsRejected` ⇒ 422 `customers.billing_profile.principal_not_removable`; `PostingPrincipalMovesIt`.
  - `WithoutPermissionOrFromAnotherTenantIsForbidden` (403 en los dos); `AnUnknownCustomerIsNotFound` (404 `customers.customer.not_found`).
  - `ACustomerWithoutProfilesAnswersAnEmptyList` (`GET /customers/{id}`: `billingProfiles` presente y `[]`).
  - `ValidationErrorsCarryTheFieldMap` (`IdentificationNumber` vacío ⇒ `validation.failed` con `errors.IdentificationNumber`).
  - `AProfileRegionOfAnotherCountryIsRejected` ⇒ `customers.billing_profile.region_invalid`.
- [ ] **Paso 2: RED** — build (compilación) y luego las pruebas filtradas por `CustomerBillingProfilesApiTests`.
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN** — filtrado por nombre mientras iteras.
- [ ] **Paso 5: commit** — `feat(customers): manage billing profiles through the customer API`

### Task 4: Fusión de clientes

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerMerge.cs` (`CustomerMergeMoveKind.BillingProfiles`, `Move(...)` en `:115-123`), `ICustomerMergeStore.cs` (`CustomerMergeOwnedCounts(int Notes, int Addresses, int BillingProfiles)`), `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerMergeStore.cs` (`CountOwnedAsync`, `MoveOwnedCoreAsync`, filtro de tenant como `OwnedAddresses`).
- Test: modify `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerMergeApiTests.cs` y `CustomerMergePreviewApiTests.cs`; `CustomerMergePlanTests.cs` si construye `CustomerMergeOwnedCounts`.

Regla R-7: se mueven todos con `ExecuteUpdate` (`customer_id` = el que queda, `is_principal = false`); si el que queda no tenía perfiles antes de mover, un segundo `ExecuteUpdate` marca principal al movido de menor `(created_at, id)`. Sin dedupe y sin tope.

- [ ] **Paso 1: pruebas que fallan:** `MergingMovesTheBillingProfilesAsNonPrincipal`; `ASurvivorWithoutProfilesGetsTheOldestMovedAsPrincipal`; `ThePreviewCountsBillingProfiles` (`moves` trae `{ kind: "BillingProfiles", count: n }`).
- [ ] **Paso 2: RED** — compilación por la firma de `CustomerMergeOwnedCounts`, luego las tres filtradas.
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(customers): move billing profiles when merging customers`

### Task 5: Perfiles en el bloque `client` de cotización y pedido

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationCustomerLookup.cs` (`QuotationCustomerBillingProfileRef`, `QuotationCustomerRef.BillingProfiles` y `BillingProfileLimit`), `QuotationsDtos.cs` (`QuotationClientBillingProfileResponse`, `QuotationClientResponse.BillingProfiles`/`BillingProfileLimit`, `:702`), `QuotationResponseComposer.cs` (`ToClientResponse`, `:258`), `src/Bootstrapper/QuotationCustomerLookup.cs` (`FindAsync` `:43-65`: ciudades, regiones y tasas de los perfiles en los mismos lotes; `ToRef` `:156-204`).
- Test: create `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationCustomerBillingProfileRefTests.cs` (patrón de `QuotationCustomerAddressRefTests.cs`); modify `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs` o la prueba de detalle de cotización que ya fija `client.addresses`.

**Interfaces:**
- Produces: `QuotationCustomerBillingProfileRef(Guid Id, string Name, string? PersonType, string IdentificationType, string IdentificationNumber, string? Phone, string? Email, string Address, string Country, Guid? CityId, string? CityName, Guid? DepartmentId, string? DepartmentName, QuotationRegionDto? Region, IReadOnlyList<WithholdingTaxRateResponse> WithholdingTaxRates, bool VatSurplus, bool IsPrincipal)` (si `WithholdingTaxRateResponse` no tiene `isActive` y `appliesTo`, crea `QuotationClientWithholdingRateResponse` con la forma de `CustomerWithholdingTaxRateDto`: el frontend usa el mismo parser que la ficha). `QuotationClientResponse.BillingProfiles` nunca `null`; `BillingProfileLimit` = 50. `FindManyAsync` (Excel) no carga perfiles (R-11) y deja la lista vacía.

- [ ] **Paso 1: pruebas que fallan:** unitaria `ToRefCarriesTheBillingProfilesPrincipalFirstWithResolvedCities` (CO con departamento resuelto, ES con `region`, una tasa borrada del catálogo se omite); integración `TheQuotationAndOrderDetailsCarryTheClientBillingProfiles` (`client.billingProfiles` y `client.billingProfileLimit == 50`, mismo orden en los dos detalles).
- [ ] **Paso 2: RED** — compilación de `Bootstrapper.UnitTests`.
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(quotations): expose the client's billing profiles on quotes and orders`

**Fin del Bloque 1:** `CustomerBillingProfilesApiTests`, `CustomerBillingProfilePersistenceTests`, `CustomerAddressesApiTests`, `CustomerMergeApiTests` y `CustomerMergePreviewApiTests` completas, una vez, en fila; la clase tocada de Catalog; `Modules.Customers.UnitTests` y `Bootstrapper.UnitTests` completos (sin contenedores; compara con la línea base). Revisión del bloque.

---

## Bloque 2 — Entrega editable en «Editar pedido» (Tareas 6 y 7, un implementador)

### Task 6: `Order.SetDelivery`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (junto a `SetBilling`, `:897`; reescribe el comentario de `SetShipping`, `:1328-1333`: el pedido ya cambia recoger/enviar por `SetDelivery`), códigos nuevos en una clase `OrderDeliveryErrors` (Domain).
- Test: create `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderDeliveryTests.cs` (datos de prueba como `OrderShippingTests.cs`).

**Interfaces:**
- Produces:
  - `public void EnsureDeliveryEditable()`: `EnsureNotInvoiced` → `order.order.already_invoiced`, luego `EnsureNotPrepared` → `order.order.already_prepared`, luego `EnsureEditable` → `order.order.not_editable`.
  - `public bool SetDelivery(bool isStorePickup, QuotationPartyDetails? shipping, MemberId updatedBy, DateTimeOffset occurredAt)`: llama `EnsureDeliveryEditable`; `isStorePickup && shipping is not null` ⇒ `order.delivery.shipping_party_on_pickup`; valida la parte con prefijo `Delivery.Shipping` (`OrderParty.Create`, como `SetBilling` `:910`); a recoger: quita la fila de envío, `IsStorePickup = true`, `AssignShipping(ShippingDetails.None)` (apaga contraentrega); a enviar con `null`: quita la fila, `IsStorePickup = false`; con parte: `current.Apply(...)` o `Add` (nunca borrar y recrear: `IX_order_parties_order_role`). Mismo estado ⇒ `false` sin `Touch`.
  - `OrderDeliveryErrors.ShippingPartyOnPickup = "order.delivery.shipping_party_on_pickup"`, `AddressRequired = "order.delivery.address_required"`, `ShopifyPartyRequired = "order.delivery.shopify_party_required"`, `ShopifyRegionRequired = "order.delivery.shopify_region_required"`, `ShopifyRegionUnmapped = "order.delivery.shopify_region_unmapped"`. **No** existe `order.delivery.shopify_postal_code_unknown` (owner, P-1: ningún país exige código postal; el handoff §11.1.4 queda sin ese código).

- [ ] **Paso 1: pruebas que fallan:** `AnInvoicedOrderRejectsTheDeliveryChange` ⇒ `already_invoiced`; `APreparedOrderRejectsTheDeliveryChange` ⇒ `already_prepared`; `InvoicedWinsOverPrepared`; `SwitchingToPickupDropsThePartyAndClearsShippingAndCashOnDelivery` (`ShippingDetails == ShippingDetails.None`, `Shipping == null`); `SwitchingToSendWithAPartyCreatesTheRow`; `SwitchingToSendWithNullUsesTheCustomer` (sin fila); `PickupWithAPartyIsRejected` ⇒ `shipping_party_on_pickup`; `TheSameDeliveryIsNotAChange` (`false`, `Version` igual); `EditingThePartyModifiesTheExistingRow` (mismo `Id` de fila); `ThePostalCodeTravelsWithTheParty`.
- [ ] **Paso 2: RED** — `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests -nologo -v q` (`CS1061` por `SetDelivery`).
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN** — filtrado por `OrderDeliveryTests`; luego `OrderShippingTests` y `OrderBillingEditTests` completas.
- [ ] **Paso 5: commit** — `feat(quotations): let an order change pickup or delivery address while editable`

### Task 7: Contrato, handlers, historial, auditoría y `deliveryEditable`

**Files:**
- Modify: `OrdersDtos.cs` (`OrderDeliveryEditRequest`; propiedad `init` `Delivery` en `SaveOrderEditsRequest` (R-2); reescribe la XML doc de `Shipping` `:293-294`; `OrderResponse.DeliveryEditable` después de `PostalCodeSupported` `:490`), `SaveOrderEdits.cs` (comando `OrderDeliveryEditRequest? Delivery = null`; R-1; aplica la entrega antes de la facturación `:202`; `IQuotationCustomerLookup` para `address_required`; `shippingResolver.ResolveAsync` con el `order.IsStorePickup` ya actualizado `:235-237`; corto «nada cambió» `:255` suma `deliveryChanged`; historial y auditoría), `PreviewOrderEdits.cs` (R-1 en `:69` y la misma aplicación sin escribir), `OrderEdits.cs` (`IOrderEdits.Delivery`), `OrderChangeSummary.cs` (`DeliveryChanged`), `OrderResponseComposer.cs:181` (`DeliveryEditable = order.IsEditable`), el endpoint que arma `SaveOrderEditsCommand`/`PreviewOrderEditsCommand` desde el request (búscalo con `codegraph callers SaveOrderEditsCommand`), `.claude/rules/quotations-pos-catalog.md` (una viñeta: la entrega se edita; H5 revertido para la entrega).
- Test: modify `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs`; `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderChangeSummaryTests.cs`.

**Interfaces:**
- Consumes: Task 6.
- Produces:
  - `public sealed record OrderDeliveryEditRequest(bool IsStorePickup, QuotationPartyRequest? Shipping)` con la XML doc del handoff §10.2 (ausente conserva; `Shipping` null = datos del cliente; la parte de envío no lleva documento).
  - `OrderResponse.DeliveryEditable` (`bool`): XML doc «el frontend sólo abre la entrega con `true`; ausente en un backend viejo = sólo lectura; no depende del origen (owner 2026-11-24)».
  - `OrderChangeSummary.DeliveryChanged(OrderDeliveryText before, OrderDeliveryText after) → string`, con `OrderDeliveryText` = `«Recoger en tienda»` | `«Enviar a la dirección del cliente»` | `«Enviar a <dirección>, <ciudad>»`. Formato exacto: `$"Entrega: {before} → {after}"`. La ciudad: nombre DIVIPOLA por `IQuotationGeographyLookup` en `CO`, `CityName` afuera.
  - Auditoría `quotation.order.delivery_changed` (recurso `order.Id`), en el mismo bloque que `quotation.order.shipping_changed` (`SaveOrderEdits.cs:290-304`). Si cambian entrega y envío, salen las dos filas `PartiesChanged`.

Orden en el handler: autorizar, validar, cargar, **R-1**, `If-Match`, … , entrega (región con `PartyRegionRules.EnsureAsync(..., "Delivery.Shipping", ...)`; recoger→enviar con `Shipping` null y `QuotationCustomerRef.Address` vacío ⇒ `order.delivery.address_required`; hook de Shopify reservado para la Tarea 10), facturación, retenciones, envío, recálculo del pago (como hoy).

- [ ] **Paso 1: pruebas que fallan** (integración, `PUT /orders/{id}` y `POST /orders/{id}/preview`):
  - `AnInvoicedOrderAnswersAlreadyInvoicedForADelivery` y `APreparedOrderAnswersAlreadyPreparedForADelivery`, cada una en el `PUT` y en el preview.
  - `ABodyWithoutDeliveryKeepsIt` (cuerpo viejo: `isStorePickup` y parte iguales, sin fila de entrega en el historial).
  - `SwitchingToPickupDropsPartyAndFreightAndRecalculates` (`isStorePickup: true`, sin parte de envío, envío en cero, total recalculado, fila `PartiesChanged` con `Entrega: Enviar a … → Recoger en tienda` y auditoría `quotation.order.delivery_changed` en la misma transacción).
  - `SwitchingToSendWithASpanishPartyKeepsItsRegion`; `ARegionOfAnotherCountryIsRejectedWithTheDeliveryKey` ⇒ `quotations.party.region_invalid` con `errors["Delivery.Shipping.RegionId"]`.
  - `SwitchingToSendWithoutPartyAndCustomerWithoutAddressIsRejected` ⇒ `order.delivery.address_required`.
  - `PickupWithAShippingPartyIsRejected` ⇒ `order.delivery.shipping_party_on_pickup`.
  - `CashOnDeliveryWithProofsAfterSwitchingToSendIsRejected` ⇒ `order.order.cash_on_delivery_pending_only`.
  - `TheDetailSaysWhetherTheDeliveryIsEditable` (`true`: `Manual`, `Shopify`, `Wholesale` por aprobar; `false`: facturado, preparado, mayorista aprobado).
  - `AWholesaleOrderAwaitingApprovalChangesItsDelivery`; `AnApprovedWholesaleOrderAnswersNotEditable`.
  - Unitaria `DeliveryChangedDescribesBothSides`.
- [ ] **Paso 2: RED** — compilación (`Delivery`, `DeliveryEditable`), luego filtrado por nombre.
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN** — filtrado por nombre.
- [ ] **Paso 5: commit** — `feat(quotations): edit the order delivery and expose deliveryEditable`

**Fin del Bloque 2:** `OrderEditsApiTests` completa una vez; además las pruebas de código postal que nunca corrieron (`--filter "FullyQualifiedName~PostalCode"` sobre `Modules.Quotations.IntegrationTests`) y `Modules.Quotations.UnitTests` completo. Un rojo en las de código postal se reporta aparte como línea base heredada, con su causa; no se corrige en este bloque sin avisar. Revisión del bloque.

---

## Bloque 3 — Shopify: código postal, otros países y empuje de la dirección (Tareas 8 a 11, un implementador)

Lee `.claude/rules/shopify.md` antes de empezar.

### Task 8: Huecos del código postal en Shopify

Los cuatro están confirmados en el código (y anunciados en `ShopifyCityCorrection.cs:7-10`: «el que empiece a hacerlo tiene que sumarlo a esas dos»):

- (a) `ShopifyCityCorrection.Party` arma la huella sin `PostalCode` y la tabla no lo guarda; la recorrección (`ShopifyCityCorrections.cs:325-326`) le pasa a `Order.CorrectExternalShippingCity` una huella sin código postal, que compara `party.ToDetails() != expected` (`Order.cs:2201`): un pedido con código postal vuelve como `ShippingChanged`.
- (b) `ShopifyOrderProjection.SameParty` (`:91-103`) no compara `PostalCode`.
- (c) `ShopifyPartyDraft` (`ShopifyPorts.cs:432-433`) no lo lleva; tampoco el borrador del planificador (`ShopifyEditPlanner.cs:201,209`) ni `ShopifyOrderDiff.AddressRows` (`:138-145`).
- (d) `ShopifyOrderWriter.ApplyEditAsync` arma `ExternalShippingParty` sin código postal (`:150-152`) y `Order.ApplyExternalEdit` lo aplica (`OrderParty.Apply` deja `PostalCode = null`, `OrderParty.cs:107`): una edición de dirección hecha en Shopify borra el código postal.

**Files:**
- Modify: `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyCityCorrection.cs` (columna y `Party` con `PostalCode`; borra el aviso de `:7-10`), `ShopifyDbContext` (columna `postal_code` 20 null), migración `AddCityCorrectionPostalCode` (`dotnet ef migrations add AddCityCorrectionPostalCode --project src/Modules/Shopify/Modules.Shopify.Infrastructure --context ShopifyDbContext -o Persistence/Migrations`), `ShopifyOrderProjection.cs` (`SameParty` compara `PostalCode`), `ShopifyPorts.cs` (`ShopifyPartyDraft(..., string? PostalCode = null)`), el importador y el planificador (llenan `PostalCode` con `snapshot.ShippingAddress.Zip`), `ShopifyOrderDiff.cs` (`AddressRows` suma la fila `zip`), `src/Bootstrapper/ShopifyOrderWriter.cs` (`ToExternal` y `ApplyEditAsync` pasan `PostalCode`), `src/Bootstrapper/ShopifyCityCorrectionWriter.cs`.
- Test: `tests/Modules/Shopify/Modules.Shopify.UnitTests/ShopifyOrderProjectionTests.cs`, `ShopifyEditPlannerTests.cs`, `ShopifyCityCorrectionProcessorTests.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTests.cs`; `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyCityCorrectionFlowTests.cs`.

- [ ] **Paso 1: pruebas que fallan:** `SamePartyComparesThePostalCode`; `AZipOnlyShopifyEditIsAShippingAddressChange` (planificador); `AShopifyAddressEditKeepsShopifysZip` (adaptador: el `ExternalShippingParty` trae el `zip` del snapshot); `ARecorrectionOfAnOrderWithPostalCodeIsApplied` (integración: pedido con código postal, sinónimo corregido ⇒ ciudad nueva, código postal intacto, sin `ShippingChanged`); `ImportedColombianOrdersKeepTheZipOnTheParty`.
- [ ] **Paso 2: RED** — compilación de `Modules.Shopify.UnitTests` y `Bootstrapper.UnitTests`; luego la de integración filtrada.
- [ ] **Paso 3: implementa.** Si `AddressRows` alimenta una respuesta HTTP (la bandeja de ediciones del frontend), la fila `zip` es un campo nuevo para el frontend: anótalo en el reporte.
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `fix(shopify): carry the postal code through edits, projections and city corrections`

### Task 9: Parte de envío de cualquier país en el estado y en el worker

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderWriter.cs` (`ExternalShippingParty(..., string? PostalCode = null, string? Country = null, string? CityName = null, Guid? RegionId = null)` y su mapeo en `FindEditStateAsync`), `src/Modules/Shopify/Modules.Shopify.Domain/ShopifyCityCorrection.cs` (`ShopifyShippingFingerprint`: `Guid? DepartmentId`, `Guid? CityId`, más `string? Country = null`, `string? CityName = null`, `Guid? RegionId = null`; el código de corrección de ciudad, sólo `CO`, exige los dos ids con `?? throw`), `ShopifyOrderProjection.SameParty` (compara país, ciudad escrita y región), `src/Bootstrapper/ShopifyOrderWriter.cs` (`Fingerprint`: en `CO` exige departamento y ciudad como hoy; afuera exige `CityName`), `ShopifyOrderPushProcessor.cs` (`BuildAsync` `:102-130`: rama de otro país), el puerto de geografía de Shopify y su adaptador en Bootstrapper (`DescribeRegionAsync(Guid regionId) → (string CountryCode, string Code)?`; confirma el nombre del puerto con `codegraph callers DescribeCityAsync`).
- Create: `src/Modules/Shopify/Modules.Shopify.Application/SpainProvinceCodes.cs` (estática, como `ColombiaProvinceCodes.cs`), `ShopifyShippingAddressRules.cs`.
- Test: create `tests/Modules/Shopify/Modules.Shopify.UnitTests/SpainProvinceCodesTests.cs`, `ShopifyShippingAddressRulesTests.cs`; create `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/SpainProvinceCodesGeographyTests.cs` (patrón de `ColombiaProvinceCodesGeographyTests.cs`); modify `ShopifyOrderPushProcessorTests.cs`, `ShopifyAdaptersTests.cs`.

**Interfaces:**
- Produces:
  - `SpainProvinceCodes.IsoFor(string ineCode) → string?` y `All` (52 entradas INE → ISO 3166-2 sin prefijo; se confirman contra Shopify en la verificación final, P-4): `01 VI, 02 AB, 03 A, 04 AL, 05 AV, 06 BA, 07 PM, 08 B, 09 BU, 10 CC, 11 CA, 12 CS, 13 CR, 14 CO, 15 C, 16 CU, 17 GI, 18 GR, 19 GU, 20 SS, 21 H, 22 HU, 23 J, 24 LE, 25 L, 26 LO, 27 LU, 28 M, 29 MA, 30 MU, 31 NA, 32 OR, 33 O, 34 P, 35 GC, 36 PO, 37 SA, 38 TF, 39 S, 40 SG, 41 SE, 42 SO, 43 T, 44 TE, 45 TO, 46 V, 47 VA, 48 BI, 49 ZA, 50 Z, 51 CE, 52 ML`. Confirmación contra Shopify: paso explícito de «Verificación final y merge» (P-4).
  - `enum ShopifyShippingAddressIssue { None, RegionRequired, RegionUnmapped }` y `ShopifyShippingAddressRules.Check(string country, string? regionCode) → ShopifyShippingAddressIssue`: `CO` ⇒ `None` (el carril lo cubre la ciudad DIVIPOLA); país con catálogo (`ES`) sin región ⇒ `RegionRequired`; región fuera de la tabla del país, o país con catálogo sin tabla ⇒ `RegionUnmapped`; país sin catálogo ⇒ `None` (se manda sin `provinceCode`). El código postal **nunca** es un motivo de rechazo (owner, P-1): no hay lista de países que lo exijan.
  - `BuildAsync`, otro país: `countryCode = party.Country`, `city = party.CityName`, `provinceCode = SpainProvinceCodes.IsoFor(regionCode)` (o `null` sin catálogo); un `Check` distinto de `None` al mandar lanza como hoy con la ciudad faltante (la validación del guardado, Tarea 10, impide llegar ahí).
  - **`zip`, sin cambios de regla** (la fijan el código, `ShopifyOrderPushProcessor.cs:122-140`, y los handoffs de código postal §5 y de perfiles §11.1.3, y vale igual para `CO`): el `PostalCode` de la parte si no está vacío; si no, el `Zip` del último snapshot **sólo** si país y `provinceCode` coinciden con los del snapshot (`KeptZip`); si no, ninguno. Sin `provinceCode` (país sin catálogo) `KeptZip` da `null`. Como `orderUpdate` reemplaza la dirección entera, «ninguno» deja el pedido de Shopify sin `zip`; si Shopify lo rechaza con un `userError`, el envío queda `Failed` como hoy. Consecuencia conocida y aceptada por los documentos: una calle distinta en la misma provincia conserva el `zip` viejo de Shopify.

- [ ] **Paso 1: pruebas que fallan:** `EveryIneCodeHasAnIsoCode` (52); `SpainProvinceCodesCoverEverySeededSpanishRegion` (integración, regiones `ES` de Geography); `RulesAskForTheRegionInSpain`, `RulesRejectAnUnmappedRegion`, `RulesAcceptACountryWithoutCatalog`; `ThePushBuildsASpanishAddress` (`countryCode: "ES"`, `city` = `cityName`, `provinceCode: "M"`, `zip` del snapshot con la misma provincia); `ThePartyPostalCodeWinsOverTheSnapshotZip`; `AnotherProvinceWithoutPostalCodeSendsNoZip` (sin `zip` en el `MailingAddressInput`); `ACountryWithoutCatalogSendsNoProvinceAndNoKeptZip`; `ReadingTheStateOfAnOrderWithASpanishPartyDoesNotThrow` (adaptador `ToShopify`); `AnOldProjectionJsonWithoutTheNewFieldsStillDeserializes` (la proyección guardada `applied_projection` antes de la rama).
- [ ] **Paso 2: RED** — compilación; luego la de integración filtrada.
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN**; `ShopifyOrderPushProcessorTests`, `ShopifyOrderProjectionTests` y `ShopifyCityCorrectionProcessorTests` completas.
- [ ] **Paso 5: commit** — `feat(shopify): build shipping addresses outside Colombia`

### Task 10: Validación de Shopify en el guardado del pedido

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderDeliveryCheck.cs`, `src/Bootstrapper/ExternalOrderDeliveryCheck.cs` (adaptador: comprueba que el pedido tenga fila espejo de Shopify por un servicio público de `Modules.Shopify.Application` (el snapshot ya no hace falta: el código postal no se valida, P-1), resuelve el código de región con el puerto de geografía y llama `ShopifyShippingAddressRules.Check`; mapea `ShopifyShippingAddressIssue` → código de `OrderDeliveryErrors`); registro en `QepServiceCollectionExtensions`.
- Modify: `SaveOrderEdits.cs` y `PreviewOrderEdits.cs` (el hook reservado en la Tarea 7).
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderEditsApiTests.cs` (o una clase nueva `ShopifyOrderDeliveryEditTests.cs` si la base de Shopify no está en esa fábrica); `tests/Bootstrapper/Bootstrapper.UnitTests/ExternalOrderDeliveryCheckTests.cs`.

**Interfaces:**
- Produces: `Task<string?> CheckAsync(Guid tenantId, Guid orderId, string country, Guid? regionId, CancellationToken cancellationToken)` (código o `null`; el código postal no entra, P-1). Sólo corre con `order.Source == OrderSource.Shopify`, sin recogida y con la parte propia cambiada. Enviar con `Delivery.Shipping == null` en un pedido de Shopify ⇒ `order.delivery.shopify_party_required` sin llamar al puerto. Un pedido sin fila espejo ⇒ `null` (no hay a quién empujar).

- [ ] **Paso 1: pruebas que fallan:** `ShopifyCustomerAddressIsRejected` ⇒ `shopify_party_required`; `ShopifySpainWithoutRegionIsRejected` ⇒ `shopify_region_required`; `ShopifyUnmappedRegionIsRejected` ⇒ `shopify_region_unmapped` (región `ES` fuera de la tabla con un doble del puerto, unitaria del adaptador); `ShopifySpainWithoutPostalCodeIsAccepted` (otra provincia, sin código postal: 200, P-1); `ThePreviewAppliesTheSameShopifyRules`; `AManualOrderSkipsTheShopifyRules`.
- [ ] **Paso 2: RED** — compilación, luego filtrado.
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(quotations): reject delivery edits Shopify could not receive`

### Task 11: Empujar a Shopify la dirección cambiada en «Editar pedido»

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationLifecycleEventPublisher.cs` y `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationLifecycleEventPublisher.cs` (constante `quotations.order.shipping-party-changed.v1`, payload `{ tenantId, orderId, source, occurredAt }` en camelCase), `SaveOrderEdits.cs` (publica sólo con `Source == Shopify`, sin recogida y parte propia cambiada; el preview **no** publica).
- Create (Shopify): `Modules.Shopify.Infrastructure/Messaging/QuotationOrderEventsProcessor.cs` (consumidor `shopify.quotation-order-events`, calcado de `CitySynonymEventsProcessor.cs`, con su copia propia del nombre del evento y del lector del payload: `ShopifyLayerTests` prohíbe referenciar Quotations), `Modules.Shopify.Application/ShopifyShippingPartyChangedApplier.cs` (busca la fila espejo del pedido; sin fila, nada; conexión no operativa ⇒ `holdReason` `ConnectionInactive` como `ShopifyOrderCorrections.cs:~388-390`; `ShopifyOrderPushes.EnqueueAsync(row, [ShopifyPushGroup.ShippingAddress], now, ct, holdReason)`; **no** mueve `applied_projection`), `Modules.Shopify.Infrastructure/Workers/QuotationOrderEventsWorker.cs`; registro junto a `AddShopifyCityCorrections` (`ShopifyInfrastructureExtensions.cs:122-132`).
- Modify: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` (entrada `Claimed` como `CitySynonymEventsWorker`, `:93-94`, con su evidencia), `CompositionRootTests.cs` (aserción del consumidor, `~:1401`), `.claude/rules/shopify.md` (tercer origen de envíos: corrección, sinónimos de ciudad, edición del pedido; la edición no pasa por `ShopifyOrderCorrectionWriter` porque no escribe el pedido desde Shopify), `README.md` (contrato de `delivery`, `deliveryEditable`, códigos y el evento).
- Test: create `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyDeliveryEditPushTests.cs` (patrón de `ShopifyCityCorrectionFlowTests.cs`: escribe el evento, `ProcessBatchAsync`, revisa el envío); modify `OrderEditsApiTests.cs` (outbox).

- [ ] **Paso 1: pruebas que fallan:** `ChangingAShopifyOrderAddressWritesTheOutboxEvent` (misma transacción que el pedido); `SwitchingAShopifyOrderToPickupPublishesNothing`; `TheConsumerEnqueuesAShippingAddressPush`; `AnOpenPushIsMergedNotDuplicated`; `AnInactiveConnectionHoldsThePush`; `TheProjectionDoesNotMove` (modelo: `AfterAnEchoALaterShopifyEditOfThatGroupIsStillAConflict`, `ShopifyOrderEditsTests.cs`); `ASpanishAddressIsPushedWithProvinceAndZip` (de punta a punta: guardar con `postalCode`, consumir, drenar el worker contra el doble de `ShopifyOrderUpdateClient`: `provinceCode: "M"`, `zip` = el de la parte); `ASpanishAddressOnAnotherProvinceWithoutPostalCodeIsPushedWithoutZip`; `EveryHostedServiceIsClassified` y `EveryStatefulSingletonIsClassified` siguen verdes.
- [ ] **Paso 2: RED** — compilación, luego filtrado.
- [ ] **Paso 3: implementa.**
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(shopify): push the shipping address changed in the order editor`

**Fin del Bloque 3:** `ShopifyCityCorrectionFlowTests`, `ShopifyDeliveryEditPushTests`, `ShopifyOrderPushWorkerTests`, `ShopifyEditSyncFlowTests`, `SpainProvinceCodesGeographyTests` y la clase de la Tarea 10 completas, una vez, en fila; `Modules.Shopify.UnitTests`, `Bootstrapper.UnitTests` y `ArchitectureTests` completos (compara con la línea base). Revisión del bloque.

---

## Verificación final y merge

- [ ] `dotnet format --verify-no-changes` y `dotnet build --no-restore -nologo -v q` sin advertencias.
- [ ] `.\scripts\test-feature.ps1` en verde, **leyendo sus avisos**; una falla de la línea base de la Tarea 0 se reporta con su nombre y se decide con el owner, no se oculta.
- [ ] **Códigos de provincia de España (P-4), antes del merge:** compara las 52 entradas de `SpainProvinceCodes.All` con la lista de provincias de España que publica Shopify (documentación de `provinceCode`/`CountryCode ES`, o una tienda de prueba), en especial `PM`, `GC`, `TF`, `CE` y `ML`. Pega en el reporte la fuente y el resultado; una diferencia se corrige con su prueba antes de mergear. Sin esta confirmación no hay merge.
- [ ] Revisión fuerte de toda la rama (modelo más capaz).
- [ ] Merge `--no-ff` a `develop` (nunca a `main`, sin push). Después, en `develop`, commit `docs(estado): …` con la fila de la feature y el Registro, más la nota de qué quedó de la línea base del código postal.
- [ ] Handoff al frontend: los fallos R-1…R-12 que cambian su contrato (código `customers.billing_profile.region_invalid`, claves `Delivery.Shipping.*` sólo para región y código postal, la fila `zip` si la hubo) y el orden de despliegue: backend primero. Por P-1 el plan del frontend (Tarea 16) tiene que **quitar** el mapeo de `order.delivery.shopify_postal_code_unknown` («Escribe el código postal: Shopify lo necesita…»): el backend no lo emite. El código postal sigue opcional también en pedidos de Shopify.

## Decisiones del owner (2026-11-25)

- **P-1. Ningún país exige código postal para Shopify.** Reemplaza la propuesta del handoff §11.1.4 (lista con `ES`) y del diseño («Comportamiento», punto 3). Se eliminan la lista de países, el valor `PostalCodeUnknown` y el código `order.delivery.shopify_postal_code_unknown` (nada más los usaba). Lo que se manda como `zip` no cambia: lo fijan el código y los dos handoffs (Tarea 9, «`zip`, sin cambios de regla»); sin código postal en la parte y con otra provincia u otro país, el envío sale sin `zip`.
- **P-2. Los perfiles cuentan en el candado de tasas**, para borrar y para desactivar (R-9).
- **P-3. La fusión mueve todos los perfiles sin dedupe y sin tope; si el que queda no tenía ninguno, el movido más antiguo queda principal** (R-7). Reemplaza el dedupe al fusionar que pide el handoff §4.
- **P-4. Los códigos ISO de provincia de España se confirman contra Shopify antes del merge a `develop`**: paso explícito de «Verificación final y merge».

## Decisiones pendientes del owner

Ninguna abierta.
