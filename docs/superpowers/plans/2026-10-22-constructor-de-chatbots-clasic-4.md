# Constructor de chatbots, plan 4 (Clasic) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el chatbot de Clasic corra en QEP contra la API real de Clasic: la API de Clasic acepta la clave del bot y expone lo que el flujo necesita, el flujo completo del Anexo B existe como archivo importable y probado de punta a punta contra el contrato real, y el README dice cómo conectarlo, importarlo, publicarlo y hacer el corte del número.

**Architecture:** Dos repositorios, sin cambio de arquitectura en ninguno. En `calentadores-classic-backend` (NestJS 9 + Fastify + Prisma 5) el `JwtAuthGuard` gana una rama por clave de API (D-K1), `GET /customers` el filtro `phone` (D-K3), y la API se ajusta en lo mínimo para que un flujo sin lógica pueda usarla (cuerpo parcial en `PUT /customers/:id`, filtros por nombre en `/services` y `/failures`, fecha por defecto en `POST /service-orders`). En `qep-backend` no se toca código de producción: el flujo completo reemplaza al subconjunto de prueba, `ClasicFlowEndToEndTests` corre contra el contrato real (`items`/`totalCount`, campos reales de `new-customer`) y la documentación describe la fase 8 y el corte (fase 9) del spec §11.

**Tech Stack:** Clasic: TypeScript 4.9, NestJS 9, `@nestjs/passport`, Prisma 5; sin pruebas automáticas (decisión del developer, 2026-10-23: de momento no se escriben pruebas unitarias ni se instala Jest en Clasic; cada tarea se verifica a mano con `curl.exe`). QEP: .NET 10, xunit.v3 + Testcontainers, los fakes de `Modules.Automations.IntegrationTests`.

**Spec:** `docs/superpowers/specs/2026-10-21-constructor-de-chatbots-design.md` (§2.6 D-K1..K4, §10, §11 fases 8 y 9, Anexos B y C). **Depende de los planes 1 y 2** (backend, ya en `develop`) y del plan 3 (frontend, en `develop` de `qep-frontend`). Donde este plan contradice al spec es porque el código de Clasic lo obligó (verificado el 2026-10-22 contra `develop` de Clasic, `54cde7c`); la tarea 7 corrige el spec.

## Decisiones nuevas (el spec no las tenía; el developer las confirma al revisar este plan)

| ID | Decisión | Por qué |
| --- | --- | --- |
| D-K5 | **`POST /customers/new-customer` guarda el teléfono normalizado a sus últimos 10 dígitos**, con el mismo helper del filtro `phone`. | El motor manda `{telefono}` en E.164 sin `+` (`573004445566`); `findCustomersByPhone` y el filtro comparan con 10 dígitos. Sin esto, un cliente creado por el bot nunca se encuentra por teléfono. |
| D-K6 | **`GET /services` gana el filtro `name` y `GET /failures` el filtro `cause`** (igualdad exacta, como vienen en la base). | Anexo B §11 dice «`GET /services` y tomar `STM`», pero el flujo no puede filtrar una lista por campo (los operadores de Condición no lo hacen y la Lista sólo elige entre ≤ 10 filas). El `failureId` del mantenimiento es la falla con `cause = 'MANTENIMIENTO'`, que hoy el bot busca con `findFirst`. Alternativa descartada: ids fijos en el flujo, que cambian por ambiente. |
| D-K7 | **La ciudad del cliente nuevo va como texto en `addressDescription` («Ciudad: …») y `departmentId`/`cityId` van fijos en el bloque HTTP** (Antioquia `5`, Medellín `5001000`, los ids de `departments.json`/`cities.json`), editables desde el editor. | Anexo B §6 quería la ciudad por Lista, pero una lista de WhatsApp tiene 10 filas y Colombia tiene 33 departamentos y Antioquia 125 municipios. La orden nace en borrador y un agente la confirma por teléfono, como hoy. Si el owner quiere la ciudad real, la alternativa es que `new-customer` acepte `cityName`/`departmentName` y los resuelva: no está en este plan. |
| D-K8 | **«Otra dirección» de un cliente existente termina en un asesor**, después de pedir la dirección como texto. | `new-customer` rechaza un documento ya registrado (`El documento ya se encuentra registrado`), así que no sirve para una dirección nueva, y el correo que mandaba el bot viejo se descartó (Anexo B). El texto de la dirección queda en «Datos capturados» de la conversación. |
| D-K9 | **`PUT /customers/:id` acepta cuerpo parcial** y, si llega `acceptPolicy: "true"` sin `acceptPolicyDate`, pone la fecha actual. **`POST /service-orders` sin `serviceDate` usa la fecha actual.** | Hoy `updateCustomer` hace `department: { connect: { id: undefined } }` y resetea `acceptPolicy` a `null` si no viene; `create` hace `new Date(undefined)`. Ninguna de las dos acepta lo que un bloque HTTP puede mandar (no hay variable «ahora» en el motor). |
| D-K10 | **La ruta de prueba de la conexión «API externa» es `/failures`.** | El probador hace `GET {baseUrl}{testPath}` y sólo mira el status: la ruta tiene que estar protegida por `JwtAuthGuard` para que una clave mala dé 401. `/services`, `/departments/*` y `/health/*` son públicas y «probarían» bien con cualquier clave. `testPath` no admite query (patrón del catálogo), así que `/customers?take=1` no sirve y `/customers` a secas trae todos los clientes (`take` por defecto `9999999`). |
| D-K11 | **No se crea un usuario bot: D-K2 queda sin efecto.** El guard pone `req.user = { id: 0, role: 'Administrador' }` y `ServiceOrdersService.create` escribe `createdBy: 'Whatsapp'` cuando no hay usuario, que es exactamente lo que hoy escribe el bot al crear la orden directo con Prisma (`phone-contact.service.ts:88`, `summary.service.ts:83`). | Pedido del developer (2026-10-23): el menor impacto posible en Clasic y nada cambia de cara al usuario de Clasic. `createdBy` es el único dato que `create` toma del usuario, así que una fila nueva en `User` no aportaba nada y sí aparecía en la lista de usuarios. |
| D-K12 | **La clave va al ConfigMap de Clasic, sin Secret** (decisión del owner, 2026-10-23: en Clasic no se trabaja con Secrets en los YAML). En el grupo `Backend-prod` se llama `CLASIC_BOT_API_KEY` (variable secreta del grupo); dentro del pod es `BOT_API_KEY`. | El spec §10 pedía un Secret, pero Clasic no tiene ninguno y todas sus credenciales (`JWT_SECRET`, AWS, SendGrid) ya viven en `prod-configMap.yaml`; una sola clave en un Secret aparte no cambia la postura y agrega un manifiesto al pipeline. Los pipelines de Clasic y de QEP leen el mismo grupo de variables (`Backend-${env}`): el prefijo evita confundirla con la clave de QEP. |

## Global Constraints

- **Clasic:** rama `feature/bot-qep` en un worktree desde `develop` (`git -C /Users/andresjaramillo/datos/repositories/clasic/calentadores-classic-backend worktree add .claude/worktrees/bot-qep -b feature/bot-qep develop`); merge `--ff-only` a `develop`; llevarlo a `main` (que despliega) es del owner. Conventional commits en español, sin atribución de IA. `pnpm check-all` (format, lint, type-check) limpio antes de cada commit.
- **En Clasic no hay TDD ni pruebas unitarias de momento** (developer, 2026-10-23). Cada tarea termina con una verificación manual contra la app local (`pnpm start:dev`, puerto 3004, `.env` con un `BOT_API_KEY` de prueba) usando `curl.exe` en PowerShell, y el reporte pega el status y la respuesta recortada, **sin la clave**. La regla de TDD del `CLAUDE.md` aplica sólo a la parte B (QEP).
- **QEP:** rama `feature/automatizaciones-clasic` en un worktree desde `develop`; merge `--ff-only` a `develop`, nunca a `main`, nunca push. `docs/estado.md` **no** se toca en la rama (se actualiza en `develop` al mergear). Antes de correr pruebas: `$env:Seed__Enabled = "false"`. Sólo `Modules.Automations.UnitTests` y `Modules.Automations.IntegrationTests` con `--filter`, nunca la suite entera.
- TDD en QEP: RED antes que GREEN, con la salida literal de las dos corridas en el reporte de cada tarea (`CLAUDE.md`).
- Copy del producto (lo que lee el cliente de Clasic por WhatsApp) en español colombiano, tuteando; los textos actuales del bot vosean en partes y traen erratas («servico», «Ocurrión»): se corrigen al copiarlos. Identificadores, rutas, códigos y nombres de enum tal cual.
- **La clave del bot nunca se imprime**: ni en logs de Clasic (el guard no la registra ni en el rechazo), ni en el reporte de una tarea, ni con `kubectl get configmap -o yaml` (el ConfigMap es texto plano: imprimirlo entero es imprimir la clave). Para verificar que existe: `kubectl -n prod-clasic-backend get configmap clasic-backend-config -o jsonpath='{range $k,$v := .data}{$k}{"\n"}{end}'` lista sólo los nombres.
- Nada de Clasic referencia a lulo-chat de forma nueva: `sendTemplateMessage` de `solicitud_os_creada` sigue saliendo por lulo (D-K4) y no se toca.
- Los ids de nodo del flujo son `^[a-z0-9-]{1,64}$`; los nombres de variable `^[a-z][a-zA-Z0-9]{0,39}$`; `nombre`, `telefono`, `empresa` y `conexion` son fijas del motor y no se declaran.

## Review Focus

1. **El cliente contesta «Acepto.» o «sí acepto»** en vez de «ACEPTO»: tiene que valer como aceptación, no gastar un intento (`TextMatch.IsAnyOf` compara igualdad normalizada, no `includes` como el bot viejo). Tarea 5, `ClasicFlowTests.ThePolicyQuestionAcceptsTheUsualWaysOfSayingYes`.
2. **El teléfono llega como `573001234567`** y Clasic guarda 10 dígitos: `GET /customers?phone=573001234567` tiene que encontrar al cliente `3001234567`. Tarea 2, verificación manual con el número completo.
3. **`PUT /customers/7` con sólo `acceptPolicy` y `sourceAcceptPolicy`** no puede tumbar el `connect` de ciudad ni borrar otros campos. Tarea 3, verificación manual: después del `PUT`, el `GET /customers/7` conserva ciudad, nombre y teléfono.
4. **`BOT_API_KEY` llega literal `#{CLASIC_BOT_API_KEY}#`** porque la variable no está en el grupo: la rama de clave queda apagada y nadie entra con ese literal. Tarea 1, verificación manual con ese literal en el `.env` y en el header.
5. **El equipo elegido tiene una orden abierta** (`haveOrderOpen`): el bot lo dice y vuelve a mostrar la lista, en vez de intentar la orden y caer en el 409. Tarea 6, `AnEquipmentWithAnOpenOrderIsRefusedAndTheListIsSentAgain`.

---

## Parte A: `calentadores-classic-backend`

### Task 1: `JwtAuthGuard` con clave de API (D-K1)

**Files:**
- Modify: `src/core/config/configurations.ts` (`botApiKey`, Joi), `.env.example`
- Modify: `src/auth/guards/jwt-auth.guard.ts`
- Create: `src/core/utils/pipeline-token.ts`

**Interfaces:**
- Produces:
  ```ts
  // src/core/utils/pipeline-token.ts
  export function configValue(value: string | undefined): string;   // el valor tal cual, o '' si es undefined, vacío o empieza por '#{' (token del pipeline sin resolver, como DeploymentInfo.Normalize en QEP)
  // configurations.ts
  botApiKey: string;   // configValue(process.env.BOT_API_KEY)
  // jwt-auth.guard.ts
  export const BOT_API_KEY_HEADER = 'x-api-key';
  @Injectable() export class JwtAuthGuard extends AuthGuard('jwt') {
    constructor(@Inject(configurations.KEY) config: ConfigType<typeof configurations>);
    canActivate(context: ExecutionContext): boolean | Promise<boolean> | Observable<boolean>;
  }
  ```
- Con clave configurada (`botApiKey !== ''`) y header igual —comparación de tiempo constante: `crypto.timingSafeEqual` sobre los `sha256` de los dos valores, para no filtrar la longitud—, pone `request.user = { id: 0, role: RolesEnum.ADMINSTRADOR }` (D-K11: ningún usuario real) y devuelve `true`; en cualquier otro caso delega en `super.canActivate(context)`. `RolesGuard` ya mapea `'Administrador'` a `Role.Administrador`. El guard no escribe a logs.
- Joi: `BOT_API_KEY: Joi.string().allow('').optional()`.

- [ ] **Step 1: Implementar** `configValue`, la clave de config y Joi, el guard. `.env.example`: `BOT_API_KEY= ""` bajo un comentario `# Chatbot de QEP (spec 2026-10-21 D-K1)`. `pnpm check-all` limpio.
- [ ] **Step 2: Verificar a mano** con la app local (`.env` con `BOT_API_KEY=clave-local-de-prueba`; `pnpm start:dev`), en PowerShell:
  ```powershell
  curl.exe -s -o NUL -w "%{http_code}`n" http://localhost:3004/failures                                        # 401: sin header sigue pidiendo JWT
  curl.exe -s -o NUL -w "%{http_code}`n" -H "x-api-key: clave-local-de-prueba" http://localhost:3004/failures  # 200
  curl.exe -s -o NUL -w "%{http_code}`n" -H "x-api-key: otra" http://localhost:3004/failures                   # 401
  ```
  Luego con `BOT_API_KEY=` vacío y reiniciando: la segunda línea da 401. Luego con `BOT_API_KEY=#{CLASIC_BOT_API_KEY}#` y el header con ese mismo literal: 401 (Review Focus 4). Pegar en el reporte los cinco status, nunca el `.env`.
- [ ] **Step 3: Commit** — `git commit -m "feat(auth): JwtAuthGuard acepta la clave de API del chatbot de QEP (spec 2026-10-21 D-K1)"`

### Task 2: Filtro `phone` en `GET /customers` y teléfono normalizado (D-K3, D-K5)

**Files:**
- Create: `src/core/utils/phone.ts`
- Modify: `src/users/dto/get-customers.dto.ts`, `src/users/users.service.ts` (`findAllByRoleId`, `createCustomer`), `src/users/users.repository.ts` (`findAllByRoleId`)

**Interfaces:**
- Produces:
  ```ts
  export function lastTenDigits(phone: string): string;   // quita todo lo que no es dígito y devuelve los últimos 10 (menos de 10 → lo que haya)
  export class CustomersQueryDto extends PaginationParams { identificationNumber?: string; phone?: string; }   // phone: @IsOptional() @IsString() @Matches(/^\+?\d{7,15}$/)
  // UsersRepository
  findAllByRoleId(id: number, identificationNumber?: string, skip?: number, take?: number, phone?: string): Promise<{ items: UserByRoleResponseDto[]; totalCount: number }>;   // ...(phone && { phone }) en el where: igualdad, como findCustomersByPhone
  ```
- `UsersService.findAllByRoleId` pasa `query?.phone ? lastTenDigits(query.phone) : null`. `createCustomer` guarda `phone: lastTenDigits(data.phone)` (D-K5). La respuesta sigue siendo `{ items, totalCount }`: **no** `data`/`total` como decía el Anexo C.

- [ ] **Step 1: Implementar** helper, DTO, servicio y repositorio. `pnpm check-all` limpio.
- [ ] **Step 2: Verificar a mano** (app local con la clave de la tarea 1; un cliente local con `phone = '3001234567'`):
  ```powershell
  curl.exe -s -H "x-api-key: clave-local-de-prueba" "http://localhost:3004/customers?phone=573001234567"   # {"items":[{...,"phone":"3001234567",...}],"totalCount":1}
  curl.exe -s -H "x-api-key: clave-local-de-prueba" "http://localhost:3004/customers?phone=%2B57+300+123+4567"   # el mismo cliente
  curl.exe -s -H "x-api-key: clave-local-de-prueba" "http://localhost:3004/customers?phone=3009999999"   # {"items":[],"totalCount":0}
  curl.exe -s -o NUL -w "%{http_code}`n" -H "x-api-key: clave-local-de-prueba" "http://localhost:3004/customers?phone=abc"   # 400 (Matches)
  ```
  Y un `POST /customers/new-customer` (multipart, `-F`) con `phone=573007654321` y un documento nuevo: el cliente creado tiene `phone = '3007654321'` (`GET /customers?identificationNumber=`). Pegar en el reporte las respuestas recortadas.
- [ ] **Step 3: Commit** — `git commit -m "feat(customers): filtro phone en GET /customers y teléfono de diez dígitos (spec 2026-10-21 D-K3)"`

### Task 3: La API lista para un flujo sin lógica (D-K6, D-K9, D-K11, límites multipart)

**Files:**
- Modify: `src/users/users.service.ts` (`updateCustomer`)
- Modify: `src/services/services.controller.ts`, `src/services/services.service.ts`, `src/services/services.repository.ts`; Create: `src/services/dto/services-query.dto.ts`
- Modify: `src/failure/failure.controller.ts`, `src/failure/failure.service.ts`, `src/failure/failure.repository.ts`; Create: `src/failure/dto/failures-query.dto.ts`
- Modify: `src/service-orders/service-orders.service.ts` (`create`: `serviceDate` y `createdBy`)
- Modify: `src/main.ts` (`fieldSize: 1024`, `fields: 20`)

**Interfaces:**
- Produces:
  ```ts
  export class ServicesQueryDto { name?: string; }     // @IsOptional() @IsString() @MaxLength(20)
  export class FailuresQueryDto { cause?: string; }    // @IsOptional() @IsString() @MaxLength(60)
  ServicesService.findAll(query?: ServicesQueryDto): Promise<Service[]>;      // where: ...(name && { name })
  FailureService.getAllFailures(query?: FailuresQueryDto): Promise<Failure[]>; // where: ...(cause && { cause })
  ```
- `updateCustomer`: `acceptPolicy: data.acceptPolicy === undefined ? undefined : data.acceptPolicy === 'true'`; `acceptPolicyDate: data.acceptPolicyDate ?? (data.acceptPolicy === 'true' ? new Date() : undefined)`; `department`/`city` sólo con `connect` cuando viene el id. `create` de órdenes: `serviceDate: data.serviceDate ? new Date(data.serviceDate) : new Date()`; el usuario se busca sólo si `userId > 0`, y `createdBy: user ? `${user.firstName} ${user.lastName}` : 'Whatsapp'` (D-K11: la misma marca que deja hoy el bot).
- `GET /services` y `GET /failures` responden el arreglo en la raíz (sin paginar); con el filtro, el arreglo filtrado (`[]` si nada coincide: el flujo mapea `$[0]` y sale por `error` si no hay elemento, `mapping_failed`).

- [ ] **Step 1: Implementar** los cuatro cambios y los límites de `main.ts` (`fieldSize: 1024`: una dirección con barrio pasa de 100 bytes; `fields: 20`: el bot manda 10 campos y el tope era exactamente 10). `pnpm check-all` limpio.
- [ ] **Step 2: Verificar a mano** (app local, clave de la tarea 1, cuerpos JSON en archivo porque PowerShell rompe las comillas):
  ```powershell
  # politica.json = { "acceptPolicy": "true", "sourceAcceptPolicy": "Whatsapp" }
  curl.exe -s -X PUT -H "x-api-key: clave-local-de-prueba" -H "Content-Type: application/json" -d "@politica.json" http://localhost:3004/customers/7   # 200; luego GET /customers/7: acceptPolicy true, acceptPolicyDate de hoy, y ciudad, nombre y teléfono intactos (Review Focus 3)
  curl.exe -s -H "x-api-key: clave-local-de-prueba" "http://localhost:3004/services?name=STM"            # [{"id":…,"name":"STM",…}] (un solo elemento)
  curl.exe -s -H "x-api-key: clave-local-de-prueba" "http://localhost:3004/failures?cause=MANTENIMIENTO"  # [{"id":…,"cause":"MANTENIMIENTO",…}]
  curl.exe -s -H "x-api-key: clave-local-de-prueba" "http://localhost:3004/services?name=NADA"           # []
  # orden.json = { "customerId": 7, "failureId": <id>, "serviceId": <id STM>, "invoiceItems": [ { "id": "<item sin orden abierta>", "contact": "Ana", "optionalPhone": "3001234567", "update": true } ] }   (sin serviceDate)
  curl.exe -s -X POST -H "x-api-key: clave-local-de-prueba" -H "Content-Type: application/json" -d "@orden.json" http://localhost:3004/service-orders   # 201 con serviceDate de hoy y createdBy "Whatsapp"; con un JWT de un administrador, createdBy sigue siendo su nombre
  ```
  Y un `new-customer` con `address` de más de 100 caracteres: 201 (antes, error del multipart). Pegar en el reporte los status y las respuestas recortadas.
- [ ] **Step 3: Commit** — `git commit -m "feat(api): cuerpo parcial en PUT /customers, filtros name y cause, serviceDate y createdBy del bot (plan 4 D-K6, D-K9, D-K11)"`

### Task 4: Despliegue de Clasic: ConfigMap y README (D-K12)

**Files:**
- Modify: `k8s/prod-configMap.yaml` (`BOT_API_KEY`), `README.md`

**Interfaces:**
- `prod-configMap.yaml`, al final de `data`: `BOT_API_KEY: "#{CLASIC_BOT_API_KEY}#"`, con un comentario de una línea: `# Chatbot de QEP (spec 2026-10-21 D-K1): la clave que QEP manda en x-api-key`. Ni Secret ni `secretRef` ni cambios en `azure-pipelines.yml` ni en el deployment: el pod ya recibe todo el ConfigMap por `envFrom`.
- README de Clasic, sección nueva «Chatbot de QEP»: qué hace el guard, cómo generar la clave (en PowerShell `[Convert]::ToBase64String((1..48 | ForEach-Object { [byte](Get-Random -Maximum 256) }))`; en Git Bash `openssl rand -base64 48`), dónde va (`CLASIC_BOT_API_KEY` como variable secreta del grupo `Backend-prod`, que el pipeline sustituye en el ConfigMap; **la misma clave** se pega en la conexión «API externa» del tenant Clasic en QEP), que las órdenes del bot quedan con `createdBy = Whatsapp` como hasta hoy (no hay usuario bot), orden del despliegue (variable en el grupo → deploy), la advertencia de que `kubectl get configmap -o yaml` imprime la clave, y la lista de endpoints que consume QEP (Anexo C corregido: tarea 7).

- [ ] **Step 1: Escribir el ConfigMap y el README.**
- [ ] **Step 2: Verificar** — `kubectl apply --dry-run=client -f k8s/prod-configMap.yaml` sin error (los tokens `#{…}#` son strings válidos). `grep -c "#{CLASIC_BOT" k8s/prod-configMap.yaml` → 1; `ls k8s` sin ningún `secret`.
- [ ] **Step 3: Commit** — `git commit -m "chore(k8s): BOT_API_KEY del chatbot de QEP en el ConfigMap (plan 4 D-K12)"`

---

## Parte B: `qep-backend`

### Task 5: El flujo completo de Clasic como fixture, validado

**Files:**
- Create: `tests/Modules/Automations/Modules.Automations.IntegrationTests/Fixtures/clasic.json` (reemplaza `clasic-subset.json`, que se borra)
- Modify: `Modules.Automations.IntegrationTests.csproj` y `Modules.Automations.UnitTests.csproj` (el `EmbeddedResource` apunta a `clasic.json`)
- Rename: `IntegrationTests/ClasicSubset.cs` → `ClasicFlow.cs`; `UnitTests/ClasicSubsetFlow.cs` → `ClasicFlow.cs`
- Create: `tests/Modules/Automations/Modules.Automations.UnitTests/ClasicFlowTests.cs`
- Modify: `FlowValidatorTests.cs:371` (usa `ClasicFlow`)

**Interfaces:**
- El fixture es `{ "flow": { … }, "layout": { "positions": { … } } }`: `flow` en la forma de `FlowJson` (Anexo A); `layout` con una posición por nodo en cuadrícula (`x = columna × 320`, `y = fila × 180`) para que el editor lo abra legible al importar.
- Produces (los dos `ClasicFlow`):
  ```csharp
  // IntegrationTests
  internal static class ClasicFlow { public static readonly Guid ClasicApi = Guid.Parse("11111111-1111-1111-1111-111111111111"); public static string FlowJson { get; } public static string LayoutJson { get; } public static string WithTimeout(string nodeId, int seconds); }
  // UnitTests
  internal static class ClasicFlow { public static readonly Guid ClasicApi; public static FlowDefinition Parse(); public static FlowValidationContext Context(); }
  ```
- **Variables** (`variables` del flujo): `clientesPorTelefono` list, `aceptacion` text, `cedula` text, `cliente` object, `equipos` list, `equipo` object, `motivo` object, `fallas` list, `falla` object, `contacto` text, `telefonoContacto` text, `servicio` object, `fallaMantenimiento` object, `orden` object, `nombres` text, `apellidos` text, `direccion` text, `barrio` text, `ciudad` text, `factura` attachment, `numeroSolicitud` text, `direccionNueva` text.
- **Nodos**, en orden; todo bloque HTTP lleva `connectionId = ClasicApi`, `timeoutSeconds: 10`, `okWhen: null` (2xx) y `sampleResponse` con la forma real de la respuesta:

  | id | kind | lo que decide | outputs |
  | --- | --- | --- | --- |
  | `policy-lookup` | http | `GET /customers`, query `phone={telefono}`, mapping `items` → `clientesPorTelefono` | ok `policy-check`, error `handoff` |
  | `policy-check` | condition | `match: any`: `clientesPorTelefono isEmpty`; `clientesPorTelefono[0].acceptPolicy equals "true"` (`equals` compara `TextOf`, y un booleano es `true`; el bot viejo mira `every`, el flujo sólo mira al primero: se documenta; si `IsEmpty` no tratara una lista vacía, usar `countEquals "0"`) | yes `welcome`, no `policy` |
  | `policy` | openQuestion | texto de `accept-policy.message.ts` tuteando («Hola, te damos la bienvenida a CLASIC, especialistas en calentamiento de agua. Para continuar debes aceptar la política de tratamiento de datos: https://clasic.com.co/politica-de-privacidad/ Responde ACEPTO si la aceptas.»); `saveTo: aceptacion`; `validation: { kind: "keywords", keywords: ["acepto", "acepto.", "si acepto", "sí acepto", "si, acepto", "sí, acepto"] }`; `maxAttempts: null` | answered `policy-save`, invalid `null` |
  | `policy-save` | http | `PUT /customers/{clientesPorTelefono[0].id}`, body json `{ "acceptPolicy": "true", "sourceAcceptPolicy": "Whatsapp" }`, mapping `[]` | ok `welcome`, error `handoff` |
  | `welcome` | message | «Para solicitar un servicio o una asistencia técnica, te vamos a pedir unos datos. Recuerda que puedes reiniciar la conversación en cualquier momento escribiendo *REINICIAR*.» (sin el saludo de `policy`, tarea 7) | next `document` |
  | `document` | openQuestion | «Escribe tu número de documento.» (tarea 7), `digits`, `saveTo: cedula` | answered `find-customer` |
  | `find-customer` | http | `GET /customers`, query `document={cedula}` (filtro exacto; `identificationNumber` casa por subcadena: revisión final, 2026-10-23), mapping `items[0]` → `cliente` | ok `customer-found`, error `handoff` |
  | `customer-found` | condition | `cliente isNotEmpty` | yes `invoice-items`, no `not-found` |
  | `not-found` | message | texto de `document.service.ts:87` tuteando, `urlButton { "Registrarme", "https://app.clasic.com.co/registro-clientes" }` | next `not-found-options` |
  | `not-found-options` | buttons | `leave` «Dejar mis datos», `other-doc` «Otro documento» (el validador corta un botón en 20 caracteres); `fallback: repeat` | leave `new-first-name`, other-doc `document` |
  | `new-first-name` … `new-city` | openQuestion ×5 | «¿Cuáles son tus nombres?» → `nombres`; «¿Y tus apellidos?» → `apellidos`; «Escribe la dirección donde está el calentador.» → `direccion`; «¿En qué barrio?» → `barrio`; «¿En qué ciudad?» → `ciudad`; todas `none` | cada una a la siguiente; la última a `new-failures` |
  | `new-failures` | http | `GET /failures`, mapping `$` → `fallas` | ok `new-failure`, error `handoff` |
  | `new-failure` | list | «¿Qué falla presenta el calentador?», botón «Ver fallas», dynamic `fallas` (`id`, `description`), `saveTo: falla`, `fallback: repeat` | chosen `invoice-photo`, tooMany `handoff` |
  | `invoice-photo` | attachment | «Envía la foto o el PDF de la factura de compra del calentador.», `accept: any`, `saveTo: factura` | received `new-customer` |
  | `new-customer` | http | `POST /customers/new-customer`, body multipart `fields`: `identificationNumber={cedula}`, `firstName={nombres}`, `lastName={apellidos}`, `address={direccion}`, `neighborhood={barrio}`, `addressDescription=Ciudad: {ciudad}`, `phone={telefono}`, `departmentId=5`, `cityId=5001000`, `failureId={falla.id}`; `fileField: file`, `fileVariable: factura`; mapping `serviceOrderNumber` → `numeroSolicitud` | ok `registered`, error `handoff` |
  | `registered` | resolve | «Recibimos tus datos. Tu solicitud quedó registrada con el número {numeroSolicitud}; un agente se comunicará contigo para confirmar la visita.» | — |
  | `invoice-items` | http | `GET /invoices/customer/{cliente.id}/all-invoice-items`, mapping `$` → `equipos` | ok `equipment`, error `handoff` |
  | `equipment` | list | «Elige el equipo o la dirección donde necesitas el servicio.», botón «Ver equipos», dynamic `equipos` (`id`, `referenceCode`, `address`), extra `other` «Otra dirección», `saveTo: equipo`, `fallback: repeat` | chosen `open-order-check`, extra `other-address`, tooMany `handoff` |
  | `open-order-check` | condition | `equipo.item.haveOrderOpen equals "true"` | yes `open-order-notice`, no `service-kind` |
  | `open-order-notice` | message | «Ya hay una orden de servicio abierta para esa dirección. Elige otro equipo o escribe REINICIAR.» | next `equipment` |
  | `other-address` | openQuestion | «Escribe la dirección, el barrio y la ciudad de la nueva dirección.» → `direccionNueva` | answered `other-address-handoff` |
  | `other-address-handoff` | handoff | «Gracias. Te paso con un asesor para registrar la dirección nueva.» (D-K8) | — |
  | `service-kind` | buttons | «¿Qué tipo de servicio necesitas?», `tech` «Servicio técnico», `maint` «Mantenimiento», `saveTo: motivo` | tech `failures`, maint `maintenance-service` |
  | `failures` / `failure` | http / list | como `new-failures` / `new-failure` | ok `failure`; chosen `contact-name`, tooMany `handoff` |
  | `maintenance-service` | http | `GET /services`, query `name=STM`, mapping `$[0]` → `servicio` | ok `maintenance-failure`, error `handoff` |
  | `maintenance-failure` | http | `GET /failures`, query `cause=MANTENIMIENTO`, mapping `$[0]` → `fallaMantenimiento` | ok `contact-name`, error `handoff` |
  | `contact-name` | openQuestion | «¿Cómo se llama la persona de contacto para la visita?» → `contacto` | answered `contact-phone` |
  | `contact-phone` | openQuestion | «¿A qué número la llamamos?», `digits` → `telefonoContacto` | answered `which-order` |
  | `which-order` | condition | `motivo.id equals "maint"` | yes `maintenance-order`, no `service-type` |
  | `service-type` | http | `GET /service-orders/auto-select-service-type`, query `invoiceItemId={equipo.id}`, mapping `$` → `servicio` | ok `create-order`, error `handoff` |
  | `create-order` | http | `POST /service-orders`, body json `{ "customerId": {cliente.id}, "failureId": {falla.item.id}, "serviceId": {servicio.id}, "invoiceItems": [ { "id": "{equipo.id}", "contact": "{contacto}", "optionalPhone": "{telefonoContacto}", "update": true } ] }`, mapping `$` → `orden` | ok `summary`, error `order-error` |
  | `summary` | resolve | el resumen de `summary.message.ts` tuteando y sin erratas: «Resumen de la solicitud:\n\nServicio: {servicio.description}\nTipo de daño: {falla.title}\nNombre de contacto: {contacto}\nNúmero de contacto: {telefonoContacto}\nNúmero de identificación: {cedula}\nDirección de visita: {equipo.item.address}\n\nGracias por contactarnos. Tu servicio será atendido con la orden N° {orden.id}; un agente se comunicará contigo para confirmar la fecha y hora de la visita.» | — |
  | `maintenance-order` | http | como `create-order` con `"failureId": {fallaMantenimiento.id}` | ok `maintenance-summary`, error `order-error` |
  | `maintenance-summary` | resolve | el mismo resumen sin «Tipo de daño» más el bloque de precios de `summary.message.ts` (desarme y limpieza…, $120.000, $150.000, $150.000) | — |
  | `order-error` | message | «No pudimos crear la orden: puede que ya exista una abierta para esa dirección o que haya un bloqueo.» (la frase del asesor la dice `handoff`, tarea 7) | next `handoff` |
  | `handoff` | handoff | «Te paso con un asesor.» | — |

  Los `{…}` de un cuerpo JSON siguen P2-R11: `{cliente.id}` suelto interpola el número; `"{equipo.id}"` entre comillas, el texto. `falla.id` es el id de la fila (texto) y `falla.item.id` el número del objeto: en el multipart va el texto; en el JSON, el número.

- [ ] **Step 1: Escribir las pruebas que fallan** (`ClasicFlowTests`, sobre `ClasicFlow.Parse()`):
  ```csharp
  [Fact] public void TheFlowIsValidForAWhatsAppConnectionWithTheClasicApi()   // FlowValidator.Validate(flow, ClasicFlow.Context()) → sin issues (reemplaza a la de FlowValidatorTests:371, que se borra)
  [Fact] public void EveryHttpBlockUsesTheClasicConnection()                  // todos los HttpNode.ConnectionId == ClasicApi
  [Fact] public void EveryHttpPathIsInTheClasicContract()                     // el conjunto {Method + Path} ⊆ el del Anexo C corregido: GET /customers, PUT /customers/{clientesPorTelefono[0].id}, GET /invoices/customer/{cliente.id}/all-invoice-items, GET /failures, GET /services, GET /service-orders/auto-select-service-type, POST /service-orders, POST /customers/new-customer
  [Fact] public void CustomersAreReadFromItemsNotData()                       // los dos GET /customers mapean desde `items…`, ninguno desde `data`
  [Fact] public void NewCustomerSendsEveryFieldTheDtoRequires()               // fields de new-customer ⊇ {identificationNumber, firstName, lastName, address, neighborhood, phone, departmentId, cityId, failureId} y FileField == "file"
  [Fact] public void ThePolicyQuestionAcceptsTheUsualWaysOfSayingYes()        // CustomerReply sobre el nodo `policy`: "ACEPTO", "Acepto.", "sí, acepto" → Answered; "no" → Invalid (usa el mismo camino que FlowEngineWaitingTests para una pregunta abierta)
  [Fact] public void EveryNodeHasALayoutPosition()                            // layout.positions tiene exactamente los ids de nodes
  ```
- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~ClasicFlowTests"`. Expected: FAIL por compilación (`ClasicFlow` no existe).
- [ ] **Step 3: Escribir `clasic.json`, renombrar las dos clases, apuntar los csproj, borrar `clasic-subset.json`.** `FlowJson.Parse` recibe sólo el miembro `flow`.
- [ ] **Step 4: Correr y verificar que pasan** — mismo filtro más `FullyQualifiedName~FlowValidatorTests|FullyQualifiedName~SimulationRunnerTests|FullyQualifiedName~FlowEngineWaitingTests` (los tres usaban `ClasicSubsetFlow`): PASS.
- [ ] **Step 5: Commit** — `git commit -m "test(automations): el flujo completo de Clasic como fixture validado (spec 2026-10-21 Anexo B)"`

### Task 6: `ClasicFlowEndToEndTests` contra el contrato real

**Files:**
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ClasicFlowEndToEndTests.cs`, `AutomationsApiHarness.cs:539-552` (`ImportChatbotAsync` recibe `layoutJson`)

**Interfaces:**
- `ArrangeAsync` importa `ClasicFlow.FlowJson` con `ClasicFlow.LayoutJson` y registra las respuestas por defecto con la **forma real**: `GET /customers?phone=` y `?identificationNumber=` → `{"items":[{"id":7,"firstName":"Laura","lastName":"Gómez","acceptPolicy":true}],"totalCount":1}`; `all-invoice-items` → arreglo con `haveOrderOpen:false`; `/failures` (sin query) → arreglo; `/failures?cause=MANTENIMIENTO` → `[{"id":5,"description":"MANTENIMIENTO","cause":"MANTENIMIENTO"}]`; `/services?name=STM` → `[{"id":12,"name":"STM","description":"Servicio técnico de mantenimiento"}]`; `auto-select-service-type` → `{"id":9,"name":"STG","description":"Servicio técnico en garantía"}`; `POST /service-orders` → 201 `{"id":55}`; `new-customer` → 201 `{"serviceOrderNumber":"OS-123"}`.
- Las pruebas existentes (`TheHappyPathCreatesAServiceOrder`, `AnUnknownCustomerGoesToRegistrationAndUploadsTheInvoice`, `AnApiErrorHandsOff`, el bucle, el timeout y la fuga de la clave) se ajustan al flujo completo: el camino ahora empieza por `policy-lookup`, y el registro pide nombres, apellidos, dirección, barrio, ciudad y la falla antes de la foto.

- [ ] **Step 1: Escribir las pruebas que fallan** (las nuevas; las viejas fallan solas al cambiar el fixture):
  ```csharp
  [Fact] public async Task ACustomerWhoHasNotAcceptedThePolicyIsAskedAndTheAcceptanceIsSaved()   // ?phone= → acceptPolicy:false; «Hola» → el primer saliente contiene "política de tratamiento de datos"; «Acepto.» → PUT /customers/7 con body que contiene "\"acceptPolicy\":\"true\"" y "\"sourceAcceptPolicy\":\"Whatsapp\"", y luego la bienvenida
  [Fact] public async Task AnUnknownPhoneSkipsThePolicy()                                        // ?phone= → {"items":[],"totalCount":0}; el primer saliente es la bienvenida; no hubo PUT
  [Fact] public async Task AnEquipmentWithAnOpenOrderIsRefusedAndTheListIsSentAgain()           // equipo 1 haveOrderOpen:true; elegirlo → saliente "orden de servicio abierta" y una segunda lista con las mismas filas; elegir el 2 sigue a los botones; ningún POST /service-orders todavía
  [Fact] public async Task MaintenanceUsesTheStmServiceAndTheMaintenanceFailure()               // botón maint → GET /services?name=STM y GET /failures?cause=MANTENIMIENTO; contacto y teléfono; el POST /service-orders lleva "serviceId":12 y "failureId":5; el resolve contiene "$120.000"
  [Fact] public async Task AnotherAddressOfAKnownCustomerHandsOff()                              // fila extra «Otra dirección» → pregunta de dirección; texto → conversación Paused con Handoff y la variable direccionNueva en GET /runs («Datos capturados»)
  [Fact] public async Task RegistrationSendsTheRealMultipartFields()                              // la parte de new-customer: nombres de las partes == {identificationNumber, firstName, lastName, address, neighborhood, addressDescription, phone, departmentId, cityId, failureId, file}; departmentId "5", cityId "5001000", failureId el id de la fila elegida, file con InvoiceBytes
  ```
- [ ] **Step 2: Correr y verificar que fallan** — Run: `dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ClasicFlowEndToEndTests"`. Expected: FAIL (compilación por `ClasicSubset`; luego por la forma de las respuestas).
- [ ] **Step 3: Ajustar `ArrangeAsync`, los fakes y las pruebas viejas; `ImportChatbotAsync(…, string flowJson, string layoutJson, …)`.**
- [ ] **Step 4: Correr y verificar que pasan** — mismo filtro: PASS (12). Y `--filter "FullyQualifiedName~ChatbotsApiTests|FullyQualifiedName~SimulationsApiTests|FullyQualifiedName~TemplatesApiTests"` siguen en PASS (comparten el harness).
- [ ] **Step 5: Commit** — `git commit -m "test(automations): el flujo de Clasic de punta a punta contra el contrato real de su API (spec 2026-10-21 Anexo C)"`

### Task 7: Documentación: runbook de Clasic, Anexo C corregido, gotchas

**Files:**
- Modify: `README.md` (bajo «### Automatizaciones: constructor de chatbots», nueva «#### Clasic: conexión, importación y corte»)
- Modify: `docs/superpowers/specs/2026-10-21-constructor-de-chatbots-design.md` (§2.6, §10, Anexos B y C)
- Modify: `CLAUDE.md` (una entrada en «Gotchas verificados»)

**Interfaces:**
- README, en este orden y en PowerShell: (1) en Clasic, migración + variables + deploy (apunta al README de Clasic); (2) en QEP, conexión «API externa» del tenant Clasic desde Integraciones: `baseUrl = https://api-clasic.qcode.co`, header `x-api-key`, ruta de prueba `/failures` (D-K10), la clave = `CLASIC_BOT_API_KEY`; «Probar» tiene que dar OK y, con una clave cambiada, fallar; (3) el módulo `automations` prendido en `tenancy.tenant_modules`; (4) importar: el cuerpo de `POST /api/v1/tenants/{tenantId}/automations/chatbots/import` se arma desde el fixture (`flow` y `layout` de `tests/…/Fixtures/clasic.json`, `externalApis: [{ id: "11111111-…", name: "API de Clasic", status: "Active" }]`, `connectionMap: { "11111111-…": "<id de la conexión>" }`, `trigger: "NewConversation"`, `restartKeyword: "reiniciar"`, `maxAttempts: 3`), con un script PowerShell de diez líneas que lo construye con `Get-Content | ConvertFrom-Json` y lo manda con `curl.exe -d "@import.json"`; (5) en el editor: revisar `departmentId`/`cityId` del bloque «Registrar cliente» (D-K7), asignar la conexión de WhatsApp, publicar; (6) simular desde el editor con `liveHttp: true` contra la API real **antes** del corte (crea una orden de verdad: usar un cliente de prueba de Clasic); (7) corte: el número de Clasic entra a QEP por Embedded Signup (spec de migración §7 paso 1) y ahí mismo Meta deja de entregarle a lulo; lulo-chat deja de reenviar a `whatsapp-bot/webhook`; `Messaging:Automation:Enqueue` ya está prendido. Lo que sigue saliendo por lulo: `solicitud_os_creada` (D-K4) y los avisos de visita (spec 3).
- Spec: Anexo C pasa a decir `items`/`totalCount`; «Departamentos y ciudades» sale (D-K7) y entran `GET /services?name=` y `GET /failures?cause=` (D-K6); `PUT /customers/{id}` con cuerpo parcial (D-K9); `new-customer` con los campos del DTO real y los límites del multipart; Anexo B §6 y §11 se corrigen (ciudad como texto, dos bloques de crear orden, «Otra dirección» a asesor); §2.6 y §10 incorporan D-K5..K12 con la nota «decididas en el plan 4 (2026-10-22)», y D-K2 (usuario «bot») queda anulada por D-K11; §10 deja de pedir un Secret ni el usuario (la clave va al ConfigMap, D-K12) y §12 deja de pedir pruebas Jest en Clasic (decisión del developer, 2026-10-23).
- `CLAUDE.md`, gotcha: **«`GET /customers` de Clasic responde `items`/`totalCount`, no `data`/`total`; `/services` y `/departments/*` son públicos, así que la ruta de prueba de la conexión tiene que ser una protegida (`/failures`)»**, con la fecha.

- [ ] **Step 1: Escribir las tres piezas.**
- [ ] **Step 2: Verificar** — el script PowerShell del README se corre contra el host local con el stub de auth (`$env:Authentication__UseDevelopmentStub = "true"`, `X-Permissions` con `automations.chatbot.manage` e `integrations.connection.manage`) y responde `201`; el chatbot abre en el editor del frontend con los nodos en cuadrícula. Pegar en el reporte el status y el id, nunca la clave.
- [ ] **Step 3: Commit** — `git commit -m "docs(automations): runbook de Clasic, Anexo C corregido contra la API real y gotcha de la ruta de prueba (plan 4)"`

---

## Merge y estado

- **Clasic:** `git -C <worktree> merge develop` (si se movió), `pnpm check-all` y la verificación manual de la tarea 3 una vez más; luego, en el árbol principal de Clasic y **sólo** si `git branch --show-current` es `develop` y `git status --porcelain` está vacío: `git merge --ff-only feature/bot-qep`. `main` lo mueve el owner: ese push es el despliegue.
- **QEP:** mismo criterio con `feature/automatizaciones-clasic`; después, en `develop`, un commit `docs(estado): plan 4 (Clasic) del constructor de chatbots en develop` que actualiza la fila de Automatizaciones y el Registro de `docs/estado.md`. Nada se empuja.
- **Lo que no es código y queda al developer** (README de la tarea 7): generar la clave, cargar `CLASIC_BOT_API_KEY` en `Backend-prod`, desplegar Clasic, crear la conexión en el tenant Clasic, importar y publicar, simular con `liveHttp` contra un cliente de prueba, y el corte del número.
