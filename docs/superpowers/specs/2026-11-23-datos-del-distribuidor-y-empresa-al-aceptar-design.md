# Datos del distribuidor y empresa al aceptar la invitación — diseño

Fecha: 2026-11-23. Módulos: Wholesale, Companies, Customers, Bootstrapper. Estado: **aprobado por el owner en la conversación (opción A,
enfoque 1)**; revisado contra el código el mismo día (ver «Revisión» al final). Plan:
`docs/superpowers/plans/2026-11-23-datos-del-distribuidor-y-empresa-al-aceptar.md`.

## Intención

Lo que el mayorista llena al invitar a un distribuidor debe terminar donde corresponde cuando el distribuidor acepta: en el
cliente que nace en el tenant del mayorista y en una **empresa** dentro del tenant nuevo del distribuidor. Hoy el cliente pierde
el teléfono y la empresa no se crea, así que el distribuidor entra a un tenant sin empresa y tiene que volver a escribir lo que el
mayorista ya sabía.

## Estado actual (verificado en el código)

- El formulario captura correo, razón social, país, tipo y número de documento, contacto y teléfono (`InviteDistributorCommand`,
  `InviteDistributor.cs:8-22`); precio, asesor y empresa facturadora son datos del mayorista, no del distribuidor.
- El teléfono es el **celular del aviso por WhatsApp**: se guarda normalizado a E.164 sólo si viene
  (`DistributorWhatsAppNotice.NormalizeAsync`), y el `PUT` del distribuidor invitado lo puede borrar (`Distributor.ChangeNoticeTarget`).
  `Distributor.Phone` puede ser `null`.
- `DistributorInvitationService.ResolveTargetAsync` con `NewTenantChoice` crea el tenant (`RegisterInvitedTenantAsync`) con el
  `DisplayName` y el `Slug` del cuerpo del accept, o la razón social y el slug sugerido si no vienen
  (`src/Api/WholesaleInvitationEndpoints.cs:323-327`), y el país del distribuidor (`DistributorInvitationService.cs:232`).
  **No crea ninguna empresa.** La ruta es `POST /api/v1/wholesale-invitations/{token}/accept`.
- `ResolveTargetAsync` devuelve `created = true` en **tres** caminos: tenant reservado ya existente (D-17, «pida lo que pida el
  cuerpo», también con `LinkTenantChoice`), slug tomado por la otra pestaña (Review Focus 1) y alta nueva. Con `LinkTenantChoice`
  sobre un tenant existente devuelve `false` (`DistributorInvitationService.cs:205-283`).
- `WholesaleCustomerProvisioning.EnsureCustomerAsync` pasa `null` como teléfono a `RegisterCustomerData`
  (`src/Bootstrapper/WholesaleCustomerProvisioning.cs:35`). `RegisterCustomerData` sólo tiene nombre, tipo y número de documento,
  teléfono, correo y país: no hay ciudad, dirección ni contacto en Customers por este camino. `phone_e164` de Customers no es único.
- `RegisterInvitedTenantAsync` sólo lo llama Wholesale; el registro público usa `RegisterOwnerTenantAsync` y no cambia.
- `CreateCompanyCommand` exige país, tipo de documento válido para el país y, en Colombia, `CityId` (que además tiene que existir en
  Geography: 422 `companies.company.city_not_found`); fuera de Colombia, `CityName`. Las cuentas bancarias pueden ir vacías;
  `ChargesVat` ausente es `true`, y una empresa colombiana que cobra IVA siembra las tasas por defecto después del commit
  (`CompanyDefaultTaxRateSeeding`). El handler autoriza contra el `IExecutionContext` del request
  (`CompaniesAuthorization.EnsureAuthorized`: mismo tenant y `companies.company.manage`).
- En el accept quien llama todavía no es miembro del tenant nuevo: `HttpExecutionContext.TenantId` lee la claim de tenant y, sin
  ella, lanza `InvalidOperationException` (500); con la de otro tenant, el handler responde 403. **El caso de uso de Companies no
  sirve en el accept**; `ShopifySetupCompanies` sí lo usa porque ahí el asesor está dentro de su tenant.
- `companies.companies` no tiene índice único por documento (EMP-08 lo retiró; `CompaniesUnitOfWork`), y el filtro `taxId` de
  `ICompanyRepository.SearchAsync` es `ILIKE`, no igualdad.
- La tabla de tipos de documento del distribuidor (`DocumentTypesByCountry`) es la misma de Companies (`CompanyDocumentTypes`,
  atadas por prueba de paridad), y los topes coinciden: razón social 160 = `Company.NameMaxLength`, documento 32 =
  `Company.TaxIdMaxLength`.

## Objetivos

1. El formulario de invitación captura **ciudad** y **dirección** del distribuidor, con la ciudad validada al invitar.
2. Al aceptar sobre el tenant reservado (el que nace de la invitación) se crea una empresa en ese tenant con los datos del
   distribuidor.
3. El cliente del mayorista nace con el **teléfono** del distribuidor, si lo hay.
4. Toda la cadena es idempotente: un reintento o dos pestañas a la vez no duplican la empresa ni el cliente.

## No objetivos

- ~~Aceptar vinculando un tenant existente no crea empresa.~~ **Cambiado por el owner (2026-11-24):** al vincular un tenant existente
  (`created = false`) la empresa se crea **sólo si ese tenant no tiene ninguna** (activa o inactiva), por
  `ICompanyProvisioning.EnsureFirstCompanyAsync`, bajo el mismo candado; si ya tenía alguna, no se escribe nada.
- No se tocan el registro público de tenants ni `RegisterOwnerTenantAsync`.
- ~~No se copian al cliente la ciudad, la dirección ni el contacto: `RegisterCustomerData` no los admite. Decisión pendiente del owner.~~
  **Decisión del owner 2026-11-24: ciudad, dirección y contacto sí se copian al cliente.** Con teléfono y ciudad el cliente nace
  completo (`CreateCompleteAsync`, con ciudad y dirección); el contacto es la etiqueta de la dirección principal de la libreta
  (`CompleteCustomerData.AddressLabel`; sólo con ciudad colombiana y calle). Sin teléfono o si Customers rechaza la ficha completa,
  cae al incompleto de antes. Un cliente existente por documento no se actualiza.
- No se crean cuentas bancarias ni retenciones, y no se cambia `ChargesVat` (queda en `true`, el valor por defecto de Companies).
- ~~No se agrega edición de los datos del distribuidor mientras está invitado (`ChangeTerms` sólo cambia lista, asesor y empresa
  facturadora). Un error en ciudad o dirección se corrige borrando la invitación y creando otra.~~
  **Decisión del owner 2026-11-24: se pueden editar mientras está invitado.** El `PUT` del distribuidor acepta razón social, país,
  tipo y número de documento, contacto, correo, ciudad y dirección (`Distributor.ChangeProfile`); ver «Contrato HTTP».
- El formulario del frontend (`qep-frontend`) lleva su propio spec; este documento sólo fija el contrato.

## Anti-objetivos

- Que la invitación se gaste con la relación a medias: se mantiene la regla de `mayorista-compras.md` (la invitación se consume en
  el último commit, el de `wholesale`).
- Relajar las validaciones de Companies para que acepte una empresa sin ciudad. Companies sigue siendo la única puerta.
- Crear la empresa por un camino que se salte las reglas de Companies (validador, ciudad, siembra de tasas).
- Que un dato que escribió el mayorista bloquee al distribuidor: lo que Companies rechazaría en el accept se rechaza antes, al invitar.

## Enfoques considerados

1. **Dentro del accept, síncrono (elegido).** Paso nuevo después de crear el tenant, con un puerto propio de Wholesale y un
   adaptador en Bootstrapper. El distribuidor entra con la empresa ya creada; un fallo reintenta el accept entero, que ya es
   idempotente (D-3).
2. **Por evento de outbox.** Companies consumiría un evento y crearía la empresa después. Desacopla, pero el distribuidor puede
   entrar antes de que exista y un fallo pasa en silencio. Descartado.

## Diseño

### 1. Formulario de invitación y `Distributor`

- `DistributorInviteRequest` e `InviteDistributorCommand` agregan, al final y opcionales en la firma, `CityId` (Guid?), `CityName`
  (string?) y `Address` (string?).
- `InviteDistributorValidator` **replica** (no incluye: `CompanyWriteRules` es `internal` de Companies y Wholesale no referencia
  Companies, `WholesaleLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules`) las reglas de ciudad de
  `CompanyWriteRules`, con los mismos nombres de propiedad para que la pantalla marque el mismo campo: país `CO` → `CityId`
  obligatorio y distinto de `Guid.Empty` («The city is required.»); otro país válido → `CityName` obligatorio; `CityName` ≤ 120
  (`Company.CityNameMaxLength`); `Address` opcional, ≤ 200 (`CompanyContactInfo.AddressMaxLength`).
- El handler de invitar verifica que la ciudad exista (sólo en `CO`) por un puerto nuevo `IWholesaleCityLookup` (adaptador en
  Bootstrapper sobre Geography, copia de `CompanyGeographyLookup`), después de `DistributorTerms.EnsureValidAsync` y antes del
  chequeo de documento: 422 **`wholesale.distributor.city_not_found`** (código nuevo, en `WholesaleErrorCodes`). Sin esto un
  `CityId` inexistente pasaría la invitación y haría fallar cada accept con `companies.company.city_not_found`.
- `Distributor` agrega `CityId`, `CityName`, `Address` (columnas nullable `city_id`, `city_name` varchar(120), `address`
  varchar(200) en `wholesale.distributors`; migración sólo de expansión, sin backfill). `Distributor.Invite` los recibe; el dominio
  sólo recorta y convierte vacío en `null`. La auditoría de invitar suma `cityId`, `cityName` y `address` a los campos nombrados.
- Las filas ya existentes no tienen ciudad: **si una invitación sin la ciudad que su país exige se acepta, la empresa no se crea** y
  el accept sigue funcionando. Se registra un `Information` con el id del distribuidor y nada más. Decisión del asistente para no
  romper invitaciones vivas.
- `DistributorDetailDto` agrega `City` (`DistributorCityDto(Guid? Id, string Name)`, el nombre resuelto por `IWholesaleCityLookup`
  en Colombia o el escrito afuera; `null` sin ciudad) y `Address`. BFF: la pantalla de detalle pinta el nombre sin conocer
  Geography; el motivo va en el DTO. **El resumen de la lista no cambia** (la grilla no los muestra y resolverlos por fila sería una
  consulta más por página).

### 2. Puerto de aprovisionamiento de empresas (Companies)

- Nuevo `ICompanyProvisioning` público en `Modules.Companies.Application` (implementación `CompanyProvisioning` en el mismo
  proyecto, registrada en `AddCompaniesInfrastructure`, igual que `ICustomerProvisioning`): altas que hace el sistema, **sin
  `IExecutionContext` ni permiso**; el aislamiento lo da el `tenantId` y la autorización ya la dio la invitación.
- `CreateCompanyHandler` y `CompanyProvisioning` comparten un creador interno extraído del handler (normaliza país, corre
  `IValidator<CreateCompanyCommand>`, busca la ciudad, valida retenciones, `Company.Create`, `repository.Add`, auditoría
  `companies.company.created`, guardado y siembra de tasas después del commit). El handler sólo agrega `EnsureAuthorized` antes y
  usa `executionContext.SubjectId` como actor; el aprovisionamiento usa el usuario que acepta. Las pruebas existentes del handler
  son la red del refactor.
- `EnsureCompanyAsync(Guid tenantId, Guid actorUserId, ProvisionedCompanyData data, CancellationToken)` es idempotente **bajo un
  candado**: abre una transacción en `CompaniesDbContext`, toma `pg_advisory_xact_lock(CompanyProvisioningLock.Namespace,
  hashtext(tenantId))`, busca una empresa del tenant (activa o inactiva) con el mismo tipo y número de documento exactos
  (`SearchAsync` con `taxId` y comparación exacta en memoria, porque el filtro es `ILIKE`) y, si existe, devuelve su id sin
  actualizarla; si no, la crea y confirma. Sin el candado, dos pestañas que llegan juntas al paso (las dos pasan
  `ResolveTargetAsync` con `created = true`) crearían dos empresas: no hay índice único que lo frene. Para eso
  `ICompaniesUnitOfWork` suma `BeginTransactionAsync` y `LockProvisioningAsync` (patrón de `ISiigoUnitOfWork`).
- La siembra de tasas corre fuera del candado, después del commit, como en el handler.
- No hace falta una prueba de arquitectura nueva: `CompaniesLayerTests.ApplicationDoesNotReferencePersistenceLibraries` ya cubre
  que el puerto y su implementación no vean EF Core.

### 3. Paso nuevo en el accept (Wholesale)

- Puerto `IWholesaleCompanyProvisioning` en `Modules.Wholesale.Application/Ports.cs`; adaptador `WholesaleCompanyProvisioning` en
  `src/Bootstrapper/` sobre `ICompanyProvisioning` (igual que `IWholesaleCustomerProvisioning`).
- En `AcceptAsync`, después de `ResolveTargetAsync` y **sólo si `created`** (es decir, el destino es el tenant reservado, venga el
  cuerpo que venga: en un reintento D-17 el cuerpo puede ser `LinkTenant`) y la invitación tiene la ciudad que su país exige: llamar
  a `EnsureCompanyAsync` con razón social, país, tipo y número de documento, `CityId`/`CityName`, dirección, correo (el de la cuenta
  que acepta si vino, si no el del distribuidor, como el cliente) y `Distributor.Phone`. El actor es el usuario que acepta.
- Orden: tenant → empresa → proveedor vinculado → cliente → commit final. Un fallo de la empresa deja el tenant reservado creado y
  el accept reintenta sobre él (D-17), sin consumir la invitación.
- Si Companies rechaza (`ValidationException`, `CompaniesDomainException`), el accept responde ese error tal cual; no se traga. Con
  la validación al invitar sólo debería pasar por deriva de datos; la salida es borrar la invitación e invitar de nuevo.
- Riesgo a cubrir en integración: en el alta nueva, `QuotationsNewTenantProvisioning` usa el `QuotationsDbContext` del scope y,
  luego, `CompanyDefaultTaxRates` lo enrola en la transacción de Catalog. Nada del accept lo usa después, pero la prueba de punta a
  punta en Colombia lo ejercita.

### 4. Teléfono del cliente

- `IWholesaleCustomerProvisioning.EnsureCustomerAsync` agrega `string? phone` antes del `CancellationToken`;
  `WholesaleCustomerProvisioning` lo pasa como cuarto argumento de `RegisterCustomerData` en lugar de `null`. El reuso por documento
  no cambia: un cliente existente no se actualiza.

## Contrato HTTP (para el spec del frontend)

- `POST /api/v1/tenants/{tenantId}/wholesale/distributors`: `cityId` (obligatorio con `country = "CO"`), `cityName` (obligatorio
  con otro país), `address` (opcional). Errores nuevos: 422 `validation.failed` con `errors.CityId` / `errors.CityName` /
  `errors.Address`, y 422 `wholesale.distributor.city_not_found`.
- `GET …/wholesale/distributors/{id}`: `city` (`{ id, name }` o `null`) y `address`.
- `PUT …/wholesale/distributors/{id}` (decisión del owner 2026-11-24): suma `businessName`, `country`, `identificationType`,
  `identificationNumber`, `contactName`, `email`, `cityId`, `cityName` y `address`, todos opcionales; ausente conserva lo guardado
  y `address: ""` borra la dirección. Sólo en `Invited`, con las reglas de invitar (422 `validation.failed` por campo, 422
  `wholesale.distributor.city_not_found`, 409 `wholesale.distributor.identification_taken` contra los demás); la ciudad se exige a
  una invitación vieja sin ella sólo si se manda la ciudad o cambia el país. En `Active` o `Blocked`, un valor distinto del
  guardado es 409 `wholesale.distributor.invalid_transition`. Cambiar el correo no reenvía la invitación ni rota el token.
  - Nota (2026-11-24): en `Invited`, cambiar el tipo o el número de documento cuando el tenant reservado de la invitación ya
    existe (aceptación a medias) es 409 `wholesale.distributor.acceptance_in_progress` y no escribe nada, porque el reintento es
    idempotente por documento y duplicaría la empresa y el cliente. Los demás campos siguen editables. Límite conocido: una
    aceptación a medias vinculando un tenant existente no se detecta.
  - Nota (2026-11-24, marca de aceptación iniciada): ese límite, y el del `PUT` que confirmaba antes de que la aceptación
    creara el tenant, se cierran con `wholesale.distributors.acceptance_started_at` (columna nullable, migración aditiva
    `AddDistributorAcceptanceStartedAt`). `AcceptAsync` la confirma después de las validaciones que rechazan sin escribir y
    antes de la primera escritura en otro módulo; sube `version`, así que un `PUT` simultáneo pierde con 412 o ve la marca, y si
    el `PUT` confirmó primero, la aceptación relee y vuelve a validar (hasta 3 veces; después 412 `concurrency.conflict`). El 409
    sale con la marca **o** con el tenant reservado existente (aceptaciones a medias anteriores a la columna). La invitación se
    sigue consumiendo sólo en el último commit. Límites: la marca no se borra (para corregir el documento tras un fallo posterior
    hay que borrar la invitación e invitar de nuevo), durante el rollout un pod viejo ni la pone ni la mira, y una vinculación a
    medias anterior a la migración no se detecta hasta que se reintente.
- Al exigir la ciudad, **el formulario actual deja de poder invitar en Colombia** hasta que salga el frontend: backend y frontend se
  despliegan juntos (decisión pendiente del owner).

## Pruebas

- **Unitarias de Wholesale:** validador de invitar con ciudad por país; el handler de invitar responde
  `wholesale.distributor.city_not_found` con una ciudad que el puerto no encuentra; `Distributor.Invite` guarda ciudad y dirección;
  `AcceptAsync` con `NewTenantChoice` llama al puerto de empresa con los datos esperados, llama al camino «primera empresa» con
  `LinkTenantChoice` sobre un tenant existente (Companies decide si ya tenía), y llama a `EnsureCompanyAsync` (no al camino «primera empresa») en un reintento D-17 con cuerpo `LinkTenant`; una invitación sin ciudad no llama al
  puerto y acepta igual; el cliente recibe el teléfono.
- **Unitarias de Companies:** `CompanyProvisioning` crea, reutiliza por documento exacto (también una inactiva), no confunde un
  documento que sólo contiene al otro, rechaza ciudad ausente en `CO` con las claves de `CompanyWriteRules`, y siembra tasas igual
  que el handler. Las pruebas existentes de `CreateCompanyHandler` siguen verdes.
- **Integración (filtradas mientras se itera, clase completa una vez por bloque):** aceptar en Colombia crea la empresa en el tenant
  nuevo y siembra el IVA; reintentar el accept no duplica; dos accepts a la vez dejan una sola empresa; el cliente nace con
  teléfono; invitar con una ciudad inexistente da 422; el detalle devuelve ciudad y dirección.
- Arquitectura: `ArchitectureTests` de Wholesale y Companies siguen verdes.

## Decisiones pendientes del owner

1. ~~¿Se amplía `RegisterCustomerData` para copiar también ciudad, dirección y contacto al cliente?~~ Resuelta el 2026-11-24: sí, ver la nota de arriba.
2. ~~¿Se permite editar los datos del distribuidor (ciudad, dirección, razón social) mientras está invitado?~~ Resuelta el
   2026-11-24: sí, ver la nota de arriba.
3. ~~¿Aceptar vinculando un tenant existente crea la empresa?~~ Resuelta por el owner (2026-11-24): sólo si el tenant no tiene ninguna.
4. ¿Backend y frontend se despliegan juntos, o se abre una ventana con la ciudad opcional al invitar (las invitaciones sin ciudad
   aceptarían sin empresa, como las viejas)?
5. ¿Las invitaciones vivas sin ciudad se quedan sin empresa al aceptar (propuesta actual), o el mayorista debe re-invitar?

## Revisión (2026-11-23)

Se verificó cada afirmación contra `DistributorInvitationService`, `WholesaleInvitationEndpoints`, `HttpExecutionContext`,
`CreateCompanyHandler`, `CompaniesAuthorization`, `CompanyWriteRules`, `CompaniesUnitOfWork`, `CompanyRepository.SearchAsync`,
`ICustomerProvisioning`, `WholesaleCustomerProvisioning`, `ShopifySetupCompanies`, `CompanyDefaultTaxRates`,
`TenantRegistrationService.RegisterInvitedTenantAsync`, `Distributor`, `InviteDistributorValidator` y `WholesaleLayerTests` /
`CompaniesLayerTests`. El enfoque (puerto sin permiso, hermano de `ICustomerProvisioning`, adaptador en Bootstrapper) es correcto y
encaja con las capas. Se corrigió:

- **Condición del paso:** decía «`created` y `NewTenantChoice`». En un reintento D-17 el cuerpo puede ser `LinkTenant` y
  `created` es `true`: con esa condición la empresa no se crearía nunca tras un primer intento fallido. Ahora es sólo `created`.
- **Validador:** «incluye o replica» → replica; incluir es imposible por visibilidad y por capas.
- **Ciudad inexistente:** no se validaba al invitar, y el error del mayorista bloquearía cada accept. Se agrega
  `IWholesaleCityLookup` y `wholesale.distributor.city_not_found`.
- **Idempotencia:** «busca por documento» no alcanzaba: no hay índice único, el filtro es `ILIKE` y dos pestañas pueden llegar a la
  vez. Se agrega candado por tenant, comparación exacta y reuso también de una inactiva.
- **Autorización:** se confirmó que el handler de Companies no sirve en el accept (sin claim de tenant, 500; con otra, 403).
- **Arquitectura:** la prueba nueva de Companies que proponía ya existe.
- **DTO:** el resumen no cambia; el detalle devuelve la ciudad resuelta.
- **Teléfono:** es el del aviso y puede ser `null`.
- **Datos del tenant:** el nombre sale del cuerpo del accept o de la razón social. La regla de `mayorista-compras.md` que dice que
  `NewTenant` no lee nombre ni slug del cuerpo está desactualizada respecto del código (gana el código; corregir la regla queda
  fuera de este trabajo).
- Se agregaron el contrato HTTP, el riesgo del `QuotationsDbContext` y las decisiones pendientes 4 y 5.
