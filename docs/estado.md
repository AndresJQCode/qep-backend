# Estado del proyecto `qep-backend`

Archivo vivo: se actualiza al cerrar un trabajo, tomar una decisión o desplegar. **Verificado contra el código el
2026-10-21** (`develop` en `49c42709`, igual a `origin/develop`; `qep-frontend` en `develop`, `5e7dd261`). Ante
cualquier duda gana el código ([CLAUDE.md](../CLAUDE.md)). La línea `Estado:` de cada spec dice si el diseño se aprobó,
no si se construyó: varios siguen diciendo «borrador» y están construidos.

## Cómo se lee

Estados: **Construido** (mergeado a `develop`) · **En curso** · **Aprobado, sin empezar** · **Por verificar**.
«Push» dice si el trabajo ya llegó a `origin`. Para comprobarlo: `git log --oneline origin/develop..develop` en cada repo.

**Push al 2026-10-22 (noche).** Backend: nada pendiente. Frontend: 6 commits sin push (CB-13, CB-14 y las correcciones
de la revisión final del constructor de chatbots, `1d453314..208e13f2`). `develop` va 627 commits delante de
`origin/main` en el backend y 522 en el frontend.

## Variantes: backend y frontend salen juntos

Desde variantes, las líneas de cotización, pedido y POS llevan `variantId`; un cuerpo con `productId` responde `422
validation.failed` (CLAUDE.md, «Un cuerpo crudo con `productId` ya no sirve en las líneas»). El frontend de variantes ya
existe (VAR-01..13, cierre `22cde669`, en `origin/develop` del frontend; verificado el 2026-10-22), así que los dos
`develop` vuelven a ser compatibles. Siguen saliendo en la misma ventana (decisión del owner del 2026-10-20).

## Trabajo en curso

| Trabajo | Estado | Dónde |
| --- | --- | --- |
| Cliente identificado en POS (spec 2026-10-14 + cambios del owner 2026-10-22) | Backend **construido y mergeado** (`d4d4eff3..bcc92424`, ff a `develop`, sin push): permiso `pos.sale.customer` (exige `pos` y `customers`; admin y cajero), `GET`/`POST /pos/customers` (elegibles filtrados en SQL con `SearchSelectableAsync`; alta rápida como ficha incompleta con documento, auditada con `source: "pos"`; 422 `pos.customer.identification_taken` con `existing` vía `IHasProblemExtensions`), `customerId` opcional en `POST /pos/sales` (huella sin cambio cuando no viene; 403/422 `pos.sale.customer_*` después de la repetición) e índice parcial `IX_sales_tenant_customer`. 8 tareas + POSC-09 con revisión por tarea, revisión final y una tanda de correcciones. Frontend (POSC-11..18) en curso en `qep-frontend` rama `feature/pos-cliente-identificado` | [spec](../../qep-frontend/docs/superpowers/specs/2026-10-14-pos-cliente-identificado-design.md), [plan backend](superpowers/plans/2026-10-22-pos-cliente-identificado-backend.md) |
| Variantes de producto | Backend **construido** (T1–T15 y T14b, revisión final `92f74496`). Pruebas escritas y **sin ejecutar** desde la T4; revisión con Fable pendiente. Frontend construido (VAR-01..13, en `origin/develop` del frontend) | [estado propio](superpowers/plans/2026-10-20-variantes-estado.md) |
| Automatizaciones (antes «Asistentes»): spec 1, asistente en la conversación y horarios de atención | **Construido en los dos repos.** Backend (`62cb0aaf`): 7 tareas, revisión final + una ronda de corrección. Frontend (`dd22f151..551c05d3`, ff a `develop` del frontend, **sin push**): 9 tareas AH-01..09 con revisión por tarea y revisión final; chip y filtro «Asistente» en la bandeja, pausar/devolver desde el hilo, página `/settings/business-hours`. Falta la prueba manual contra el backend real | [spec](superpowers/specs/2026-10-21-mensajeria-asistente-y-horarios-design.md), [plan backend](superpowers/plans/2026-10-21-mensajeria-asistente-y-horarios-backend.md), [investigación](superpowers/specs/2026-10-21-asistentes-investigacion.md) |
| Automatizaciones: spec 4, constructor de chatbots (motor, editor, bloque HTTP) | **Backend 1 construido y mergeado** (`46455d73..ff7f6956`, ff a `develop`): fases 1-3 del spec (Mensajería lista para un bot, módulo `automations` y permisos, módulo `Automations` con agregado, validador, endpoints del editor, plantillas e import/export), 20 tareas con revisión por tarea, revisión final en dos paquetes y una tanda de correcciones. **Backend 2 construido y mergeado** (`6261ea26..a194e6ab`, ff a `develop`): el motor atiende (corridas, worker con abandono y purgas, `GET /runs`, «Datos capturados»), proveedor `external-api` con política anti-SSRF, bloque HTTP con `http_calls`, simulador del borrador. `Messaging:Automation:Enqueue` prendido; sólo responde un tenant con el módulo `automations` encendido y un chatbot publicado. **Frontend (plan 3) construido y mergeado** en `develop` del frontend (`551c05d3..208e13f2`): 14 tareas CB-01..14 con revisión por tarea, revisión final y una tanda de correcciones; lista, creación, editor (lienzo, propiedades, autoguardado con cola única, validación, publicar, ajustes, simulador) y en la bandeja chip con chatbot y paso, autor bot, interactivos y «Datos capturados». Importar/exportar quedó fuera (decisión del developer). El 2026-10-23 se corrigió en `develop` un entrante fuera de orden que el bot contestaba dos veces (`35799550`, ver Registro). Falta la prueba manual contra el backend real y Clasic (plan 4, sin escribir) | [spec](superpowers/specs/2026-10-21-constructor-de-chatbots-design.md), [plan backend 1](superpowers/plans/2026-10-21-constructor-de-chatbots-backend-1.md), [plan backend 2](superpowers/plans/2026-10-22-constructor-de-chatbots-backend-2.md) |
| Envío de cotizaciones por el canal del tenant | Spec en borrador para el owner. **Plan 1 de 3 construido** en los dos repos (proveedores habilitados por tenant); planes 2 y 3 sin escribir | [spec](superpowers/specs/2026-10-21-envio-de-cotizaciones-por-canal-del-tenant-design.md), [plan 1](superpowers/plans/2026-10-21-proveedores-habilitados-por-tenant.md) |
| Shopify: fuente del documento del comprador | Parte 1 **construida** (`d00a20a8`, merge `b7447106`); faltan el endpoint de opciones, el select del frontend y la documentación | [detalle](#shopify-fuente-del-documento-del-comprador) |
| Shopify: días de historial en «Sincronizar ahora» | Spec commiteado (`42565f98`); sin código en ningún repo | [spec](superpowers/specs/2026-10-21-shopify-historial-dias-design.md) |
| Onboarding: «Primeros pasos» y bienvenida a invitados | **En pausa** (owner, 2026-10-21): spec incompleto, a retomar; ver su sección «Para retomar». Sin código en ningún repo. Módulo nuevo `Onboarding` en el backend y `/setup` en el frontend. Cierra OD-0106 si se aprueba. §9 suma el onboarding de la tienda de Shopify (cinco pasos después de instalar, con importación de productos) | [spec](superpowers/specs/2026-10-21-onboarding-design.md) |
| Productos desde Shopify | En cola (después de variantes). Diseñado como el paso 4 del onboarding de Shopify (spec de onboarding §9.4 y §9.6, propuesta S-7): importación, emparejamiento por id de variante y webhooks de productos; requiere el scope `read_products` | [spec](superpowers/specs/2026-10-21-onboarding-design.md) |
| Centro de notificaciones (campanita) | En cola (después del anterior); sin diseño | memoria del proyecto |
| Alertas de salud de la conexión Shopify | En cola (depende de la campanita); sin diseño | memoria del proyecto |

Orden acordado con el owner: variantes → productos desde Shopify → campanita → alertas.

**Automatizaciones: siguiente paso (2026-10-22, noche).** Los specs 1 y 4 (backend y frontend) están construidos en
los dos repos. Antes de producción: prueba manual del editor (autoguardado, 412 con dos pestañas, publicar, simular) y
de la bandeja con un chatbot corriendo contra el backend real. Después: el plan 4 (Clasic), los specs 2 y 3 en lo que el
4 no tomó (envío proactivo y claves de API), importar/exportar si se necesita, y al final la migración de lulo-chat
(`docs/superpowers/specs/2026-10-21-migracion-lulo-chat-design.md`, sin commitear, con 10 preguntas abiertas en §4).
Todo con el «arranca» del developer.

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
| 2026-10-21 mensajería: asistente y horarios de atención (Automatizaciones, spec 1) | Construido; backend (`62cb0aaf`) y frontend (`551c05d3`, sin push) | `automation/*` y `/messaging/business-hours`; permiso `messaging.business_hours.manage` sólo en `admin`; festivos de Colombia por interruptor (D-H9). El `GET /business-hours` trae `connections` (D-H10 corregido al planear) |
| 2026-10-21 Automatizaciones: constructor de chatbots (spec 4) | Backends 1 (`ff7f6956`) y 2 (`a194e6ab`) y frontend (`208e13f2`, sin push) construidos; Clasic sin plan | Toma de los specs 2 y 3 lo que el bot necesita (interactivos salientes, «API externa»). Reglas del controlador R1-R21 en el reporte de la sesión; pendientes para el backend 2 abajo, en Registro |
| 2026-10-21 Automatizaciones: investigación y roadmap | Concepto; specs 2–3 y migración de lulo-chat sin escribir (el 4 se escribió y se está construyendo) | Orden: interactivos → claves de API y envío proactivo → constructor (motor, editor React Flow, bloque HTTP) → migración. IA diferida |

Construido sin spec propio: enviar el pedido por WhatsApp (`POST /orders/{id}/send`, `OrderEndpoints.cs:114`; frontend
`3e7e4c00`), clasificación opcional del cliente, país del tenant, mensajería en tiempo real, filtros de la bandeja y filtro de ventas
del POS por número parcial y documento del cliente (2026-10-23).

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

- 2026-10-23 — Mergeado a `develop` de los dos repos el filtro de ventas del POS por documento del cliente (backend
  `a11da4f9`, frontend `4ea1a9e4`, ff). `GET /pos/sales` acepta `customerDocument` (≤ 32, contra la foto
  `customer_identification_number` de la venta) y `number` deja de ser exacto: los dos coinciden por un pedazo con
  `ILIKE` y comodines escapados, y se combinan con Y. En el popover de Ventas, «Documento del cliente» junto a «Número de
  venta». Pruebas: Pos unitarias 226/226, integración 61/61, `ArchitectureTests` 125/125; frontend `src/features/pos`
  341/341. Pendiente del owner: la lista abre en «hoy», así que buscar una venta de otro día exige cambiar el rango.
  Sin push.
- 2026-10-23 — Mergeado a `develop` el arreglo del entrante fuera de orden (`35799550`, ff): el bot volvía a mandar el
  mensaje de fuera de horario sin que el cliente escribiera, porque Meta reintentó 19 min tarde un «hola» de las 21:42
  que llegó detrás de uno de las 21:56 ya contestado (`dev_qep`, 2026-10-22). `InboundIngestion.ShouldEnqueue` ya no
  encola un entrante anterior al `last_inbound_at` previo de la conversación; el mensaje sigue entrando al hilo. Regla en
  el spec 4 (§5.1) y en CLAUDE.md. Pruebas: Messaging integración 529/529, unitarias 1281/1281, Automations integración
  148/148. Sin push.
- 2026-10-22 — Mergeado a `develop` del frontend el frontend del constructor de chatbots (plan 3, `551c05d3..208e13f2`),
  construido con subagentes en 14 tareas (CB-01..14) con revisión por tarea, revisión final de la rama y una tanda de
  correcciones. Lo más delicado: una sola cola de escritura por editor (autoguardado de 2 s, validar, publicar, ajustes,
  borrar) con la `version` que devolvió la escritura anterior; un 412 recarga el editor. La revisión atrapó antes del
  merge: Suprimir con el foco fuera del lienzo borraba el bloque seleccionado; un `cta.url` `javascript:` quedaba
  clicable (ahora sólo `https`, `src/lib/https-url.ts`); un falso «No pudimos guardar»; una simulación muerta tras un
  reinicio fallido. **Otra sesión mergeó la rama a mitad (CB-01..12) y la empujó a `origin/develop` (`1d453314`, 21:25)**;
  el resto entró encima sin cambios de árbol. Importar/exportar quedó fuera por decisión del developer. Suite: sólo las
  5 fallas de `catalog/product-form` que ya tenía `develop`. Falta la prueba manual contra el backend real.
- 2026-10-22 — Mergeado a `develop` del frontend el frontend del spec 1 de Automatizaciones (`dd22f151..551c05d3`, con
  `develop` integrado en la rama y ff; sin push), construido con subagentes en 9 tareas (AH-01..09) con revisión por
  tarea, revisión final de la rama y una tanda de correcciones (franjas ordenadas al guardar, solape en excepciones,
  confirmar al eliminar). Decisiones: la hora y los días se formatean en `es-CO` fijo (idioma de la UI, no formato
  regional); los festivos como día calendario; «Hasta» 00:00 se guarda como 1440 (D-H12). Suite completa: 10 fallas
  en `landing`, `product-form` y la ruta `/`, idénticas en `develop` antes del merge. Falta la prueba manual contra el
  backend real. Al verificar se encontró que el bloqueante de `variantId` ya no aplica: el frontend de variantes
  (VAR-01..13, `22cde669`) está en `origin/develop` del frontend.
- 2026-10-22 — Mergeado a `develop` el backend del cliente identificado en POS (`d4d4eff3..bcc92424`, ff), construido con
  subagentes en 9 tareas (POSC-01..09) con revisión por tarea, revisión final de la rama y una tanda de correcciones; la
  compuerta de merge corrió `dotnet build -warnaserror` y los proyectos de prueba de Pos, Customers, Bootstrapper,
  ArchitectureTests y el catálogo de autorización de Tenancy, todos verdes. El gotcha de CLAUDE.md sobre clientes
  `Incomplete` se corrigió: `POST /pos/customers` crea incompletos con documento. Pendiente del owner: «Completar ficha»
  en Clientes podría precargar el documento de una ficha creada en la caja. Frontend en curso.
- 2026-10-22 — Mergeado a `develop` el backend 2 del constructor de chatbots (`6261ea26..a194e6ab`, ff), construido con
  subagentes en 15 tareas (0-14) con revisión por tarea, revisión final de la rama y una tanda de correcciones. Motor
  puro (`FlowEngine`) y `ConversationRun`; `RunProcessor`/`RunEffects` con lock por conversación no bloqueante
  (`pg_try_advisory_xact_lock`), `clientId` determinístico por `(runId, baseStepNumber, effectIndex)` (el spec §5.3
  tiene la errata) y tope de 25 reentradas por unidad; worker con abandono a las 24 h y purgas (inbox 7 días,
  `http_calls` 30 días, simulaciones 2 h); el motor respeta el módulo apagado. `GET /runs` y «Datos capturados» para el
  asesor. Integrations: proveedor `external-api` con política anti-SSRF (rangos prohibidos, sin proxy ni redirects,
  headers reservados) y probador. Bloque HTTP con interpolación JSON-aware, escape del path, mapeo con `$`/`[0]` (Clasic
  responde arrays en la raíz) y espera de 20 s por el adjunto. Simulador del borrador. Flag `Enqueue` prendido.
  **Límites conocidos** (README): una caída entre la llamada HTTP y el commit de la unidad repite la llamada; una
  unidad de más de ~45 s quema intentos del inbox de esa conversación y, pasados ~70 s, entrantes siguientes pueden
  cerrarse `failed` sin que el bot los lea (el arreglo es un `RetryLater` de Mensajería que no cuente intento).
- 2026-10-22 — Mergeado a `develop` el backend 1 del constructor de chatbots (spec 4 de Automatizaciones,
  `46455d73..ff7f6956`, ff), construido con subagentes en 20 tareas. Mensajería: interactivos salientes con los límites de
  Meta, autoría `sent_by_kind`/`sent_by_chatbot_id`, `MessageDto.sentByChatbot` e `interactive`, `IBotOutbound`, cola
  `messaging.automation_inbox` tras `Messaging:Automation:Enqueue` (apagado) con `IAutomationInbox` (lease, cerca por
  `attempts`, cierre de filas envenenadas). Tenancy: módulo `automations` (depende de `messaging`). Permisos
  `automations.chatbot.read`/`manage` sólo en `admin`. Módulo `Automations`: esquema de siete tablas, agregado `Chatbot`,
  modelo del flujo y validador, endpoints de §4.4 salvo simulación y `GET /runs`, plantillas del sistema embebidas,
  import/export. La revisión final (dos paquetes) no encontró Critical; la tanda de corrección cerró los Important que el
  backend 2 consume. Al verificar apareció que la suite de Mensajería moría a las ~512 pruebas en macOS (cada host de
  prueba dejaba vivo el watcher de los user-secrets): se corrigió en el harness (`ff7f6956`). **Pendiente para el backend
  2, antes de prender el flag:** purgar filas procesadas de `automation_inbox`; validar al publicar lo que el motor no puede
  ejecutar (0 botones, lista fija vacía, `timeoutSeconds` fuera de 1..10, `maxAttempts` fuera de 1..5, regex vacía, URL
  del botón); las versiones publicadas se cargan con sus blobs en cada `FindAsync` (table splitting + migración vacía);
  decisión del owner sobre si la respuesta del bot marca como leído para los asesores (`MarkRepliedAsync`).
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
