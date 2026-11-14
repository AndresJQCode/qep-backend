# Soporte (PQRSF y soporte técnico) — diseño

Fecha: 2026-11-14. Estado: borrador para aprobación del owner. Cruza `qep-backend` y `qep-frontend`
(consola del tenant) más una página pública que sirve el mismo SPA en otro host.

Convención de marcas, como en los demás specs:

- **[owner]**: decisión que tomó el owner en la conversación del 2026-11-14.
- **[diseñador]**: propuesta de este documento, para revisarse.
- **DECISIÓN PENDIENTE**: nadie la ha tomado; el plan no puede asumirla.

Hay un prototipo navegable del backoffice en https://claude.ai/artifact/GYSwnp8CmfpEurFUhrgaqG (versión 9).
Ilustra las pantallas, pero este spec no depende de él: si discrepan, manda el spec.

## 1. Intención

Que un tenant gestione en QEP las **peticiones, quejas, reclamos, sugerencias y felicitaciones**
(PQRSF) de sus clientes, y también el **soporte técnico**, con trazabilidad, plazos que corren solos
y una respuesta formal cuando la ley la pide. Hoy esa gestión vive en hojas de cálculo, en el
WhatsApp personal del asesor o en Lulo Desk (`desk.lulocrm.com/pqrsf`), que QEP reemplaza.

La pieza central es el **ticket**: un caso con radicado, tipo, estado, responsable, reloj de
resolución y un hilo de seguimiento. Se abre desde tres puertas: la ficha de un contacto de la
bandeja de Mensajería, la creación manual del asesor y un formulario público por tenant, anónimo si
el solicitante lo quiere.

Por qué ahora: Colombia exige términos de respuesta para las peticiones (Ley 1755 de 2015), y un
tenant que atiende por WhatsApp ya tiene la conversación en QEP; lo que falta es el expediente que
la ordene.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- Un solo módulo, **«Soporte»**, para PQRSF y soporte técnico [owner, D1, D7].
- Ticket con radicado propio por tenant, máquina de estados cerrada y reloj de resolución en días
  hábiles que se detiene cuando se espera al cliente o el caso está resuelto, nunca por una espera interna.
- Abrir un ticket desde la ficha del contacto de Mensajería, vincular al ticket los mensajes del
  hilo que correspondan y responder por el canal de origen.
- Formulario público por tenant, con su logo, sus tipos de ticket publicados y sus textos legales de
  tratamiento de datos, con opción de trámite anónimo.
- Respuesta formal en PDF cuando el caso la exige, y encuesta de satisfacción al cerrar.
- Auditoría de cada cambio y plazos visibles para el asesor (por vencer, vencido).

**No objetivos (primera versión)**

- Campos propios por tenant en el formulario [owner]. El tenant muestra, oculta y exige los fijos.
- Constructor visual de formularios.
- Agentes de IA que respondan tickets.
- Correo entrante como puerta (un correo que se convierte en ticket). El correo existe sólo como
  canal de **respuesta** elegido a mano.
- Reabrir un ticket `Cerrado`.
- Integración con la SIC ni radicación electrónica ante entidades.

**Anti-objetivos (fallaría aunque «funcione»)**

- Que un ticket pierda la prueba de quién aceptó qué texto legal y cuándo.
- Que el formulario público sirva para listar o adivinar tickets ajenos (ver §9.4: no se replica la
  búsqueda pública por nombre o correo de Lulo Desk).
- Que el reloj corra mientras el caso espera al cliente, o se detenga por una espera interna.
- Que un mensaje basura del cliente reanude un reloj o se cuele en un expediente sin que el asesor
  lo decida.
- Que el módulo Soporte conozca tablas de Messaging, Customers o Store por nombre sin una
  excepción escrita y amarrada por pruebas.

## 3. Restricciones verificadas en el código

Lo siguiente se leyó en el repo el 2026-11-14; las rutas son de `qep-backend` salvo indicación.

- **Plantilla de módulo: Loans.** Cuatro assemblies bajo `src/Modules/Loans/` (`Modules.Loans.Api`,
  `.Application`, `.Domain`, `.Infrastructure`). Permisos en `LoansPermissions.cs:7` y `:9`
  (`loans.loan.read` y `loans.loan.manage`). Auditoría por puerto propio: `ILoansAuditPublisher`
  (`ILoansAuditPublisher.cs`). Registro en `QepServiceCollectionExtensions.cs:1145`
  (`AddLoansInfrastructure`) y `Program.cs:329` (`MapLoansEndpoints`). Capas amarradas por
  `tests/ArchitectureTests/ArchitectureTests/LoansLayerTests.cs`.
- **Módulos y entitlements.** `TenantModuleKey` en `Modules.Tenancy.Domain/TenantModuleKey.cs`:
  `Loans` en la línea 77, lista `All` en 88 a 89, `DefaultForNewTenants` en 92, dependencias desde
  la 96 (`[Loans] = [Customers]` en la 109). Agregar una clave exige una migración de Tenancy que
  reescribe `CK_tenant_modules_module_key`; el precedente más reciente es
  `20261112055320_AddAiModuleKey.cs`.
- **Permisos, dos mitades.** La constante y su política: `AddAuthorization` empieza en
  `QepServiceCollectionExtensions.cs:2676`; las políticas de Loans están en 2984 a 2988 con el
  comentario «sin estas políticas, RequireAuthorization no resuelve y el síntoma es 500, no 403».
  Además, cada permiso se declara con `PermissionDefinition` y `RequiredModules`
  (`QepServiceCollectionExtensions.cs:2547` a `2556` para Loans) y se concede a los roles de
  sistema (1822 a 1823 y 1926 a 1927 para Loans). `ModuleEntitlementMask` (Authorization.Application)
  quita el permiso cuando el módulo está apagado.
- **Mensajería.** `ConversationEventType` está en
  `Modules.Messaging.Domain/ConversationEvent.cs:4`; el último valor es `AutomationContinued`, se
  guarda por nombre y los valores nuevos van al final. El evento es un `record` con ids; los nombres
  se resuelven al leer. `Details` es `jsonb` (`MessagingDbContext.cs:361`).
- **Lectura entre módulos.** `PipelineConversationDirectory`
  (`src/Bootstrapper/PipelineConversationDirectory.cs:13`) lee conversaciones desde el
  `MessagingDbContext`, sin tracking: es el modelo para que Soporte lea Mensajería sin referenciarlo.
- **Clientes: fusión y borrado.** `CustomerMergeReferences` (`src/Bootstrapper/CustomerMergeReferences.cs:30`)
  y `CustomerDeletionReferences` (`src/Bootstrapper/CustomerDeletionReferences.cs:43`) conocen por
  nombre las tablas de cada módulo. Los motivos que bloquean el borrado están en
  `CustomerDeletionBlockReasons` (`Modules.Customers.Application/BulkDeleteCustomers.cs:31` a `41`) y
  se consultan en `FindBlockersCoreAsync` (`CustomerDeletionReferences.cs:83`). El candado por
  cliente es `CustomerReferenceLock` (`BuildingBlocks.Infrastructure/CustomerReferenceLock.cs:21`).
- **Errores.** El mapeo central está en `src/Api/ApiExceptionHandler.cs`; el mapa `errors` del 422
  se arma en las líneas 66 a 90 y `validation.failed` en la 186.
- **Slug del tenant.** `Tenant.Slug` (`Modules.Tenancy.Domain/Tenant.cs:46`), validado por
  `ValidateSlug` (`Tenant.cs:365`): 3 a 63 minúsculas, dígitos o guiones. **No hay lista de
  reservados.** La del slug de tienda sí existe: `StoreSlug.Reserved`
  (`Modules.Store.Domain/StoreSlug.cs:12` a `16`) e incluye `soporte`.
- **País y zona horaria del tenant.** `Tenant.Country` (`Tenant.cs:67`, ISO-3166-1 alfa-2, por
  defecto `CO`; hoy informativo, ninguna regla lo lee) y `Tenant.TimeZone` (`Tenant.cs:54`).
- **Hosts de producción.** Hoy la consola se sirve en `https://qep.qcode.co` y la API en
  `qep-api.qcode.co` (`k8s/prod-ingress.yaml:21` a `24`; `Cors__AllowedOrigins__0` en
  `k8s/prod-configMap.yaml:50`). Los docs prevén pasar a `app.lulocrm.com` y `api.lulocrm.com`.
- **CORS.** Hay una sola política, `spa` (`src/Bootstrapper/Cors/CorsServiceCollectionExtensions.cs`
  líneas 27 a 45): lista exacta de `Cors:AllowedOrigins`, con `AllowCredentials`. Se aplica a toda la
  API (`Program.cs:216` a `222`). La cookie de sesión es `SameSite=Lax` (`src/Api/SessionCookieWriter.cs:28`):
  con la API en `qep-api.qcode.co`, `soporte.lulocrm.com` es otro sitio; con la API en
  `api.lulocrm.com`, sería el mismo sitio y la cookie viajaría.
- **CSRF.** `RequireCsrfHeaderMiddleware` (`src/Bootstrapper/Csrf/RequireCsrfHeaderMiddleware.cs:34`)
  exige `X-Qep-Client` en todo método que muta, salvo excepciones por ruta (líneas 60 a 70: webhooks,
  API externa, `StorefrontPaths`, firmadas).
- **Limitador.** La política `Public` es 120 por minuto por IP (`Program.cs:100` a `106`).
- **Anti-bot.** Existe `ICaptchaVerifier` (`Modules.Store.Application/ICaptchaVerifier.cs`, Cloudflare
  Turnstile, con `expectedHostname`) y su adaptador hacia Buyers (`src/Bootstrapper/BuyerCaptcha.cs:11`).
- **Horario y festivos.** `IBusinessHoursRepository.FindDefaultAsync`
  (`Modules.Messaging.Application/BusinessHoursPorts.cs:7`), `BusinessHoursClock.BusinessSeconds`
  (`Modules.Messaging.Domain/BusinessHoursClock.cs:9`, cuenta **segundos hábiles**, no días) y
  `ColombianHolidays.For(year)` (`Modules.Messaging.Domain/ColombianHolidays.cs:12`).
- **Plantillas de WhatsApp del sistema.** `MessageTemplateKind` (`Modules.Messaging.Domain/MessageTemplate.cs:6`
  a `20`: `Free`, `Quotation`, `External`, `BillingReminder`, `BillingOverdue`, `Order`),
  `SystemTemplateVariables.cs:9`, `SystemTemplateSend.cs:8` y el puerto de salida
  `IBillingTemplateOutbound` (`BillingTemplateOutbound.cs:33`).
- **Archivos.** `IMessagingMediaStore` (`Modules.Messaging.Application/IMessagingMediaStore.cs:8`) para
  adjuntos del hilo; el módulo Storage (`IStorageUnitOfWork.cs:3`) para archivos de otros módulos.
- **PDF.** `IQuotationPdfRenderer` se registra en
  `Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:95`; el servicio es
  `qcode-pdf` (Typst, `QuotationsOptions.cs:44` a `50`, `BaseUrl` por defecto
  `https://qcode-pdf.qcode.co`); en producción exige `Quotations:Pdf:ApiKey`
  (`QuotationsOptionsValidator.cs:23` a `29`). La plantilla Typst viaja como recurso de
  `Modules.Quotations.Infrastructure.csproj`.
- **Precedentes de consentimiento.** `customers.data_consents` es prueba legal insert-only con foto
  del texto (migración `20261111074351_DetachDataConsentsFromCustomers.cs`,
  `Modules.Customers.Application/CustomerDataConsents.cs`). Store guarda sólo una URL de política:
  `StoreConsent(At, Ip, PolicyUrl, PolicySetAt)` (`Modules.Store.Domain/StoreConsent.cs:9`). Buyers
  usa una versión fija: `BuyerDataPolicy.Version` (`Modules.Buyers.Domain/BuyerDataPolicy.cs`).
- **Documento de identidad.** `DocumentTypesByCountry` (`src/BuildingBlocks/BuildingBlocks.Domain/Identification/DocumentTypesByCountry.cs:8`)
  con `All` (línea 16), `AllowedFor(country)` (21) e `IsAllowed(type, country)` (34). Colombia:
  `NIT`, `CC`, `CE`, `PASAPORTE`; España: `NIF`, `NIE`, `PASAPORTE`; el resto: `TAX_ID`, `PASAPORTE`.
  La búsqueda de un cliente por documento es `FindExistingIdentificationsAsync`
  (`Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs:390`), que otros módulos
  consumen por adaptadores de Bootstrapper (`PosCustomerLookup.cs:47`, `StoreCustomerDirectory.cs:167`
  y `:177`, `ShopifyCustomerDirectory.cs:46`).
- **Persona natural y jurídica en Customers.** `PersonType` (`Natural`, `Juridica`) en
  `Modules.Customers.Domain/PersonType.cs`. `Customer.Name` es la persona de contacto y
  `Customer.BusinessName` la razón social (`Customer.cs:135` a `147`). `EnsurePersonTypeMatches`
  (`Customer.cs:1395`) exige `NIT` para `Juridica` **sólo en Colombia**
  (`CustomersErrorCodes.PersonTypeRequiresNit`, `CustomersErrorCodes.cs:26`). La regla no obliga lo
  inverso: **un NIT también puede ser de una persona natural.** No encontré en Customers una
  validación ni una columna del dígito de verificación del NIT.
- **Frontend** (`qep-frontend`, rama `develop`, commit `8725140b` al escribir esto):
  `src/features/pipeline/hooks/use-pipeline-access.ts:20` (`usePipelineAccess`, el patrón para
  gatear una pestaña por módulo y permiso), `src/features/messaging/components/contact-sheet.tsx`
  (la ficha del contacto), `src/features/pipeline/components/deal-strip.tsx` y `deal-sheet-host.tsx`
  (el panel global del negocio). Cómo se abre ese panel (parámetro de URL o estado) **no se
  verificó**: el implementador lo lee en `deal-sheet-host.tsx` antes de copiar el patrón.

## 4. Marco legal (contexto, no asesoría)

Este documento no es asesoría jurídica. Los plazos son **parámetros configurables** con un valor por
defecto; el contenido legal que ve el solicitante es del tenant, no de QEP.

- **Ley 1755 de 2015, art. 14.** Peticiones en general: 15 días hábiles. Peticiones de documentos o
  información: 10 días hábiles. Consultas a las autoridades: 30 días hábiles. Cuando no sea posible
  resolver en el plazo, se informa antes de que venza, con los motivos, y se señala un nuevo plazo
  razonable, que **no puede exceder el doble** del inicial (prórroga única). Una petición
  incompleta se puede requerir para que se complete; si el solicitante no lo hace en el plazo que la
  ley fija, se entiende desistida. **DECISIÓN PENDIENTE (verificación):** el artículo exacto de ese
  requerimiento y su plazo (la investigación previa anotó 10 días para requerir y un mes para
  completar) no están confirmados contra el texto de la ley; hay que validarlos con el abogado antes
  de fijar los valores por defecto.
- **A quién aplica.** Que un tenant privado esté obligado a estos términos, y para qué tipos de ticket,
  **DECISIÓN PENDIENTE** de confirmar con el abogado del owner. Por eso los términos son
  configurables por tipo de ticket (§7.5), y cada tipo dice si su término es legal o sólo interno.
- **Ley 1480 de 2011 (Estatuto del Consumidor).** La reclamación directa al proveedor precede a la
  queja ante la SIC. El plazo de respuesta de 15 días **no está confirmado**: queda como parámetro
  configurable sin valor impuesto por QEP.
- **Ley 1581 de 2012 (protección de datos).** Sólo contexto: quien recoge datos personales necesita
  autorización previa, expresa e informada y debe poder probarla. El diseño guarda esa prueba (§9.5);
  el texto lo redacta el tenant.

## 5. Enfoques considerados

**5.1 Un módulo o dos**

| Enfoque | Qué sacrifica |
| --- | --- |
| A. Un módulo `Soporte` con un solo agregado `Ticket`, categoría y origen como campos | La categoría `soporte técnico` convive con las PQRSF y comparte reglas de plazo que no le aplican; se resuelve con términos por categoría. |
| B. Dos módulos: PQRSF y tickets técnicos | Duplica estados, reloj, hilo, respuesta y reportes; el asesor ve dos bandejas. |
| C. Un ticket como tipo de conversación dentro de Mensajería | Ata el expediente al canal: un caso del formulario público o creado a mano no tiene conversación. Mensajería ya es el módulo más grande. |

**Elegido por el owner: A** [owner, D1].

**5.2 Dónde vive el formulario público**

| Enfoque | Qué sacrifica |
| --- | --- |
| A. Bajo el host de la tienda | Sólo sirve a tenants con tienda. |
| B. `{slug}.lulodesk.com` | Dominio nuevo, DNS y certificado comodín, otra lista de reservados. |
| C. `soporte.lulocrm.com/{tenantSlug}` | Los slugs de tenant compiten con rutas fijas de la raíz; exige lista de reservados. |

**Elegido por el owner: C**, mismo SPA con otro host [owner, D6]. Es reversible: la API recibe siempre
`{tenantSlug}`, así que pasar a subdominios o a dominios propios del tenant sólo cambia cómo el SPA
saca el slug (del primer segmento de la ruta al primer segmento del host).

**5.3 Qué tanto diseña el tenant el formulario**

Constructor libre (descartado: es otro proyecto), campos propios acotados (descartado por el
owner para la primera versión) o **formulario fijo con parámetros** [owner]. Ver §9.3.

## 6. Decisiones del owner

| # | Decisión |
| --- | --- |
| D1 | PQRSF y soporte técnico en **un solo módulo**. |
| D2 | Términos por defecto **15 / 10 / 30 días hábiles**, reutilizando el horario de atención (`business_hours`) de Mensajería para saber qué días son hábiles. |
| D3 | La **respuesta de fondo** se envía como PDF; su contenido es el de §8.3. |
| D4 | Un ticket **bloquea el borrado del cliente** («lo bloquea»). Cualquier ticket, abierto o cerrado, como cotizaciones y pedidos; la fusión mueve los tickets al cliente que queda. |
| D5 | Satisfacción por **plantilla aprobada o según el canal** (§8.4). |
| D6 | Formulario público en `https://soporte.lulocrm.com/{tenantSlug}`, mismo SPA, con `Tenant.Slug`; los slugs de tenant ganan una lista de reservados. `lulodesk.com` descartado por ahora. |
| D7 | El módulo se llama **«Soporte»** en pantalla. «PQRSF» queda como título del formulario público. |
| — | El formulario **no lleva campos propios** por tenant. |
| — | Los textos legales de cada tenant son **versionados e insert-only**; Soporte tiene los suyos y no lee `Store.DataPolicyUrl`. |
| — | El formulario público lleva **tipo y número de documento como un par**, configurable (se muestra u oculta y se exige junto); en anónimo no se piden. Sólo el **NIT** cambia los campos de identidad (§9.3). |
| — | Las categorías son **tipos de ticket parametrizables por tenant**, sembrados con valores por defecto (§7.5). |
| — | La **encuesta de satisfacción** se activa o desactiva por tenant (§8.4). |
| — | Quien tenga `support.ticket.read` o `support.ticket.manage` ve **todos** los tickets del tenant (§10). |
| — | El ticket se **vincula al cliente por tipo y número de documento** (§9.3). |
| — | **PIN de consulta:** en el formulario web se muestra una vez al radicar y, si no es anónimo, se envía al correo, nunca por WhatsApp; en un ticket creado desde una conversación, la información del ticket con el PIN se envía por ese canal (§9.4). |

Además, de las revisiones del prototipo [owner]: estados y transiciones de §7.1, `espera_interna`
no detiene el reloj, el cambio de estado es una acción y no una pestaña (mirar nunca escribe en el
hilo), no se vuelve a `Nuevo`, el descarte de mensajes no clasificados, y el interruptor «Seguir el
hilo (canal)» por ticket. El owner también decidió servir `soporte.lulocrm.com` con el mismo SPA en
Vercel, como un dominio más.

**[coordinador]** marca las decisiones del coordinador de la sesión, revisables por el owner: el
reloj de §7.2, los festivos por país, la encuesta y el cierre automático de §8.4, las plantillas de
§8.5, la respuesta formal por tipo de ticket de §8.3, la política CORS aparte de §10 y los detalles
de la búsqueda del cliente de §9.3. La visibilidad de todos los tickets es del owner.

Decisiones del coordinador que cierran lo que bloqueaba el plan, revisables por el owner:

| # | Decisión |
| --- | --- |
| C1 | Permisos `support.ticket.read`, `support.ticket.manage` y `support.settings.manage`; admin los tres, asesor `read` y `manage` (§10). |
| C2 | «Seguir el hilo» **no** se apaga solo al entrar a `espera_interna`: queda manual en la primera versión (§8.2). |
| C3 | Encuesta de tres opciones, Buena, Regular y Mala, con botones de respuesta rápida; comentario libre opcional en el mensaje siguiente (§8.4). |
| C4 | Sin dígito de verificación del NIT en la primera versión: el número se guarda normalizado como lo escribe el solicitante (§9.3). |
| C5 | Los demás campos obligatorios son fijos; sólo la razón social con NIT es configurable. Si el tenant oculta el par de documento, no hay vínculo automático y el asesor vincula a mano (§9.3). |
| C6 | Sin campo «Empresa» en la primera versión (§9.3). |
| C7 | País y ciudad salen del módulo Geography, como en el resto del sistema (§9.3). |
| C8 | Un solo campo de término por tipo, también para soporte técnico; la marca «término legal» sólo cambia los textos del PDF y del vencimiento, no el cálculo (§7.5). |
| C9 | Soporte no depende de otros módulos para prenderse (§10). |

## 7. Dominio

### 7.1 Estados y transiciones

Seis estados: `nuevo`, `abierto` (se muestra «En gestión»), `espera_cliente`, `espera_interna`,
`resuelto` y `cerrado`.

| Desde | Puede pasar a |
| --- | --- |
| `nuevo` | `abierto`, `espera_cliente`, `resuelto` |
| `abierto` | `espera_cliente`, `espera_interna`, `resuelto` |
| `espera_cliente` | `abierto`, `espera_interna`, `resuelto`, `cerrado` |
| `espera_interna` | `abierto`, `espera_cliente`, `resuelto` |
| `resuelto` | `cerrado`, `abierto` (se muestra «Reabrir (En gestión)») |
| `cerrado` | nada (terminal) |

- `nuevo` sólo existe al nacer. Ningún estado vuelve a él.
- `cerrado` desde `espera_cliente` se presenta como «Cerrar por desistimiento».
- El dominio rechaza cualquier transición fuera de la tabla con un código de dominio (422). El
  nombre del código es **decisión del implementador**, con el prefijo del módulo.
- El estado se cambia con una acción explícita (insignia más selector). Cada cambio escribe un
  evento en la traza del ticket; listar o filtrar por estado no escribe nada.

### 7.2 Reloj de resolución

- Cada ticket tiene un vencimiento calculado en **días hábiles** a partir del término de su
  tipo, fotografiado al radicar (§4, §7.5 y D2). Un día es hábil si el horario predeterminado del tenant atiende ese día
  de la semana y no es festivo. Sin horario configurado se usan lunes a viernes. [diseñador]
- **Festivos por país** [coordinador]: con `Tenant.Country = CO` se usa `ColombianHolidays`; con
  otro país, lunes a viernes sin festivos, hasta que exista un calendario de ese país.
- **Inicio y fin** [coordinador]: el plazo cuenta desde el **día hábil siguiente** a la radicación
  (Ley 1755 art. 14, «siguientes a su recepción») y vence al **final del día hábil**, en la zona
  horaria del tenant (`Tenant.TimeZone`).
- **Cuándo se detiene** [coordinador]: en `espera_cliente`, `resuelto` y `cerrado`.
  `espera_interna` **no** lo detiene [owner]: la espera es del tenant, y es justo el caso en el que
  el plazo sigue corriendo. Al salir de una pausa (incluido reabrir desde `resuelto`) el reloj se
  reanuda **desde donde quedó**: el vencimiento se corre por los días hábiles que estuvo detenido.
- **Cambio de tipo**: recalcula el vencimiento con el término del tipo nuevo [owner], contado
  **desde la radicación**, más las pausas acumuladas [coordinador]. Editar un tipo no mueve los
  vencimientos ya calculados (§7.5) [owner].
- **Prórroga** [coordinador]: única, con motivo obligatorio y aviso al solicitante antes de que
  venza el plazo; el nuevo plazo **no excede el doble del término inicial**. Es una interpretación
  a validar con el abogado, así que el tope es configurable. Queda en la traza.
- `BusinessHoursClock.BusinessSeconds` cuenta segundos, no días. El cálculo en días hábiles es
  código nuevo; el adaptador de Bootstrapper expone a Soporte sólo «¿es hábil esta fecha?» y
  «suma N días hábiles». Soporte no referencia Mensajería.
- **DECISIÓN PENDIENTE:** si el tenant no atiende sábados pero la ley cuenta los días hábiles
  según el calendario general, el horario de la bandeja puede no ser el calendario legal correcto.
  El owner eligió reutilizarlo (D2); el abogado debe confirmar que los términos pueden contarse con
  el horario del tenant y no con lunes a viernes.

### 7.3 Ticket

Campos propuestos [diseñador]; la lista exacta la cierra el plan.

- Identidad: `id`, `tenant_id`, radicado `SOP-000127` (prefijo más contador de seis dígitos **por
  tenant**; el contador se asigna en la misma transacción, con unicidad `(tenant_id, number)`).
- `ticket_type_id` y la **foto del tipo al radicar**: nombre, término en días hábiles, si el término
  es legal y si exige respuesta formal (§7.5).
- `intake`: cómo llegó (formulario público, ficha del contacto, manual; el manual puede declarar el
  medio real: teléfono, presencial, etc.).
- `status`, `assignee_membership_id`, vencimiento, instantes de pausa y de prórroga.
- `customer_id` **nullable** y `requester` (foto del solicitante: nombres y apellidos o razón
  social, correo, teléfonos, país, ciudad, dirección, tipo y número de documento). En un ticket
  anónimo ambos son nulos.
- `reply_channel` (hilo abierto, WhatsApp por plantilla o correo) y `conversation_id` nullable.
- `follow_thread` (bool, por defecto verdadero) y `follow_off_at`.
- `version` para concurrencia optimista, como el resto del repo.

### 7.4 Traza y auditoría

Cada cambio de estado, asignación, prórroga y vínculo se registra como evento del ticket. Además,
auditoría operativa con un puerto propio (`ISupportAuditPublisher`, hermano de
`ILoansAuditPublisher`), en la misma transacción que el cambio.

### 7.5 Tipos de ticket y términos

[owner] Las categorías son **tipos de ticket parametrizables por tenant**: el tenant los crea, edita,
desactiva y ordena. No hay subtipos: cada tipo es una fila propia con:

- nombre;
- término en días hábiles (un solo campo, también para soporte técnico) [coordinador];
- si el término es **legal** o sólo interno [coordinador];
- si exige respuesta formal en PDF (§8.3);
- si se publica en el formulario público;
- si está activo.

QEP siembra estos tipos al prender el módulo, y el tenant los edita [owner]:

| Tipo | Término | Término legal | Respuesta formal exigida |
| --- | --- | --- | --- |
| Petición general | 15 días hábiles | Sí | Sí |
| Petición de documentos o información | 10 días hábiles | Sí | Sí |
| Consulta | 30 días hábiles | Sí | Sí |
| Queja | 15 días hábiles | Sí | Sí |
| Reclamo | 15 días hábiles | Sí | Sí |
| Sugerencia | 15 días hábiles | Sí | No |
| Felicitación | 15 días hábiles | Sí | No |
| Soporte técnico | Sin término | — | No |

La columna «respuesta formal exigida» sigue la propuesta del coordinador de §8.3. Que se publiquen
todos en el formulario por defecto lo propone el diseñador.

- **Foto al radicar** [owner]: el ticket guarda el tipo y su término de ese momento. Editar un tipo
  no mueve los vencimientos ya calculados.
- **Cambiar el tipo de un ticket** recalcula el vencimiento con el término del tipo nuevo (§7.2) [owner].
- **Un tipo usado no se borra: se desactiva** [owner]. Un tipo desactivado no se ofrece al crear
  tickets ni en el formulario, y los tickets que lo usan lo conservan.
- La siembra es idempotente por tenant y corre al prender el módulo. Cómo se dispara (evento de
  Tenancy o primer acceso, como el embudo de Pipeline) lo decide el implementador.
- **Término legal o interno** [coordinador]: hay un solo campo de término por tipo, y el tenant puede
  darle término también a soporte técnico. La marca «término legal» sólo cambia los textos del PDF
  y del vencimiento (por ejemplo «vence el plazo legal» frente a «vence el objetivo»), no el
  cálculo. Un tipo sin término no tiene vencimiento.

## 8. Mensajería, respuesta y satisfacción

### 8.1 Abrir un ticket desde la ficha del contacto

La ficha del contacto (`contact-sheet.tsx`) gana una pestaña «Soporte» gateada por módulo y
permiso, con el patrón de `usePipelineAccess`. Desde ahí el asesor crea un ticket para el cliente
de esa conversación; el ticket nace con `conversation_id` y `reply_channel = hilo abierto`.

Soporte lee las conversaciones por un directorio de Bootstrapper sobre `MessagingDbContext`, como
`PipelineConversationDirectory` (`Bootstrapper/PipelineConversationDirectory.cs:13`). Soporte nunca
escribe en tablas de Mensajería: lo que necesita escribir (un mensaje de respuesta, una apertura por
plantilla, un evento del hilo) entra por puertos de Mensajería con adaptador en Bootstrapper.

### 8.2 Vinculación de mensajes al ticket

Los mensajes del hilo no cambian de dueño: Soporte guarda **su propia tabla de vínculos**
`(tenant_id, ticket_id, message_id, estado)` en su esquema. [diseñador]

Reglas [owner]:

- Mientras el ticket sigue el hilo, los mensajes entrantes de esa conversación posteriores a la
  creación del ticket se vinculan **solos y sin fijar**: no exigen acción del asesor.
- El asesor puede **fijar** un mensaje (queda en el expediente, siempre visible) o **soltarlo**.
- Un mensaje vinculado y no fijado aparece plegado en la línea de tiempo; uno fijado, en línea.
- Si la conversación tiene **dos o más tickets abiertos que siguen el hilo**, un entrante no se
  puede asignar solo: queda **sin clasificar** y la línea de tiempo pregunta «¿De qué ticket es?».
  El asesor lo asigna a uno (lo fija) o elige **«De ninguno»**, que lo descarta y deja una nota en
  los tickets abiertos.
- Cuando queda un solo ticket que sigue el hilo, los sin clasificar pasan a él.
- **«Seguir el hilo (canal)» por ticket**, activo por defecto. Apagado, el ticket deja de vincular
  entrantes desde `follow_off_at`: el caso es que el tenant tiene 15 días para responder y no se
  quiere desfijar a mano cada mensaje del cliente. Con el interruptor apagado el reloj no se
  reanuda por mensajes. El texto de la franja es «Este ticket no sigue el hilo (WhatsApp) desde …».
- Un entrante vinculado mientras el ticket está en `espera_cliente` lo pasa a `abierto` y reanuda
  el reloj.
- El interruptor **no** se apaga solo al entrar a `espera_interna`: es manual en la primera versión
  [coordinador].

La conversación recibe eventos nuevos al **final** de `ConversationEventType`
(`ConversationEvent.cs:4`; se guarda por nombre). Propuesta [diseñador]: `TicketOpened` y
`TicketClosed`, con el id del ticket en un campo nuevo y opcional del `record`. El nombre y la
forma exacta los cierra el plan.

### 8.3 Respuesta al solicitante

- La respuesta va al canal de origen: el hilo abierto de WhatsApp o Instagram, WhatsApp por
  plantilla si la ventana de 24 horas ya cerró, o correo. El asesor ve el canal antes de enviar.
- **Creación manual:** elige el medio de recepción y el canal de respuesta (hilo abierto, WhatsApp
  nuevo por plantilla o correo). Las opciones sin dato (sin teléfono, sin correo) salen
  deshabilitadas. Un ticket cuyo canal es WhatsApp y que no tiene conversación crea una al enviar,
  con la plantilla de apertura de Soporte.
- **Adjuntos del asesor.** En el hilo viajan por Mensajería (`IMessagingMediaStore`, R2). Los de las
  notas internas van por Storage. La respuesta formal lleva un PDF generado.
- **Cuándo es formal** [coordinador]: cada tipo de ticket dice si exige respuesta formal (por
  defecto sí para petición general, petición de documentos o información, consulta, queja y
  reclamo; ver §7.5). El asesor puede elegirla en cualquier tipo. Enviarla **no resuelve el ticket sola**: el compositor ofrece «Enviar y
  marcar Resuelto».
- **Fuera de la ventana de 24 horas** de WhatsApp, la respuesta sale con la plantilla de aviso de
  respuesta (§8.5).
- **Respuesta de fondo en PDF** (D3), generado con Typst en `qcode-pdf`. Contenido:
  1. Membrete del tenant: logo, razón social, NIT, dirección y correo.
  2. Radicado, fecha de radicación y fecha de respuesta.
  3. Destinatario: nombre o razón social, documento, correo o dirección.
  4. Asunto y referencia («Respuesta a su reclamo SOP-000127 del …»).
  5. Resumen de lo que pidió el solicitante (asunto y descripción del ticket).
  6. Análisis y decisión: el único texto libre, que escribe el asesor.
  7. Paso siguiente: qué puede hacer el solicitante si no está de acuerdo (por ejemplo, la SIC en
     consumo, tras la reclamación directa). Texto configurable por tenant.
  8. Firma del responsable (nombre y cargo) y lista de anexos.

  El PDF se adjunta al canal de origen y queda descargable en la consulta por radicado.
- **DECISIÓN PENDIENTE:** `qcode-pdf` identifica al cliente por el hash de la clave. Soporte
  necesita su propia clave y sección de configuración (`Support:Pdf:*`) y no debe reutilizar
  `Quotations:Pdf:ApiKey`, para no mezclar el tráfico en los logs. Su alta es del owner.
- **DECISIÓN PENDIENTE:** el contenido del punto 7 depende de lo que el abogado indique por
  tipo de ticket.

### 8.4 Satisfacción (D5)

**Interruptor** [owner]: el tenant activa o desactiva la encuesta en la configuración de Soporte.
Desactivada, no se envía, y el cierre automático funciona igual.

**Cuándo** [coordinador]: la encuesta se envía al pasar a `resuelto`, **una vez por ciclo de
resolución**. Si el ticket se reabre y se vuelve a resolver, sale otra. La idempotencia es por
`(ticket, número de resolución)`.

**Cierre automático** [coordinador]: un ticket `resuelto` pasa a `cerrado` solo a los **N días
hábiles** sin respuesta del cliente (N configurable por tenant, por defecto 5), mediante un worker.
Una respuesta del cliente vinculada antes de ese plazo sigue las reglas de §8.2.

**Por qué canal** [owner, D5; la forma concreta es del diseñador]:

- WhatsApp: **plantilla aprobada por Meta**, la de encuesta de §8.5. Sin plantilla aprobada no se
  envía, y el ticket queda «sin encuesta», no fallido.
- Instagram: mensaje con los mismos botones, dentro de la ventana de atención, si sigue abierta.
- Correo: enlace a una página pública de calificación.
- Formulario público: la calificación se ofrece en la página de consulta por radicado.
- Anónimo: sólo por la página de consulta.

**Escala** [coordinador]: tres opciones, **Buena, Regular y Mala**, como botones de respuesta rápida,
que funcionan en WhatsApp e Instagram. Después del botón, el cliente puede dejar un comentario libre
en el mensaje siguiente; es opcional. En correo y en la página de consulta se ofrecen las mismas tres
opciones.

**DECISIÓN PENDIENTE (despliegue):** el texto de la plantilla y su categoría de Meta. Una plantilla
aprobada no se edita: cambiarla es otra plantilla y otra aprobación.

### 8.5 Plantillas de WhatsApp

[coordinador] Tres valores nuevos de `MessageTemplateKind` (`MessageTemplate.cs:6` a `20`), cada uno
con sus variables en `SystemTemplateVariables` y su valor en el emisor, como las plantillas del
sistema que ya existen:

| Plantilla | Cuándo sale |
| --- | --- |
| Apertura de Soporte | Información del ticket (radicado, tipo, fecha límite, enlace de consulta y PIN) fuera de la ventana de 24 horas: ticket creado desde un hilo, ticket manual con canal WhatsApp sin hilo abierto (§8.3) y «Reenviar PIN» (§9.4). Lleva el PIN como variable. |
| Aviso de respuesta | Respuesta del asesor fuera de la ventana de 24 horas. Si es respuesta formal, el PDF va como encabezado de documento. |
| Encuesta | Al pasar a `resuelto` (§8.4). |

La información del ticket con el PIN sale por el canal del ticket: dentro de la ventana de 24 horas
es un mensaje normal; fuera de ella, por la plantilla de apertura de Soporte, que lleva el PIN como
variable [owner]. El PIN del formulario web no sale por WhatsApp, así que son tres plantillas y no
cuatro [owner].

Nombres de los valores, textos y variables: los cierra el plan. Cada plantilla necesita aprobación
de Meta por conexión, y una plantilla cuyas variables no coinciden con las que manda el backend
rompe el envío en producción sin error de compilación.

## 9. Formulario público

### 9.1 URL y rutas

- Página: `https://soporte.lulocrm.com/{tenantSlug}` y
  `https://soporte.lulocrm.com/{tenantSlug}/consultar`. Es el mismo SPA en Vercel con un dominio más
  y un diseño mínimo, sin consola [owner]. Hace falta el registro DNS `soporte`; el registro
  explícito gana sobre el comodín de las tiendas. `desk.lulocrm.com/pqrsf` redirige a la ruta nueva.
- API: `/api/v1/public/support/{tenantSlug}/…` con configuración del formulario, radicar y
  consultar. El slug que usa es `Tenant.Slug`, no el de la tienda: son distintos y sólo el primero
  existe siempre.
- Respuestas con `Cache-Control: no-store`, como las rutas del comprador (`BuyerNoStoreMiddleware.cs:12`).
  Un tenant con el módulo apagado o con el formulario despublicado responde como si no existiera.

### 9.2 Slugs reservados

`ValidateSlug` (`Tenant.cs:365`) no tiene lista de reservados. Con el slug en la raíz del host, un
tenant llamado `consultar`, `assets` o `api` choca con una ruta fija de `soporte.lulocrm.com`.

- **Tenants nuevos** [owner]: el registro rechaza un `Tenant.Slug` que esté en la lista de reservados.
  La lista sale de las rutas fijas del host de soporte; su contenido exacto lo fija el implementador
  junto con esas rutas.
- **Tenants existentes** [owner]: si alguno ya tiene un slug reservado, se le asigna uno nuevo a mano y
  se le avisa. No hay migración automática. Antes de desplegar se comprueba si hay alguno.
- Cambiar `Tenant.Slug` **no afecta la tienda**, que tiene su propio slug (`StoreSlug`) [owner].

### 9.3 Qué ve y qué configura el tenant

El formulario tiene un núcleo fijo y parámetros [owner: sin campos propios]. Referencia de los
campos: el formulario de Lulo Desk que mostró el owner.

**Caso.** Tipo de ticket, asunto y descripción, fijos. El formulario ofrece los tipos activos que el
tenant marcó como publicados (§7.5).

**Trámite anónimo.** Opción «¿Desea realizar el trámite como persona anónima?». Si es sí, no se piden
datos personales ni documento (ver §9.4).

**Identidad y contacto (no anónimo).** Con **cualquier tipo de documento que no sea NIT** (`CC`, `CE`,
`PASAPORTE`, `NIE`, `NIF`, `TAX_ID`) [owner]:

| Campo | Obligatorio |
| --- | --- |
| Tipo de documento | Sí |
| Número de documento | Sí |
| Nombres | Sí |
| Apellidos | Sí |
| Dirección de correspondencia | No |
| Teléfono fijo | No |
| Teléfono celular | Sí |
| País | Sí |
| Ciudad | Sí |
| Correo electrónico | Sí |

Con **NIT** [owner]: los mismos campos, pero **Nombres y Apellidos se reemplazan por Razón social**.

- No hay pregunta «¿persona natural o empresa?». Sólo el NIT cambia los campos.
- **Razón social** aparece siempre con NIT, y el tenant decide en la configuración del formulario si
  es obligatoria u opcional [owner]. Si llega vacía, el solicitante se muestra como «NIT <número>»
  [coordinador]. Esta opción es sólo de la razón social: no se extiende a los demás obligatorios.
- **El formulario trata todo NIT como razón social.** En Customers un NIT también puede ser de una
  persona natural: `PersonType` (`Natural`, `Juridica`) y `Customer.EnsurePersonTypeMatches`
  (`Customer.cs:1395`) sólo obligan el NIT para `Juridica` en Colombia, no al revés. Esto no estorba
  mientras el ticket guarda su propia foto del solicitante, pero **importa al vincular el ticket a
  un cliente** (ver «Vínculo con el cliente» más abajo): la razón social del ticket no equivale a
  `Customer.BusinessName`, y el nombre de contacto del cliente (`Customer.Name`) no viene en un
  ticket con NIT. El vínculo no copia datos del ticket al cliente.
- **Sin dígito de verificación del NIT** en la primera versión [coordinador]: el número se guarda
  normalizado tal como lo escribe el solicitante. El formulario de referencia tampoco lo pide, y en
  Customers no encontré validación ni columna del dígito.

**Parámetros del tenant.**

- El **par documento** (tipo y número) se muestra u oculta **y se exige junto**. El tipo se valida
  contra `DocumentTypesByCountry.IsAllowed(type, country)` con el **país del solicitante**, y la
  lista que ve la persona es `AllowedFor(country)`. En anónimo **no se piden**.
- **Dirección de correspondencia** y **Teléfono fijo** se pueden ocultar.
- **Los demás obligatorios son fijos** en la primera versión [coordinador]: el único campo cuya
  obligatoriedad configura el tenant es la razón social con NIT. Si el tenant **oculta el par de
  documento**, se piden Nombres, Apellidos, celular, país, ciudad y correo; no hay vínculo automático
  con el cliente y el asesor vincula a mano.
- **Sin campo «Empresa»** en la primera versión [coordinador]: estaba en Lulo Desk y no está en el
  formulario de referencia del owner.
- **Adjuntos:** el tenant los permite o no. Límites de cantidad y tamaño: decisión del implementador.
- **Marca:** logo del tenant, color, texto de introducción y mensaje al radicar.
- **Textos legales:** §9.5.

**Dónde vive la regla.** La definición de los campos según el tipo de documento (qué se pide, qué es
obligatorio) la resuelve el **backend** y el formulario la consume desde la configuración pública.
No se duplica en el frontend. [diseñador]

**Versionado.** La definición del formulario se guarda por versión: cada ticket apunta a la versión
con la que se radicó, y editar el formulario no reescribe los tickets viejos.

**País y ciudad.** País es obligatorio y manda sobre la lista de tipos de documento. El país por
defecto es `Tenant.Country` (`Tenant.cs:67`) [coordinador]. País y ciudad salen del módulo
**Geography**, como en el resto del sistema [coordinador]. El implementador verifica al planear qué
endpoint público existe para el formulario anónimo y lo anota en el plan; si no existe, crearlo es una
tarea del plan.

**Vínculo con el cliente.** [owner] El ticket se vincula al `Customer` por **tipo y número de
documento**:

- **Exactamente un candidato:** se vincula solo.
- **Varios candidatos:** el ticket queda sin vincular. El asesor ve los candidatos y elige uno de
  ellos u otro cliente.
- El asesor **siempre** puede asignar o cambiar el cliente a mano.

Detalles [coordinador]:

- **Sin coincidencia:** el ticket queda sin cliente, con la foto del solicitante. No se crea un
  cliente automático.
- **Búsqueda:** por el número normalizado de Customers, `CustomerIdentificationSql.NormalizedNumber`
  (`Modules.Customers.Infrastructure/Persistence/CustomerDuplicateQueries.cs:13`, clase en la línea 7),
  la misma expresión del índice `IX_customers_tenant_identification_normalized`. Va por un puerto de
  Soporte con adaptador en Bootstrapper. Incluye clientes `Incomplete` y excluye los fusionados
  (`merged_into_id IS NULL`, como las consultas de duplicados de ese archivo).
- **Candado:** escribir `customer_id` toma `CustomerReferenceLock` en modo compartido, para no
  cruzarse con un borrado o una fusión.

Riesgo anotado [diseñador]: el formulario no tiene sesión, así que quien escribe el documento de
otra persona cuelga su caso del expediente de esa persona, y ese `customer_id` bloquea el borrado
(D4) y entra en la fusión. El asesor puede corregirlo cambiando el cliente a mano; el cambio queda en
la traza.

### 9.4 Consulta pública por radicado

Lulo Desk permite buscar por «radicado o email o identificación o nombre». **No se replica:**
cualquiera podría enumerar casos y datos de terceros. La consulta pide **radicado más PIN**. El
radicado solo no alcanza, porque es consecutivo.

**PIN** [owner]: crear un ticket le da al solicitante un PIN para consultar su radicado.

- **Formulario web:** el PIN se **muestra una vez en pantalla**, justo después de generar el radicado
  [owner].
- **Formulario web, no anónimo:** además se **envía al correo** que escribió el solicitante, que es
  obligatorio. **No** se envía por WhatsApp al celular del formulario, así que no hace falta una
  cuarta plantilla [owner].
- **Formulario web, anónimo:** sólo se muestra en pantalla, una vez, con el aviso de que no se puede
  recuperar [owner].
- **Ticket creado desde una conversación** (WhatsApp u otro canal con hilo): al crear el ticket, el
  sistema envía por ese mismo canal la información del ticket: radicado, tipo, fecha límite si el
  tipo tiene término, enlace de consulta y PIN [owner]. Dentro de la ventana de 24 horas va como
  mensaje normal; fuera de ella, por la plantilla de apertura de Soporte, que lleva el PIN como
  variable (§8.5).
- **Ticket creado a mano por el asesor:** la misma información sale con el acuse de radicación por el
  canal de respuesta del ticket (hilo, plantilla de apertura o correo) [coordinador].

Detalle [coordinador]:

- **Forma:** numérico de 6 dígitos, generado con un aleatorio criptográfico. Se guarda **sólo su
  hash**, nunca en claro.
- **Fuerza bruta:** 6 dígitos son pocos. Cinco intentos fallidos por radicado bloquean la consulta
  de ese radicado durante 15 minutos, además del limitador y de Turnstile.
- **«Reenviar PIN»** en la página de consulta: genera uno nuevo y lo manda al destino registrado del
  ticket, nunca a uno que escriba el visitante. Para un ticket del formulario web, al correo
  registrado; para un ticket creado desde un hilo, al canal de origen (con la plantilla de apertura si
  la ventana cerró); para uno creado a mano, a su canal de respuesta. La respuesta en pantalla es la
  misma exista o no el radicado. Un ticket anónimo no tiene a dónde reenviarlo.

- Muestra estado, vencimiento y la respuesta (incluido el PDF). Nada más del expediente.
- Limitador propio y más estricto que `Public` (120 por minuto, `Program.cs:100`), tanto para
  radicar como para consultar. Valores: decisión del implementador.
- Anti-bot con Turnstile reutilizando `ICaptchaVerifier` mediante un adaptador de Bootstrapper,
  como `BuyerCaptcha.cs:11`. El `expectedHostname` es `soporte.lulocrm.com`. **DECISIÓN PENDIENTE:**
  la clave del sitio y su configuración en `Production`.
- **Anónimo.** Sin `customer_id`, sin foto del solicitante, sin IP guardada y sin documento. Su única
  vía de respuesta es esta página: la respuesta se **publica**, no se envía. La ley obliga a
  tramitar la petición anónima.

### 9.5 Textos legales por tenant

Siguen el precedente de `customers.data_consents` (prueba insert-only con foto del texto) y no el de
Store (sólo una URL).

- Cada tenant escribe su **autorización de tratamiento de datos**, su **política** (texto o URL) y,
  si quiere, sus **términos**. Cada edición crea una **versión nueva**; las anteriores no se
  modifican ni se borran.
- QEP trae un texto base de ejemplo, editable, marcado como ejemplo. El tenant es responsable del
  contenido y conviene que lo revise su abogado.
- Al radicar sin ser anónimo, el solicitante acepta la autorización. Soporte guarda por ticket: la
  versión aceptada, la hora, una **foto del texto** y la IP. En anónimo no se acepta autorización
  (no se recogen datos personales) y no se guarda IP.
- El formulario muestra los textos vigentes. Un texto sin versión publicada no deja publicar el
  formulario si hay datos personales por recoger.
- **Soporte tiene sus propios textos y no lee `Store.DataPolicyUrl`.** Soporte no ve Store; leerlo
  exige un puerto y deja dos fuentes de verdad.

## 10. Impactos cruzados

- **Permisos** [coordinador; la visibilidad es del owner]:

  | Permiso | Qué permite |
  | --- | --- |
  | `support.ticket.read` | Ver todos los tickets del tenant. |
  | `support.ticket.manage` | Crear, responder, cambiar estado, asignar y prorrogar. |
  | `support.settings.manage` | Formulario, textos legales, tipos de ticket, términos y encuesta. |

  Roles de sistema: admin los tres; asesor `read` y `manage`. **Visibilidad** [owner]: todo miembro
  cuyo rol tenga `support.ticket.read` o `support.ticket.manage` ve **todos** los tickets del tenant.
  No hay «sólo los suyos».
- **Permisos, dos mitades.** Constantes en `SupportPermissions` **y** su política en `AddAuthorization`
  (`QepServiceCollectionExtensions.cs:2676`): sin la política el síntoma es 500, no 403. Además,
  `PermissionDefinition` con `RequiredModules`, la concesión a los roles de sistema que correspondan
  y las pruebas del catálogo. Un permiso nuevo exige reiniciar la API y recargar la sesión. Las
  pruebas de integración que necesitan un permiso de otro módulo lo piden por `X-Permissions`.
- **Módulo en `TenantModuleKey` y migración de Tenancy.** Una clave nueva (propuesta: `support`),
  en `All` (`TenantModuleKey.cs:88`), fuera de `DefaultForNewTenants` [diseñador], con la migración
  que reescribe `CK_tenant_modules_module_key` al estilo de `20261112055320_AddAiModuleKey.cs`. Sus
  dependencias: **ninguna** [coordinador]. Sin Messaging, Soporte funciona con el formulario público,
  la creación manual y la respuesta por correo; con Messaging aparecen el hilo, la pestaña de la ficha
  y las plantillas de WhatsApp. El vínculo con el cliente necesita Customers prendido; sin él, el
  ticket conserva sólo la foto del solicitante.
- **`SupportLayerTests`.** Un archivo nuevo en `tests/ArchitectureTests/ArchitectureTests/`, como
  `LoansLayerTests.cs`, con las reglas de capas y la de «Application no referencia EF Core ni
  Npgsql».
- **Fusión de clientes.** `ticket.customer_id` es un id de cliente vivo: entra en `MoveAsync` de
  `CustomerMergeReferences` (`CustomerMergeReferences.cs:30`) y en sus dos pruebas amarre
  (`CustomerMergeReferencesTests` y `CustomerMergeAcrossModulesApiTests`). Si no, la fusión deja
  tickets apuntando a un cliente inactivo.
- **Borrado de clientes.** D4: **cualquier ticket** del cliente, abierto o cerrado, bloquea el
  borrado, como cotizaciones y pedidos (§4.2 del spec de borrado: «cualquier fila, en cualquier
  estado»). Hay que agregar un motivo a `CustomerDeletionBlockReasons` (`BulkDeleteCustomers.cs:31` a
  `41`; el orden del contrato está en `Ordered`, línea 40), la consulta en `FindBlockersCoreAsync`
  (`CustomerDeletionReferences.cs:83`), y su prueba en `CustomerDeletionAcrossModulesTests`. El
  frontend de clientes debe mostrar el motivo nuevo. El escritor de tickets toma
  `CustomerReferenceLock` en modo compartido, como Quotations y Loans.
- **CORS y seguridad del origen público.** Agregar `https://soporte.lulocrm.com` a
  `Cors:AllowedOrigins` le daría a esa página acceso **con credenciales** a **toda** la API (la
  política `spa` es única y global). Hoy la API está en `qep-api.qcode.co` y la cookie no viajaría
  desde `soporte.lulocrm.com`; cuando la API pase a `api.lulocrm.com`, los dos hosts serán el mismo
  sitio y la cookie `SameSite=Lax` sí viajaría. **No se debe hacer en ningún escenario.** Decisión
  [coordinador]: una política CORS **aparte, sin credenciales,** aplicada sólo al grupo
  `/api/v1/public/support/…`, con el origen exacto de `soporte.lulocrm.com`. Vale con la API en
  `qep-api.qcode.co` y con la API en `api.lulocrm.com`. Para el CSRF, o bien esa ruta lleva su excepción explícita en
  `RequireCsrfHeaderMiddleware` (como `StorefrontPaths`) con la garantía de que ningún endpoint del
  grupo autentica por cookie, o bien el formulario manda `X-Qep-Client` y la política nueva lo
  permite. Decisión del implementador, con prueba de que la ruta no acepta la sesión.
- **Host y DNS.** `soporte.lulocrm.com` lo sirve el mismo SPA en Vercel, como un dominio más [owner].
  Hace falta el registro DNS `soporte` en `lulocrm.com` apuntando a Vercel, explícito para que gane
  sobre el comodín de las tiendas. El ingress de k8s no cambia. Lo ejecuta el owner.
- **Slugs de tenant reservados.** §9.2. Afecta el registro de tenants y la consola del operador.
- **`ConversationEventType`.** Valores nuevos al final y el campo de ticket en `ConversationEvent`.
  Un evento nuevo debe verse bien en el hilo del frontend, que resuelve los nombres al leer.
- **Auditoría y outbox.** Puerto de auditoría propio (§7.4). Los eventos de integración
  (`support.ticket.created.v1`, etc.) y su consumidor, si Pipeline o Reporting los quieren, son
  **DECISIÓN PENDIENTE**; no se necesitan en la primera fase. Cuando existan, su payload es contrato
  leído por nombre.
- **Reportes.** Soporte necesita sus propias cifras (por tipo, vencidos, tiempo de resolución,
  satisfacción). Si Reporting las lee por SQL crudo, hereda la advertencia de renombres: se amarra
  con prueba.
- **Frontend** (`qep-frontend`). Pestaña en la ficha del contacto (`contact-sheet.tsx`), tira de
  ticket (equivalente a `deal-strip.tsx`), panel global del ticket, bandeja y detalle, ajustes del
  formulario y de los textos legales, y la página pública. La entrada «Soporte» del menú se gatea
  por módulo y permiso con el patrón de `use-pipeline-access.ts`. El frontend no se lleva a `main`
  antes que el backend, como ya pasa con los demás módulos.
- **Pruebas.** Un formulario público sin sesión y un `POST` anónimo se prueban con el stub de
  autenticación **apagado** (`Authentication__UseDevelopmentStub=false`): en modo stub el CSRF ni se
  registra y una excepción rota pasaría. Una prueba de integración nueva usa el host compartido por
  clase (`Shared<Modulo>Host`) sólo si cumple las reglas del `CLAUDE.md` del repo.
- **`docs/estado.md`** se actualiza en `develop`, al mergear, nunca en la rama de feature.

## 11. Lo que queda al implementador

Nombres de clases, tablas y columnas; el esquema `support`; formas exactas de los DTO (con el motivo
escrito en cada uno, como pide la regla de backend for frontend del repo); el cálculo de días
hábiles; límites del formulario y del limitador; la forma del contador de radicados; la plantilla
Typst; y el diseño de las pantallas. Todo cambio de dominio que endurezca una regla obliga a barrer
las pruebas de integración que arman cuerpos a mano.

## 12. Fases sugeridas para el plan

1. **Núcleo.** Módulo, dominio, estados, reloj, permisos, entitlement y migración de Tenancy,
   API de bandeja y detalle, creación manual, auditoría, bloqueo del borrado y fusión de clientes,
   `SupportLayerTests`, pantallas de bandeja y detalle.
2. **Mensajería.** Directorio de conversaciones, vínculos, fijar y soltar, no clasificados, «De
   ninguno», «Seguir el hilo», eventos del hilo, pestaña en la ficha, respuesta por el canal de origen.
3. **Formulario público.** Slugs reservados, ruta y host, CORS y CSRF, anti-bot, consulta por radicado
   y clave, configuración del formulario, par de documento y campos según el tipo, textos legales y
   consentimientos.
4. **Respuesta formal y satisfacción.** PDF de fondo, plantilla de encuesta y su aprobación,
   prórroga y avisos de vencimiento, reportes.

Cada fase deja el módulo usable y se mergea a `develop` por separado.

## 13. Decisiones pendientes y puntos sin verificar

Resumen para el owner. El detalle está en la sección indicada. Ninguna bloquea el plan: las que lo
bloqueaban se cerraron en §6. Entre corchetes, si bloquea el **despliegue** (un valor, un
texto o una confirmación legal que el diseño ya deja configurable) o **ninguno**.

1. [despliegue] Validar con el abogado la interpretación del reloj: inicio al día hábil siguiente, fin al
    final del día hábil y tope de la prórroga en el doble del término inicial (§7.2).
2. [despliegue] Artículo y plazo del requerimiento por petición incompleta, y a qué tenants aplica (§4).
3. [despliegue] Plazo de la reclamación directa (Ley 1480): sin confirmar (§4).
4. [despliegue] Si los términos pueden contarse con el horario del tenant y no con lunes a viernes (§7.2).
5. [despliegue] Clave y configuración de `qcode-pdf` propias de Soporte (§8.3).
6. [despliegue] Contenido legal del «paso siguiente» del PDF por tipo de ticket (§8.3).
7. [despliegue] Clave del sitio de Turnstile y su configuración en producción (§9.4).
8. [despliegue] Texto y categoría de Meta de las plantillas de Soporte, incluida la de encuesta (§8.4, §8.5).
9. [ninguno hasta que haya consumidor] Eventos de integración (`support.ticket.*`) y consumidores (§10).
10. [despliegue] Retención: los tickets no se borran en la primera versión; la política de conservación de datos
     personales y de los consentimientos la define el owner con el abogado.
11. [lo resuelve el implementador al planear] No verificado: cómo abre el frontend el panel global del negocio (§3).
