# Plan maestro — `qep-backend`

Ledger de continuidad **de este repositorio**. Una sesión de backend empieza aquí, no en el
historial de chat.

> **Última actualización:** 2026-08-11
> **Alcance:** sólo slices que se ejecutan en `qep-backend`. El frontend lleva el suyo en
> `qep-frontend/sdd/02-plan/plan-maestro.md`. Ninguna fila aparece en los dos
> (`SDD-ADR-08`).

## Retomar el trabajo

1. Leer `qep-backend/sdd/README.md` y `qep-frontend/sdd/AGENTS.md`.
2. Revisar **Estado global** y el **Ledger de slices** de abajo.
3. Verificar `git status` en **este** repo. La carpeta que contiene los dos checkouts **no es
   un repositorio** desde el 2026-08-09: `git rev-parse` ahí falla, y eso es lo correcto.
4. Buscar el primer slice no `Complete` cuyas dependencias estén `Complete`, confirmar que
   sus bloqueos están cerrados, marcarlo `In Progress` y ejecutar el ciclo de
   `qep-frontend/sdd/00-metodo/ciclo-de-trabajo.md`.
5. **Un slice a la vez en este repositorio.** El frontend corre en paralelo con el suyo.
6. Cerrar con una entrada de **Handoff**, incluso si quedó bloqueado o sin commit.

## Estado global

| Campo | Valor |
| --- | --- |
| Fase activa | Fase 2 — módulos de producto. `catalog` es el primero con gate cerrado |
| Módulo activo | `catalog` — `En curso`, gate `CAT-00` cerrado el 2026-08-10 |
| Slice activo | **Ninguno.** `CAT-02` cerró el 2026-08-11 con `CAT-02a` y `CAT-02b` en `Complete`: runtime de 12/12 criterios, revisión de 4 lentes y su transacción de corrección. El próximo es `CAT-03`, que todavía no tiene spec |
| Último slice completado | **`CAT-02` (`a` y `b`), el 2026-08-11** — el primero cerrado en este ledger. La historia previa de backend —`AUTH-04`, `AUTH-05`, `AUTH-11`— vive en el ledger del frontend: eran slices de dos repos con un solo spec, y `SDD-ADR-08` decidió **no partirlos retroactivamente** porque están `Complete` y renumerar borra trazabilidad |
| Último commit verificado | Sesión del 2026-08-11, en tres: `ec5540e` (`fix(config)`: quitar la cadena de conexión de `appsettings.json`), `55f36e6` (`docs(readme)`) y el que cierra esta entrada del ledger. Antes: `ccd2eca` (`chore(i18n)` — **contiene además un cambio funcional en `Program.cs` que su mensaje no declara**; ver Handoff), `797c099` (`docs(CAT-02b)`), `3c2c9ec` (`feat(CAT-02b)`), `968c4a8` (`feat(CAT-02)`), `594ee11` (`docs(SDD-ADR-08,CAT-02)`). Rama **`feature/catalog-api`**, sin publicar; se creó rama en vez de commitear sobre `main`, que es la rama por defecto de este repo. Se suma **`84ebc5c`** (`fix(config)`: credenciales de k8s a un Secret propio), del handoff del 2026-08-11. Incluye `CLAUDE.md`, que **nunca estuvo versionado**: entra acá por decisión explícita del owner, y con eso queda cerrada la decisión que este ledger venía registrando como no tomada |
| Decisiones abiertas | **`DECISIÓN-PENDIENTE-INFRA-01` cerrada el 2026-08-11 por el owner: infraestructura y despliegue quedan explícitamente fuera del alcance del método.** No se abre módulo de plataforma ni se reserva prefijo. El corte es **por efecto, no por carpeta** —ver «Alcance del método» abajo—, y la obligación que **no** desaparece es la entrada de handoff en este ledger. Pendiente que deja: es una decisión estructural, así que `convenciones-de-id.md` pide que produzca un `SDD-ADR-*` en `qep-frontend/sdd/01-contexto/decisiones-de-arquitectura.md`, que es autoridad del otro repositorio. **Ese registro ya arrastra deuda: `SDD-ADR-08` se cita en tres archivos de este repo y no tiene entrada; el índice termina en `SDD-ADR-07`.** **`DECISIÓN-PENDIENTE-CAT-04` cerrada el 2026-08-10 por el owner:** el `code` de producto es único por tenant, con `IX_products_tenant_code` y traducción a `422 catalog.product.code_taken` en Infrastructure |
| Contradicciones abiertas | `SDD-CT-14` — parcialmente cerrada: siguen fallando 5 pruebas de `RealAuthenticationApiTests` con `Expected: Created / Actual: Unauthorized`. `SDD-CT-07` — un registro de tenant fallido deja un usuario huérfano en `identity.users`; no bloquea, pide slice de mantenimiento. `SDD-CT-08` — `500` intermitente en `POST /auth/register-tenant`, no reproducida. Las tres se registran en el ledger del frontend, que sigue siendo el registro de contradicciones del producto |

### Próxima acción ejecutable

**Abrir `CAT-03` — API de tasas de impuesto.** Empieza por su spec, que todavía no existe. Lo
que ya está decidido: el porcentaje es **entero de 0 decimales** (`P-008`, owner, 2026-08-10),
y sus dos permisos —`catalog.tax_rate.read` y `.manage`— **los trae `CAT-03` con su
implementación**. Estaban registrados desde `CAT-02` sin nada que los consumiera y la revisión
de 4 lentes los hizo retirar; volver a agregarlos es parte de este slice, no un pendiente
suelto.

Antes de arrancar, tres seguimientos abiertos por la revisión de `CAT-02` (tramo 6 del spec):
el mensaje veneno de `AuditProjectionWorker`, que merece `SDD-CT` y no lo introdujo `CAT-02`;
el `ETag`/`If-Match` de productos, que es trabajo cruzado con `CAT-01`; y los **22 archivos**
donde `dotnet format` falla en todo el repositorio. Ninguno bloquea `CAT-03`.

#### Historia de `CAT-02`, cerrado el 2026-08-11

Orden de trabajo que se ejecutó, con RED antes que GREEN en cada tramo:

1. ~~**Andamiaje**: cuatro `.csproj` de `Modules.Catalog.*` en `Backend.slnx`, más
   `CatalogLayerTests.cs`.~~ **Hecho el 2026-08-10.** RED literal `error CS0234: ... 'Catalog'
   no existe en el espacio de nombres 'Modules'`; GREEN `Superado: 16, Total: 16` (eran 12);
   `dotnet build Backend.slnx` → `Compilación correcta. 0 Advertencia(s), 0 Errores`.
   Evidencia completa en el spec. Commiteado en `968c4a8`.
2. ~~**Dominio**: `Product` con sus invariantes, en `Modules.Catalog.UnitTests`.~~ **Hecho el
   2026-08-10.** RED literal `error CS0103: El nombre 'ProductId' no existe en el contexto
   actual` y `CS0246` sobre `CatalogDomainException`; GREEN `Superado: 13, Total: 13`; build
   limpio y `ArchitectureTests` sin regresión en `16/16`. Ocho invariantes con prueba, cada
   una con su código de dominio. Commiteado en `968c4a8`.
3. ~~**Persistencia y migración**: `CatalogDbContext`, `InitialCatalog` con
   `IX_products_tenant_code`.~~ **Hecho el 2026-08-10**, junto con el primer endpoint
   (`GET /products`), porque una prueba de integración sin ruta que ejercer no verifica nada.
   GREEN `Superado: 3, Total: 3` contra PostgreSQL real. Sin regresión: `Tenancy` sigue con
   los mismos 5 fallos de `SDD-CT-14`, verificados por nombre. Commiteado en `968c4a8`.
4. ~~**Casos de uso y endpoints**, uno a uno, con integración contra PostgreSQL real.~~
   **Hecho el 2026-08-10** como `CAT-02b`. RED de runtime `Expected: Created / Actual:
   MethodNotAllowed`; GREEN `Superado: 15, Total: 15`. **Los 12 criterios de aceptación
   cubiertos por prueba.** Sin regresión.

5. ~~**Runtime contra la API local.**~~ **Hecho el 2026-08-11**, los 12 criterios endpoint por
   endpoint, con la auditoría verificada en base y no por status HTTP. Tramo 5 del spec.
6. ~~**Revisión adversarial de 4 lentes y su transacción de corrección.**~~ **Hecha el
   2026-08-11.** Seis hallazgos, cuatro corregidos: token de concurrencia —al que llegaron dos
   lentes por separado—, permisos de `tax_rate` retirados, comodines de `LIKE` escapados y
   autorización antes que validación. Regresión de **203 pruebas** con los 5 fallos de
   `SDD-CT-14` verificados por nombre. Tramo 6 del spec.

**`CAT-02` cumple el DoD y está `Complete`.** Los ítems de UI van `N/A` con razón: es un slice
de backend, y su interfaz es `CAT-01`, en el otro repositorio.

**Antes de cualquier `dotnet build`, `dotnet test` o comando `ef`: verificar que `Api.exe` no
esté corriendo.** Si lo está, los tres fallan por archivo bloqueado. El nombre del proceso es
`Api.exe` porque `src/Api/Api.csproj` no declara `AssemblyName`.

**La factory de integración debe fijar `Notifications:EmailProvider`** (`SDD-CT-17`): sin eso
las pruebas mueren al arrancar con `OptionsValidationException`, heredando el `infobip` con
credenciales vacías del `appsettings.json`.

## Alcance del método

Cerrado por el owner el 2026-08-11 (`DECISIÓN-PENDIENTE-INFRA-01`). **Infraestructura y
despliegue quedan fuera del alcance del método.** No requieren slice, spec, gate ni prefijo.

**El corte es por efecto, no por carpeta.** Definirlo por ruta sería un agujero: un cambio en
`src/Api/appsettings.json` puede tumbar el arranque, y una migración vive en `src/` pero se
parece a infraestructura. La pregunta que decide es una sola: **¿cambia el comportamiento
observable de la API?**

| Fuera del método — sin slice | Dentro — exige slice con ID |
| --- | --- |
| Manifiestos de `k8s/`, `azure-pipelines.yml`, `Dockerfile`, `compose.yaml` | Contratos HTTP: rutas, cuerpos, códigos de estado, códigos de error |
| Dónde vive un valor de configuración: `appsettings.json` ↔ ConfigMap ↔ Secret ↔ user-secrets | Dominio, invariantes, permisos, políticas de autorización |
| Secretos, observabilidad de plataforma, recursos y probes del pod | Migraciones de base y forma del esquema |
| Comentarios, README, y este ledger | Cualquier cambio de configuración que **altere** comportamiento, no sólo lo mueva |

La última fila es la que hay que mirar con cuidado. Mover `ConnectionStrings__QepDatabase` del
ConfigMap a un Secret está fuera. Cambiar `Registration__PublicTenantSignupEnabled` de `false`
a `true` **no**: eso es una decisión de producto que se ejecuta por configuración.

**Lo que la exención NO exime:** la entrada de handoff en este ledger, con evidencia literal y
comandos con su resultado real. Sigue siendo obligatoria. El precedente es la sesión del
2026-08-11: sin ese handoff se habría perdido que la plantilla de deploy aplica una lista
blanca y no el directorio, que es lo que evitó una caída de producción.

## Ledger de slices — Módulo `catalog`

Ficha y gate: `qep-frontend/sdd/03-modulos/catalog/` · Estado del módulo: `En curso` ·
Owner: Andres Jaramillo

| ID | Resultado revisable | Depende de | Estado | Evidencia / commit / PR |
| --- | --- | --- | --- | --- |
| `CAT-02` | **API de productos** — fila padre, ya no es unidad ejecutable | `CAT-00` (gate cerrado) | Partido el 2026-08-10 | Se partió al medir **1043 líneas autoradas** con un endpoint de cinco, contra el umbral de ~400 de `convenciones-de-id.md`. No se renumera nada y los commits que citan `feat(CAT-02)` siguen válidos: el ID padre no se toca. Spec único en [`03-modulos/catalog/slices/CAT-02-api-de-productos.md`](../03-modulos/catalog/slices/CAT-02-api-de-productos.md) |
| `CAT-02a` | Andamiaje del módulo, dominio `Product`, persistencia con `InitialCatalog` y `GET /products` | `CAT-00` | **Complete** | Tres tramos con RED y GREEN literales en el spec. Commits `968c4a8` y `594ee11`. Runtime en el tramo 5 y revisión de 4 lentes en el tramo 6, ambos el 2026-08-11 |
| `CAT-02b` | Escrituras: `GET` por id, `POST`, `PUT`, `deactivate`, validadores, traducción del índice único y pruebas de auditoría y outbox | `CAT-02a` | **Complete** | Código en `3c2c9ec`. Cubre `CA-CAT-02-03` a `-09`, `-11`, `-12` y las mitades pendientes de `-01` y `-10`. **Runtime del 2026-08-11: los 12 criterios verificados contra la API local**, con `422 catalog.product.code_taken` confirmado en vivo —el `500` de `SDD-CT-06` que este slice existía para cerrar— y la atomicidad del outbox probada por lo que **no** dejó rastro: el `403` y los tres `422` no escribieron fila. **Revisión de 4 lentes y su corrección** en el tramo 6: token de concurrencia, permisos de `tax_rate` retirados, comodines de `LIKE` escapados y autorización antes que validación. Regresión de 203 pruebas, con los 5 fallos de `SDD-CT-14` verificados **por nombre** |
| `CAT-03` | API de tasas de impuesto | `CAT-02` | Pending | Sin spec todavía. Se separa de `CAT-02` porque es otro recurso, con otros permisos y otra migración, y juntos pasarían holgado el umbral de 400 líneas. El porcentaje es **entero de 0 decimales** (`P-008`, decidido por el owner el 2026-08-10): no admite retenciones con fracción, y eso está declarado como límite de alcance en el gate |

## Handoff

### 2026-08-11 — Forma de la configuración en k8s: híbrida, con Secret propio

**Sin slice.** Ver `DECISIÓN-PENDIENTE-INFRA-01` en Estado global. Se registra acá porque el
cierre de sesión es obligatorio aunque el trabajo no tenga ID, no para darle uno por la puerta
de atrás.

**La pregunta que lo originó:** reemplazar el `appsettings.json` entero desde un ConfigMap
—como en otro proyecto del owner— contra inyectar variables de entorno. Gana la segunda, por
precedencia: `appsettings.json` < `appsettings.{Env}.json` < variables de entorno. El archivo
reemplazado es todo-o-nada y duplica el contrato de configuración fuera del repositorio; una
clave nueva en código queda borrada en silencio por un ConfigMap viejo. Las variables se
superponen clave por clave y dejan la imagen como dueña de los valores por defecto.

**El hallazgo que lo volvió urgente.** `azure-pipelines.yml` pasaba
`removeAppsettingsPath: $(Build.SourcesDirectory)/Api/appsettings.json` — vestigio del enfoque
de reemplazo, **con la ruta mal**: el archivo real es `src/Api/appsettings.json`, así que era un
no-op silencioso. El híbrido ya funcionaba por accidente, no por decisión.

**Qué cambió:**

- `k8s/prod-secret.yaml` **nuevo**, con las cuatro credenciales que estaban en texto plano en
  el ConfigMap: `ConnectionStrings__QepDatabase` (lleva la contraseña),
  `Notifications__Infobip__ApiKey`, `Storage__R2__AccessKeyId` y `__SecretAccessKey`.
- `k8s/prod-deployment.yaml` suma un `secretRef` **después** del `configMapRef`: ante colisión
  de claves gana la fuente posterior.
- `k8s/prod-configMap.yaml` pierde esas cuatro y además las cinco que sólo repetían el valor
  por defecto de la imagen (`Notifications__EmailProvider`, los dos `Audit__*`,
  `Storage__PresignedUrlMinutes`). `Registration__PublicTenantSignupEnabled` se conserva
  duplicada **a propósito**: que un anónimo pueda crear un tenant es decisión de seguridad y
  producción la declara explícita.
- `src/Api/appsettings.json` pierde 18 líneas: la sección `Authentication` completa, el bloque
  `R2` entero, los dos `""` de Infobip y **`DataProtection`, que era configuración muerta** —
  `rg DataProtection` sobre todo el repositorio devolvía esa única línea del propio archivo.
- `README.md` y `CLAUDE.md` decían que la cadena de conexión la fija `prod-configMap.yaml`.
  Corregidos: gana el código (`SDD-ADR-01`).

**El defecto que casi llega a producción.** La plantilla `k8s/deploy.yml@k8sTemplates` **no
aplica el directorio**: `manifestDirectory` sólo dice dónde buscar. Recorre una lista blanca
—`namespace`, `configMap`, `service`, `deployment`, `ingress`— y aplica
`<dir>/<env>-<entrada>.yaml` uno por uno. `secret` no está en ella. Sin declararla explícita,
`prod-secret.yaml` **nunca se habría aplicado**, y con la cadena de conexión ya fuera del
ConfigMap los pods quedaban en `CreateContainerConfigError` de forma permanente. Corregido
declarando `manifests` en `azure-pipelines.yml`, con `secret` antes que `deployment`.

**Evidencia:**

```
dotnet build Backend.slnx -c Release
Compilación correcta.
    0 Advertencia(s)
    0 Errores
```

```
dotnet test Backend.slnx -c Release --no-build
Con error!  - Con error: 5, Superado: 52, Total: 57 - Modules.Tenancy.IntegrationTests.dll
Correctas!  - Con error: 0, Superado: 19, Total: 19 - Modules.Catalog.IntegrationTests.dll
(resto de los ensamblados, todos en verde)
```

Los 5 fallos son los de `SDD-CT-14`, verificados **por nombre** y no por conteo:
`RoleDowngradeRemovesPermissionsOnTheNextRequest`, `MutatingRequestWithoutCsrfHeaderIsRejected`,
`SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken`,
`SuspendingMembershipRevokesTheMembersActiveSession`, `LogoutRevokesTheSessionCookie`.
Cero regresión.

Comportamiento idéntico al quitar los `""`, confirmado en código y no por suposición:
`Coalesce` (`QepServiceCollectionExtensions.cs:316`) filtra por `IsNullOrWhiteSpace`, y
`StorageOptions.R2` y `NotificationsOptions.Infobip` tienen `= new()` con `string.Empty` en
cada campo. Ausente y `""` se comportan igual.

**Lo que NO se verificó: el pipeline nunca corrió.** Todo lo de arriba es lectura de la
plantilla más build y pruebas locales. El primer deploy es la prueba real.

**Cuatro seguimientos abiertos:**

- **`prod-wildcard-certificate.yaml` no lo aplica nadie.** No está en la lista por defecto ni
  en la nueva. Se dejó así: agregarlo lo re-aplicaría en cada deploy, y eso es un cambio de
  comportamiento que nadie pidió. Confirmar con el owner si se aplicó a mano.
- **El `sed` del tag corre sobre credenciales ya sustituidas.** En `deploy.yml` el reemplazo de
  tokens es el paso previo al `find prod-*.yaml -exec sed -i "s|TAG|<buildId>|g"`. Una
  credencial que contenga la cadena literal `TAG` en mayúsculas se corrompe en silencio, y el
  síntoma es "credencial inválida" sin ninguna pista. Ya pasaba con el ConfigMap.
- **El enmascarado en logs no vive en este repositorio.** `replacetokens@5` está sin `logLevel`
  explícito; depende de que las variables del grupo `Backend-prod` estén marcadas como *secret*
  en Azure DevOps. Verificar antes del primer deploy.
- **Data Protection sin persistir.** Al borrar la sección muerta quedó a la vista que no hay
  `PersistKeysToFileSystem` en ningún lado: las claves son efímeras por pod, así que cada deploy
  desloguea a todo el mundo y con más de una réplica se rompe. `replicas: 1` lo tapa hoy. Es
  trabajo propio, no forma de la configuración.

**Estado al cerrar:** commiteado en **`84ebc5c`**, sobre `feature/catalog-api`, sin publicar.
Siete archivos: `azure-pipelines.yml`, `k8s/prod-configMap.yaml`, `k8s/prod-deployment.yaml`,
`k8s/prod-secret.yaml` (nuevo), `src/Api/appsettings.json`, `README.md` y `CLAUDE.md`. Este
último **nunca estuvo versionado** y entra por decisión explícita del owner tomada en esta
sesión, con lo que se cierra el pendiente que el ledger arrastraba desde el 2026-08-11.

### 2026-08-11 — Revisión de 4 lentes de `CAT-02` y su corrección

Corrieron los cuatro lentes **en paralelo y ciegos entre sí** sobre
`git diff 4677d59..3c2c9ec -- src/ tests/` (1870 líneas autoradas, 41 archivos). Obligatorios
por dos razones independientes: superan las 400 líneas y tocan permisos. Evidencia literal en
el **tramo 6** del spec.

Seis hallazgos. **Dos lentes independientes convergieron en el mismo** —`Product` sin token de
concurrencia—, uno describiéndolo como *lost update* silencioso y el otro como violación de la
invariante `EnsureActive()`. Esa convergencia es lo que lo movió a bloqueante.

**Corregidos en la transacción única:** el token de concurrencia (`A`), los permisos de
`tax_rate` publicados sin implementación (`B`), los comodines de `LIKE` sin escapar (`C`) y el
orden validador/autorización (`D`).

**`A` entró por decisión explícita del owner.** La revisión lo planteó como candidato a slice
propio —columna, migración, traducción y prueba son más que una corrección— y el owner resolvió
incluirlo. Se registra porque corrió el límite de la transacción, no por trámite.

**Seguimiento, no corregido acá:**

- **`E` — mensaje veneno en `AuditProjectionWorker`.** Aborta el lote entero ante el primer
  mensaje que no parsea, y como ese mensaje sigue siendo el primero por `OccurredAt` en cada
  tick, reprime la cola para siempre. El comentario del propio archivo afirma lo contrario.
  **No lo introdujo `CAT-02`**: vive en el módulo `Audit` y es anterior. Lo que `CAT-02` hizo
  fue ampliar el radio, porque agregó un segundo productor a una cola compartida que antes sólo
  alimentaba `Storage`. Merece `SDD-CT`.
- **`F` — `GetProduct` lee con tracking** sin mutar nada. Menor.
- **`ETag`/`If-Match` en productos.** La corrección puso el token **interno**, que cierra el
  *lost update*; exponer el contrato como en `Tenant` y `Membership` obliga al frontend a mandar
  `If-Match` y agrega un `412` al contrato que el spec declaró. Es trabajo cruzado con `CAT-01`.
- **`dotnet format` falla en 22 archivos del repositorio**, casi todos ajenos a `CAT-02`
  (`SessionService.cs`, `TenancyUnitOfWork.cs`, `StorageEndpoints.cs`…). Es sistémico y
  anterior: el comando nunca corrió en CI. Se formatearon **sólo los de `CAT-02`**, que es lo
  que pide el DoD.

**Una lección que vale más que los hallazgos:** la primera prueba RED de `D` **pasó en verde
sin tocar el código**. El lente de riesgo acertó el defecto y erró el escenario — describió un
llamador sin permiso, a quien frena la política del endpoint antes de que el handler exista.
El escenario real era el cruce de tenants. Sin exigir que el RED falle **por el motivo
correcto**, la corrección se habría escrito contra un caso que ya funcionaba, y el defecto
seguiría ahí con una prueba en verde encima.

### 2026-08-11 — Runtime de `CAT-02` y hallazgos

**Qué se hizo:** el runtime que faltaba para el DoD. Los 12 criterios de aceptación
verificados endpoint por endpoint contra la API local con PostgreSQL real, más la consulta
directa a `platform.outbox_messages` y `audit.entries`. Evidencia literal —comandos, códigos
de estado y cuerpos— en el **tramo 5** del spec de `CAT-02`.

**Lo que queda para cerrar `CAT-02a` y `CAT-02b`:** la revisión de riesgo de 4 lentes. Nada
más.

**Cómo levantar la API con el stub, porque cuesta una vuelta descubrirlo:**
`dotnet run --project src/Api --no-launch-profile`, con `ASPNETCORE_ENVIRONMENT`,
`ASPNETCORE_URLS` y `Authentication__UseDevelopmentStub=true` en el entorno. Sin
`--no-launch-profile` los dos perfiles de `launchSettings.json` fijan el stub en `false` y
ganan sobre el shell: la API arranca con auth real y todo devuelve `401`.

#### Tres hallazgos, ninguno bloqueante de `CAT-02`

1. ~~**`appsettings.json` declara una base que no existe.**~~ **Corregido el 2026-08-11 por
   indicación del owner.** Decía `Database=dev_lulo_crm2`; la real es `dev_lulo_crm_v2` y
   llega por user-secrets. Al mirarlo de cerca resultó peor de lo reportado: el valor
   **commiteado** era `Database=dev_lulo_crm`, que tampoco coincide con `compose.yaml` —el
   compose de este repo crea `qep` en `5432`—, así que estaba roto contra la propia
   infraestructura local del repositorio, no sólo contra la máquina del developer.

   **Qué se hizo:** se quitó el bloque `ConnectionStrings` de `src/Api/appsettings.json`. La
   clave pasa a ser requerida y se provee por user-secrets en local y por
   `ConnectionStrings__QepDatabase` en k8s. Ausente, los seis `AddXInfrastructure` fallan al
   arrancar con `InvalidOperationException: Connection string 'QepDatabase' is required.`

   **Por qué se borró en vez de vaciarse:** el guard es `?? throw`, sólo contra `null`. Con
   `""` la cadena vacía lo atraviesa y el error lo termina tirando Npgsql, sin decir qué
   falta.

   Se actualizaron también `README.md` (paso de `user-secrets` en Ejecución local y la tabla
   de configuración) y `CLAUDE.md`, que describían el estado viejo.

   **Verificado con arranque real, las dos mitades:**

   - *Con el secreto presente* (`Development`): `dotnet build Backend.slnx` →
     `Compilación correcta. 0 Advertencia(s), 0 Errores`. La API arranca, los **seis** módulos
     registran `No migrations were applied. The database is already up to date.` —lo que exige
     conexión real, no sólo configuración—, `Now listening on: http://localhost:5000`,
     `/health/live` → `200`, y `GET T/products?search=cat02b` → `200` con el producto de la
     corrida anterior.
   - *Sin el secreto* (`ASPNETCORE_ENVIRONMENT=Staging`, donde los user-secrets no se cargan):

     ```txt
     Unhandled exception. System.InvalidOperationException: Connection string 'QepDatabase' is required.
     ```

     Es el mensaje exacto que el `README` promete. Falla en `AddAuditInfrastructure`
     (`QepServiceCollectionExtensions.cs:102`), que corre **antes** de `AddAuthentication`
     (línea 110): por eso el error que ve el developer es el de la cadena de conexión y no el
     de la audiencia JWT.

   **Deuda de método, declarada:** este arreglo **no pertenece a ningún slice**. Se hizo por
   pedido directo del owner mientras `CAT-02b` seguía activo. Queda como fila a reconocer
   —junto con el `SDD-CT-19` que este hallazgo merece— cuando se abra el slice de
   configuración tipada, que ya lo tenía en su alcance.
2. **El mapa `errors` sale en el idioma del sistema operativo del servidor.** Observado:
   `"'Name' no debería estar vacío."`. El `code` es contrato estable; el texto no. Importa
   para el frontend, que hoy podría estar mostrándolo.
3. **El `search` de productos ignora mayúsculas pero no acentos.** `valvula` no encuentra
   `Válvula`. `CA-CAT-02-10` sólo exige lo primero, así que el criterio pasa. Observación de
   producto, no defecto del slice.

#### Lo que se commiteó aparte, y una advertencia sobre ese commit

Los cambios de traducción que estaban sin commitear se cerraron en **`ccd2eca`**
(`chore(i18n): traducir al español los comentarios del código`). La edición local de la
cadena de conexión (`5433`/`dev_lulo_crm2`) se descartó, no se commiteó.

**Advertencia para quien lea el historial:** ese commit no es sólo i18n. Arrastra un cambio
funcional —la eliminación del bloque `AddUserSecrets` / `IsEnvironment("Local")` de
`Program.cs`, 43 líneas— que su mensaje no menciona. Buscar por qué desapareció ese bloque
mirando mensajes de commit no lo va a encontrar. Queda anotado acá porque el historial es la
única otra forma de saberlo.

#### Datos de prueba que quedaron

Cuatro productos en `catalog.products` de `dev_lulo_crm_v2` (códigos `VS-001` y `CAT02B-*`,
en los tenants `…00a1` y `…00b1`), con sus 6 filas de outbox ya proyectadas. No se borraron:
no hay endpoint de baja y un `DELETE` por SQL sobre la base del developer no es de esta
sesión.

### 2026-08-10 — Apertura de este ledger

Creado al ejecutar `SDD-ADR-08`, que reparte `sdd/` por repositorio porque **backend y
frontend los llevan developers distintos, cada uno a su ritmo**. Antes, un slice de backend
tenía que commitear su spec en `qep-frontend` y su código acá.

Lo que entró en este repo: `sdd/README.md`, este ledger y el spec de `CAT-02`, movido desde
`qep-frontend/sdd/03-modulos/catalog/slices/`.

Lo que **no** se movió, a propósito: método, requisitos, registro de módulos, ADRs y las
fichas y gates de módulo. Siguen siendo autoridad única en `qep-frontend/sdd/`.

Commiteado en `feature/catalog-api`: `968c4a8` el código, `898cc10` este ledger y el spec.
En `qep-frontend` no se tocó nada desde este lado; ahí sigue abierto el merge de
`origin/feature/catalog`, que es del dev de frontend.
