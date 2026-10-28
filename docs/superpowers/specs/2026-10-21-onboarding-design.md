# Onboarding: «Primeros pasos» y bienvenida a invitados

- **Estado:** **incompleto, en pausa** (owner, 2026-10-21). El owner priorizó otras tareas. Antes de
  planear o construir algo, hay que leer «Para retomar», al final.
- **Fecha:** 2026-10-21.
- **Repos:** `qep-backend` (módulo nuevo `Onboarding`) y `qep-frontend` (feature `onboarding`).
- **Cierra:** OD-0106 («¿Cuál es el flujo inicial de onboarding?»), si se aprueba.
- **Revisión:** v4, después de tres rondas.
  - **Ronda 1:** tres revisores (alguien nuevo en la app, diseño, backend) dieron «OK con cambios».
  - **Ronda 2:** el mismo recorrido como alguien nuevo dio «OK con cambios», con 5 puntos
    obligatorios.
  - **Ronda 3:** chequeo final contra el código: los 5 puntos se pueden construir y quedaban 2
    contradicciones.
  - Todo está incorporado.
  - §9.10, §9.11 y D-6 se escribieron **después** de esas rondas y no pasaron por revisión.
- **Mockup:** HTML interactivo, versión 3 (con D-6):
  [claude.ai/artifact/C4HBWvnpiTR2acM8QPvVCa](https://claude.ai/artifact/C4HBWvnpiTR2acM8QPvVCa). Es
  privado del owner y tiene seis escenas: `/setup`, estados de un paso, bienvenida, lista vacía,
  celular e instalación desde Shopify.

## 1. Por qué

Hoy no hay onboarding. Quien registra una empresa cae en `/quotes`, que vacío sólo dice «No hay
cotizaciones para mostrar.» (`quotes-list-page.tsx:142`). No sabe qué hacer ni en qué orden.

**Lo que traba a alguien nuevo** (verificado contra el código el 2026-10-21):

1. **Tasas de impuesto.** Ningún tenant nuevo las tiene: sólo las crea `CatalogSeeder.cs`, que es
   de desarrollo. El selector de IVA dice «No hay tasas de impuesto creadas», sin enlace
   (`product-form.tsx:252`). El producto se guarda igual, con el precio «IVA incluido» y sin IVA
   discriminado.
2. **Cliente.** Cotizar exige un cliente activo con CUC (`QuotationCustomerEligibility.cs:32-50`).
   El CUC lo asigna el sistema al guardar. La ficha pide tipo y número de documento, contacto,
   WhatsApp, dirección y ciudad. «Ciudad» queda deshabilitada hasta elegir un departamento que no
   está marcado obligatorio (`customer-form.tsx:488`, `:564-572`).
3. **Enviar.** La vigencia viene precargada, y la entrega y la facturación toman los datos del
   cliente. Lo único que hay que elegir a mano es la **empresa que factura**, aunque haya una sola
   (`quote-billing-card.tsx:66-80`). La persona se entera recién cuando toca «Enviar»
   (`quote-actions.ts:48`).
4. **Qué es «enviar».** Es un WhatsApp **real** con el PDF, que le llega al teléfono del cliente
   desde la cuenta de la plataforma (`SendQuotation.cs:129`). Descargar el PDF no cambia el estado.
5. **Empresa.** La pantalla se titula «Crear empresa» (`company-form-page.tsx:37`) justo después
   de que la persona «creó su empresa» al registrarse. Pide una cuenta bancaria sin decir para qué.

**Lo que decidió el usuario:**
- El momento de valor **depende de los módulos** que tenga prendidos el tenant.
- Entran en el alcance la **guía de configuración** y la **bienvenida para invitados**.
- Quedan fuera las preguntas al registrarse y los datos de ejemplo.

**Lo que tomé de la investigación** (Shopify, Pipedrive, Linear, Notion, Slack, respond.io, Wati,
Intercom):
- **Pasos:** pocos, escritos como acción, que se marcan solos cuando se hace la acción real.
- **Lugar:** la guía vive en el inicio (Shopify) y no en un widget.
- **Sin interrupciones:** nada de tours ni de muros de modales.
- **Invitados:** reciben otra cosa, una introducción corta a lo que les toca.
- **Requisitos:** los de WhatsApp se dicen antes del error, no después.

## 2. Objetivos, no objetivos, anti-objetivos

- **Objetivo.** Quien entra por primera vez sabe en menos de 10 segundos qué hacer, y llega a su
  primer momento de valor sin pedir ayuda. En el tenant por defecto ese momento es una cotización
  enviada por WhatsApp.
- **Objetivo.** El invitado sabe a qué entró y qué puede hacer con su rol.
- **No objetivo.** Preguntas al registrarse, datos de ejemplo, correos de recordatorio, video y
  métricas de activación (quedan para después).
- **Anti-objetivo.** Un tour forzado, un modal que se abre solo, un paso que se marca hecho con un
  clic, o una guía que empuja a `/setup` a quien ya trabaja todos los días.

## 3. Concepto: metas con pasos, según los módulos

La guía se arma con **metas**. Cada meta es un momento de valor y tiene de 1 a 5 pasos. Las arma el
backend con los módulos prendidos. Un paso que comparten varias metas se hace una sola vez.

| Meta | Módulo | Pasos | Opcional |
|---|---|---|---|
| `first-quote` · **Envía tu primera cotización** | quotations | `tax-rates`¹ → `product` → `customer` → `first-quote-sent` | no |
| `first-chat` · **Atiende tu primer chat** | messaging | `whatsapp` → `first-reply` | no |
| `first-sale` · **Haz tu primera venta en caja** | pos | `pos-first-sale` | no |
| `company-details` · **Completa los datos de tu empresa** | companies | `company-details` | **sí** (D-6) |
| `shopify` · **Recibe tus pedidos de Shopify** | shopify | `shopify` → `shopify-setup` → `shopify-first-order` | **sí**, salvo para una empresa que llegó desde Shopify (§9.5), donde es la meta principal |
| `team` · **Trabaja en equipo** | — | `team` | **sí** |

¹ Depende de D-1, no de cuántas tasas haya en el momento. Con D-1 aprobada, el paso no existe. Sin D-1,
existe siempre y se marca hecho con ≥1 tasa. Así nunca se esfuma al completarse ni cambia «1 de 5» por
«1 de 4».

- **Meta principal.** Es la primera meta no opcional que aplica, en el orden de la tabla. En el
  tenant por defecto (`DefaultForNewTenants`) es `first-quote`, con 4 o 5 pasos, más las dos
  opcionales al final.
- **Cuándo está hecho cada paso.** El backend lo calcula en vivo y nunca escribe en un GET. Todas
  son consultas `EXISTS` sobre índices que ya existen:

| Paso | Hecho cuando | Dónde |
|---|---|---|
| `company-details` | la empresa por defecto (D-6) tiene NIT, ciudad y ≥1 cuenta bancaria | `companies.companies` |
| `tax-rates` | ≥1 tasa de impuesto | Catalog |
| `product` | ≥1 producto (`Product.Create` crea la variante por defecto) | `catalog.products`, `IX_products_tenant` |
| `customer` | ≥1 cliente activo con CUC | Customers |
| `first-quote-sent` | ≥1 cotización con `sent_at` no nulo; no vuelve atrás si se anula o vence | `Quotation.cs:731` |
| `whatsapp` | ≥1 conexión `whatsapp-cloud` en cualquier estado | `integrations.connections` |
| `first-reply` | ≥1 mensaje saliente de un miembro | Messaging |
| `pos-first-sale` | ≥1 venta de POS | Pos |
| `shopify` | ≥1 conexión `shopify` (no se usa `shopify.stores`, que se crea tarde) | `integrations.connections` |
| `shopify-setup` | los pasos 1 a 4 de la configuración de la tienda están hechos (§9.4) | Shopify |
| `shopify-first-order` | ≥1 pedido de Shopify importado como pedido de QEP | `shopify.orders` |
| `team` | ≥1 membresía con `origin='invitation'`, en cualquier estado | `tenancy.memberships` |

- **`blockedBy`** sólo marca dependencias reales, y lo manda el backend:
  - `first-quote-sent` depende de `product`. La empresa ya no lo bloquea, porque existe la empresa
    por defecto (D-6). El cliente tampoco: el buscador del editor permite crear uno
    (`customer-combobox.tsx:281`).
  - `pos-first-sale` no depende de nada: `pos.session.company_required` se cumple con la empresa por
    defecto.
  - `first-reply` depende de `whatsapp`.
  - Cuando lleguen los planes 2 y 3 del envío por canal, `first-quote-sent` va a depender también
    de un canal. Ese cambio se hace sólo en el backend.
- **Advertencia de entorno.** Con `Quotations:WhatsApp:*` sin configurar, el envío cae en
  `LogWhatsAppSender`: queda `Sent` sin entregarse nada. En producción está configurado; en local
  el paso se marca hecho igual.

## 4. Frontend

### 4.1 Página `/setup` «Primeros pasos»

**Entrada en el menú:**
- Es el **primer ítem del sidebar** mientras hay guía y no está cerrada, con la insignia «2/5» de
  la meta principal. El nombre accesible es «Primeros pasos, 2 de 5».
- Se ve igual en el sidebar expandido, en el riel colapsado (`RailTooltip`) y en el drawer de
  ≤700px. `/setup` pasa a ser una `SidebarRoute`.

**Qué se descarta:** el widget, el Sheet (`ui/sheet.tsx` es un Dialog modal de Radix), la apertura
automática y la marca en `localStorage`.

**Landing:**
- El registro (`routes/register.tsx:66-68`) navega a `/setup` en vez de `/quotes`. Si llega con
  `returnTo` (por ejemplo, el enlace de Shopify), ese destino sigue mandando.
- `landingFor` manda a `/setup` **sólo mientras la meta principal esté incompleta** y la guía no
  esté cerrada. Las metas de POS, mensajería y las opcionales nunca redirigen: así no se arma una
  pared para quien ya cotiza todos los días.
- `landingFor` es puro y síncrono (`landing.ts:35-46`), así que recibe el estado del onboarding
  como argumento, tanto desde `resolveLanding` como desde `routes/index.tsx`.

**Cabecera:**
- **Para quien registró la empresa**, armada según las metas: «Te damos la bienvenida a Lulo CRM.
  Aquí armas cotizaciones con tus precios y se las mandas a tus clientes por WhatsApp. Te faltan 4
  pasos para enviar la primera, unos 10 minutos.»
- **Para un admin invitado:** «Esto es lo que falta para que {empresa} esté lista».

**Metas:** se muestran como secciones con su propio progreso. La primera incompleta va abierta y
las opcionales van al final, bajo «Opcional».

**Anatomía de un paso.** Cada paso es un `<li>` dentro de un `<ol>`:
- **Siempre visible:**
  - un ícono de estado (`aria-hidden`) más el estado en texto `sr-only`;
  - el título, que es el disparador del acordeón (sin `aria-label`, porque `aria-expanded` ya lo
    dice);
  - un tiempo estimado mientras está pendiente.
- **Al abrirlo:**
  - una línea de por qué importa;
  - los requisitos, si los hay;
  - un CTA verbo + objeto;
  - «Omitir por ahora» si el paso es opcional;
  - «¿Cómo se hace?» si hay artículo de ayuda.

| Estado | Cómo se ve |
|---|---|
| hecho | check relleno, título apagado sin tachar, sin CTA |
| actual | el primero pendiente no bloqueado: abierto, con botón primario |
| pendiente | cerrado, con botón secundario |
| bloqueado | candado y «Primero: {paso}», con enlace a ese paso; usa `components/blocked-button.tsx` |
| sin permiso (`actionable: false`) | «Lo hace un administrador», sin botón |
| omitido | círculo punteado, insignia «Omitido» y enlace «Retomar» |

**Progreso:**
- Se escribe «3 de 5» por meta. Sólo los pasos de las metas opcionales se pueden omitir; una meta
  omitida muestra «Omitida» y no cuenta como hecha.
- El `Progress` lleva `aria-label="Progreso de {meta}"` y `aria-valuetext="3 de 5 pasos"`.
- Una región `aria-live="polite"` anuncia los cambios de estado.
- La transición respeta `prefers-reduced-motion`.

**Volver a la guía:**
- `companies/new`, `catalog/products/new`, `customers/new` y `settings/tax-rates/new` aceptan
  `returnTo=/setup`, con `validateReturnToSearch` de `features/auth/utils/return-to.ts`. Al
  guardar vuelven ahí en vez de ir a su lista.
- `quotes/new` **no** lleva `returnTo`: crear lleva al editor, y el editor es donde se envía.

**Frescura:**
- `/setup` pide los datos de nuevo al montar.
- El ítem del menú vive en el shell, que no se remonta. Por eso el `MutationCache` global (el mismo
  enganche de `createSessionAwareQueryClient`) invalida la query del onboarding cuando cualquier
  mutación sale bien y la guía está activa. Ninguna feature tiene que conocer al onboarding.

**Celebrar una meta, no un paso.** Si una meta pasa a completa entre dos respuestas (por ejemplo,
se envió la primera cotización), sale **un** toast: «¡Enviaste tu primera cotización!», con la
acción «Ver lo que sigue» hacia `/setup`. Los pasos no tienen toast propio: ya sale el de cada
feature.

**Al terminar todo:** «¡Todo listo!» con 2 o 3 enlaces de «Lo que sigue» (por ejemplo, «Convierte
una cotización en pedido») y el botón «Cerrar guía», que manda `dismissed: true`. Hasta entonces el
ítem del menú muestra un check.

**Cerrar antes de terminar:**
- Se confirma con un `dialog` con `role="alertdialog"`: «Puedes volver a abrirla desde el menú de
  tu usuario o desde Configuración».
- El foco vuelve al encabezado.
- **Las dos entradas existen:** «Guía de configuración» en `user-menu.tsx` y un enlace en
  `/settings`, cuando la guía está cerrada y sin completar.

**Ayuda:** al pie va «¿Necesitas ayuda? Escríbenos por WhatsApp», sólo si existe
`VITE_SUPPORT_WHATSAPP` (D-4).

### 4.2 Copy por paso (tuteo)

| Paso | Título | Por qué | Botón | Tiempo |
|---|---|---|---|---|
| `company-details` | Completa los datos de tu empresa | Agrega tu NIT y tu cuenta bancaria si quieres que salgan en tus cotizaciones y pedidos, para que te paguen. | Completar datos | 3 min |
| `tax-rates` | Configura el IVA | Así tus cotizaciones muestran el IVA separado del precio. | Configurar IVA | 1 min |
| `product` | Agrega tu primer producto | Con su precio con IVA incluido, listo para cotizar. | Agregar producto | 2 min |
| `customer` | Registra un cliente | Es a quien le llega la cotización. Para probar, regístrate a ti mismo con tu WhatsApp. | Agregar cliente | 2 min |
| `first-quote-sent` | Envía tu primera cotización | Elige el cliente y el producto, revisa el PDF y toca «Enviar por WhatsApp». | Crear cotización | 3 min |
| `whatsapp` | Conecta tu WhatsApp | Los mensajes de tus clientes llegan a una bandeja que comparte tu equipo. Necesitas acceso a Meta Business, un número que no esté en otra plataforma y un computador. | Conectar WhatsApp | 10–15 min; Meta puede tardar en aprobar el número |
| `first-reply` | Responde tu primer mensaje | Escríbete desde otro teléfono y contesta desde la bandeja. | Abrir bandeja | 2 min |
| `pos-first-sale` | Haz tu primera venta | Abre la caja, cobra y ciérrala al final del día. | Abrir caja | 3 min |
| `shopify` | Conecta tu tienda Shopify | Si vendes en Shopify, sus pedidos llegan solos aquí. | Conectar | 5 min |
| `team` | Invita a tu equipo | Para que tus vendedores coticen con tus mismos precios. | Invitar | 1 min |

**Un solo término para «tu empresa».** En el editor, «Facturación» es a quién se le factura, y la
empresa propia se llama «Empresa que factura» (`document-billing-sections.tsx:20-22`, `:68-73`).
El paso, la pantalla `/companies/new` y el riel del editor usan ese mismo término. Así nadie cree
que ya completó «los datos de facturación» cuando el aviso al enviar dice que faltan.

### 4.3 Arreglos en pantallas existentes (en el alcance)

- **Empresa que factura** (en `QuoteBillingCard`, que comparten `/quotes/new`, el editor y la
  creación de pedidos): si el tenant tiene **una sola** empresa, se elige sola,
  igual que ya hace con la cuenta cuando hay una (`quote-billing-card.tsx:66-80`). Esto quita el
  único tropiezo real antes de enviar.
- **Estados vacíos:**
  - `/quotes`: «Crea cotizaciones y mándalas por WhatsApp», con «Crear cotización» y el enlace
    «Ver primeros pasos».
  - `/catalog/products` y `/customers`: lo mismo, con su acción.
  - El componente `EmptyState` va en `src/components/` porque lo consumen tres features.
- **Selector de impuestos vacío** (`product-form.tsx:252`): un enlace «Crear tasa de impuesto».
- **Pantalla de empresa:** el título pasa a «Tu empresa (la que factura)». La cuenta bancaria
  explica «aparece en tus cotizaciones para que el cliente sepa dónde pagarte».
- **Formulario de cliente:** «Departamento» lleva asterisco y el voseo pasa a tuteo («Selecciona»).
- **`shopify-connect-page.tsx:108-109`:** «QEP» se reemplaza por `readTenantBrand()`.

### 4.4 Bienvenida a invitados

Un `Dialog` de una sola pantalla, nunca un tour.

- **Cuándo:** si `welcome.pending` es `true`.
- **Contenido:**
  - «Te damos la bienvenida a {empresa}»;
  - «Entraste como {rol}», con el `displayName` de `/auth/me`;
  - hasta 3 tarjetas de «Lo que puedes hacer»;
  - «¿Dudas? Pregúntale a tu administrador».
- **Tarjetas:**
  - Se eligen **por permiso y por módulo**, no por clave de rol (hay roles personalizados; es la
    misma regla que `landingFor`).
  - Una tarjeta que llevaría a un bloqueo no se muestra. Por ejemplo, «Crea cotizaciones» no
    aparece para un asesor si el tenant todavía no tiene empresa ni producto: en su lugar va «Tu
    administrador está terminando de configurar la cuenta».
  - Ejemplos: «Crea cotizaciones», «Atiende la bandeja», «Abre caja y vende», «Factura pedidos».
- **Acciones:** tocar una tarjeta acusa y navega. «Empezar» acusa y cierra, y Esc o la X también
  acusan.
- **Invitado admin con guía abierta:** primero ve la bienvenida y después «Primeros pasos», que ya
  está en el menú.

### 4.5 Estructura y montaje

Árbol canónico en `src/features/onboarding/`:
- `services/onboarding.api.ts`: parser tolerante. Una meta o paso desconocido se ignora, y un 404
  se lee como sin onboarding.
- `types/onboarding.ts`: copy, rutas, íconos y tiempos por paso.
- `hooks/use-onboarding.ts`
- `hooks/use-goal-celebration.ts`
- `pages/setup-page.tsx`
- `components/`: `goal-section`, `setup-step`, `dismiss-guide-dialog`, `member-welcome-dialog`.
- `utils/welcome-highlights.ts`

Montaje:
- **Container:** `src/routes/_authenticated/setup.tsx`.
- **Ítem del menú:** dinámico, en `sidebar-nav-items.ts` y `sidebar.tsx`.
- **Reabrir:** `user-menu.tsx` y `/settings`.
- **Bienvenida:** `<MemberWelcome />` se monta en `AppShell`.
- **Degradación:** con un 404, un error o `checklist: null` no hay ítem ni redirect, y `/setup`
  redirige al landing normal. El onboarding nunca bloquea el uso de la app.

**Estados que exige CLAUDE.md:**

| Pieza | Carga | Vacío | Error | Éxito |
|---|---|---|---|---|
| `/setup` | skeleton de metas | «¡Todo listo!» | «No pudimos cargar tu guía» + «Intenta de nuevo» | metas |
| Ítem del menú | no se pinta | — | no se pinta | se pinta |
| Bienvenida | no se pinta | — | no se pinta | dialog |
| Mutaciones | botón en espera | — | toast de error, sin cambio optimista | se actualiza |

## 5. Backend: módulo `Onboarding`

- **Patrón Reporting.** Application declara el puerto `IOnboardingProgressSource`. El adaptador
  vive en `src/Bootstrapper/` y lee los DbContext de cada módulo, como
  `QuotationsReportSource.cs:17`. El módulo tiene sus propias `OnboardingLayerTests`
  (`ReportingLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules` es el
  precedente).
  - No va en Tenancy, que no ve los otros módulos.
  - Tampoco van consultas en `src/Api`.
- **Esquema `onboarding`:**
  - `tenant_checklists (tenant_id PK, dismissed_at, source, primary_goal)`. `primary_goal` es nulo
    salvo para una empresa que llegó desde Shopify (§9.5).
  - `checklist_skips (tenant_id, step_key)`
  - `member_welcomes (membership_id PK, acknowledged_at)`
- **Backfill en la migración:**
  - Todos los tenants existentes con `dismissed_at=now()` y `source='backfill'`.
  - Todas las membresías que no están `Invited`, como acusadas.
  - Usa `ON CONFLICT DO NOTHING` con guarda `to_regclass`, como `AddTenantProviders.cs:81-94`.
  - No hace falta una constante de «fecha de lanzamiento».
- **`welcome.pending`** es `true` cuando:
  - la membresía tiene `origin = 'invitation'` (en minúscula, `Membership.cs:21`);
  - `AcceptedAt` no es nulo (no `CreatedAt`: re-invitar lo reinicia, `Membership.cs:365-369`);
  - y no hay acuse.
- **`actionable`** se calcula por paso con los permisos efectivos del miembro. Así un rol
  personalizado con `tenancy.settings.update` pero sin `integrations.connection.manage` ve el paso
  sin botón.

### 5.1 Contrato

```
GET  /api/v1/tenants/{tenantId}/onboarding                       .RequireAuthorization()
200 {
  "checklist": null | {
    "dismissed": false,
    "audience": "owner" | "invited",
    "primaryGoal": "first-quote",
    "goals": [{ "key": "first-quote", "optional": false, "stepKeys": ["product","customer","first-quote-sent"] }],
    "steps": [{ "key": "product", "status": "Done" | "Pending" | "Skipped",
                "optional": false, "actionable": true, "blockedBy": [] }]
  },
  "welcome": { "pending": false }
}

PUT  /api/v1/tenants/{tenantId}/onboarding/checklist                { "dismissed": bool }  tenancy.settings.update · 204
PUT  /api/v1/tenants/{tenantId}/onboarding/checklist/steps/{stepKey} { "skipped": bool }   tenancy.settings.update · 204
POST /api/v1/tenants/{tenantId}/onboarding/welcome/acknowledge                           el propio miembro · 204, idempotente
```

- **`checklist: null`** sólo cuando el miembro no tiene `tenancy.settings.update` o el tenant viene
  del backfill. Una guía cerrada sigue viniendo, con `dismissed: true`, para poder reabrirla.
- **`audience`** sale de `Membership.Origin`: `registration` → `owner`, cualquier otro → `invited`.
  Elige la cabecera de `/setup` (§4.1); el frontend no conoce el origen de la membresía.
- **Estados:** usan el nombre del enum (`CLAUDE.md:143`) y las claves van en kebab-case.
- **Toast de envío:** al enviar la primera cotización salen dos toasts seguidos, el de la feature
  y el de la meta. Es aceptable.
- **Invalidación global:** hoy `lib/query-client.ts` sólo tiene `MutationCache.onError`. El
  `onSuccess` global que invalida la query del onboarding se agrega ahí.
- **Errores:**
  - 422 `onboarding.step.unknown`
  - 422 `onboarding.step.not_skippable`
  - 403 `authorization.denied` para un tenant ajeno, nunca 404
- **Sin `If-Match`:** fijar un estado es idempotente.

## 6. Decisiones del owner (con propuesta por defecto)

- **D-1. Sembrar el IVA al registrar.** Sembrar IVA 19 %, 5 % y Exento si el país es CO.
  *Propuesta:* sí. Con eso `tax-rates` desaparece de la meta principal y el producto deja de
  guardarse sin IVA. Sin la siembra, el paso queda.
- **D-2. Landing.** El landing va a `/setup` mientras la meta principal esté incompleta.
  *Propuesta:* sí.
- **D-3. Primera cotización a uno mismo.** Sugerirlo en el copy. *Propuesta:* sí, sólo como texto,
  sin mecánica nueva.
- **D-4. Número de WhatsApp de soporte.** *Propuesta:* configurable en build como la marca
  (`VITE_SUPPORT_WHATSAPP`). Sin número no se muestra.
- **D-5. Lugar en la cola.** El orden acordado es variantes → productos desde Shopify → campanita →
  alertas. *Propuesta:* el backend en paralelo, porque no toca variantes. El frontend sólo se puede
  probar de punta a punta con las variantes del frontend (`estado.md`, «Bloqueante»).
- **D-6. Empresa por defecto. Decidido (owner, 2026-10-21):** al registrar un tenant se crea una
  empresa que factura, para que quien no necesita facturar no tenga ese paso de fricción.
  - **Datos de arranque:** la empresa nace con el nombre del tenant (`displayName`). NIT, ciudad y
    cuenta bancaria quedan vacíos hasta que el tenant los complete. El paso opcional
    `company-details` invita a completarlos.
  - **Qué cambia en el dominio** (propuesta; hoy `Company.cs` exige NIT, ciudad y ≥1 cuenta:
    `tax_id_required`, `city_required`, `bank_accounts_required`):
    - la empresa gana el estado «datos incompletos», y esas tres reglas aplican sólo al
      completarlos;
    - el formulario de una empresa nueva creada a mano las sigue exigiendo.
  - **Qué cambia donde se usa la empresa** (propuesta):
    - **Cotización:** se envía sin cuenta. Con la empresa por defecto, el editor la elige sola
      (§4.3) y el aviso «Faltan datos» deja de pedir la cuenta.
    - **PDF:** sin NIT ni cuenta, se omiten esas líneas y el bloque de datos de pago. No se imprime
      un campo vacío.
    - **Pedido:** hoy convertir una cotización exige cuenta (`Quotation.cs:866`). Pasa a ser
      opcional, y sin cuenta el pedido queda con «Pago: por definir».
    - **Shopify:** el paso 1 de §9.4 queda hecho solo. La cuenta de cobro es opcional, y sin cuenta
      no se aplica `currency_mismatch`. Si el tenant ya tiene cuentas, la regla de moneda sigue.
    - **POS:** abre caja con la empresa por defecto (`pos.session.company_required` se cumple).
  - **Cómo se crea:** como `PipelineProvisioner`, sin que Tenancy llame a Companies. O en el registro
    por evento, o perezosa en la primera lectura que la necesite. Decide quien construye.
  - **Tenants existentes:** no cambian. Un tenant sin empresas recibe la por defecto en la primera
    lectura.
- **Decisiones del onboarding de Shopify:** están en §9.8. S-1 a S-7 ya están decididas; S-8 sigue
  pendiente.

## 7. Lo que queda a criterio de quien construye

- **Íconos:** los de lucide.
- **Visual del skeleton:** a criterio.
- **Artículos de ayuda:** sin artículo, no se muestra «¿Cómo se hace?».
- **Textos de «Lo que sigue»:** se escriben en el plan.

## 8. Verificación

- **Frontend:** `bun run lint` y `bun run build`. Pruebas con `render-route` para:
  - registro → `/setup`;
  - meta principal completa → landing normal;
  - un cajero invitado → bienvenida sin ítem en el menú;
  - un 404 → nada;
  - cerrar la guía y reabrirla desde el menú de usuario y desde `/settings`;
  - bloqueado y sin permiso;
  - toast de meta completada.
- **Backend:** pruebas del módulo, la migración con backfill y `ArchitectureTests`.
- **Manual:**
  - registrar una empresa y recorrer la meta hasta enviarte una cotización a tu propio WhatsApp;
  - riel colapsado y pantalla ≤700px.

## 9. Onboarding de la tienda de Shopify

Agregado el 2026-10-21, a pedido del usuario. El alcance es lo justo para que la integración con
Shopify funcione cuando el comercio instala la app desde su tienda.

### 9.1 Qué exige Shopify y qué recomienda

Shopify **no** exige un asistente de onboarding. Exige que, al aceptar los permisos, el comercio
llegue a una pantalla que funcione y que pueda dejar la conexión configurada.

**Obligatorio** (riesgo de rechazo; [App Store requirements](https://shopify.dev/docs/apps/launch/shopify-app-store/app-store-requirements)):

| Regla | Qué pide | Estado hoy |
|---|---|---|
| 2.3.2 y 2.3.4 | OAuth antes de cualquier pantalla, también al reinstalar | Cumple (`/shopify/install`) |
| 2.3.3 | Mandar al comercio a la interfaz de la app al aceptar los permisos | Cumple, pero sin cuenta cae en `/login` (§9.2) |
| 2.3.1 | No pedir el dominio `*.myshopify.com` | El camino B no lo pide. El A (`/integrations/shopify/new`) sí, pero arranca desde QEP, como HubSpot o Klaviyo. Riesgo bajo |
| [Integrating with Shopify](https://shopify.dev/docs/apps/build/integrating-with-shopify) | Si la app conecta una cuenta externa, el primer paso es enlazar la tienda con esa cuenta, y la configuración de la conexión tiene que existir | El enlace existe; la configuración guiada no |
| 2.1.3 y 2.1.4 | La app funciona desde su interfaz y los datos que sincroniza llegan bien | Una empresa nueva queda con todos sus pedidos en espera (§9.2) |
| [Compliance webhooks](https://shopify.dev/docs/apps/build/compliance/privacy-law-compliance) | `customers/data_request`, `customers/redact` y `shop/redact` responden 2xx, o 401 con una firma inválida; se revisa de forma automática | Los dos primeros sólo se auditan; hay que confirmar la respuesta |
| 4.5.3 a 4.5.5 | Un screencast del onboarding y credenciales de prueba con acceso completo | Falta. El login es sólo con Google, así que hace falta una cuenta de Google de prueba para el revisor |
| 1.2 y 1.2.1 | Todo cobro de la app pasa por Shopify. Desde el 2026-04-02 no hay excepciones para apps no listadas (respuesta del staff en el foro, no política publicada) | Decidido (S-1): la app es gratis en Shopify. Falta confirmarlo con Shopify Support |

**Recomendado** (Built for Shopify y [guía de onboarding](https://shopify.dev/docs/apps/design/user-experience/onboarding)):
- Cinco pasos o menos, que se marquen solos cuando se hace la acción.
- Una pantalla que diga con claridad si la app está configurada y funcionando.
- Un estado de conexión con el nombre de la tienda y las acciones de conectar y desconectar.

**App no embebida.** La app sigue sin embeberse. Sigue permitido según el staff de Shopify en el foro
(2026-08-24), pero así no se puede obtener el sello Built for Shopify (S-8).

### 9.2 Dónde se traba hoy quien instala desde Shopify

1. **Sin sesión.** `callback.tsx:95-100` lo manda a `/login`, que dice «Acceso reservado a personas
   invitadas por tu organización» (`login-page.tsx:142`). El registro funciona, pero el texto empuja
   a irse.
2. **Al enlazar.** Cae en `/integrations/shopify/{id}` (`link.$installId.tsx:102-107`): siete campos
   sin orden.
   - Pide «Empresa de cobro» y «Cuenta de cobro», y una empresa recién creada no tiene ninguna. Es
     un callejón sin salida.
3. **Atributo del documento.** Es texto libre y el comercio no sabe qué escribir.
   - Sin documento ni clasificación por defecto, el comprador entra como cliente incompleto
     (`ShopifyCustomerResolver.cs:157-175`).
   - El selector con opciones reales sigue pendiente (`estado.md`, «Shopify: fuente del documento
     del comprador»).
4. **Productos.** Cada SKU tiene que coincidir con el `Code` de una variante activa; si no, el
   pedido queda con `product_unmatched`. Una empresa nueva no tiene productos, así que **todos** sus
   pedidos quedan en espera.
5. **IVA del envío.** Exige una tasa de impuesto (`freight_tax_rate_missing`), y una empresa nueva
   no tiene ninguna. La D-1 lo resuelve.
6. **Historial.** No se trae nada anterior al enlace (D-S4), y «Sincronizar ahora» devuelve 0 sin
   explicar por qué. La spec de días de historial está aprobada pero sin construir.

### 9.3 Flujo propuesto

```
Shopify: Instalar → OAuth (sin cambios)
  └─ /shopify/callback
       ├─ sin sesión → /shopify/link/{installId} en estado «Conecta {tienda} con Lulo CRM»
       │     ├─ «Ya tengo cuenta»  → Google → vuelve aquí
       │     └─ «Soy nuevo»        → /register?returnTo=… (nombre sugerido: el de la tienda)
       └─ con sesión → elegir empresa (lo que ya existe) → Enlazar tienda
             └─ /integrations/shopify/{connectionId}/setup   ← configuración guiada (§9.4)
                   └─ al terminar: «Conectada y funcionando» (estado de la conexión, §9.4)
```

- **Llegada sin sesión.** `SessionGate` hoy redirige a `/login`. En la página de enlace, en cambio,
  la falta de sesión se vuelve un estado propio de esa página: «Conecta {tienda} con Lulo CRM»,
  con dos botones (el patrón de Gorgias).
  - Al registrarse, el nombre de la empresa viene sugerido con el de la tienda.
  - El registro respeta `returnTo`, como ya define la §4.1.
- **Después de enlazar** se navega a `…/setup`, no a la página de la tienda.
- **Retomar.** Si el comercio sale y vuelve, la página de la tienda muestra un aviso «Termina de
  configurar tu tienda» con el paso donde quedó, mientras `shopify-setup` siga incompleto.

### 9.4 Configuración guiada: cinco pasos

**Pantalla:**
- Pasos lineales con un indicador (hecho, actual, pendiente), un solo paso abierto a la vez y
  «Atrás» y «Continuar».
- Cada paso guarda al continuar con el `PUT` de la tienda que ya existe (`If-Match`). No hay un
  guardado al final que se pueda perder.

| # | Paso | Qué pide | Hecho cuando | Si falta |
|---|---|---|---|---|
| 1 | **Tu empresa y tu cuenta de cobro** | La empresa que factura ya existe (D-6) y queda elegida. La cuenta de cobro es **opcional**: con una cuenta, su moneda debe ser la de la tienda | la tienda tiene empresa; sale hecho solo con la empresa por defecto | `currency_mismatch` sólo si hay una cuenta |
| 2 | **Quién atiende los pedidos** | El asesor, por defecto quien está configurando | la tienda tiene asesor activo | `store_not_configured`, `advisor_inactive` |
| 3 | **Cómo reconocer a tus compradores** | De dónde sale el documento (selector con las fuentes reales de la tienda y «Otro…»), el atributo del tipo, el tipo por defecto y la **clasificación por defecto**, obligatoria aquí. El texto dice: «Sin esto, tus compradores entran como clientes incompletos» | la tienda tiene clasificación por defecto y el comercio confirmó la fuente del documento | `customer_incomplete` |
| 4 | **Tus productos** | Dos caminos (abajo) | importación terminada, todos los SKU con su par en QEP, o el comercio decidió seguir con los que faltan | `product_unmatched` |
| 5 | **Trae tus pedidos recientes** | De 1 a 30 días (7 por defecto), según la spec de días de historial. Sincroniza y muestra el resultado | se corrió una sincronización desde aquí | — |

**Paso 4, «Tus productos».** La pantalla muestra cuántos productos y variantes activas tiene la
tienda y cuántos SKU ya tienen su par en QEP. Hay dos caminos:

- **Traer mis productos de Shopify.** Viene recomendado cuando el catálogo de QEP está vacío.
  - **Qué se trae:**
    - cada producto activo de Shopify crea un producto en QEP;
    - sus opciones (hasta 3) entran a la biblioteca de opciones, y si ya existen se activan;
    - cada variante crea una variante de QEP, con el SKU como `Code`, el código de barras, el precio
      en la moneda de la tienda y la imagen.
  - **Encaje con variantes.** El modelo de variantes se hizo pensando en esto: hasta 3 opciones y
    hasta 2.048 variantes, como Shopify (spec de variantes §3.2 y §3.3).
  - **Progreso.** Es un trabajo asíncrono que muestra «Importando 340 de 1.200». Se puede salir y
    volver.
  - **Paso 5.** Queda bloqueado mientras la importación corre; si no, los pedidos entrarían a
    esperar productos.
  - **Variantes sin SKU.** Se importan igual y se emparejan por el id de la variante de Shopify
    (S-3). Se listan aparte con «Agrégales un SKU en Shopify si también las vendes fuera de
    Shopify».
  - **Coincidencias.** Una variante cuyo SKU ya existe en QEP no se duplica: se empareja y no se
    pisa (S-2).
  - **Permiso nuevo.** Hace falta el scope `read_products`, porque hoy sólo hay `read_orders` y
    `read_customers` (`ShopifyAppOptions.cs`). Una tienda instalada antes de este cambio lo aprueba en
    Shopify, con el aviso «Shopify te va a pedir un permiso más» (S-5).
- **Ya los tengo en Lulo CRM.** Se comprueba la cobertura: «1.180 de 1.200 SKU coinciden». Debajo
  van los que faltan, con dos acciones:
  - **«Traer sólo los que faltan»**, que es la importación anterior limitada a esos SKU;
  - **«Seguir así»**, con el aviso de que los pedidos con esos productos van a quedar en espera.

**Paso 5, resultado.** Muestra «Entraron 12 pedidos. 3 esperan:» y un renglón por motivo, cada uno
con su arreglo:
- `product_unmatched` → paso 4, con los SKU que faltan;
- `customer_incomplete` → paso 3;
- `currency_mismatch` → paso 1;
- `freight_tax_rate_missing` → Impuestos.

Con 0 pedidos dice «No hubo pedidos en esos días. Los nuevos entran solos cada 10 minutos».

**Al terminar: «Conectada y funcionando»** (el patrón *Account connection* de Shopify). Pasa a ser la
cabecera de la página de la tienda y muestra:
- el estado de la conexión, el nombre de la tienda y la última sincronización;
- los pedidos importados en los últimos 7 días y los que están en espera, con enlace a la bandeja;
- «Desconectar», con confirmación.

Un estado `NeedsAttention` (por ejemplo `app_uninstalled` o `webhooks_not_registered`) se pinta ahí
en rojo, con su acción.

### 9.5 Encaje con «Primeros pasos»

- **Pasos de la meta `shopify`:** `shopify` → `shopify-setup` → `shopify-first-order`.
  - El CTA de `shopify-setup` lleva a `…/setup`.
  - `shopify-first-order` es el momento de valor: «Tu primer pedido de Shopify ya está en Lulo CRM».
- **Empresa que llegó desde Shopify.** Para ella, `shopify` es la **meta principal** y no es
  opcional.
  - Al enlazar desde el camino B (`onboarding:shopify_oauth:B`), `LinkShopifyStore` publica un
    evento.
  - Onboarding guarda `tenant_checklists.primary_goal='shopify'` si la guía del tenant todavía no
    tiene ninguna meta cumplida.
  - Así no se escribe en el GET (§3), y una empresa que ya cotizaba no cambia de meta.
- **Importar productos también marca hecho el paso `product`** de `first-quote`.

### 9.6 Contrato (BFF)

```
GET  /api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/setup        integrations.connection.manage
200 {
  "steps": [{ "key": "billing"|"advisor"|"buyers"|"products"|"history", "status": "Done"|"Pending", "blockedBy": [] }],
  "store":    { "name", "shopDomain", "currency", "version" },
  "billing":  { "companies": [{ "id", "name", "accounts": [{ "id", "label", "currency" }] }], "selectedAccountId" },
  "advisor":  { "options": [{ "memberId", "name", "isMe" }], "selectedMemberId" },
  "buyers":   { "documentSources": [{ "key", "label", "seenInOrders" }], "documentKey", "documentTypeKey",
                "defaultDocumentType", "classifications": [{ "id", "name" }], "defaultClassificationId" },
  "products": { "scopeGranted": bool, "reauthorizeUrl": string|null, "shopifyVariants": int,
                "matched": int, "missingSample": [{ "sku", "title" }], "catalogEmpty": bool,
                "import": null | { "status": "Running"|"Done"|"Failed", "done": int, "total": int, "withoutSku": int } },
  "history":  { "lastRun": null | { "days", "fetched", "imported", "waiting": [{ "reason", "count" }] } }
}
POST …/stores/{connectionId}/products/import   { "mode": "All" | "Missing" }   202 · 409 shopify.products.import_running
POST …/stores/{connectionId}/products/coverage/accept                          204 (seguir con los que faltan)
POST …/stores/{connectionId}/sync              { "historyDays": 1..30 }         (spec de días de historial)
PUT  …/stores/{connectionId}                   (sin cambios, If-Match)
```

- **Errores:**
  - 422 `shopify.products.scope_missing` mientras falte `read_products`. La respuesta trae
    `reauthorizeUrl`.
  - 409 `shopify.products.import_running` si ya hay una importación en curso.
- **`documentSources`** reemplaza el endpoint de opciones pendiente en `estado.md`: es el mismo
  dato, servido por el BFF de esta pantalla.
- **Webhooks de productos.** `products/create`, `products/update` y `products/delete` mantienen
  sincronizados los productos importados, según la regla de S-2. Requieren el mismo scope.

### 9.7 Material para la revisión de Shopify

- **Screencast.** El flujo §9.3 completo con una tienda de desarrollo: instalar sin cuenta, crear
  cuenta, enlazar, los cinco pasos, el primer pedido y desinstalar y reinstalar.
- **Credenciales de prueba.** Una cuenta de Google de prueba con acceso completo, más instrucciones
  para el revisor.
- **Comprobaciones:**
  - las respuestas de los tres webhooks de privacidad (2xx, o 401 con una firma mala);
  - justificar `read_products` en la solicitud de scopes (3.2);
  - `read_all_orders` no hace falta mientras el historial no pase de 60 días.

### 9.8 Decisiones del owner para Shopify

S-1 a S-7 las decidió el owner el 2026-10-21. S-8 sigue pendiente, con su propuesta por defecto.

- **S-1. Cobro de la app. Decidido (owner, 2026-10-21):** la app es gratis en Shopify y QEP cobra su
  suscripción como hoy.
  - Sigue en pie confirmarlo con Shopify Support antes de enviar a revisión. Si el acceso por
    Shopify costara, ese cobro tendría que pasar por Shopify Billing (1.2.1, sin excepciones para
    apps no listadas desde 2026-04).
- **S-2. Quién manda después de importar. Decidido (owner, 2026-10-21): Shopify.**
  - En los productos importados, nombre, variantes, SKU y precio se actualizan por webhook. En QEP
    se ven como «Sincronizado desde Shopify», con esos campos de sólo lectura.
  - El IVA, las escalas, los empaques y la descripción interna los maneja QEP.
  - A los productos que ya existían y se emparejaron por SKU no se les pisa nada.
- **S-3. Emparejar por id de variante. Decidido (owner, 2026-10-21): sí.** Los pedidos se emparejan
  primero por el id de la variante de Shopify, guardado al importar, y después por SKU. Así una
  variante sin SKU también entra.
- **S-4. Tiendas con precios sin impuestos** (`taxesIncluded=false`). **Decidido (owner,
  2026-10-21): sí.** Al importar se suma el IVA de la tasa del producto, para que el precio quede
  «con IVA incluido» como en el resto de QEP. Un producto que en Shopify no lleva impuesto entra
  como Exento.
- **S-5. Permiso `read_products` en tiendas ya instaladas. Decidido (owner, 2026-10-21): sí.** Se
  pide sólo cuando el comercio elige «Traer mis productos». A las instalaciones nuevas se les pide
  desde el inicio.
- **S-6. Borradores y archivados. Decidido (owner, 2026-10-21):** activos → activos, borrador →
  inactivos, y los archivados no se traen.
- **S-7. Cola. Decidido (owner, 2026-10-21):** «Productos desde Shopify», que ya era lo siguiente
  después de variantes, se construye como parte de este onboarding. Va con esta spec y no con una
  aparte, y depende del frontend de variantes.
- **S-8. App embebida.** *Propuesta:* sigue sin embeberse, que está permitido. Embeber (App Bridge,
  session tokens, la configuración dentro del admin de Shopify) se evalúa sólo si se busca Built for
  Shopify.

### 9.9 Verificación

- **Con una tienda de desarrollo de Shopify:**
  - instalar sin cuenta → «Soy nuevo» → registro → enlazar → los cinco pasos;
  - importar productos, incluidas variantes sin SKU y una tienda con `taxesIncluded=false`;
  - traer 7 días → que entre un pedido de punta a punta;
  - desinstalar y reinstalar: OAuth otra vez y la conexión reactivada;
  - aprobar el scope nuevo en una tienda ya instalada.
- **Backend:**
  - importador (mapeo de opciones, variantes, precios e IVA);
  - emparejamiento por id de variante y por SKU;
  - webhooks de productos;
  - respuestas de los webhooks de privacidad;
  - `ArchitectureTests`.
- **Frontend:**
  - los estados del asistente: carga, error, importación en curso, importación fallida y
    sincronización con pedidos en espera;
  - render-route de callback sin sesión → estado «Conecta {tienda}» (no `/login`).

### 9.10 QEP reemplaza la app «lulo crm» que ya existe

**Intención del owner (2026-10-21).** QEP pasa a ser el backend de la app de Shopify «lulo crm» que ya
está publicada, y conserva el nombre. No se crea una app nueva.
- **Cuándo:** el cambio se hace **cuando QEP tenga todo lo que hoy hace Lulo CRM**. La generación de
  guías no está implementada en QEP, pero está en el roadmap.
- **Dominio:** QEP pasa a servirse en el mismo dominio de hoy, `app.lulocrm.com` y `api.lulocrm.com`.
- **Ficha:** la categoría (Envíos) no cambia.

**La app de hoy** ([ficha](https://apps.shopify.com/lulo-crm); código en `lulo-crm/lulo-crm-backend` y
`lulo-crm-frontend`, la v1, que es la que está en producción; `lulo-crm-v2` es trabajo sin terminar
contra una app de prueba aparte):
- La publica Quality Code Software House SAS, desde el 2025-02-27. Es gratis, está en la categoría
  Envíos y no tiene reseñas.
- **Stack:** .NET con MongoDB. La API está en `api.lulocrm.com` y el SPA en `app.lulocrm.com`, en k8s.
- **No es embebida** (no usa App Bridge). La configura el Partner Dashboard, sin `shopify.app.toml`.
- **OAuth:** a mano y con el flujo de instalación viejo (legacy). El `state` no se verifica, el HMAC
  del callback tampoco, y el `ApiVersion` es `2025-01`.
- **Token:** offline y **sin expiración**, guardado **en texto plano** en Mongo
  (`tenantIntegrations.Metadata.AccessToken`).
- **Scopes:** fulfillments y fulfillment orders (lectura y escritura), `read_orders` y
  `write_orders`, `read_products` y `write_products`, `read_customers`, `read_shipping`,
  inventario (lectura y escritura) y `read_locations`. QEP necesita un subconjunto de esos, así que
  reusar la app no pide scopes nuevos.
- **Funciones que hay que igualar:**
  - Coordinadora: guías por SOAP (AGS, AGW, NYS), novedades y el push de tracking.
  - Inter Rapidísimo: cotizar, preenvío, guía, rótulo, recogida, sedes y ciudades.
  - Envíos, rótulos, seguimiento y cotizadores.
  - Devolver el fulfillment a Shopify con el tracking (`fulfillmentCreateV2`) y `orderMarkAsPaid`.
  - Orígenes y bodegas, billetera y saldo, API keys del tenant.
  - Confirmación de pedido por WhatsApp (cron cada 5 min), notificaciones y plantillas.
  - Exportes a Excel.

QEP no genera guías por API: exporta los pedidos en Excel con columnas por transportadora
(`OrdersExportLayout`). La lista de arriba es el piso de paridad para hacer el cambio.

**Por qué reusar la misma app y no crear otra:**
- Se conservan el nombre, la ficha y las tiendas que ya la tienen instalada.
- La distribución sigue siendo pública, y la app es gratis, así que S-1 se cumple sin cambios.
- Una app creada desde el 2026-04-01 ya nace obligada a usar tokens que expiran.

**Qué cambia en la configuración de la app el día del cambio** (Partner Dashboard; conviene pasarla a
`shopify.app.toml` para que quede en el repo):
- **App URL:** de `https://api.lulocrm.com/shopify` (backend) a `https://app.lulocrm.com/shopify/install`
  (SPA de QEP).
- **Redirect URLs:** de `https://app.lulocrm.com/integrations/shopify/install` a
  `https://app.lulocrm.com/shopify/callback`, más `http://localhost:3002/shopify/callback` para local.
  Se pueden dejar las dos durante la transición.
- **Webhooks de cumplimiento:** ver §9.11.
- **Scopes:** sin cambios. Los de hoy ya incluyen `read_products`, así que S-5 no aplica a las
  tiendas de la app de hoy. Hay que pedir el acceso a datos protegidos (handoff §2) si la app no lo
  tiene.
- **Embebida:** no (S-8), igual que hoy.
- **Versión de webhooks de la app:** la más reciente que QEP soporta (D-S29, `2026-10`). Hoy es
  `2025-01`, que ya salió de soporte.

**Dominio: lo que hay que mover en QEP.** Hoy QEP vive en `qep.qcode.co` y `qep-api.qcode.co`. La
cookie de sesión es `SameSite=Lax`, así que el SPA y la API tienen que quedar en el mismo sitio
(`lulocrm.com` los dos). Hay que mover:
- `VITE_API_BASE_URL` en el entorno Production de Vercel;
- el CORS de la API (un solo origen: `https://app.lulocrm.com`);
- los orígenes autorizados de Google Identity;
- el `redirect_uri` de Instagram en Meta y los dominios de la app de Meta;
- `Shopify:App:AllowedRedirectOrigins` y `Shopify:App:WebhookCallbackUrl`;
- `Notifications:LoginUrl`;
- los dominios del proyecto en Vercel y el ingress del backend.

El túnel local del owner (`qep-api.lulocrm.com`) es otro subdominio y no choca.

**Ficha.** La categoría no cambia. Hay que actualizar la descripción con lo que suma QEP (pedidos,
cotizaciones, mensajería) cuando se haga el cambio. Un cambio de ficha pasa por la revisión de
Shopify, y el screencast de §9.7 sirve para eso.

**Tiendas que ya la tienen instalada.** Sus tokens y sus datos viven en el backend de hoy, no en QEP.
Hay dos caminos:

- **A. Volver a abrir la app.**
  - Al cambiar el App URL, la próxima vez que el comercio abre «lulo crm» desde su admin cae en
    `/shopify/install`. Hace OAuth otra vez (los scopes nuevos se aprueban ahí) y entra al onboarding
    de §9.3.
  - Sin migrar tokens ni datos.
  - Hay que avisarles antes por correo o en la app vieja.
- **B. Migrar los tokens y la relación tienda → empresa** desde la base de hoy.
  - Como es la misma app, los tokens siguen valiendo, y se migran de inmediato a tokens que expiran
    (abajo).
  - Evita el segundo OAuth, pero exige crear las empresas en QEP.
  - Los scopes de la v1 ya cubren lo que QEP pide, así que no hay que aprobar nada nuevo.
  - Si el reemplazo incluye migrar las cuentas de Lulo CRM v1 a QEP (empresas, usuarios, pedidos), B
    sale casi gratis dentro de esa migración y es mejor que A: el comercio no nota el cambio.
  - Decidirlo junto con el plan de migración de datos de la v1, que esta spec no cubre.

**Bloqueante, con o sin reemplazo: tokens que expiran.** Shopify exige tokens offline que expiran a
todas las apps públicas desde el **2027-01-01**
([changelog](https://shopify.dev/changelog/expiring-offline-access-tokens-required-for-all-public-apps-as-of-january-1-2027)).
A las apps nuevas ya se los exige desde el 2026-04-01. Después de esa fecha, un token que no expira
recibe errores de autenticación.

QEP hoy pide el token sin `expiring=1` y no lo refresca (`ShopifyAdminGateway.cs:58`, spec Shopify
§4.3). Hace falta:
- pedir `expiring=1` en el canje del code;
- guardar `refresh_token` con su vencimiento, cifrado como el token;
- refrescar antes de `expires_in`, en los workers y en los webhooks;
- con el camino B, migrar los tokens que no expiran por token exchange
  ([guía](https://shopify.dev/docs/apps/build/authentication-authorization/migrate-to-expiring-offline-access-tokens));
- si el refresh falla, pasar la conexión a `NeedsAttention` con «Vuelve a abrir Lulo CRM desde tu
  Shopify».

**El mismo plazo alcanza a la app de hoy.** La v1 usa tokens que no expiran. Si el cambio a QEP no
está hecho antes del **2027-01-01**, la integración con Shopify de Lulo CRM v1 deja de funcionar ese
día en todas las tiendas: no puede leer pedidos ni crear fulfillments.

Como la paridad (con las guías) no va a estar en 10 semanas, **la v1 necesita un parche propio antes
de esa fecha**:
- migrar sus tokens por token exchange con `expiring=1`;
- guardar el `refresh_token`;
- refrescar antes de cada llamada.

Es una urgencia de la v1, aparte de QEP.

**Pendiente de confirmar con el owner:** cuántas tiendas tienen hoy la app instalada y la usan.

### 9.11 Webhooks en el cambio de backend

**Qué tiene registrado hoy cada tienda** (v1, `ShopifyService.cs:55-71`; REST, JSON, versión de la app
`2025-01`). Sólo dos suscripciones por tienda:
- `orders/create` → `https://api.lulocrm.com/v1/shopify/webhooks/Orders/create/{tenantId}`
- `orders/updated` → `https://api.lulocrm.com/v1/shopify/webhooks/Orders/updated/{tenantId}`

`{tenantId}` es el id de Mongo del tenant de la v1. No hay `app/uninstalled`.

**Webhooks de cumplimiento** (configurados en la app):
`https://api.lulocrm.com/webhooks/shopify/{customers/redact|customers/dataRequest|shop/redact}`.

**Problemas de la v1 que hay que saber:**
- **Los de cumplimiento siempre responden 201.** La validación de HMAC llama con los argumentos en
  otro orden, ignora el resultado y compara hex contra la firma, que viene en base64
  (`ShopifyApi.cs:40-54`, `ValidateHmacWebhooks.cs:12`). Shopify exige 401 con una firma mala.
  Cualquier envío de la app a revisión, incluido el cambio de ficha, falla esa comprobación
  automática mientras la v1 los atienda.
- **Los de pedidos no validan HMAC.** Cualquiera que conozca la URL puede inyectar un pedido.
  Mitigación parcial: se compara el dominio de la tienda con el del tenant.

**Propuesta para el cambio:**

1. **QEP atiende las rutas viejas en `api.lulocrm.com` como alias** del receptor que ya tiene
   (`ShopifyWebhookEndpoints.cs:26`):
   - `POST /v1/shopify/webhooks/Orders/{create|updated}/{legacyTenantId}`, sin distinguir
     mayúsculas.
   - `POST /webhooks/shopify/{customers/redact|customers/dataRequest|shop/redact}`.

   Cómo se comporta el alias:
   - Valida el HMAC con el secreto de la app, que es el mismo, e ignora `{legacyTenantId}`.
   - Identifica la tienda por `X-Shopify-Shop-Domain`, como hace QEP.
   - Si la tienda no está enlazada en QEP (todavía no volvió a abrir la app), responde **200 y
     descarta**. Así Shopify no reintenta ni borra la suscripción, y cuando la tienda se enlace los
     pedidos de esos días entran con «Trae tus pedidos recientes» (§9.4, paso 5).
   - Con una firma mala responde **401**.

   El DNS de `api.lulocrm.com` se cambia una sola vez y no hay ventana en que los webhooks peguen
   contra nada.
2. **Al enlazar una tienda, QEP limpia lo viejo.** Lista las suscripciones de la app en esa tienda
   (`webhookSubscriptions`, con su token, porque es la misma app), borra las que no apuntan a su
   receptor y registra las suyas: `orders/create`, `updated`, `paid`, `cancelled`,
   `app/uninstalled` y, con S-2, `products/*`.
   - Sin esa limpieza, cada pedido llegaría dos veces: por el alias y por la ruta nueva. La
     idempotencia por `ExternalKey` lo absorbería, pero duplica carga y ruido.
3. **Webhooks de cumplimiento.** El día del cambio se apuntan en la configuración de la app a la ruta
   de QEP (`https://api.lulocrm.com/api/webhooks/shopify`). Los alias del punto 1 cubren cualquier
   entrega que llegue a la ruta vieja.
4. **Retiro de los alias.** Cuando todas las tiendas activas estén enlazadas en QEP, o a los 90 días
   del cambio, se retiran los alias de pedidos. Los de cumplimiento se quedan hasta confirmar que
   Shopify ya no los usa: llegan hasta 48 h después de una desinstalación.

**Pruebas:** con una tienda de desarrollo instalada en la app de hoy (no en la de prueba de la v2):
- un pedido antes de enlazar entra por el alias como 200 descartado;
- enlazar borra las suscripciones viejas;
- un pedido después de enlazar entra una sola vez;
- una firma mala recibe 401 en las tres rutas de cumplimiento;
- `shop/redact` a la ruta vieja funciona.

## Para retomar

La spec quedó en pausa el 2026-10-21. Está escrita, revisada en tres rondas hasta §9.9 y tiene
mockup. No tiene plan de implementación ni código en ningún repo.

**Urgente, aunque esta spec siga en pausa** (no esperan a retomarla):
1. **Lulo CRM v1 y los tokens que expiran.** La app de Shopify de hoy (`lulo-crm/lulo-crm-backend`)
   usa tokens que no expiran, guardados en texto plano. Si no se migra a tokens que expiran antes del
   **2027-01-01**, su integración con Shopify se cae ese día (§9.10).
2. **Webhooks de cumplimiento de la v1.** Responden 201 con cualquier firma. Cualquier envío de la app
   a revisión falla mientras sigan así (§9.11).
3. **QEP y los tokens que expiran.** Mismo plazo. Bloquea el cambio y cualquier tienda nueva (§9.10).

**Decisiones del owner que siguen abiertas:**
- **D-1:** sembrar IVA 19 %, 5 % y Exento al registrar.
- **D-2:** landing en `/setup` mientras falte la meta principal.
- **D-4:** número de WhatsApp de soporte.
- **D-5:** lugar en la cola.
- **D-6, punto abierto:** que un pedido sin cuenta de cobro quede con «Pago: por definir». Hoy
  convertir exige cuenta (`Quotation.cs:866`). Es el efecto de la empresa por defecto que más toca el
  dominio.
- **S-8:** app embebida o no. La recomendación es no, con una página embebida mínima como plan B si
  la revisión la exige.

**Datos que faltan:** cuántas tiendas tienen hoy la app «lulo crm» instalada y la usan.

**Fuera de esta spec, pero decide partes de ella:**
- **El plan de migración de cuentas de Lulo CRM v1 a QEP.** Decide entre el camino A (volver a abrir
  la app) y el B (migrar tokens) de §9.10.
- **La generación de guías en QEP.** Es la condición para hacer el cambio de app y de dominio.

**Pasos al retomar:**
1. Responder lo abierto de arriba.
2. Pasar §9.10, §9.11 y D-6 por la misma revisión que el resto: alguien nuevo en la app, diseño y
   backend.
3. Que el owner apruebe la spec y su línea `Estado:` pase a «aprobado».
4. Escribir el plan de implementación de los dos repos (`superpowers:writing-plans`). Tiene que
   respetar el orden de la cola, porque S-7 deja «Productos desde Shopify» dentro de este trabajo y
   eso depende del frontend de variantes.
