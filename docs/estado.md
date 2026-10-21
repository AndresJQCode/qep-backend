# Estado del proyecto `qep-backend`

Archivo vivo: se actualiza al cerrar un trabajo, tomar una decisión o desplegar. **Verificado contra el código el
2026-10-21** (`develop` en `49c42709`, igual a `origin/develop`; `qep-frontend` en `develop`, `5e7dd261`). Ante
cualquier duda gana el código ([CLAUDE.md](../CLAUDE.md)). La línea `Estado:` de cada spec dice si el diseño se aprobó,
no si se construyó: varios siguen diciendo «borrador» y están construidos.

## Cómo se lee

Estados: **Construido** (mergeado a `develop`) · **En curso** · **Aprobado, sin empezar** · **Por verificar**.
«Push» dice si el trabajo ya llegó a `origin`. Para comprobarlo: `git log --oneline origin/develop..develop` en cada repo.

**Push al 2026-10-21.** Backend: nada pendiente. Frontend: 5 commits sin push (Zenvia por tenant en la consola e
Integraciones, `63045bef..5e7dd261`, y la marca «Lulo CRM», `9904a7dc`). `develop` va 627 commits delante de
`origin/main` en el backend y 522 en el frontend.

## Bloqueante: backend y frontend de `develop` no son compatibles

Desde variantes, las líneas de cotización, pedido y POS llevan `variantId`; un cuerpo con `productId` responde `422
validation.failed` (CLAUDE.md, «Un cuerpo crudo con `productId` ya no sirve en las líneas»). El frontend de `develop` no
tiene ninguna referencia a `variantId` ni a `deletedVariantIds`: agregar una línea desde el frontend contra este backend
falla. Ninguno de los dos `develop` se despliega solo hasta que exista el frontend de variantes (decisión del owner del
2026-10-20: salen en la misma ventana).

## Trabajo en curso

| Trabajo | Estado | Dónde |
| --- | --- | --- |
| Variantes de producto | Backend **construido** (T1–T15 y T14b, revisión final `92f74496`). Pruebas escritas y **sin ejecutar** desde la T4; revisión con Fable pendiente; frontend sin empezar | [estado propio](superpowers/plans/2026-10-20-variantes-estado.md) |
| Automatizaciones (antes «Asistentes»): spec 1, asistente en la conversación y horarios de atención | Backend **construido y mergeado** (`62cb0aaf`): 7 tareas, revisión final + una ronda de corrección. Nada pone `Active` en producción hasta el spec 4 (motor). Frontend: plan de 9 tareas en `qep-frontend/docs/superpowers/plans/2026-10-21-mensajeria-asistente-y-horarios-frontend.md`, sin empezar. Maquetación visual del módulo en sesión aparte (rama `feature/automatizaciones-maquetacion` del frontend) | [spec](superpowers/specs/2026-10-21-mensajeria-asistente-y-horarios-design.md), [plan backend](superpowers/plans/2026-10-21-mensajeria-asistente-y-horarios-backend.md), [investigación](superpowers/specs/2026-10-21-asistentes-investigacion.md) |
| Envío de cotizaciones por el canal del tenant | Spec en borrador para el owner. **Plan 1 de 3 construido** en los dos repos (proveedores habilitados por tenant); planes 2 y 3 sin escribir | [spec](superpowers/specs/2026-10-21-envio-de-cotizaciones-por-canal-del-tenant-design.md), [plan 1](superpowers/plans/2026-10-21-proveedores-habilitados-por-tenant.md) |
| Shopify: fuente del documento del comprador | Parte 1 **construida** (`d00a20a8`, merge `b7447106`); faltan el endpoint de opciones, el select del frontend y la documentación | [detalle](#shopify-fuente-del-documento-del-comprador) |
| Shopify: días de historial en «Sincronizar ahora» | Spec commiteado (`42565f98`); sin código en ningún repo | [spec](superpowers/specs/2026-10-21-shopify-historial-dias-design.md) |
| Onboarding: «Primeros pasos» y bienvenida a invitados | **En pausa** (owner, 2026-10-21): spec incompleto, a retomar; ver su sección «Para retomar». Sin código en ningún repo. Módulo nuevo `Onboarding` en el backend y `/setup` en el frontend. Cierra OD-0106 si se aprueba. §9 suma el onboarding de la tienda de Shopify (cinco pasos después de instalar, con importación de productos) | [spec](superpowers/specs/2026-10-21-onboarding-design.md) |
| Productos desde Shopify | En cola (después de variantes). Diseñado como el paso 4 del onboarding de Shopify (spec de onboarding §9.4 y §9.6, propuesta S-7): importación, emparejamiento por id de variante y webhooks de productos; requiere el scope `read_products` | [spec](superpowers/specs/2026-10-21-onboarding-design.md) |
| Centro de notificaciones (campanita) | En cola (después del anterior); sin diseño | memoria del proyecto |
| Alertas de salud de la conexión Shopify | En cola (depende de la campanita); sin diseño | memoria del proyecto |

Orden acordado con el owner: variantes → productos desde Shopify → campanita → alertas.

**Envío por canal.** La cotización y el pedido salen por la conexión de Zenvia del tenant (plan 2 recortado a Zenvia,
`07217b94`: `IWhatsAppChannelDirectory`, `QuotationsWhatsAppChannelDirectory`, `WhatsAppChannelSelection`). La cuenta
global `Quotations:WhatsApp:{ApiToken, FromNumber, TemplateId, OrderTemplateId}` y `LogWhatsAppSender` ya no existen.
Sin canal el envío responde `quotation.whatsapp.no_channel` / `order.whatsapp.no_channel`; con más de uno,
`*.whatsapp.channel_required`. Falta el plan 3: envío por Meta, elección del canal (diálogo y Configuración) y la
copia del frontend para los códigos nuevos.

## Specs

| Spec | Estado | Notas |
| --- | --- | --- |
| 2026-10-07 módulo POS | Construido (módulo Pos) | El spec aún dice «borrador» |
| 2026-10-07 módulos por tenant | Construido (`TenantModuleKey`, `TenantModulesEndpoints`) | El spec aún dice «borrador» |
| 2026-10-07 WhatsApp por tenant | Construido (`CompleteWhatsAppSignup`, `MessagingConnectionDirectory`) | El spec aún dice «borrador» |
| 2026-10-08 consola de operador | Construido (`OperatorPermissions`, módulo Platform) | El spec no tiene línea de estado |
| 2026-10-08 integraciones | Construido (módulo Integrations) | |
| 2026-10-09 mensajería WhatsApp | Construido (módulo Messaging) | |
| 2026-10-10 mensajería: asignación y BSUID | Construido (merge `11e0ae57`) | La clave de conversación es el BSUID |
| 2026-10-11 contactos, plantillas y campañas | Construido (`CampaignEndpoints`, ficha de contacto) | |
| 2026-10-11 notas de voz del asesor | Construido (`OggOpusSniffer`, `ConcentusOpusTranscoder`) | |
| 2026-10-12 respuestas rápidas | Construido (`QuickReplyEndpoints`) | |
| 2026-10-13 embudo de ventas | Construido (módulo Pipeline) | |
| 2026-10-14 gestor de plantillas de WhatsApp | Construido (`fb1734ea`) | Pendientes en [su plan](superpowers/plans/2026-10-15-gestor-de-plantillas-pendientes.md) |
| 2026-10-14 mensajería Instagram | Construido en el backend | Sin verificar contra Meta real: V-4 y V-8 |
| 2026-10-14 mensajería: marcar no leída | Construido (`de85c573`) | |
| 2026-10-14 versión desplegada | Construido (`8d15b8d4`; `DeploymentInfo`, `VersionEndpoints`) | El spec aún dice «borrador» |
| 2026-10-15 integración Shopify (pedidos) | Construido en los dos repos | Falta prueba contra Shopify real antes de `main` |
| 2026-10-15 mensajería: motivo de resolución | Construido (`ResolutionReason`) | |
| 2026-10-15 pedidos sin cotización | Construido; backend (`1dc604d1`) y frontend (`93c5336`) | 10 menores diferidos, entre ellos `UpdateParties` |
| 2026-10-16 mensajería: leída al responder | Construido (`79a58230`) | |
| 2026-10-17 flete | Construido; backend (`07487ded`) y frontend (`5feba65`) | Pendientes del owner P1/P2 con valor por defecto en el spec |
| 2026-10-18 layout de documentos | Construido en los dos repos | El spec aún dice «borrador» |
| 2026-10-20 variantes de producto | Backend construido; frontend sin empezar | Ver «Trabajo en curso» |
| 2026-10-21 envío de cotizaciones por canal | Planes 1 y 2 (sólo Zenvia) construidos; falta el 3 | Spec en borrador para el owner; el recorte del plan 2 lo aprobó el owner |
| 2026-10-21 Shopify: días de historial | Aprobado en conversación, sin empezar | Tope 30 días, desde `LinkedAt`, en cada pulsación; el cron no cambia. Extiende D-S4 |
| 2026-10-21 onboarding | Incompleto, en pausa; sin empezar | Metas por módulo; estado calculado en vivo, sin escribir en el GET; backfill cierra la guía de los tenants existentes |
| 2026-10-21 mensajería: asistente y horarios de atención (Automatizaciones, spec 1) | Backend construido (`62cb0aaf`); frontend sin empezar | `automation/*` y `/messaging/business-hours`; permiso `messaging.business_hours.manage` sólo en `admin`; festivos de Colombia por interruptor (D-H9). El `GET /business-hours` trae `connections` (D-H10 corregido al planear) |
| 2026-10-21 Automatizaciones: investigación y roadmap | Concepto; specs 2–4 y migración de lulo-chat sin escribir | Orden: interactivos → claves de API y envío proactivo → constructor (motor, editor React Flow, bloque HTTP) → migración. IA diferida |

Construido sin spec propio: enviar el pedido por WhatsApp (`POST /orders/{id}/send`, `OrderEndpoints.cs:114`; frontend
`3e7e4c00`), clasificación opcional del cliente, país del tenant, mensajería en tiempo real y filtros de la bandeja.

Los specs `2026-09-*` que el README cita (`README.md:449`, `:564`, `:2028`) se borraron en `8662d50c` («limpieza»); el
historial de `git` los conserva.

## Antes de llevar `develop` a `main` (lo hace el owner)

- **Variantes:** backend y frontend salen en la misma ventana (decisión del owner, 2026-10-20). Antes: correr las
  pruebas por módulo (Catalog, Quotations, Pos, Reporting, Shopify, Identity, `Bootstrapper.UnitTests`,
  `ArchitectureTests`), la revisión con Fable, las decisiones del §11 del traspaso, el escáner del POS con códigos
  reales y el plan del despliegue destructivo (`AddProductVariants` borra columnas; traspaso §13).
- **Push del frontend:** los 5 commits de `develop` local.
- **Shopify:** `develop` no va a `main` sin el frontend de Shopify (`CLAUDE.md`, 2026-10-18); `shopify` sigue en
  `DefaultForNewTenants`. Falta la verificación manual contra una app y tienda de Shopify reales, y
  `SHOPIFY_APP_CLIENT_ID` / `SHOPIFY_APP_CLIENT_SECRET` en el grupo `Backend-prod`.
- **Shopify, documento del comprador:** las tiendas con `company` pasan a `shipping_address.company` (ver su sección).
- **Shopify, tokens que expiran (bloqueante con fecha):** desde el **2027-01-01** toda app pública tiene que usar tokens
  offline que expiran; a las apps creadas desde el 2026-04-01 ya se los exige. QEP pide el token sin `expiring=1` y no
  lo refresca (`ShopifyAdminGateway.cs:58`). Detalle en el spec de onboarding §9.10.
- **Shopify, app «lulo crm»:** QEP reemplaza como backend a la app publicada «lulo crm» en el mismo dominio
  (`app.lulocrm.com`, `api.lulocrm.com`), cuando tenga la paridad con la v1, guías incluidas (decisión del owner,
  2026-10-21; spec de onboarding §9.10 y §9.11).
  - Para el cambio, QEP atiende como alias las rutas de webhooks de la v1.
  - **Urgente aparte de QEP:** Lulo CRM v1 usa tokens que no expiran. Si no se parchea antes del 2027-01-01, su
    integración con Shopify se cae ese día.
- **Pos:** antes de desplegar, comprobar que ningún tenant tenga un rol custom con la clave `cashier`.
- **Zenvia por tenant:** el despliegue deja a Origen Botánico sin envío hasta que se cree su conexión de Zenvia
  (producción no tiene Integrations antes): el procedimiento está en README § Plantilla de WhatsApp › En producción.
  Después se borran `QUOTATIONS_WHATSAPP_*` de `Backend-prod` y se comprueba que el Secret vivo ya no las tenga.
- **`dotnet format`:** roto en `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ReplyMarksReadApiTests.cs`,
  `WHITESPACE` en las líneas 139 y 208 (comprobado el 2026-10-21).
- **Meta/Instagram:** V-4 y V-8 sin verificar contra Meta real.

## Decisiones del owner pendientes

- Gestor de plantillas: qué errores de Meta pausan la conexión.
- Flete: P1 (retención sin el envío) y P2 (giro del recaudo), con valor por defecto en el spec.
- Shopify: si se saca `shopify` de `DefaultForNewTenants` hasta que exista el frontend (alternativa a esperar).
- Variantes: las del §11 del [traspaso](superpowers/plans/2026-10-20-variantes-handoff.md) (columna «Variante» en la
  variante por defecto, código de barras de una variante inactiva, borrado con `CASCADE` del historial, carrera de
  borrado, búsqueda por código de barras, `deletedVariantIds`).
- Envío por canal: la revisión del spec, que sigue en borrador. Y del plan 2: aceptar la ventana sin envío de Origen
  Botánico tras el despliegue o cerrarla con una siembra de una sola vez desde `QUOTATIONS_WHATSAPP_*`.
- Onboarding (§6 del [spec](superpowers/specs/2026-10-21-onboarding-design.md)): D-1 sembrar IVA 19 %, 5 % y Exento al
  registrar un tenant de CO; D-2 landing en `/setup` mientras falte la meta principal; D-4 número de WhatsApp de
  soporte; D-5 en qué lugar de la cola entra (la propuesta es el backend en paralelo a variantes).
- Onboarding de Shopify (§9.8 del mismo spec). S-1 a S-7 las decidió el owner el 2026-10-21; queda **S-8**: app
  embebida o no.
- Shopify, documento del comprador: migrar el dato de las tiendas con `company` o avisarles; si el select de «Otro…»
  aplica también al tipo de documento; si se importa `billing_address`.

## Hotfixes y decisiones que no se ven en el código

- USD sin IVA (hotfix 2026-10-14): sin backfill; fila de IVA oculta en el frontend; solo COP discrimina IVA. La
  generalización a otras monedas no se le consultó al owner.
- ImageSharp exige licencia en Release: `main` ya tiene la supresión (hotfix 2026-10-14).
- Pago parcial con redondeo (hotfix 2026-10-19): en `main` y en `develop` (`merge/hotfix-pago-parcial`).

## Defectos conocidos

- Un registro de tenant que falla después de aprovisionar deja el usuario en `identity.users` sin membresía
  (`RegistrationEndpoints.cs:89-104`).
- `develop` de backend y frontend incompatibles por `variantId` (ver «Bloqueante»).
- Los harness de integración no fijan `Seed:Enabled`: con `Seed:Enabled=true` en los user-secrets del developer caen
  con `Seed:OwnerEmail is required`. Mientras tanto, se corren con `Seed__Enabled=false`.

## Shopify: fuente del documento del comprador

**Origen (2026-10-21).** La tienda `01a15b17-2dce-71d5-8ff0-8605bf9de724` tenía «Atributo del documento» = `company`, que en
Shopify es el campo «Empresa» de `shipping_address`, no un atributo del checkout. `DocumentOf` sólo leía
`note_attributes`/`customAttributes`, así que el documento nunca se encontraba y el pedido #1277B quedó con el aviso
`customer_incomplete`.

**Decisiones del owner.** La clave es la **ruta exacta**; si no existe en el pedido, no hace nada (sin prioridad ni caída entre
fuentes). El select lleva una opción «Otro…» que abre el input libre.

**Hecho (`d00a20a8`, mergeado a `develop` en `b7447106`, con push).** `ShopifyAddress.Company`; los dos parsers y el query de
GraphQL lo leen; `ShopifyDocumentSources` resuelve la clave (`shipping_address.company` lee la dirección, cualquier otra es
un atributo). Con prueba primero. Pasaron 359 unitarias, 81 de integración y 11 de arquitectura de Shopify; no se corrió la
suite completa. La rama `fix/shopify-document-source` y el worktree `../qep-backend-doc-source` ya se pueden borrar.

**Falta.**
1. Endpoint `GET …/shopify/stores/{connectionId}/document-sources`: `shipping_address.company` más las claves de atributo que
   ya aparecen en `shopify.orders.payload` de esa tienda. Prueba primero. No existe ninguna ruta todavía.
2. Frontend (`qep-frontend`, rama `develop`): select con «Otro…» para «Atributo del documento» y, por simetría, para
   «Atributo del tipo de documento» (esto último es una suposición, sin confirmar con el owner). Sin empezar.
3. Documentación: el spec de Shopify sigue diciendo que `document_attribute_key` es una clave de `customAttributes`
   (`2026-10-15-integracion-shopify-pedidos-design.md:366` y `:422`); ahora es una ruta. Revisar también el README.

**Al desplegar.** Las tiendas que tenían `company` hay que pasarlas a `shipping_address.company` (el `company` a secas ya no
lee la dirección) y reintentar los pedidos que quedaron incompletos. Pendiente de decidir si se migra el dato o se avisa.

**Fuera de este trabajo, sin decidir.** `billing_address` no se importa (ni los parsers ni el query la piden) y el pedido
siempre manda `Billing: null` (D-S7 y D-S15). Propuesta sin respuesta: llenar la parte `Billing` cuando la dirección de
facturación esté completa; cambiaría D-S7 y D-S15.

## Registro

- 2026-10-21 — Mergeado a `develop` el backend del spec 1 de Automatizaciones (`62cb0aaf`: asistente en la conversación y
  horarios de atención), construido con subagentes en 7 tareas. La revisión final encontró un Critical real —mover el
  predeterminado a un horario más antiguo daba 409 porque EF ordena los `UPDATE` por clave primaria— que se corrigió con
  `SwapDefaultAsync` en una transacción con orden fijo. Decisiones del developer en la revisión del spec: D-H9 pasa a
  festivos de Colombia por interruptor; el módulo se renombra «Automatizaciones». Frontend pendiente (plan escrito).
- 2026-10-21 — Mergeado a `develop` el plan 2 del envío por canal, recortado a Zenvia (`bba8ba1b..07217b94`, decisión
  del owner): la conexión de Zenvia lleva `templateId` y `orderTemplateId`, cotización y pedido salen por la conexión
  del tenant y se retira la cuenta global. Revisión independiente hecha; tres correcciones en la misma rama.
- 2026-10-21 — El owner pone en pausa el spec de onboarding para priorizar otras tareas. Lo pendiente quedó en la
  sección «Para retomar» del spec. Las urgencias de los tokens de Shopify (v1 y QEP, 2027-01-01) no esperan a
  retomarlo.
- 2026-10-21 — Tres cosas en el spec de onboarding:
  - **D-6 (decidida por el owner):** al registrar un tenant se crea una empresa que factura por defecto. NIT, ciudad y
    cuenta pasan a ser opcionales mientras la empresa tenga «datos incompletos».
  - **Cambio a QEP:** QEP pasa a `app.lulocrm.com` y `api.lulocrm.com` cuando tenga la paridad con Lulo CRM v1,
    guías incluidas. La categoría de la ficha no cambia.
  - **Webhooks (§9.11):** QEP atiende las rutas viejas de la v1 como alias y limpia las suscripciones al enlazar. Se
    encontró que la v1 responde 201 en los webhooks de cumplimiento con cualquier firma, y que no valida el HMAC de
    los de pedidos.
- 2026-10-21 — El owner decide que QEP reemplace como backend a la app de Shopify «lulo crm» que ya está publicada,
  conservando el nombre (spec de onboarding §9.10). Al investigarlo aparece que Shopify exige tokens que expiran a
  todas las apps públicas desde el 2027-01-01, y QEP no los soporta.
- 2026-10-21 — El owner decide S-1 a S-5 del onboarding de Shopify (§9.8):
  - **S-1:** la app es gratis en Shopify y QEP cobra su suscripción como hoy. Falta confirmarlo con Shopify Support.
  - **S-2:** en los productos importados manda Shopify.
  - **S-3:** los pedidos se emparejan por el id de la variante de Shopify y después por SKU.
  - **S-4:** a los precios sin impuestos se les suma el IVA al importar.
  - **S-5:** `read_products` se pide a las tiendas ya instaladas cuando eligen traer los productos.
  - **S-6:** los borradores entran inactivos y los archivados no se traen.
  - **S-7:** «Productos desde Shopify» se construye como parte de este onboarding.
- 2026-10-21 — Se suma al spec de onboarding la §9, el onboarding de la tienda de Shopify:
  - Shopify no exige un asistente, pero sí OAuth primero, una interfaz que funcione al aceptar los permisos, la
    configuración de la conexión, los webhooks de privacidad y el cobro por Shopify.
  - Hoy una empresa nueva que instala desde Shopify queda con todos sus pedidos en espera por `product_unmatched`.
  - El paso 4 trae los productos desde Shopify.
- 2026-10-21 — Se escribe el spec de onboarding («Primeros pasos» por metas según módulos y bienvenida a invitados),
  revisado en tres rondas contra el código. Sin commitear ni implementar; espera al owner.
- 2026-10-21 — Se verifica este archivo contra el código: los once specs «Por verificar» están construidos; variantes
  (backend), la fuente del documento de Shopify y el plan 1 del envío por canal ya están en `develop` con push; se
  confirma el `dotnet format` roto; se detecta que `develop` de backend y frontend no son compatibles por `variantId`.
- 2026-10-21 — Se mergea a `develop` el plan 1 del envío por canal (proveedores habilitados por tenant), en los dos repos.
- 2026-10-21 — Se mergea a `develop` la parte 1 de la fuente del documento de Shopify (`b7447106`); quedan el endpoint de
  opciones, el select del frontend y la documentación.
- 2026-10-21 — Se mergea a `develop` el backend de variantes, con la revisión final (`92f74496`).
- 2026-10-21 — Se escribe y commitea el spec de días de historial para «Sincronizar ahora» de Shopify (origen: un pedido
  anterior al enlace no entraba por D-S4). El frontend está sin verificar.
- 2026-10-20 — Se crea este archivo. Diseño y plan de variantes en `develop` local.
