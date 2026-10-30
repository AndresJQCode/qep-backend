# `qep-backend` — reglas del repositorio

Monolito modular .NET 10. Este archivo es **prescriptivo**: reglas, precedentes y gotchas
que **no** se deducen leyendo el código. Lo descriptivo —qué existe, cómo se corre, los
contratos HTTP y la arquitectura— vive en [`README.md`](README.md) y no se duplica acá.

Es autosuficiente para el trabajo diario: no depende del `CLAUDE.md` de la carpeta
contenedora, que es local, no está versionado y puede no existir en un clon nuevo.

## Topología

`qep-backend` y `qep-frontend` son **repositorios git independientes**, cada uno con su
remoto y **su propio developer**. La carpeta que los contiene **no es un repositorio**. Nunca
se commitea desde ahí.

**Que el checkout hermano exista no alcanza: hay que verificar en qué rama está.** El
2026-08-11 se leyó `qep-frontend` estando ese repo en `feature/catalog`, nueve commits detrás
de `develop`, y se reportó como faltante contenido que sí existe. El árbol de trabajo del otro
repo es un **estado**, no la autoridad. Antes de concluir que algo falta ahí:
`git -C ../qep-frontend branch --show-current`, y si el dato no aparece,
`git -C ../qep-frontend log -S "<término>" --all` más `git branch -a --contains <sha>`.

## Reglas duras

- **Nunca imprimir el valor de un secreto.** `dotnet user-secrets list` y equivalentes se
  corren enmascarados o contando (`| Select-String -Pattern "Clave" | Measure-Object`). Para
  verificar que un secreto existe alcanza con eso. `user-secrets` los mantiene fuera del repo
  —su único trabajo— pero **no están cifrados**: lo que se lea queda en el historial de la
  sesión, en un log o en una captura. Lo que se filtre se rota. Precedente: el 2026-08-09 se
  imprimió la API key de Infobip completa al verificar si estaba configurada. Qué secretos usa
  el repo y cómo se cargan: [README § Secretos de usuario](README.md#secretos-de-usuario).
- **`kubectl get secret` imprime los valores. `describe` no.** El campo `.data` es base64 —
  codificación, no cifrado— así que `-o yaml`, `-o json` y cualquier `-o custom-columns` que
  incluya `.data` filtran el secreto completo. Para ver **qué claves** tiene un Secret, sin sus
  valores:

  ```powershell
  kubectl -n <ns> describe secret <nombre>            # nombres y tamaño en bytes
  kubectl -n <ns> get secret <nombre> -o jsonpath='{range $k,$v := .data}{$k}{"\n"}{end}'
  ```

  Precedente: el 2026-08-11 se volcaron las cuatro credenciales de `qep-backend-secret`
  —contraseña de base, API key de Infobip y el par de R2— al verificar un despliegue con
  `-o custom-columns=KEYS:.data`. Las cuatro se rotaron. Es la **segunda** vez que pasa en este
  proyecto: la primera fue la API key de Infobip el 2026-08-09.

- **`.mcp.json` se versiona: la clave va por `${VAR}`, nunca literal.** No está en `.gitignore`,
  así que un valor escrito ahí se commitea. Claude Code expande variables de entorno en `url`,
  `headers`, `command`, `args` y `env`, así que el archivo puede viajar en el repo sin secreto
  adentro y cada quien pone el suyo en su entorno. La única que usa hoy es
  `QCODE_PDF_API_KEY` — de dónde sale y dónde se configura, en
  [README § `QCODE_PDF_API_KEY`](README.md#qcode_pdf_api_key--el-mcp-de-qcode-pdf). **No es** la
  clave con la que el backend renderiza cotizaciones (`Quotations:Pdf:ApiKey`): `qcode-pdf`
  resuelve el `clientId` por el hash de la clave, así que compartirla mezclaría las pruebas de
  un desarrollador con el tráfico real en sus logs, y rotarla obligaría a tocar producción.

- **TDD obligatorio.** RED antes que GREEN, con evidencia literal de ambos.
- **No inventar.** Campos, estados, rutas, permisos, roles y códigos de error deben existir en
  el código. Lo que falte se registra explícitamente como decisión pendiente, no se asume.
- **La autoridad es el código.** Ante discrepancia entre el código y un documento, gana el
  código y el documento se corrige.
- **Commits:** conventional commits. **Sin atribución de IA.**
- **Antes de commitear se comprueba la rama, y si es la por defecto (`main`) se crea una
  primero.** La comprobación es `git branch --show-current` **en ese momento**, no lo que se
  haya leído al abrir la sesión: el 2026-09-05 el snapshot del arranque decía `main` mientras
  el repo estaba en `develop`, y se le informó al developer la rama equivocada. La rama es un
  **estado** y cambia entre comandos —mismo criterio que con el checkout de `qep-frontend`—,
  así que ni el contexto inicial ni un `git status` de hace diez minutos son autoridad sobre
  ella.
- **Prohibido mergear a `main`** (decisión del owner, 2026-10-10). Los merges de un agente
  terminan en `develop`; llevar `develop` a `main` lo hace el owner, nunca un agente. Un pedido de
  "mergear" o "publicar" sin destino significa `develop`.
- **[`docs/estado.md`](docs/estado.md) se actualiza en `develop`, al mergear.** Es el estado global
  del proyecto, y sin esta regla se queda viejo. El 2026-10-21, a un día de creado, decía «sin empezar» para
  variantes con el backend ya mergeado, y once specs construidos seguían en «Por verificar». Cada merge a
  `develop`, decisión del owner o despliegue actualiza la fila que corresponde y el Registro, en el mismo
  merge o en un commit `docs:` inmediatamente después. **Las ramas de feature no lo tocan:** si cada rama lo
  edita, dos ramas en paralelo chocan ahí en cada merge. Nada pasa a «Construido» sin evidencia en el código:
  la clase, la ruta o el commit.

## Entorno de desarrollo

**Windows con PowerShell.** Todo comando que se entregue al developer va en PowerShell.

| Qué                 | Bash (no sirve)       | PowerShell                                              |
| ------------------- | --------------------- | ------------------------------------------------------- |
| Variable de entorno | `VAR=valor comando`   | `$env:VAR = "valor"`, en línea aparte                   |
| curl real           | `curl`                | **`curl.exe`** — `curl` es alias de `Invoke-WebRequest` |
| Encadenar           | `A && B`              | `A; if ($?) { B }` — no hay `&&` ni `\|\|`              |
| Ver archivo         | `cat`, `head`, `tail` | `Get-Content`, `-TotalCount`, `-Tail`                   |

**Cuerpos JSON a un `.exe`:** PowerShell rompe las comillas dobles al pasarlas. Van a archivo
y se mandan con `-d "@archivo.json"`. Git Bash está instalado y es alternativa válida, pero se
ofrece después de la versión de PowerShell.

Esto vive acá y no en un archivo compartido porque **este repo tiene su propio developer**:
declarar su entorno en su repo no le impone nada al del frontend.

## Cómo se escriben los mensajes

**En español colombiano, tuteando.** Vale para las respuestas del asistente al developer y
para todo texto que el sistema le muestra o le manda a una persona: correos, plantillas de
WhatsApp, mensajes de error legibles, avisos. **Pisa el estilo de salida del asistente**, que
por defecto vosea: sin esta regla, cada sesión nueva vuelve al rioplatense.

| Sí                                       | No (voseo)                                  |
| ---------------------------------------- | ------------------------------------------- |
| tienes, puedes, quieres                  | tenés, podés, querés                        |
| elige, revisa, intenta de nuevo          | elegí, revisá, intentá de nuevo             |
| dime, avísame, escríbenos                | decime, avisame, escribinos                 |
| No tienes permiso para ver este reporte. | No tenés permiso para ver este reporte.     |

- Registro profesional y cercano: nada de modismos rioplatenses (`dale`, `che`, `laburo`), y
  tampoco regionalismos colombianos marcados (`parce`, `bacano`). "Listo", "claro" y "de una"
  sí caben en la conversación con el developer; en un texto del producto, no.
- **No se traduce lo que es contrato:** identificadores, códigos de error
  (`quotation.export.empty`), nombres de enum (`Sent`), rutas y permisos quedan tal cual.
- Un comentario de código o una documentación que se dirige al lector también tutea. Lo que
  ya está escrito con voseo se corrige cuando se toca el archivo, no en un barrido aparte.

## Convenciones del backend

Capas `Domain` → `Application` → `Infrastructure` → `Api`, un assembly cada una, verificadas
por `tests/ArchitectureTests/` con **un archivo por módulo**. Un módulo nuevo trae su propio
`<Modulo>LayerTests.cs`. El detalle de los patrones ya implementados —CQ propio, DDD,
outbox/inbox, repositorio + unit of work, concurrencia optimista— está en
[README § Patrones técnicos y componentes](README.md#patrones-técnicos-y-componentes).

Lo que hay que saber **antes** de escribir, y no se ve leyendo un módulo ya hecho:

- **Las APIs se construyen como backend for frontend, no como REST genérica.** El consumidor
  es `qep-frontend` y ninguno más, así que la forma de cada respuesta se decide por lo que la
  pantalla necesita dibujar, no por la pureza del recurso. Ante una decisión discutible gana la
  que le ahorra lógica al cliente, **y el motivo se escribe en el DTO**: desde el backend no se
  ve la pantalla que lo obligó, y sin esa nota el siguiente lo "simplifica".
- **Qué significa eso en concreto, con los precedentes ya escritos.** Un agregado que la
  pantalla no puede calcular es un endpoint, no un problema del cliente:
  `/reports/orders/summary` existe porque sumar páginas de 50 filas del lado del cliente da un
  total que depende de la página que se esté mirando, y `MaxPageSize` impide pedir el período
  entero. Las colecciones de tamaño fijo viajan **completas, incluso en cero**
  (`ReportStatusSliceDto`, `PriceChangeFieldSliceDto`): un elemento que desaparece obliga a la
  pantalla a conocer el enum del backend para dibujar el que falta. Un total que no cierra se
  explica con un campo y no con una omisión (`QuotationValidityDto.WithoutExpiry`). Los enums
  viajan con su nombre (`Sent`, `PriceBaseUsd`) porque el diccionario lo tiene el frontend. El
  422 de validación lleva el mapa `errors` (`ApiExceptionHandler.cs:51`), el único 422 que el
  formulario sabe leer para marcar el input. Y una colección que se edita vuelve entera y en
  orden, que es lo que el formulario repinta.
- **Aislamiento de tenant, doble capa.** La ruta lleva `{tenantId:guid}` y la política exige
  el permiso; **además** el handler revalida tenant y permiso antes de tocar el repositorio
  (`CatalogAuthorization`, `StorageAuthorization`). Devuelve **403, nunca 404**: un 404
  confirmaría que el id existe en otro tenant. Todo método de repositorio recibe `tenantId`.
- **Un permiso nuevo necesita dos mitades:** la constante en `<Modulo>Permissions` **y** su
  política en `AddAuthorization` (`QepServiceCollectionExtensions`), que se registran una por
  una a mano. Sin la política, `RequireAuthorization` no resuelve y el síntoma es **500, no
  403** — no se parece en nada a la causa.
- **Un caso de uso con texto libre lleva validador de FluentValidation aunque el dominio ya
  valide:** el dominio da un código (`DomainException` → 422), el validador da el campo
  (`ValidationException` → 422 `validation.failed`, con el mapa `errors`). Ningún status HTTP
  se arma a mano en un handler: el mapeo es central en `src/Api/ApiExceptionHandler.cs`.
- **Traducir errores de base va en Infrastructure, no en Application.**
  `Modules.<Modulo>.Application` no referencia EF Core ni Npgsql, y `ArchitectureTests` lo
  verifica. Discriminar por **nombre de índice**, no sólo por `SqlState 23505`: hay varios
  índices únicos y devolver el código equivocado manda a corregir el campo equivocado
  (precedente: `catalog.product.code_taken`).
- **Auditoría y outbox en la misma transacción.** Dos caminos: el atómico (`IAuditRecorder`,
  atado al `DbContext` del productor) y el de outbox (`I<Modulo>AuditPublisher`), para
  operaciones operativas. Una prueba que verifica sólo el status HTTP deja pasar el efecto que
  importa.

## Gotchas verificados

- **`Api.exe` corriendo bloquea todo.** `dotnet build`, `dotnet test` y los comandos `ef`
  fallan por archivo bloqueado. Detener el proceso primero. El nombre es `Api.exe` porque
  `src/Api/Api.csproj` no declara `AssemblyName`.
- **Las migraciones se generan con el factory de diseño, no con `--startup-project`.**
  `Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`:

  ```powershell
  dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations
  ```

- **Subir de banda el SDK toca tres archivos, y `rollForward` no ayuda.** `global.json` pide
  `latestPatch`, que sólo rueda **dentro** de la misma banda de feature: de `10.0.4xx` a
  `10.0.4xx` sí, de `4xx` a `5xx` no. Se mueven juntos `global.json`, el tag del `FROM` del
  `Dockerfile` —clavado a la banda, nunca al flotante `10.0`— y el requisito del README. Si el `Dockerfile` queda atrás, el `docker build` muere con
  `SDK not found` y exit 155 sin que nadie haya tocado el repositorio. Las rutas de SDK del
  allowlist en `.claude/settings.json` llevan comodín a propósito, para que no sean un cuarto
  lugar que se olvida.
- **Cambiar `Directory.Packages.props` sin regenerar los lock files no rompe tu máquina: rompe
  el `docker build`.** Los 74 `packages.lock.json` —uno por `.csproj`, activados por
  `RestorePackagesWithLockFile` en `Directory.Build.props`— se regeneran con
  `dotnet restore --force-evaluate` y se commitean **junto** con el props. En local un `restore`
  normal los actualiza solo y no protesta; el `Dockerfile` corre `--locked-mode`, donde NuGet
  no resuelve nada sino que exige que el grafo coincida exacto, y falla con `NU1004`. El
  diff de los locks es además el único lugar donde se ve el arrastre transitivo: el parche de
  .NET del 2026-09-05 movió `Microsoft.IdentityModel.*` de `8.0.1` a `8.19.2`, que no está
  declarado en el props y por lo tanto no aparece en su diff.
- **`Notifications:EmailProvider` vale distinto según el ambiente, y las dos mitades son
  necesarias.** `appsettings.json` trae `log` —el canal que registra el correo y no lo manda—
  porque es el mismo archivo en local y en producción y su único trabajo es ser el default que
  arranca sin credenciales. Producción lo pisa con `infobip` desde `k8s/prod-configMap.yaml`, y
  `NotificationsOptionsValidator` **rechaza `log` en `Production`** con `ValidateOnStart`: sin esa
  mitad, olvidarse la clave dejaría las invitaciones marcadas como entregadas sin que salga un
  correo, sin error y sin log. Hasta el
  2026-09-06 el default era `infobip`, así que una factoría de integración que no fijara la clave
  mataba **todas** las pruebas de su archivo en el arranque; ya no, pero todas la fijan igual y
  conviene seguir haciéndolo: deja explícito qué canal ejerce la prueba.
- **El stub de desarrollo concede sólo los permisos de tenancy por defecto.** Una prueba que
  necesita un permiso de otro módulo tiene que pedirlo por `X-Permissions`, o su 403 va a
  venir del permiso faltante y no de lo que cree estar probando.
- **Probar auth en local tiene dos trampas.** El interruptor y los dos modos están en
  [README § Activar y desactivar el modo de desarrollo](README.md#activar-y-desactivar-el-modo-de-desarrollo-auth).
  Lo que ahí no se dice: (1) `UseDevelopmentStub` es `true` por defecto en `Development`
  (`QepAuthenticationMode.cs:15`), pero **los dos perfiles de `launchSettings.json` lo fijan en
  `false`**, así que un `dotnet run` normal corre con auth real; para usar los headers `X-*` hay
  que pedir el stub con `$env:Authentication__UseDevelopmentStub = "true"`. (2) En modo stub
  **no se registra el middleware de CSRF** (`Program.cs:64-67`), así que un `POST` sin
  `X-Qep-Client: web` pasa; ese header sólo se puede probar con el stub apagado.
- **Los user-secrets se cargan en `Development` sin ayuda de nadie.** Los agrega
  `WebApplication.CreateBuilder`, que es comportamiento del framework. El
  `if (IsEnvironment("Local"))` que este archivo describía en `Program.cs:26` ya no existe: era
  redundante en `Development` y lo eliminó `ccd2eca`.
- **La cadena de conexión no está en `appsettings.json`, y es requerida.** Se quitó el
  2026-08-11: apuntaba a una base inexistente —ni siquiera la del `compose.yaml` de este repo,
  que crea `qep` en `5432`— y llevaba contraseña, que es justo lo que un `appsettings*.json`
  versionado no debe llevar. Ausente, los seis `AddXInfrastructure` fallan al arrancar con
  `InvalidOperationException: Connection string 'QepDatabase' is required.` **El guard es
  `?? throw`, o sea sólo contra `null`:** dejar la clave con `""` lo atraviesa y el error lo
  termina tirando Npgsql, que no explica nada. En local se pone por user-secrets (ver
  [README § Ejecución local](README.md#ejecución-local)); en k8s la fija `prod-secret.yaml`
  como `ConnectionStrings__QepDatabase` — **no** el ConfigMap, que es texto plano para
  cualquiera con `get` sobre ConfigMaps en el namespace.
- **Endurecer una regla de dominio rompe las pruebas de integración que arman el cuerpo a
  mano, y el síntoma no se parece a la causa.** El handler devuelve `422 validation.failed`
  donde la prueba esperaba `Created`, o —peor— un 422 que la prueba sí esperaba pero por
  otro motivo. Pasó dos veces en una semana y costó 23 de 24 fallas el 2026-09-05: `CityId`
  pasó a obligatorio en `CreateCompanyRequest` (`613912a`, 2026-08-31) y once pruebas de
  Companies seguían posteando sin él; `ValidUntil` pasó a obligatorio para enviar (`86efda1`,
  2026-09-04) y `ReportingApiHarness.CreateSentQuotationAsync` —de un día antes— tumbó las
  doce de Reporting desde una sola línea. **Al agregar un campo requerido o una precondición,
  hay que barrer las pruebas de integración por cuerpos crudos**, no sólo los harness: las que
  usan el harness se arreglan solas y esconden el problema.
- **Defecto conocido sin cerrar:** un registro de tenant que falla después de aprovisionar
  deja el usuario en `identity.users` **sin membresía**. `RegistrationEndpoints.cs:89-104`
  aprovisiona antes de crear el tenant, en módulos con unidades de trabajo distintas y sin
  compensación.
- **Una plantilla de WhatsApp rechazada con "An error occurred while sending the template for
  approval" no la rechazó Meta:** es Zenvia que no logró enviarla. El contenido, el tono y la
  categoría nunca se revisaron, así que cambiarlos no sirve — mira el `name` (sólo minúsculas,
  dígitos y guiones bajos), el `examples` (sin `$`, `#`, `%`) y el `Content-Type` del PDF de
  ejemplo. Costó cuatro intentos el 2026-09-04. El procedimiento entero está en
  [README § Plantilla de WhatsApp](README.md#plantilla-de-whatsapp-zenvia).
- **El texto de la plantilla no se edita: se crea otra.** Meta no permite modificar una plantilla
  aprobada, así que cambiar una coma implica plantilla nueva, aprobación nueva y editar el
  `templateId` de la conexión de Zenvia de cada tenant (no hay cuenta global desde el plan
  2026-10-21). Si además cambian las variables, se toca `WhatsAppQuotationMessage` y
  `ZenviaWhatsAppSender` — y el `templateId` de las conexiones se actualiza en **el mismo
  despliegue** que ese código: una plantilla cuyas variables no coinciden con las que el backend
  manda rompe el envío en producción sin error de compilación.
- **`Pos` necesita la capacidad `pos` y no viene con el signup.** `GrantDefaultModulesOnSignup`
  concede siete módulos (los seis base y `shopify`) sin `pos`, así que en un tenant nuevo todo `/pos/*`
  da 403 aunque el rol tenga los permisos: se prende con la fila de `tenancy.tenant_modules` (spec de entitlements,
  «Operación»). En pruebas, `PosApiHarness.EnablePosAsync`. Y `cashier` es clave de rol de sistema
  reservada: antes de desplegar se comprueba que ningún tenant tenga un rol custom con esa clave
  (spec 2026-10-07, «Despliegue»).
- **La cotización por Meta no usa `OutboundGuards` ni `ConversationOpener.OpenAsync`** (spec 2026-10-21). `QuotationTemplateOutbound`
  busca la conversación por teléfono (`OpenByPhoneAsync`) y no por `customer_id`, para no caer en el hilo de otro número (D16b);
  pasa `seen: 0` para no tocar los no leídos (D16a); y el PDF va por `id` de media, no por URL. Un cambio en el reclamo o la
  liquidación (`IOutboundMessages`, `OutboundSettlement`) se prueba también con `QuotationTemplateOutboundTests`.
- **`Conversations` como módulo no existe; la bandeja de WhatsApp es `Messaging`** (spec 2026-10-09).
  Los dieciocho módulos construidos son Audit, Authorization, Catalog, Companies, Customers, Geography,
  Identity, Integrations, Messaging, Notifications, Pipeline, Pos, Quotations, Reporting, Shopify, Storage, Suppliers y Tenancy — cada
  uno con su `<Modulo>LayerTests.cs` en `tests/ArchitectureTests/`. `Platform` también existe, pero sin
  `PlatformLayerTests` (H26). Messaging no referencia
  Integrations, Customers ni Storage: lo que necesita entra por `IMessagingConnectionDirectory`,
  `IMessagingCustomerDirectory` e `IMessagingMediaStore`, con adaptadores en Bootstrapper.
- **La clave de una conversación es el BSUID, no el teléfono** (spec 2026-10-10). `wa_id` puede venir
  `null` y dos conversaciones con BSUID pueden compartir número (reciclado). Un cuerpo de webhook de prueba
  sin `from_user_id` se descarta en silencio —la entrega queda procesada y no aparece nada—: es el síntoma,
  no un bug de la ingesta.
- **El historial de coexistencia no pasa por `IngestOnceAsync`; `history_managed` decide si la regla de abiertas corre**
  (spec 2026-10-29). La apagan: el entrante en vivo (`InboundIngestion`), `Resolve` y `Reopen`, la respuesta desde QEP
  (`OutboundMessages`), toda asignación (`Conversation.Assign` —tomar, transferir—, el traspaso del bot y
  `TryAutoAssignAsync`), el eco (`CoexistenceIngestion`, R11) y marcar leída o no leída (`MarkReadAsync`, `MarkUnreadAsync`,
  `MarkUnreadFromAsync`). La campaña no. Una acción nueva del asesor sobre una conversación tiene que decidir qué hace con la
  bandera: si no la apaga, la siguiente parte del historial le pisa el estado o los no leídos sin error (precedente: marcar
  leída faltaba y el contador volvía). Y el resumen de la importación sólo escribe si la parte cambia algo: subir `version`
  sin cambio le da un 412 al asesor.
- **Un eco (`smb_message_echoes`) a un número desconocido crea la conversación `Resolved`, no `Open`** (P-5, owner
  2026-10-30). Abierta y sin asignar caería en la bandeja y en el triage del embudo (`TriageRules.IsPending` sólo mira
  abierta + cliente) sin que haya nada pendiente. No lleva `history_managed`, así que la regla de abiertas no la abre; la
  reabre el entrante en vivo con su `Reopened`. No la «arregles» a `Open` para que se vea el eco: el hilo está en
  «Resueltas».
- **Un cliente puede ser `Incomplete`** (sin CUC, dirección ni clasificación): lo crean la ingesta
  de Messaging, la importación de pedidos de Shopify (`Customer.CreateIncompleteFromExternalSale`, sin BSUID ni IGSID)
  y `POST /pos/customers` (incompleto **con** documento y teléfono, sin CUC; spec 2026-10-14, POSC-05). Los de
  Messaging y Shopify no traen documento, los del POS sí: el pre-chequeo de identificación de Pos (el 422 con `existing`)
  depende de que `FindExistingIdentificationsAsync` devuelva también a los incompletos dueños de un documento; no lo
  "optimices" con `Completeness == Complete`. Todo código nuevo que lea `Cuc`, `IdentificationType`, `Address`,
  `Country` o `ClassificationId` de un `Customer` tiene que tratar el `null`; Quotations lo rechaza con
  `client_incomplete` **antes** que `client_cuc_missing`, y un reporte que agrupe por clasificación o ciudad
  filtra `Completeness == Complete`.
- **El webhook de Meta (`/api/webhooks/whatsapp`) no se prueba con el stub de auth.** En modo stub el
  middleware de CSRF ni se registra, así que la exención por ruta sólo se ve con
  `Authentication__UseDevelopmentStub=false`. Y su limitador `webhook` no es el `Public`: es concurrencia
  global con cola, porque un 429 hace que Meta reintente hasta 7 días. El resto está en
  [README § Mensajería](README.md#mensajería-whatsapp-cloud).
- **La expresión normalizada del documento vive en dos lugares y tienen que ser iguales carácter por carácter** (spec 2026-10-24).
  `CustomerIdentificationSql.NormalizedNumber` (las consultas de coincidencias y anomalías) y el literal de la migración
  `AddCustomerIdentificationNormalizedIndex` (`upper(regexp_replace(identification_number, '[^0-9A-Za-z]', '', 'g'))`): una migración
  no lee constantes. Si difieren, la consulta sigue dando el resultado correcto pero Postgres deja de usar
  `IX_customers_tenant_identification_normalized` y recorre la tabla, sin error ni prueba de resultado que se ponga roja. Sólo lo
  dice `TheMatchQueryUsesTheNormalizedIndex`; cambia las dos juntas, con una migración nueva para el índice.
- **Todo camino que inserte o reclame destinatarios de campaña respeta el candado de `MarkFrozenAsync`** (spec 2026-10-24 §4.4). El
  desduplicado `duplicate_contact` corre una sola vez, al terminar de congelar la audiencia, con la campaña `FOR UPDATE`;
  `AddRecipientsAsync` inserta con `FOR SHARE` y sólo si `audience_frozen_at IS NULL`, y el worker no reclama mientras sea `NULL`. Un
  camino nuevo que escriba en `campaign_recipients` fuera de ahí deja un `Pending` que nadie desduplica y manda dos plantillas al
  mismo número; y un destinatario sembrado a mano en una prueba nunca sale si su campaña no tiene `audience_frozen_at`.
- **Un fixture de `message_templates` sin `id` deja el catálogo vacío** (spec 2026-10-14). El catálogo de envío lee
  del espejo `messaging.message_templates`, que indexa por `meta_template_id`, y el refresco salta los elementos sin
  `id`: el síntoma es `messaging.template.not_available` en una prueba que antes pasaba, no un error del espejo. Y el
  webhook `message_template_status_update` hay que suscribirlo en la app de Meta: sin él los estados sólo cambian
  con el refresco.
- **Integrations no usa el `IAuditRecorder` ni el `IOutboxWriter` compartidos:** los dos están
  ligados a `TenancyDbContext`, y una segunda ligadura le robaría en silencio la auditoría o el outbox a
  Tenancy. Un módulo nuevo con auditoría atómica declara su propio puerto (`IIntegrationsAuditRecorder`,
  como `IIdentityAuditRecorder`). Y un consumidor de una conexión nunca referencia
  `Modules.Integrations.Application`: declara su puerto y el adaptador va en Bootstrapper
  (`IntegrationsLayerTests`).
- **Instagram es un canal de la misma bandeja de Messaging, no un módulo** (spec 2026-10-14). La conversación lleva
  `channel` (`WhatsApp` | `Instagram`) y en Instagram `user_id` es el IGSID numérico y `wa_id` es siempre `null`: todo
  código nuevo que empareje por teléfono tiene que tratar ese `null`. Las plantillas, las campañas y `POST /conversations`
  responden `messaging.channel_unsupported`; el catálogo de plantillas de una conexión de Instagram devuelve `[]`, no 422.
- **El webhook de Instagram (`/api/webhooks/instagram`) se firma con `Meta:Instagram:AppSecret`, no con `Meta:App:AppSecret`**
  (V-1): son dos secretos distintos aunque vivan en la misma app de Meta. El verificador acepta cualquiera de los dos para
  Instagram, así que una firma inválida en producción casi siempre es que falta `META_INSTAGRAM_APP_SECRET` en el pipeline.
  El `timestamp` de Instagram viene en **milisegundos**; el de WhatsApp, en segundos. Y los echo (lo que el negocio responde
  desde la app) llegan dentro de `messages` con `is_echo`, no en un campo aparte: la ingesta los deduplica por `mid` con lo
  que QEP ya mandó.
- **Un adjunto de Instagram no tiene `meta_media_id`: tiene `message_media.source_url`**, una URL pública con vencimiento
  que el worker de medios baja sin token. `MediaTransfer` decide el camino por `SourceUrl`; `InsertMediaAsync` acepta
  `meta_media_id = ''` si hay URL. Hacia afuera no hay subida a Meta: al adjunto saliente se le da una URL prefirmada de R2
  de 10 minutos (`IMessagingMediaStore.CreateDownloadUrlAsync`), y la nota de voz se transcodifica a WAV con Concentus
  (`ConcentusOpusTranscoder`) porque Instagram no acepta Opus. V-4 y V-8 (que Meta descargue la URL firmada y acepte el
  WAV) siguen **sin verificar contra Meta real**: el handoff dice qué mirar.
- **El payload de los eventos de Quotations es contrato con Pipeline** (spec 2026-10-13 §4.5).
  `QuotationLifecycleEventPublisher` escribe `quotations.quotation.sent.v1`, `…converted.v1` y
  `quotations.order.cancelled.v1` con campos camelCase, y `QuotationEventPayloads` los lee por nombre: renombrar
  un campo compila, pasa las pruebas de Quotations y deja los mensajes marcados como procesados sin efecto. Se
  cambia en los dos lados, y en `QuotationEventPayloadsTests`. Pipeline lee las conversaciones de Messaging por
  `MessagingDbContext` desde Bootstrapper (`PipelineConversationDirectory`), como Reporting lee Quotations: no
  por un puerto de Messaging.
- **Un pedido tiene datos propios y puede no tener cotización** (spec 2026-10-15). `Order` lleva cliente,
  asesor, cuenta de cobro, partes, líneas y totales; `QuotationId` es opcional y `Source` dice de dónde nació
  (`Quotation`, `Manual`, `Shopify`). Convertir **copia**: la cotización `Converted` queda congelada y editar un
  pedido pendiente edita `order_items`, nunca `quotation_items`. Un pedido sin sesión se crea sólo por
  `IExternalOrderWriter` (acepta cliente incompleto); el manual exige cliente completo con CUC. Los eventos
  `quotations.order.created.v1` y `order.cancelled.v1` llevan `source` y `quotationId` nullable: Pipeline los lee
  por nombre y guarda la foto en `pipeline.deal_orders` (un `order.created` **con** cotización no hace nada: ya
  actuó el `converted`). `Observaciones` del Excel sale de `Order.Notes`, que es un solo campo al
  convertir (el `notes` del cuerpo la reemplaza; `null` hereda las de la cotización; `""` la deja vacía; nunca se
  concatenan). Un `INSERT` a mano en `quotations.orders` (siembras, pruebas) tiene que llenar las columnas propias
  y copiar `order_items`/`order_parties`: son `NOT NULL` desde `AddOrderOwnData`.
- **El embudo de un tenant nace en su primer acceso, no al prender el módulo.** Tenancy no publica nada al
  activar una capacidad; `PipelineProvisioner` crea el embudo con `INSERT … ON CONFLICT (tenant_id) DO NOTHING`.
  Una consulta directa a `pipeline.stages` de un tenant que nunca abrió el tablero devuelve cero filas: no es
  un error.
- **Dueño de un negocio: la API exige membresía activa, el evento no.** Crear un negocio por la API con un
  responsable que no es miembro activo da 422 `pipeline.deal.owner_not_member`; el negocio que crea un evento
  de cotización conserva al asesor de la cotización aunque ya esté inactivo: la cotización existe y su
  asesor es un hecho, no una elección del usuario. No unifiques las dos reglas.
- **El orden del historial lo da el ordinal del dominio, no `occurred_at`.** `DealStageHistoryEntry.Ordinal` lo asigna
  el agregado (único por `(tenant_id, deal_id)`); dos cambios en el mismo instante se ordenan por él. Y la carrera
  del triage se cierra con el índice único `IX_triage_decisions_conversation_inbound`, que
  `PipelineUnitOfWork` traduce a `pipeline.triage.already_decided` **por nombre de índice**: no la muevas a una
  comprobación previa en el handler, que dejaría dos negocios.
- **EF ordena los `UPDATE` de un `SaveChanges` por clave primaria, no por el orden en que tocaste las entidades.**
  Con un índice único parcial que admite una sola fila (`IX_business_hours_tenant_default`, `WHERE is_default`), «quitar
  el predeterminado al anterior y ponérselo al nuevo» en un solo `SaveChanges` falla con 409 cada vez que el nuevo tiene
  un id **menor** (GUID v7 más antiguo): su `UPDATE … true` corre antes que el `… false` del anterior. La prueba que sólo
  mueve el predeterminado del más viejo al más nuevo pasa por casualidad. La solución es dos `SaveChanges` en una
  transacción explícita con el orden fijo (`BusinessHoursRepository.SwapDefaultAsync`, 2026-10-21), como
  `TryAutoAssignAsync`; y cualquier `SaveChanges` fuera de `MessagingUnitOfWork` pasa por `MessagingSaveChanges.SaveAsync`,
  que es donde viven las traducciones por nombre de índice —un `SaveChanges` directo del `DbContext` responde 500 donde
  tocaba 409 o 412. Un intercambio parecido en otro módulo (un «único activo» por tenant) tiene el mismo problema.
- **Dos eventos del mismo cambio en el hilo necesitan `occurred_at` distinto.** `MessageQueries` ordena por
  `occurred_at, id` y los GUID v7 **no** están ordenados dentro del mismo milisegundo, así que «pausó el asistente» salía
  antes que «tomó la conversación». `ConversationAssignmentSteps.CommitAsync` y `TryAutoAssignAsync` escriben el evento
  n-ésimo de un cambio en `now + n µs`; una prueba que compare los tipos sin orden esconde la regresión.
- **En esta máquina las pruebas de integración necesitan `$env:Seed__Enabled = "false"`.** Los user-secrets traen
  `Seed:*` y el host de pruebas falla en el arranque con `Seed:OwnerEmail is required when Seed:Enabled is true`. No es
  del código: es la configuración local del developer (2026-10-21).
- **Un `FromSql` crudo debe listar todas las columnas mapeadas de la entidad.** El `SELECT … FOR UPDATE` de
  `QuotationSettingsStore` no se puede componer (EF lo envolvería en una subconsulta), así que EF no completa
  las columnas que falten: al agregar `freight_tax_rate_id` a `tenant_quotation_settings`, guardar el mínimo de
  compra dio 500 hasta que se sumó al `SELECT`. Al extender una tabla, busca los `FromSql` que la leen.
- **Una prueba de migración que baja a una versión vieja tiene que volver a la última antes de llamar a la
  API.** El modelo de hoy pide las columnas de las migraciones posteriores (`AddFreight`); sin
  `MigrateAsync(null)` al final, el `POST` de la prueba da 500 (`QuotationSettingsMigrationTests`).
- **Al agregar una columna al catálogo del Excel de pedidos, revisa los encabezados de las fijas de la semilla.**
  Una columna nueva cuyo encabezado choca con una visible del layout guardado nace oculta en silencio, sin
  error: la regla de choque está en
  [README § Columnas del Excel de pedidos](README.md#columnas-del-excel-de-pedidos-por-tenant-homologación).
- **El webhook de Shopify (`/api/webhooks/shopify`) se prueba con el stub de auth apagado**, igual que el de Meta: en
  modo stub `RequireCsrfHeaderMiddleware` ni se registra, así que una prueba con los headers `X-*` pasa aunque la
  exención esté rota (`ShopifyApiHarness.WithRealAuthentication`, `ShopifyWebhookApiTests.WebhookIsExemptFromCsrf`). El
  webhook se exime por el prefijo `/api/webhooks/` y el inicio B de la instalación por ruta exacta
  (`SignedAnonymousPaths`, duplicada de `IntegrationsEndpoints` y amarrada por `ShopifyInstallApiTests`); `install/complete`
  **no** está exento: lo llama el SPA con `X-Qep-Client` (2026-10-18, `0c6c06ae`). Una ruta anónima nueva que firme Shopify va a esa
  lista, con su prueba, o responde 403 sólo en producción.
- **Un pedido externo repetido lo frena `IX_orders_tenant_external_key`, no la búsqueda previa.** `ExternalOrderService`
  busca por la clave antes de crear, pero el webhook y la conciliación pueden llegar a la vez, y el worker puede caer entre
  crear el pedido y guardar la fila del espejo: lo único que deja un solo pedido es el índice único parcial
  `(tenant_id, source, external_key)`, que `QuotationsUnitOfWork` traduce **por nombre** a `order.order.external_key_taken`
  y el adaptador resuelve releyendo el existente (`AlreadyExisted`). No lo cambies por una comprobación en el handler. La
  clave es `"{shopId}:{shopifyOrderId}"` y no la conexión, para que borrar y volver a enlazar la tienda no duplique; un
  `INSERT` a mano en `quotations.orders` con `source = 'Shopify'` necesita una clave que no exista. Precedente (Review
  Focus 1 de `docs/superpowers/plans/2026-10-18-shopify-backend.md`): borrar la fila del espejo y recibir el pedido otra vez deja un pedido, y si llega cancelado lo anula —hasta
  `8b58fe9f` (2026-10-18) la fila nueva cancelada quedaba `Ignored` con el pedido vivo (`ReimportAfterMirrorRowDeleted*`).
- **Todo cambio de una fila del espejo de Shopify pasa por `ShopifyOrderIngestion.ApplyAsync`.** El webhook, la
  conciliación, «Sincronizar ahora» y el reintento manual llaman al mismo método, que captura el estado anterior antes de
  `ApplySnapshot` y corre la reacción de pago y cancelación (§6.3) antes del guardado (2026-10-18, `82c4b2d9..f9b98fd1`, `WorkerFailedReactionIsSeenAgainOnRetry`). Llamar al importador o
  a `ApplySnapshot` directo compila y pasa las pruebas del importador, y pierde en silencio el pago o la cancelación.
- **`ShopifyLayerTests` prohíbe que el módulo Shopify referencie módulos de negocio:** Application sólo ve Tenancy y Audit.
  La conexión (Integrations), el pedido (Quotations), el cliente (Customers), el producto (Catalog) y la ciudad
  (Geography) entran por los puertos de `ShopifyPorts.cs`, con adaptador en `src/Bootstrapper/Shopify*.cs`. El costo son
  dos copias que se cambian juntas, cada una con su prueba de paridad en `Bootstrapper.UnitTests`, que ve los dos módulos:
  `ShopifyWebhookTopics` vive en Integrations.Application (los que el enlace registra) y en Shopify.Application (los que el
  worker atiende), amarradas por `ShopifyWebhookTopicsParityTests` (2026-10-18, `7b83a958`), y
  `ShopifyCashOnDeliveryGateways.Normalize` copia `CarrierName.Normalize`, amarrada por `ShopifyNormalizationParityTests`
  (2026-10-18, `e157d9c2`).
- **`develop` no va a `main` sin el frontend de Shopify** (2026-10-18). `shopify` está en `DefaultForNewTenants`
  (`TenantModuleKey.cs`, decisión del owner del 2026-10-15) y en `Production` la app está configurada a la fuerza —sin ella
  el pod no arranca—, así que el catálogo de Integraciones de todo tenant nuevo trae la tarjeta Shopify con
  `onboarding.kind = "ShopifyOAuth"`. El frontend de `develop` (`c940537`, `provider-catalog.tsx`) trata todo `kind` que no
  sea `Form` ni `InstagramLogin` como WhatsApp: el «Conectar» de Shopify lleva a `/integrations/whatsapp/new`, el Embedded
  Signup de Meta. Pasa igual si `develop` llega a `main` por otra razón (un hotfix, otra feature): antes de llevarlo, el
  frontend de Shopify tiene que estar listo para desplegarse junto. La alternativa es del owner: sacar `shopify` de
  `DefaultForNewTenants` hasta que exista el frontend (una línea y su prueba, más la entrada de `pos` de este archivo, que
  cuenta siete módulos, y el README). El paso a paso está en el README, § Integración con Shopify, «Despliegue».
- **Un token del pipeline sin resolver llega literal, no vacío.** Si la variable no está en el grupo `Backend-prod`, la
  plantilla deja `#{NOMBRE}#` en el ConfigMap o el Secret, y un `Require` que sólo mira `IsNullOrWhiteSpace` lo da por
  configurado: el pod arranca y rechaza cada firma (es el síntoma que la entrada de Instagram atribuye a
  `META_INSTAGRAM_APP_SECRET`). `ShopifyAppOptionsValidator` trata `#{…}` como ausente en `Production` desde `f72c7d1b` (2026-10-18),
  como `DeploymentInfo.Normalize`; un secreto requerido nuevo lleva la misma regla.
- **La variante por defecto lleva el id del producto, y el relleno de `variant_id` depende de eso** (spec 2026-10-20 §8).
  `Product.Create` le da a la primera variante el id del producto —también al crear con opciones—, y `AddProductVariants`
  hizo lo mismo con cada producto que existía. Por eso `AddVariantToLines` (Quotations) y `AddVariantToSaleLines` (Pos)
  rellenan con `variant_id = product_id` sin consultar `catalog`, que es otro esquema y otro `DbContext`. No cambies
  `Create` para que estrene un id: las pruebas que mandan el id del producto como `variantId` dejan de valer en silencio.
  Al revés tampoco sirve: `variant_id = product_id` **no** dice que la variante sea la por defecto (un producto con
  opciones también tiene una con ese id); eso lo dice `OptionIds` vacío, y fuera de Catalog no se ve.
- **Un `INSERT` a mano en `quotation_items`, `order_items` o `sale_lines` necesita `variant_id`.** Es `NOT NULL` sin default
  desde `AddVariantToLines` y `AddVariantToSaleLines`, y va junto con el `product_id` **de esa variante**. Del lado de
  Catalog, `catalog.products` ya no tiene `code` y `catalog.product_prices` se une por `variant_id`: un SQL que lea el
  catálogo pasa por `catalog.product_variants` (precedente: `ExportLoadSeeder`). Las pruebas de migración que siembran
  **antes** de esas migraciones no lo llevan: la migración lo rellena.
- **Un cuerpo crudo con `productId` ya no sirve en las líneas.** Las de cotización, pedido y POS (`AddQuotationItemRequest`,
  `BatchQuotationItemAdditionRequest`, `QuotationEditItemRequest`, `OrderItemAdditionRequest`, `OrderEditItemRequest`,
  `PosSaleLineRequest`, `PosPreviewLineRequest`) llevan `variantId`, y la copia de escalas `sourceVariantId` /
  `targetVariantIds`. Un objeto anónimo con `productId` se deserializa con `VariantId = Guid.Empty` y responde `422
  validation.failed` por el `VariantId` vacío, con la clave según la ruta: `VariantId` en `POST /quotations/{id}/items`,
  `ToAdd[i].VariantId` en el batch y en `POST /orders/{id}/items`, `Items[i].VariantId` en las listas completas
  (alta, guardado y preview de cotización y pedido) y `Lines[i].VariantId` en el POS. Una variante repetida en una lista
  completa sale en `errors.Items`, sin posición. El síntoma es una prueba que esperaba `Created`, no un error de nombre
  de campo. Los records tipados se arreglan solos; el barrido es por `new { productId` y por JSON literal.
- **`CatalogVariantUsageChecker` lee tablas de Quotations y Pos por nombre.** Es SQL crudo en Bootstrapper sobre
  `quotations.quotation_items`/`order_items` (unidas a `quotations`/`orders` por el tenant) y `pos.sale_lines` (unida a
  `pos.sales`), y corre en la conexión del `PUT` de producto para quedar dentro de su transacción. Renombrar
  `variant_id` o una de esas tablas compila, y el `GET` y el `PUT` de producto responden 500 (`42703`). Sólo
  `ProductVariantDeletionApiTests` usa un doble del puerto; el resto de las pruebas de Catalog que leen o guardan un
  producto corre el checker real contra el esquema migrado y se cae con ese 500, que no se parece a la causa. Se cambia
  junto con el checker y sus pruebas: `CatalogVariantUsageCheckerTests` (que arma réplicas mínimas de las tablas, a
  mano: no siguen solas a la migración) y las dos `TheCatalogUsageQueryFindsTheBackfilledVariant`
  (`VariantLinesMigrationTests`, `SaleLinesVariantMigrationTests`), que resuelven el checker real del contenedor y lo
  corren contra el esquema de las migraciones.
- **`CustomerMergeReferences` escribe en cinco esquemas por nombre.** La fusión de clientes conoce por nombre las tablas de
  quotations, pos, pipeline, messaging y shopify: es SQL crudo en Bootstrapper sobre `quotations.quotations`/`orders.client_id`,
  `pos.sales.customer_id`, `pipeline.deals`/`deal_quotations`/`deal_orders`/`triage_decisions.customer_id`,
  `messaging.conversations.customer_id`, `messaging.campaigns.audience_customer_ids` y `shopify.orders.qep_customer_id`, sobre la
  conexión del `CustomersDbContext` para quedar dentro de su transacción. Renombrar una de esas columnas compila y la fusión
  responde 500 (`42703`); como todo es una transacción, no fusiona nada. Lo amarran `CustomerMergeReferencesTests` (réplicas
  hechas a mano: no siguen solas a una migración) y `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema`.
  Una tabla nueva que guarde un id de cliente vivo entra en `MoveAsync` y en esas dos pruebas, o la fusión la deja apuntando a un
  cliente inactivo. Y `merged_into_id` apunta siempre a un no fusionado: quien lea un fusionado sigue un solo salto.
- **El `Version++` del fusionado es lo que impide fusionar al mismo cliente en dos que quedan.** `ApplyMergedInto` pasa por
  `Touch` y sube la versión del fusionado, así que dos fusiones simultáneas del mismo cliente hacia distintos que quedan
  chocan por concurrencia optimista, y `merged_into_id` lo saca de toda fusión posterior (`customers.merge.already_merged`).
  No quites el `Touch` ni marques fusionados con un `ExecuteUpdate` que se salte el agregado.
- **`OrdersReportSource` lee `quotations.orders` y `pos.sales` por nombre** (spec 2026-10-23). Es SQL crudo en
  Bootstrapper, un `UNION ALL` de las dos tablas (`ReportRowsSql`), porque son de `DbContext` distintos y EF no los une.
  Renombrar una columna de cualquiera de las dos compila y deja `/reports/orders` y `/summary` respondiendo 500: lo amarran
  `OrdersReportPosApiTests` y las demás pruebas `OrdersReport*`, que corren contra el esquema migrado. Un estado nuevo de
  `PosSaleStatus` u `OrderStatus` que no sea venta tiene que entrar en `is_void`, o empieza a sumar en el resumen sin
  error. Y un filtro opcional nuevo se omite del texto cuando no viene, no se manda como parámetro nulo: PostgreSQL no
  tipa un `NULL` suelto (`could not determine data type of parameter`).
- **Borrar una variante es explícito: `deletedVariantIds`. Omitirla del `PUT` responde `catalog.variant.missing_in_request`.**
  No lo «simplifiques» a «la que no viene se borra» (era lo que pedía el plan, y el spec §6 lo corrigió): la tabla de
  variantes llega a 2.048 filas y el formulario la pagina, así que una fila ausente puede ser una página que no se
  mandó. La listada se borra si ninguna línea la usa y se desactiva si alguna sí; borrarla se lleva su historial de
  precios por `CASCADE`.
- **La regla de recorte de la foto del nombre vive en `BuildingBlocks.Domain/SnapshotText.cs`.** El nombre visible de una
  variante pasa de los 200 de `product_name` (producto de 200 más hasta tres valores de 60), y
  `QuotationProductSnapshot` y `PosSaleLine` lo recortan con `SnapshotText.Fit` (200 con «…», sin partir un par
  sustituto). No copies la regla en un módulo —la copia de Pos ya se retiró una vez—: Quotations y Pos no se ven entre
  sí, y dos copias divergen sin que nada se ponga rojo. Ensanchar la columna es una migración en cada módulo más su
  constante (`QuotationProductSnapshot.NameMaxLength`, `PosSaleLine.ProductNameMaxLength`).
- **`Messaging:Automation:Enqueue` está prendido (plan 2) y es lo único que hace que el bot conteste.** Sin el flag el webhook no
  encola y el bot queda mudo sin error. La cola se purga (filas procesadas a los 7 días, `Automations:Workers:InboxRetentionDays`) y
  antes de desplegar la migración `AddAutomationEngine` en un ambiente donde alguien puso a mano `automation_status` ≠ 'None', la
  migración normaliza esas filas a 'None'.
- **El motor sólo responde una conversación `Open`, sin asesor y con el asistente `Active`; `IBotOutbound` lo vuelve a verificar al
  enviar** (`RunProcessor.DecideAsync`): entre el claim del inbox y el envío un asesor puede haber tomado la conversación.
- **Un entrante que llega fuera de orden no se encola para el bot** (`InboundIngestion.ShouldEnqueue`, 2026-10-23). La cola
  ordena por llegada y Meta reintenta una entrega fallida hasta horas después: el 2026-10-22, con QEP sin recibir unos diez
  minutos, un «hola» de las 21:42 llegó a las 22:01 detrás de uno de las 21:56 que el bot ya había contestado, y el bot mandó
  el mismo mensaje otra vez sin que el cliente escribiera. Por eso se compara su `occurred_at` contra el `last_inbound_at`
  **de antes** (el CTE `old`; el `SET` ya lo pisó con `GREATEST`). No lo muevas al motor: ahí sólo cubriría uno de los tres
  caminos. Una prueba que postee webhooks con timestamps decrecientes ya no ejerce el bot desde el segundo.
- **El `clientId` de un mensaje del bot no depende del `attempts` de la fila.** Es `DeterministicClientId.For(runId,
  baseStepNumber, effectIndex)` y el id de una corrida nueva sale del id del mensaje del inbox (`RunIdFor`), así un reintento de la
  fila —o un `Start` que se deshizo— reusa corrida y `clientId`s y Mensajería, idempotente por `clientId`, no duplica (P2-R5). El
  plan escribía `(runId, nodeId, índice, attempts)`: con `attempts` el reintento SÍ reenvía. El motor sube `StepNumber` en cada bloque
  que entra, también al reenviar uno que espera, para que dos unidades confirmadas nunca compartan base.
- **El cliente HTTP del bloque se registra en Bootstrapper, no en `AddAutomationsInfrastructure`**, porque su handler anti-SSRF
  (`ExternalApiUrlPolicy.CreateHandler`) es de `Integrations.Infrastructure`. Sin redirecciones, sin proxy, `RemoveAllLoggers()`: el
  log de `HttpClient` escribiría la URL con la query del cliente. Cambiar el handler sin pasar por ahí deja a cualquier admin con un
  proxy hacia la red interna; `CompositionRootTests` amarra que el handler sea el de la política.
- **Una llamada HTTP del bloque no se puede deshacer, y la unidad sí.** Si el proceso muere entre la llamada y el commit, el
  reintento vuelve a llamar (P2-R37; la API de Clasic no tiene llave de idempotencia). Si `http_calls` no se puede escribir, se loguea
  y se sigue: lanzar deshace la unidad y repite la llamada.
- **Nada de lo que responde una API externa, sus headers ni la clave sale a logs, auditoría ni `http_calls`** (D-H5, D-H6). La
  prueba `TheKeyNeverAppearsInLogsAuditOrHttpCalls` corre el host en `Trace`, porque el log de headers de `HttpClient` es `Trace`:
  a `Information` quitar `RemoveAllLoggers()` pasaría sin verse. `authHeader` no puede ser un header reservado (`Host`, `Content-*`,
  `Proxy-*`…, P2-R30); la lista vive en `ExternalApiUrlPolicy.IsReservedHeader` y copiada en el patrón del catálogo
  (`IntegrationProviders.ExternalApi`), amarradas por una prueba: se cambian juntas.
- **Un valor del cliente en la ruta del bloque HTTP se escapa y se vigila** (P2-R31/R32/R34): `Uri.EscapeDataString`, y un segmento
  `.`/`..` (también `%2E%2E`) o un valor vacío o de sólo `/` y `\` (P2-R46) rechaza la llamada (`path_dot_segment`,
  `path_value_empty`) sin hacerla, porque
  `System.Uri` resuelve `..` aun codificado y un `{id}` vacío llega a la colección con un `PUT`. El cuerpo se interpola
  JSON-aware (P2-R11): dentro de un string, el texto escapado; suelto, el literal JSON (`null` si falta la variable).
- **El mapeo de la respuesta no usa la gramática de las variables.** `mapping.from` acepta `$`, `$[0]`, `[0].id` y rutas con puntos
  (`JsonPath.IsValidResponsePath`, P2-R36): Clasic responde arreglos en la raíz (`[].id`, Anexo C). Un `from` que no cumple es
  `http.mapping_path_invalid` al validar (antes guardaba `null` en silencio). Y el validador detecta `{referencia}` con un patrón
  más laxo (`VariableToken`: `{_x}`, `{a..b}`) que `TemplateText`, que es lo que el motor reemplaza.
- **Esperas con tope, pero el lease de 30 s no acota la unidad.** El adjunto del bloque HTTP se espera 20 s (4 × 5 s, un presupuesto
  por bloque para la url y el archivo), no los 60 s de D-H8; el motor corta en `EngineReentries.Max = 25` reentradas por unidad (HTTP
  u horario que vuelven a entrar al motor) con `automations.run.loop_detected`, igual que el simulador (P2-R40); y un solo
  `ClaimAsync` reclama una fila, para que cada unidad arranque con el lease entero (P2-R24). Aun así el peor caso (25 × (20 + 10) s
  más 15 s por envío) pasa el lease: por eso el candado de la conversación es `pg_try_advisory_xact_lock` y, ocupado, la fila se
  reprograma a 5 s (P2-R45) en vez de bloquear al worker; cada reprogramación gasta un intento (README, «El lease de 30 s no acota la
  unidad»). No vuelvas al `pg_advisory_xact_lock` que espera.
- **`http_calls.outcome` mide 24, no 16** (spec §4.2 dice 16; `response_too_large` ya tiene 18, migración `WidenHttpCallOutcome`).
  Un outcome nuevo más largo es otra migración.
- **La causa de un traspaso del bot vive en el evento `AutomationPaused` (`details.handoff`), no en la corrida** (spec 2026-10-24).
  `conversation_runs.failure_code` sólo la tiene cuando falló; un bloque «Pasar a un asesor» alcanzado deja `HandedOff` sin código. Un
  `connection_unavailable` del bloque HTTP lleva el motivo en `ErrorCode` (`secret_unreadable`, `not_chosen`, …) y sigue sin fila en
  `http_calls`. `IIntegrationConnections.ResolveAsync` devuelve `ConnectionResolution`: un consumidor nuevo lee `.Connection` y decide qué
  hace con `.Unavailable`, y los códigos `snake_case` los arma sólo `ConnectionReasonCodes` (Bootstrapper). `HandoffCauseKind`
  (Automations) y `AutomationHandoffKind` (Messaging) se cambian juntos: los amarra `HandoffCauseKindParityTests`.
- **El simulador corre el borrador, no la versión publicada, y `Start` exige cuerpo** (`{ "liveHttp": false }`; sin cuerpo no es lo
  mismo). Con `liveHttp` un bucle de HTTP puede tardar `25 × timeoutSeconds` en un solo request. El bloque HTTP del simulador usa
  `JsonPath.SelectResponse`, el mismo mapeo del ejecutor.
- **Las plantillas del sistema de Automatizaciones se cargan al arrancar**: un JSON embebido roto tumba el host (a propósito).
- **`GET /customers` de Clasic responde `items`/`totalCount`, no `data`/`total`, y el cliente se busca con `?document=`, no con
  `?identificationNumber=`** (plan 4, 2026-10-23). `identificationNumber` es la búsqueda del admin y casa por subcadena: con «1» el
  bot leería el cliente, las direcciones y la orden de otra persona; `document` es igualdad exacta. `?phone=` recibe el `wa_id`
  completo (`57…`) y Clasic recorta a diez dígitos; con el valor vacío responde **400 a propósito** (QEP manda `telefono = ''` sin
  `wa_id`, y un filtro vacío ignorado le aceptaría la política a un cliente cualquiera): ese 400 va por `error` al asesor, no se
  «arregla» en Clasic. Y `/services` y `/departments/*` son públicas, así que la ruta de prueba de la conexión «API externa» tiene
  que ser una protegida (`/failures`, D-K10): con una pública, «Probar» da OK con cualquier clave.
- **En una Lista dinámica, `x.id` es el id de la fila (texto) y `x.item.id` es el valor del elemento.** Un cuerpo JSON hacia
  una API que espera un número lleva `{x.item.id}` sin comillas: `"{x.id}"` compila, pasa las pruebas con el fake y Clasic
  responde 500 (`invoiceItems[0].id` como string; verificado el 2026-10-23 en la revisión final del plan 4). En una query de
  URL sí va `{x.id}`, porque es texto.
- **El respaldo colombiano del formato regional vive en tres copias que se cambian juntas** (spec 2026-10-23, D-R5), porque los
  módulos no se ven entre sí: `RegionalNumberFormat`/`RegionalDateFormat` (`Modules.Tenancy.Application`),
  `EmailRegionalFormat.Colombian` (`Modules.Notifications.Application`, que no ve Tenancy) y `QuotationPdf.LegacyNumberFormat`
  (dominio de Quotations: el formato con el que se supone generada una fila de `quotation_pdfs` con `number_format` nulo). Cambiar
  `TenantNumberFormats.CommaDecimal` sin la del dominio compila y regenera en silencio todos los PDF guardados de los tenants
  colombianos en el siguiente export. Las amarran `EmailRegionalFormatTests.ColombianMatchesTheTenancyFallback` y
  `QuotationPdfTests.ALegacyPdfWithoutFormatCountsAsCommaDecimal`. Y el historial de cambios es texto congelado: un tenant que
  cambia de formato ve las filas viejas con el anterior, y eso no es un bug.
- **`OrdersExportProcessor` no lee el formato del tenant, a propósito** (D-R8): la hoja del pedido la importa el ERP.
  «Unificarlo» con el Excel de cotizaciones compila, pasa sus pruebas unitarias y rompe la importación en silencio. La amarra
  `OrderExportApiTests.TheErpSheetIgnoresTheTenantRegionalFormat`.
- **`other` de Botones/Lista existe sólo con `fallback = otherOutput`** (spec 2026-10-25 D-3): `FlowNodeKinds.OutputsOf` la omite con
  `repeat` y `handoff`, el motor no la sigue aunque la versión publicada la tenga, y `FlowNormalizer` la quita al guardar. Al agotar los
  intentos sin salida conectada manda `Chatbot.ExhaustedAction` (`Restart` por defecto), no el flujo. El traspaso a un miembro pasa por
  `IMessagingAssignees.CanReplyAsync` en el adaptador: si no puede, cae sin asignar sin fallar.
- **`customers.data_consents` es prueba legal: sólo se inserta** (spec 2026-10-26 D-5). Nada la edita ni la borra salvo la fusión,
  que mueve `customer_id` (`CustomerMergeStore.MoveOwnedAsync`); el estado vigente es la última fila por `(occurred_at, sequence)`.
  Una respuesta es una fila: el reintento de una unidad del motor lo frena `IX_data_consents_reply_message`, que `CustomersUnitOfWork`
  traduce por nombre; no lo cambies por la consulta previa. Los CHECK de la tabla exigen identidad de canal en lo que escribe el bot y
  en `Revoked` + `ThisConversation`, y la prohíben en `Revoked` + `Other`: un `INSERT` a mano tiene que cumplirlos. El texto que se
  guarda es el que salió (`RunState.ConsentPrompt`, columna `conversation_runs.consent_prompt`), no la plantilla del bloque.
  **Dos relojes:** la fila del bot lleva la hora de Meta (truncada al segundo) y la revocación la del servidor; ordenar por
  `(occurred_at, sequence)` es lo buscado, nunca por `sequence` sola. **Un toque vale sólo para la pregunta que cita:** los ids
  `accepted`/`rejected` son fijos y WhatsApp deja tocar los botones viejos, así que un toque cuyo `reply_to_message_id` no es el
  `ConsentPrompt.PromptMessageId` vigente es una respuesta inválida (`FlowEngine.IsStaleConsentTap`); la fila guarda el citado en
  `replied_to_message_id`. **Deploy y rollback:** el kind `dataConsent` es sólo hacia adelante —un backend viejo no lee un flujo
  publicado que lo tenga— y el `Down` de `AddCustomerDataConsents` borra la prueba legal: nunca se corre. El rollback es revertir el
  código y dejar la tabla.
- **«Política de datos» tiene tres modos para «No acepto»** (spec 2026-10-26 repetir): `onReject` = `repeat` | `rejectedOutput` (default de todo bloque viejo) | `handoff`. `rejected` existe sólo con `rejectedOutput`: `OutputsOf` la omite y `FlowNormalizer` la quita. En `repeat`, el contador es `RunState.ConsentRejections` (columna `conversation_runs.consent_rejections`), no `Attempts` ni `ConsentPrompt`: vuelve a 0 en `Go`, `Leave` y `Restart`, nunca en `Resend`. En los dos modos sin `rejected`, todo camino que hoy saldría por ella (intentos agotados, cliente sin registrar) traspasa con `onRejectMemberId`, sin leer la regla de agotamiento del chatbot. Los modos son sólo hacia adelante: tras un rollback del backend, el motor viejo sigue la `rejected` ya quitada como `null` y termina con `EndRun(OutputUnconnected)` ante «No acepto» o el agotamiento, así que toda versión publicada en `repeat` o `handoff` hay que republicarla en `rejectedOutput`.
- **El sinónimo de una ciudad casa después de los dos nombres, y confirmar uno igual a una ciudad real es 422** (spec 2026-10-26).
  `CityRepository.MatchByNameAsync` compara el nombre, luego el oficial del DANE y sólo si ninguno casa busca el sinónimo vivo:
  una fila de sinónimo con el texto de una ciudad real —sólo llega por SQL— nunca le gana. `resolve` e `impact` lo rechazan con
  `geography.synonym.matches_city`. Si inviertes el orden, el sinónimo le quita el pedido a una ciudad legítima sin error. Lo
  amarran `ARealCityNameWinsOverASynonymWithTheSameText` y `CityOperatorApiTests`.
- **`CityCorrectionInsights` lee `quotations`, `customers`, `shopify`, `tenancy` e `identity` por nombre.** Es SQL crudo en
  Bootstrapper que alimenta los impactos y los contadores de la consola de ciudades. Renombrar una columna de cualquiera de esas
  tablas compila y deja respondiendo 500 (`42703`) a los dos `impact`, a `GET synonyms` y `GET unresolved/{id}`, y a `resolve`,
  `PUT synonyms/{id}` y `retry` **después** de confirmar su escritura (la respuesta lee los contadores): el cambio queda hecho
  aunque el cliente vea el 500. El recorrido (`POST backfill`) no lee este adaptador. Lo amarra `CityCorrectionInsightsTests`
  (en el proyecto de Shopify, que es el que sabe armar pedidos, clientes y huellas reales): una tabla o columna nueva que lea el
  adaptador entra ahí.
- **El payload de `geography.city_synonym.*` es contrato con Shopify y vive literal en dos pruebas.** `GeographyEventPublisher`
  lo escribe y `CitySynonymEventPayloads` lo lee por nombre: renombrar un campo compila, pasa las pruebas de cada lado por
  separado y deja el mensaje marcado como procesado sin trabajos sembrados. Se cambia en los dos lados y en las dos pruebas con el
  mismo literal, `GeographyEventPublisherTests` y `CitySynonymEventPayloadsTests`.
- **Un trabajo de corrección de ciudad aplica la ciudad *actual* del sinónimo, y uno borrado salta lo pendiente.**
  `ShopifyCityCorrectionProcessor` relee el sinónimo al procesar: si el operador lo corrigió entre el evento y el trabajo manda la
  ciudad nueva, y si lo borró, el trabajo termina `Skipped` con `synonym_deleted` y el pedido queda como estaba. Por eso borrar
  no publica nada ni deshace lo ya corregido, y por eso `IShopifyCitySynonymLookup` devuelve también los borrados: filtrarlos
  haría que el trabajo no supiera por qué saltó. Lo amarran `CitySynonymEndToEndTests.DeletingWhileJobsArePendingSkipsThem` y
  `ShopifyCityCorrectionProcessorTests`.
- **`run_units` se escribe en el mismo `SaveChanges` de la unidad y vive 3 días (`RunDetailRetentionDays`); armarla nunca lanza
  (`RunUnitWriter`).** Un camino nuevo que cierre una corrida fuera del motor llama `WriteClosed`, o la vista no explica por qué
  terminó.
- **La regla de redondeo de un documento vive en dos columnas nullable, y `null` es `PriceRounding.LegacyFor`** (spec 2026-10-27).
  `LegacyFor` tiene el literal `"COP"` porque BuildingBlocks no ve el catálogo; `TheLegacyRuleMatchesTheDefaultForEveryCatalogueCurrency`
  la amarra a `PriceRoundingDefaults.For`. Una moneda nueva con otros decimales pone roja esa prueba: decide ahí si los documentos viejos
  de esa moneda se leen con la regla vieja. La regla sólo baja al documento en `QuotationPricingRecalculation`/`OrderPricingRecalculation`
  (`UsePriceRounding`); un camino nuevo que rehaga líneas sin pasar por ahí calcula con la regla guardada, no con la vigente. Los pedidos
  externos nunca toman la del tenant.
- **ImageSharp sigue en 3.1.12 con sus avisos suprimidos, y subirla exige una licencia de Six Labors** (2026-10-07,
  ampliado el 2026-10-27 con GHSA-v76p-62qx-wwq2). Un `NU1903` nuevo de `SixLabors.ImageSharp` rompe el restore, el build y
  el `docker build` por `TreatWarningsAsErrors`, y no es culpa de la rama en curso. Las versiones corregidas (3.2.0 y 4.x)
  traen en `build/SixLabors.ImageSharp.targets` el target `SixLabors_ValidateLicense`, que corre antes de `CoreCompile` y en
  `Release` —el Docker— falla sin `SixLaborsLicenseKey` ni `sixlabors.lic`; en `Debug` sólo avisa, así que en local no se ve.
  Mientras no haya clave, cada aviso nuevo se analiza: se suprime en `Directory.Build.props` **sólo** si su vector queda fuera
  de las `Configuration` restringidas (`ImageSharpConfiguration` de Storage: JPEG, PNG, WebP; `ImageSharpMessagingImageCompressor`:
  JPEG, PNG) y se documenta ahí el porqué. Un aviso en el decodificador de JPEG, PNG o WebP no se suprime: obliga al upgrade.
  **Para el upgrade:** pedir la licencia Community (gratis por debajo de USD 1M de facturación anual; la condición la confirma
  el owner) en https://licensing.sixlabors.com/; guardar la clave como secreto en `Backend-prod` y pasarla al `docker build`
  como build-arg hacia la propiedad `SixLaborsLicenseKey`, nunca commiteada (un `sixlabors.lic` en el repo es la clave en
  claro); subir la versión en `Directory.Packages.props` con `dotnet restore --force-evaluate` de los lock files; quitar los
  `NuGetAuditSuppress` y su comentario; y verificar con un build `Release` local con la clave puesta. La 4.x es salto de mayor:
  revisar la API en Storage y en `ImageSharpMessagingImageCompressor`.

## Verificación

Los comandos (`restore` / `format` / `build` / `test`) están en
[README § Verificación](README.md#verificación).
