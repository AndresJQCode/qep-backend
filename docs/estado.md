# Estado del proyecto `qep-backend`

Archivo vivo: dice cómo están las cosas **hoy**. Lo cumplido sale de aquí; la historia queda en el [Registro](#registro).
Ante cualquier duda gana el código ([CLAUDE.md](../CLAUDE.md)). La línea `Estado:` de cada spec dice si el diseño se
aprobó, no si se construyó.

## Cómo se lee

Estados: **Construido** (en `main`) · **En curso** · **Aprobado, sin empezar** · **Por verificar**. **Por confirmar:**
marca algo que no se pudo comprobar contra git; se conserva hasta que alguien lo confirme.

**Git al 2026-11-27.** En los dos repos `origin/main` tiene todo `origin/develop`: backend `cd7e97bcd` (con
`origin/develop` en `f05851c85`), frontend `09cafc80` (con `origin/develop` en `549adf7d`). Todo commit del backend que
cita este archivo está en `origin/main`. Ramas del backend sin mergear: `feature/integracion-coordinadora` (con push) y
`feature/replicas-dos` (sin push), cada una con su worktree. Para comprobarlo de nuevo, en cada repo:
`git log --oneline origin/main..origin/develop` y `git branch -a --no-merged origin/main`.

**Despliegues (no se ven en git; salen del Registro).** El último despliegue registrado del backend es `6f56c7265`
(2026-11-25, con el frontend `472eda35`); el anterior, `8fb3f174` (2026-11-16). Lo que entró a `main` después de
`6f56c7265` no tiene despliegue registrado: está en «[Al desplegar](#al-desplegar-lo-que-está-en-main-sin-despliegue-registrado)».

## Trabajo en curso

Sólo lo que tiene algo pendiente distinto de una prueba manual (esas están en su sección) o de pruebas automáticas sin
correr (también en su sección).

| Trabajo | Pendiente | Dónde |
| --- | --- | --- |
| WMS: bodegas, ledger y motor de tareas | En `main` (T1 `c7e03f03f`, correcciones `809b028ed`, T2 `7d3a503e2`, decisiones M3/B4/B5 y dos celulares `a5f7ce066`, tarjeta `snLjp0i1`), sin despliegue registrado. **Falta:** frontend del tablero (tarjeta Trello `jhyqfSSe`) y de novedades (W2), app (`resumable`, `short`, `count`, `not_offered`, traspaso de celular), cortes T3–T8. **Por confirmar:** qué cubre el frontend `feature/wms-web`, ya en `main` del frontend (`208bc8cf`) | `docs/superpowers/specs/2026-11-26-wms-tareas-y-operaciones-design.md` (§13.1), [plan de correcciones](superpowers/plans/2026-11-26-wms-t1-correcciones-revision.md) |
| Cotizaciones de proyecto (módulo `projects`) | En `main` (`8603d6e9a`), sin despliegue registrado; frontend en `main` del frontend (`c7a37778`, `e047c413`). **Falta:** pruebas y despliegue (ver sus secciones) | `docs/superpowers/specs/2026-11-26-cotizaciones-de-proyecto-design.md` |
| Shopify: parte de envío de pedidos de otros países | En `main` (`bef48e2b2`, `98b1d85ad`), sin despliegue registrado. **Falta el frontend** (spec en `qep-frontend`, `25b283f3`) | [plan](superpowers/plans/2026-11-26-shopify-parte-de-envio-otros-paises-backend.md), [handoff](superpowers/specs/2026-11-24-shopify-parte-de-envio-otros-paises-backend-handoff.md) |
| Teléfono con indicativo del país | En `main` (backend `da98b8e20`, frontend `494cbeed4`), sin despliegue registrado. **Falta:** el selector todavía no ofrece todos los países; verificar contra Siigo real el cliente con `indicative` y `number` | [spec Siigo](superpowers/specs/2026-11-25-siigo-telefono-indicativo-design.md), [spec Excel](superpowers/specs/2026-11-25-excel-pedidos-indicativo-design.md), [plan](superpowers/plans/2026-11-25-siigo-telefono-indicativo.md) |
| RRHH: editar y eliminar registros de «Cargo y lugar de trabajo» | En `main` (`4a4d5eec6`, tarjeta Trello `W4Ijr9DW`), sin despliegue registrado; frontend en `main` del frontend (`22f8e9fd`, `15ec1a21`). **Por confirmar:** el texto de la confirmación al borrar un registro con PDF | [spec](superpowers/specs/2026-11-26-hr-editar-eliminar-cargo-lugar-design.md), [plan](superpowers/plans/2026-11-26-hr-editar-eliminar-cargo-lugar.md) |
| RRHH: recordatorio de cumpleaños de empleados | En `main` (`bc2502615`, tarjeta Trello `KNdEXvgF`), sin despliegue registrado. El GET y el PUT del ajuste traen `candidates` y `connections` con estado de plantilla (`18df9611f`, tarjeta `pjzEGHMV`), en `develop` sin push. **Falta:** frontend (tarjeta `4kvFw8GV`, que ya puede dejar `GET /memberships` y las dos llamadas de Messaging); `test-feature.ps1` sobre `18df9611f`; las pruebas del feature original están escritas y sin correr. **Orden de salida:** backend primero | [spec](superpowers/specs/2026-11-26-hr-birthday-notices-design.md), [plan](superpowers/plans/2026-11-26-hr-birthday-notices.md), [spec BFF](superpowers/specs/2026-11-27-hr-birthday-settings-bff-design.md) |
| Plataforma: consola de colas | En `develop` sin push (merge `345fb9525`, tarjeta Trello `CN1QTtcc`). `GET …/operator/queues` con el permiso `operator.queues.read`: las ocho colas, `null` si no se mide o no se pudo leer, tope total de 10 s. **Decisión del owner:** autorizó la prueba nueva de `CompositionRootTests`. **Falta:** frontend (tarjeta `tCGWdb4x`, con el contrato comentado) y la suite completa. Dos unitarias de Messaging ajenas a esta rama quedan rojas en `develop` (`TheKindIsStoredByNameAtTheEnd` de `AppointmentReminder` y `WholesaleInvitationNotice`) | [spec](superpowers/specs/2026-11-27-plataforma-colas-design.md) |
| País de «Facturar a» obligatorio en cotizaciones y pedidos | En `develop` sin push (merge `0f9b7573f`, tarjeta Trello `PGAmC4DJ`). La facturación guarda `CO` literal; guardar desde la consola una facturación con nombre sin país da 422; Siigo reporta `customer_country_missing`. **Falta:** frontend (tarjeta `QolgF4Ma`, con el contrato comentado) y la suite completa. **Orden de salida:** con el frontend, o la consola recibe el 422 | [spec](superpowers/specs/2026-11-27-pais-facturar-a-obligatorio-design.md) |
| Número de documento numérico en NIT, CC y CE (en RRHH: CC, CE, PEP y PPT) | En `main` (`cd5cbcabc` y correcciones `931c795d0`), sin despliegue registrado. Se bloquea siempre, también un número viejo con letras que vuelve sin cambios (decisión del owner). **Falta:** frontend (prompt entregado al owner) y la suite completa | [spec](superpowers/specs/2026-11-27-numero-documento-solo-digitos-design.md), [plan](superpowers/plans/2026-11-27-numero-documento-solo-digitos.md) |
| Integración con Siigo | En `main` y desplegada (`6f56c7265`); cliente con indicativo (`da98b8e20`), clientes de otros países (`840c5e719`, frontend `8e0fb497`) y token por credenciales (`f8673c363`) sin despliegue registrado. **Falta:** verificar contra Siigo real el cliente con indicativo y el extranjero. **Enviar la factura por WhatsApp** (`POST …/siigo-invoice/{invoiceId}/send-whatsapp`): **por confirmar** el botón del detalle del pedido en el frontend; antes de usarlo en producción, la plantilla `Invoice` aprobada en Meta o en Zenvia (más `invoiceTemplateId` en cada conexión de Zenvia) y la regla de lifecycle de `invoices/` en R2 | Specs y planes `2026-11-18-integracion-siigo`, `2026-11-21-siigo-invoice-whatsapp`, `2026-11-26-siigo-ciudades-exterior`; README § Integración con Siigo |
| Servicios en cotizaciones y pedidos | En `main` (`bd7458c8`) con frontend (`31dd3ec1`). **Sin empezar:** fase 2 del spec, ReteICA separada por actividad (hoy una por municipio) | `docs/superpowers/specs/2026-11-18-servicios-en-cotizaciones-design.md` |
| Menos consultas por request | En `main` (`52ab991f`, `f562db4f`). **Pendientes:** `FindActiveMembershipIdAsync` (1 consulta por request); membresía por usuario en `CustomersAuthorNames`/`CopilotMemberNames` (sin puerto por lote); caché de sesión del comprador. Mientras `Valkey__Areas__Permissions` siga apagada, un cambio de permisos tarda ≤ 60 s en los otros pods | [plan fase 2](superpowers/plans/2026-11-17-consultas-por-request-fase-2.md), [README § Verificación › Consultas por request](../README.md) |
| Soporte/PQRSF (fases 1–4b, tiempo real, PIN, documentos legales, variables, prioridad, tareas internas) | En `main` y desplegado (`5904b975` … `fc063d20`, tareas `6d7f0bd66`). **Por confirmar:** qué fases cubre el frontend de Soporte (hay pantallas en `qep-frontend`, p. ej. `5919454a`, `0a666e28`), incluida la página `/calificar` con `Referrer-Policy: no-referrer` y el portal (`qep-frontend-support`). **Falta:** DNS `soporte` en `lulocrm.com` hacia Vercel; PDF de respuesta formal real contra `qcode-pdf` (decisión larga, con y sin logo, anónimo, 10 anexos, `#`, `[`, `$`); V-1..V-3 contra Meta; dashboard y alertas de la cola `support_survey` en `migracion-k8s`; regla de borrado en `support/` de R2; texto del abogado y categoría de Meta de `qep_soporte_formal` y `qep_soporte_encuesta`; revisar avisos de `HtmlSanitizer`/`AngleSharp`. Sin definir: avisos internos de vencimiento (P-6) | [spec](superpowers/specs/2026-11-14-support-tickets-pqrs-design.md), planes `superpowers/plans/2026-11-1[4-6]-support-*`, [README § Soporte](../README.md) |
| Réplicas múltiples y Valkey | En producción las áreas `Presence`, `RateLimiting` y `Sessions` (desde 2026-11-25); `Permissions` en `main` sin despliegue registrado. Rama sin mergear `feature/replicas-dos`. **Antes de prender `Doorbells`:** decisiones del owner en README «Antes de prender `Doorbells`». **Tarea 8** (`replicas: 2`, PDB, `preStop`, `terminationGracePeriodSeconds: 45`) sólo cuando el frontend del hub sólo WebSocket + `skipNegotiation` (`e2d6155b`) esté desplegado y Valkey prendido con `Presence` y `RateLimiting`. `SendBudget` sin construir | [spec réplicas](superpowers/specs/2026-10-29-replicas-multiples-design.md), [spec Valkey](superpowers/specs/2026-11-13-valkey-estado-compartido-design.md), planes `superpowers/plans/2026-11-13-*` |
| Observabilidad y fluidez | En producción (`eeea44bd`). **Falta:** aplicar y publicar la tabla con todos los endpoints (`migracion-k8s`, rama `feature/qep-endpoints-table`, `a4c9539`) y medir en Grafana el efecto del arreglo de las vistas previas; la parte de errores de `QepHigh5xx` no se probó con 5xx reales | [spec](superpowers/specs/2026-10-30-observabilidad-fluidez-design.md), [plan](superpowers/plans/2026-10-30-observabilidad-fluidez.md) |
| Chatbots: reproducción de corridas | RC-B1 «Registro» en `main` (`906bd3af`). **Siguen** RC-B1b (antes, la regla de ciclo de vida de R2 sólo para `automations/http-responses/` a 3 días), RC-B2, RC-B3 y el frontend | [spec](superpowers/specs/2026-10-25-chatbot-reproduccion-de-corridas-design.md), [plan backend](superpowers/plans/2026-10-26-reproduccion-de-corridas-backend.md) |
| Automatizaciones: constructor de chatbots y flujo de Clasic | En `main` (backends `ff7f6956`, `a194e6ab`, plan 4 `94de87c0`; Clasic `159e864`). **Falta:** el runbook de Clasic del README (variable `CLASIC_BOT_API_KEY`, deploy de Clasic, conexión «API externa», importar `Fixtures/clasic.json` en el tenant Clasic, simular con `liveHttp` contra un cliente de prueba) y el corte del número. Después: lo que quede de los specs 2 y 3 (las claves de API ya existen, módulo `ApiAccess`) y la migración de lulo-chat (`docs/superpowers/specs/2026-10-21-migracion-lulo-chat-design.md`, 10 preguntas abiertas en §4), con el «arranca» del developer | [spec](superpowers/specs/2026-10-21-constructor-de-chatbots-design.md), [plan 4 Clasic](superpowers/plans/2026-10-22-constructor-de-chatbots-clasic-4.md) |
| Proveedores (módulo `Suppliers`) | En `main` (`95e0fbc9`). **Falta:** la fila «terceros como capability» en `qep-docs/docs/07-governance/open-decisions.md`, estado Diferida (spec §2, plan backend Task 9) | [spec](../../qep-frontend/docs/superpowers/specs/2026-10-23-proveedores-design.md) |
| Shopify: reemplazar la app «lulo crm» | Decisión del owner (2026-10-21): QEP reemplaza como backend a la app publicada «lulo crm» en el mismo dominio (`app.lulocrm.com`, `api.lulocrm.com`) cuando tenga la paridad con la v1, guías incluidas; QEP atiende como alias las rutas de webhooks de la v1. **Urgente aparte de QEP:** Lulo CRM v1 usa tokens que no expiran; si no se parchea antes del 2027-01-01, su integración con Shopify se cae ese día | Spec de onboarding §9.10 y §9.11 |
| Onboarding: «Primeros pasos» y bienvenida a invitados | **En pausa** (owner, 2026-10-21): spec incompleto, a retomar; ver su sección «Para retomar». Sin código. Módulo nuevo `Onboarding` y `/setup` en el frontend. Cierra OD-0106 si se aprueba | [spec](superpowers/specs/2026-10-21-onboarding-design.md) |
| Alertas de salud de la conexión Shopify | En cola; sin diseño. La campana de la que dependía ya está en `main` (`b7aaf8a73`) | memoria del proyecto |

Orden acordado con el owner: variantes → productos desde Shopify → campanita → alertas. Los tres primeros ya están en
`main`; queda el de alertas.

## Specs

| Spec | Estado | Notas |
| --- | --- | --- |
| 2026-11-22 idioma español de España y Latinoamérica (i18n) | Construido en los dos repos (backend `c99b11af7`; frontend con `es-ES`, `1bd3b489`) | `es-419`/`es-ES` por tenant y por persona. **Por confirmar:** que el diccionario `es-ES` del frontend coincida con el glosario del backend |
| 2026-11-17 etiquetas en la importación de clientes | Backend construido (`b814abcf`) | Falta el frontend (traducciones) |
| 2026-11-17 marcar cotización como enviada por fuera de QEP | Construido en los dos repos (backend `a004b612`, frontend `1724db70`) | Pruebas sin correr |
| 2026-11-16 alerta de UVT al convertir cotización en pedido | Construido en los dos repos (backend `e6829716`) | Integración por clase sin correr; falta navegador |
| 2026-11-16 tarifa de retención en el Excel de pedidos | Construido (`6235110b`; ReteICA en porcentaje desde `e919b10e`) | Sólo backend; pruebas sin correr |
| 2026-11-16 catálogo de permisos con entidad y acción (matriz de `/roles`) | Construido en los dos repos (backend `185d447b`, frontend `06ead2f5`) | Falta la prueba manual |
| 2026-11-16 roles personalizados en la pantalla de miembros | Construido en los dos repos (backend `4a24eb6d`, frontend `98e9a441`) | Pruebas sin confirmar; falta la prueba manual |
| 2026-11-15 roles asignables según los módulos del tenant | Construido en los dos repos (backend `0e95a591`, frontend `e2deee0e`) | Falta la prueba manual |
| 2026-11-15 foto en las líneas del pedido del comprador | Backend construido (`e3131d5d`) | `lines[].imageUrl` en `GET /buyer/orders/{id}` y al cancelar. Falta el storefront (spec propio) |
| 2026-11-15 enlace de pedidos de la tienda con la cuenta del comprador | Construido en los dos repos (backend `a4aa88c1`, frontend `c1fd7530`) | Pruebas de los dos repos sin correr; falta la prueba manual |
| 2026-11-14 Shopify: sincronizar ediciones del pedido | Construido en los dos repos (backend `11e3f26c`, frontend `e2e835be`) | Falta integración completa de Shopify y Quotations y V-11/V-12/V-13 |
| 2026-11-12 ReteICA por empresa | Construido en los dos repos (backend `d4e4c39e`) | Falta integración de seis proyectos (diferida por el owner) |
| 2026-10-29 agentes IA | Construido en los dos repos (backend `b73f6b4c`, `e51b5d06`, `6b94884b`) | Falta la integración de la rama y la prueba manual; copiloto apagado |
| 2026-11-17 reglas de empresa (cuenta opcional, IVA por empresa) | Construido en los dos repos (dentro de `ead8cc25`; frontend `98b8fb9c`) | Pruebas sin correr |
| 2026-11-17 productos desde Shopify | Construido (dentro de `ead8cc25`) | Pruebas sin correr; falta la prueba contra Shopify real |
| 2026-11-17 onboarding de la tienda de Shopify | Construido en los dos repos (`ead8cc25`; frontend `98b8fb9c`) | Pruebas sin correr |
| 2026-11-13 Shopify: líneas personalizadas | Construido en los dos repos (backend `d293a2b1`, frontend `8da175b8`) | Falta la prueba manual |
| 2026-11-12 Shopify: tokens que expiran | Construido (backend `b3b559b5`; texto §8 en el frontend, `41db11a2`) | Falta la prueba manual (§9), bloqueante antes del 2027-01-01 |
| 2026-11-10 impuestos por tipo y país | Construido en los dos repos (backend `8052ee2f`) | Enmienda del 2026-11-12: «pedido `Pending`» se lee «pedido editable» |
| 2026-11-10 estados del pedido | Construido en los tres repos (backend `0e85094d`) | Falta la prueba manual |
| 2026-11-09 pedidos del comprador | Construido en backend y storefront (backend `67bd435b`) | Seguimientos en «Defectos conocidos» |
| 2026-11-09 pedidos de la tienda directos | Construido en los tres repos (backend `1375946c`) | Seguimientos en «Defectos conocidos» |
| 2026-11-07 tienda pública: nombre de las líneas no disponibles en la cotización del carrito | Construido (`e24ecfb1`) | Campo aditivo `unavailable[].name` |
| 2026-11-05 tienda pública: `variantId` en el listado | Construido (`d9611601`) | Lo consume la grilla de `qep-storefront` |
| 2026-10-26 cuándo toma el bot la conversación | Aprobado 2026-11-03 (`27c8ae99`). Parte en horario construida (`0c50700a`); **cobertura fuera de horario pendiente**, sin plan ni código | Pausado por el owner. Siguiente paso: plan de implementación. Falta D-15 (slice aparte), D-5..D-11, D-14, D-16..D-16c, el CHECK y el frontend; el mapa de qué cambia está en §9 del spec. Cambiar `BotAttendance.HumanTookOver` arrastra «Iniciar» y «Continuar asistente» |
| 2026-10-29 cobros recurrentes (contratos de servicio) | Construido (backend `0165a99`, `cbaf506`; frontend `517bf22`, `893a795`) | El renombre compila y sus unitarias corrieron después (`c743770e5`: 202 en verde) |
| 2026-10-31 iniciar el bot desde el hilo | Construido (backend `5df69090`, frontend `1e0e0033`) | Falta la prueba manual. Decisión pendiente del owner: el aviso de 24 horas en conversaciones sin entrantes |
| 2026-10-29 WhatsApp: historial por coexistencia | Construido (backend `ab353bdb`, frontend `18627637`) | Decisiones del owner del 2026-10-30: aviso propio en el compositor, eco a número desconocido nace resuelto, el reporte de Mensajería excluye lo importado |
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
| 2026-10-13 embudo de ventas | Construido en los dos repos (módulo Pipeline; frontend `bdbb5963`) | La UI no usa todavía `DealDetailDto.orders`/`unlinkedCustomerOrders` ni `POST /deals/{id}/orders/{orderId}/link` |
| 2026-10-14 gestor de plantillas de WhatsApp | Construido (backend `fb1734ea`, frontend `30e7f3d`) | Pendientes del backend en [su plan](superpowers/plans/2026-10-15-gestor-de-plantillas-pendientes.md). Falta la prueba manual contra Meta real |
| 2026-10-14 mensajería Instagram | Construido en el backend | Sin verificar contra Meta real: V-4 y V-8 |
| 2026-10-14 mensajería: marcar no leída | Construido (`de85c573`) | |
| 2026-10-14 versión desplegada | Construido (`8d15b8d4`; `DeploymentInfo`, `VersionEndpoints`) | El spec aún dice «borrador» |
| 2026-10-15 integración Shopify (pedidos) | Construido en los dos repos | Falta prueba contra Shopify real |
| 2026-10-15 mensajería: motivo de resolución | Construido (`ResolutionReason`) | |
| 2026-10-15 pedidos sin cotización | Construido (backend `1dc604d1`, frontend `93c5336`) | 10 menores diferidos, entre ellos `UpdateParties` |
| 2026-10-16 mensajería: leída al responder | Construido (`79a58230`) | |
| 2026-10-17 flete | Construido (backend `07487ded`, frontend `5feba65`) | Pendientes del owner P1/P2 con valor por defecto en el spec |
| 2026-10-18 layout de documentos | Construido en los dos repos | El spec aún dice «borrador» |
| 2026-10-20 variantes de producto | Construido en los dos repos (frontend `22cde669`) | Decisiones del §11 del traspaso en «Decisiones del owner pendientes» |
| 2026-10-21 envío de cotizaciones por canal | Construido: planes 1 y 2 (Zenvia), plan 3 (`b2e8f509`, frontend `a2d28c17`) y pedido por canal (`3325745d`, frontend `c87fc63d`) | Spec en borrador para el owner. Falta la prueba manual con Meta real |
| 2026-10-21 Shopify: días de historial | Construido (2026-11-14) | Tope 30 días, desde `LinkedAt`; extiende D-S4. Falta la prueba manual |
| 2026-10-21 onboarding | Incompleto, en pausa; sin empezar | Metas por módulo; estado calculado en vivo, sin escribir en el GET; backfill cierra la guía de los tenants existentes |
| 2026-10-21 mensajería: asistente y horarios de atención (Automatizaciones, spec 1) | Construido (backend `62cb0aaf`, frontend `551c05d3`) | Falta la prueba manual contra el backend real |
| 2026-10-21 Automatizaciones: constructor de chatbots (spec 4) | Construido (backends `ff7f6956`, `a194e6ab`, plan 4 Clasic `94de87c0`; Clasic `159e864`; frontend `208e13f2`) | Pendiente en «Trabajo en curso» |
| 2026-10-21 migración de lulo-chat | Escrito, sin empezar | 10 preguntas abiertas en §4 |
| 2026-10-23 ventas POS en el reporte de pedidos | Construido en los dos repos (backend `206ed67f`, frontend `5b07d246`) | Falta la prueba manual |
| 2026-10-23 formato regional en PDF, correos y WhatsApp | Construido (`a7c559be`) | DP-1 lo cerró el envío del pedido por canal (`3325745d`). Si se revierte el despliegue y se vuelve a desplegar: `UPDATE quotations.quotation_pdfs SET number_format = NULL` antes |
| 2026-10-21 Automatizaciones: investigación y roadmap | Concepto; specs 2–3 y migración de lulo-chat sin escribir (el 4 está construido) | Orden: interactivos → claves de API y envío proactivo → constructor → migración. IA diferida |
| 2026-10-24 clientes duplicados | Construido en los dos repos (backend `f0b8dbe3`, frontend `68d3e6c1`) | Decisiones D-D1..D-D11 a ratificar |
| 2026-10-24 fusión de clientes | Construido en los dos repos (backend `ff8baabf`, frontend `bc0b4713`) | Decisiones D-F1..D-F15 a ratificar |
| 2026-10-23 proveedores (directorio) | Construido en los dos repos (backend `95e0fbc9`, frontend `f1cff1c3`) | Falta la prueba manual; «terceros» diferido (fila pendiente en `open-decisions.md` de `qep-docs`) |

Construido sin spec propio: enviar el pedido por WhatsApp (`POST /orders/{id}/send`; frontend `3e7e4c00`),
clasificación opcional del cliente, país del tenant, mensajería en tiempo real, filtros de la bandeja y filtro de ventas
del POS por número parcial y documento del cliente.

Los specs `2026-09-*` que el README cita se borraron en `8662d50c` («limpieza»); el historial de `git` los conserva.

## Pruebas manuales pendientes del owner

Todo lo de abajo ya está en `main`. Cada punto es una casilla: cuando el owner lo prueba, sale de la lista y queda en el
Registro.

**RRHH y nómina**

- [ ] **Cargo y lugar de trabajo** (`4a4d5eec6`, frontend `22f8e9fd`): editar y borrar registros de un contrato, también
  uno con PDF.
- [ ] **Recordatorio de cumpleaños** (`bc2502615`): cuando exista el frontend (tarjeta `4kvFw8GV`).
- [ ] **Vacaciones disfrutadas** (rama `feature/nomina-vacaciones`, frontend `57d183c7`): de punta a punta, y contrastar
  contra la exportación real de Siigo.
- [ ] **Exámenes médicos ocupacionales** (`cd927bc6c`, frontend `781be4ef`): en navegador.
- [ ] **Reajuste de precio de contratos** (`9e57559cc`): en navegador.
- [ ] **Eliminar un contrato de servicio** (`c743770e5`, frontend `abdcd566`): en navegador.

**Mayorista y compras**

- [ ] **Crear la cuenta sin código** (`34fbb20c5`, frontend `25e78cde`): invitar a un distribuidor con un correo propio,
  abrir el link del correo y crear la cuenta; no pide el código. Abrir el link de «Copiar» y comprobar que sí lo pide.
- [ ] **Datos del distribuidor y su empresa** (`eddcfadc8`, frontend `e93f966b`): invitar con ciudad y dirección,
  aceptar con tenant nuevo y revisar que la empresa exista en el tenant del distribuidor y el cliente, con teléfono, en
  el del mayorista.
- [ ] **Ocultar precios del mayorista** (`d8e2e7748`): en navegador.
- [ ] **Aviso por WhatsApp de la invitación** (`fcc939e1`, frontend `067a9fc4`): contra Meta real, con la plantilla
  `WholesaleInvitationNotice` aprobada en la conexión.
- [ ] **PDF de la orden de compra** (`e352beac`): generar uno real contra `qcode-pdf` (`purchase-order.typ` nunca se
  renderizó).

**Pedidos, cotizaciones, tienda e impuestos**

- [ ] **Regalo al crear y editar un pedido** (`230d8f6af`, frontend `6012dd74`): crear un pedido con un producto pagado y
  el mismo como regalo, editarlo cambiando la cantidad del regalo y quitándolo, y abrir el detalle (sólo insignia, sin
  «Agregar regalo»); un pedido mayorista no ofrece regalos.
- [ ] **Cotizaciones ocultables** (`376430dcf`, frontend `0b4a65c1`): apagar `quotations` y ver que Pedidos,
  transportadoras, contenido del PDF y canal de WhatsApp siguen abriendo.
- [ ] **Enlace de pedidos de la tienda con la cuenta del comprador** (backend `a4aa88c1`, frontend `c1fd7530`): con
  `PED-2026-0005` de la base local, iniciar sesión en la tienda con la cuenta del correo del pedido y verlo en «Mis
  pedidos»; comprar sin sesión con ese correo y ver que el pedido nuevo queda en la cuenta; en la consola, «Cambiar
  cuenta», «Quitar cuenta» y la entrada «Cuenta de la tienda» del historial.
- [ ] **Estados del pedido en cuatro ejes** (`0e85094d`, con frontend y storefront): marcar y desmarcar pagado,
  preparar y desmarcar, archivar y desarchivar, facturado; un comprobante deja el pago en «Pendiente de verificar»;
  filtros del listado; ajustes en `/orders-settings`; el comprador ve los ejes en la tienda.
- [ ] **Costo de producto y reporte de margen** (`e5212c5b`): cargar costo por variante con `admin`; sin
  `catalog.product_cost.read` no se ve; vender en el POS y marcar un pedido pagado; `/reports/margin` por moneda, con las
  líneas sin costo aparte.
- [ ] **Impuestos por tipo y país** (`8052ee2f`): crear tasas IVA, Retefuente y ReteICA con país; cargar la UVT del año
  en la consola de operador; cliente con retenciones; enviar una cotización sin empresa facturadora responde 422
  `quotation.billing.account_required`; un pedido editable cambia retenciones y empresa.
- [ ] **ReteICA por empresa y municipio** (`d4e4c39e`, `4ca73b63`): resolución por municipio de la operación, re-resolver
  al cambiar empresa, municipio o cliente, editar el porcentaje, «Sin retención», porcentaje en el PDF.
- [ ] **Retenciones por UVT** (`5447ba56`, con frontend): motivo y umbral de una tasa bajo la base mínima; aviso
  «desactualizada»; refresco al guardar un documento editable; un pedido facturado conserva la suya.
- [ ] **Fecha del pedido editable** (`e2e68e0f`, frontend `215fc37a`): en navegador.
- [ ] **Cuenta para el pago en el PDF** (`0a3a0218`, frontend `78d12926`): en navegador.
- [ ] **Tienda en modo catálogo** (`7d47fe06`, con storefront): apagar «Mostrar precios» en `/settings/store`; la tienda
  deja de mostrarlos (hasta ~6 min por la caché) y el pedido se valoriza igual.
- [ ] **Detalle y cancelación del pedido del comprador** (`ae8913cd`, `dd6ca631`, storefront): ver totales, facturación,
  envío y nota; cancelar uno sin comprobante.
- [ ] **Pedidos de la tienda directos** (`1375946c`): en navegador; el e2e del storefront no se corrió (necesita
  internet).
- [ ] **404 cacheable de la tienda** (`d363d026`): `curl.exe -i` a un host de tienda inexistente responde 404 con
  `Cache-Control: public, max-age=60`.
- [ ] **Borrar productos** (`a3e81ec1`): en navegador.
- [ ] **Empresas: país y tipo de documento** (`5534c81d`): empresa ES con NIF y Madrid, volver a CO, «Madrid, España» en
  la lista, «NIF …» en una cotización.
- [ ] **Categorías de producto y acceso rápido del POS** (`aaf63ca5`, `bd2637b13`): crear, renombrar, mover y borrar
  categorías; chips de acceso rápido en la caja.
- [ ] **Ventas POS en el reporte de pedidos** (`206ed67f`, frontend `5b07d246`): filtro «Origen», tarjeta «Pedidos por
  origen», «Consumidor final».
- [ ] **Escáner del POS con códigos de barras reales** (variantes).
- [ ] **Envío de cotización y pedido por canal** (`b2e8f509`, `3325745d`), con Meta real: sin canales, uno, varios,
  conectar en Integraciones y volver; crear `qep_pedido`, aprobarla y enviar un pedido con dos canales.

**Mensajería, clientes y chatbots**

- [ ] **Tarjetas de contacto compartidas** (`453bb3f9`): recibir por WhatsApp un contacto con varios teléfonos y correo;
  la burbuja muestra nombre, organización, teléfonos y correos.
- [ ] **Escribirle o agregar como cliente a un contacto compartido** (`bb408f7b`): un teléfono que ya es de un cliente
  muestra ese cliente; uno desconocido ofrece «Agregar como cliente»; «Escribirle» abre la conversación.
- [ ] **Búsqueda y filtro por teléfono en `/customers`** (`04764f39`, `381dcde1`): buscar por parte del teléfono, usar
  el filtro y exportar el Excel con el filtro puesto.
- [ ] **Contactos de empresa** (`014c2872`): vincular una persona a un cliente empresa; filtro de contactos; contactos en
  la ficha; Excel con la columna «Empresa»; cotización a un contacto; triage sobre una empresa con negocio abierto
  (422 `pipeline.triage.not_pending`).
- [ ] **Borrar clientes en lote** (`f34664ac`): uno con cotización queda en `blocked` y otro sólo con conversación se
  borra; el contacto borrado que vuelve a escribir se crea de nuevo. También el `curl.exe` del endpoint.
- [ ] **Clientes duplicados y fusión** (`f0b8dbe3`, `ff8baabf`): dueño del documento, «Anomalías», «Cambiar cliente…»
  en el hilo, fusionar desde la ficha y desde «Anomalías».
- [ ] **El bot suelta la conversación** (`d42e30e3`, sin frontend): tomar una conversación con una corrida del bot
  esperando y comprobar que la corrida termina `Interrupted`; borrar el chatbot responde 409
  `automations.chatbot.has_live_runs` sólo mientras atiende.
- [ ] **API externa: encabezado multimedia** (`a8681589`, contra Meta real): plantillas con encabezado de imagen, video y
  documento; uno que no coincide responde 422 `messaging.template.header_mismatch`; el hilo muestra el encabezado.
- [ ] **Indicador de escritura del asesor** (`2b5d66ac`, contra Meta real): incluida la duración de `typing_on` en
  Instagram.
- [ ] **Historial por coexistencia** (`ab353bdb`): V-1..V-6 contra Meta real.
- [ ] **Instagram:** V-4 y V-8 contra Meta real.
- [ ] **Gestor de plantillas** (`fb1734ea`) y **plantillas editables**: contra Meta real, incluida la ventana de edición.
- [ ] **Asistente y horarios de atención** (`62cb0aaf`), **«Continuar donde iba»** y **«Resueltas por chatbot»**:
  pausar el bot con una corrida esperando y usar «Continuar donde iba»; ver la tarjeta de resueltas en navegador.
- [ ] **Constructor de chatbots** (`a194e6ab`, frontend `208e13f2`): autoguardado, 412 con dos pestañas, publicar,
  simular, y la bandeja con un chatbot corriendo contra el backend real.
- [ ] **Mover una conexión entre chatbots** (`353e09bb`, frontend `0b27b1c6`): mover un número entre dos chatbots
  publicados, publicar un borrador sobre un número ocupado, las conversaciones en curso terminan con el chatbot anterior
  y el que pierde su último número muestra «Sin conexión».
- [ ] **Historial de versiones del chatbot** (`b850d1b7`, frontend `1bb2177d`): restaurar una versión como borrador.
- [ ] **Mensaje con multimedia en chatbots** (`05e2ee3fb`): verificar el frontend.
- [ ] **Bloque «Política de datos»** (`bdd8cbb4`, frontend `3dcf0269`).
- [ ] **Importar y exportar agentes IA**: exportar un agente con traspaso a un miembro y borrador de cotización,
  importarlo en otro tenant y ver que el editor pide elegirlos.
- [ ] **Reporte de uso de la plataforma** (`775de70c`): con sesión de QCode, y medir la consulta con datos reales.

**Soporte, campana y avisos**

- [ ] **Tareas internas** (`6d7f0bd66`): Andrés crea una tarea para Manuel; Manuel (sólo `support.task.use`) la ve en
  «Tareas», la resuelve y no ve ni un ticket de cliente.
- [ ] **Avisos de asignación** (`1f91890ad`): asignar una tarea y un ticket a un compañero con QEP abierto ⇒ campana, sin
  WhatsApp; que cierre QEP sin leer ⇒ WhatsApp o correo a los 30 min.
- [ ] **Campana y alertas en vivo** (`b7aaf8a73`): en Chrome con dos pestañas, una en segundo plano y otra enfocada;
  un mensaje entrante suena una sola vez y la notificación del navegador lleva al hilo.
- [ ] **Soporte/PQRSF fases 1 y 2** (`5904b975`, `5db3fd16`, `4e5d4606`): radicado `SOP-…`; vencimiento lunes a
  viernes sin festivos de Colombia; editar un ticket Resuelto responde 422 `support.ticket.not_editable`; ticket desde
  una conversación, fijar mensajes y «Seguir el hilo»; responder por WhatsApp con la ventana abierta, con
  `qep_soporte_respuesta` con la ventana cerrada y por correo.

**Shopify** (tienda de desarrollo real)

- [ ] **Tokens offline que expiran** (`b3b559b5`, bloqueante antes del 2027-01-01): §9 del spec
  `2026-11-12-shopify-tokens-que-expiran`; instalar, ver el refresco del worker y forzar `token_expired` con su texto en
  el frontend.
- [ ] **Fuente del documento del comprador** (`1f0c7a4b`, frontend `7d976a1c`): los selects listan las opciones; un
  pedido nuevo toma el documento de la elegida.
- [ ] **Líneas personalizadas** (`d293a2b1`): un pedido con una línea sin producto queda en espera con su título;
  «Asociar a un producto» lo reintenta; la sección de la tienda lista y borra la asociación.
- [ ] **Días de historial en «Sincronizar ahora»** (`f87c8d12`): poner N días, sincronizar y ver pedidos anteriores al
  enlace.
- [ ] **Sincronizar ediciones en dos sentidos** (`11e3f26c`, `6948130b`, frontend `e2e835be`): `Automatic` y `Manual`
  («Cambios por revisar»), conflicto con una corrección del asesor, corrección que llega a Shopify, envío retenido con la
  conexión inactiva, ciudad de otro departamento (422). V-11, V-12 (teléfono E.164 como eco, `note: null` borra la nota)
  y V-13.
- [ ] **Reglas de empresa, productos y onboarding** (`ead8cc25`): tienda con variantes sin SKU y `taxesIncluded=false`.
- [ ] **Entrega editable en pedidos de Shopify** (`d65658756`): que el eco de una dirección de España vuelva como se
  envió, si `orderUpdate` borra el código postal cuando no se envía, y los códigos de provincia de España contra la API.

**Sin pantallas todavía** (por API, con Scalar o `curl.exe`)

- [ ] **Agentes IA** (`2ff69228`, `b73f6b4c`): conexión `anthropic` y `openai` con su probador, `GET …/ai/models` y
  `…/ai/settings`, crear, publicar, pausar y reanudar un agente; que conteste en un canal; un chatbot con el bloque
  «Agente IA» le delega la conversación y la recupera; borrar una conexión que atiende un chatbot o un agente responde
  409 `integrations.connection.in_use_by_bot` (**por confirmar** que el frontend lo maneja).

## Pruebas automáticas sin correr

Trabajos que llegaron a `main` sin que corrieran sus pruebas (casi siempre por orden del owner). No hay reporte completo
posterior al del 2026-11-25 (`TestResults\report.md` del checkout principal, fuera de git). Corren uno a la vez.

- **Cotizaciones de proyecto** (`8603d6e9a`): integración completa de Quotations y Tenancy (`test-feature.ps1` quedó en
  verde sobre `67946f756`, antes del addendum).
- **Anulación de facturas de Siigo** (`70363e0d5`): `SiigoInvoiceBatchApiTests`, `SiigoInvoiceApiTests`,
  `SiigoInvoiceAnnulmentApiTests` completa y las de la última corrección de tandas.
- **Roles por tenant** (`5fdf11863`): `TenantRoleSeedingTests`, `TenantRoleSeedStoreTests`, `MembershipRoleUsageTests`,
  `RoleApiTests`, `MembershipApiTests`, `ArchitectureTests` y las integraciones de ServiceContracts, Support, Loans,
  Audit, Notifications, Wholesale, Ai y Hr.
- **Teléfono con indicativo** (`da98b8e20`): ninguna prueba ni `test-feature.ps1`.
- **Perfiles de facturación y entrega editable** (`d65658756`): unitarias de la rama y `.\scripts\test-feature.ps1 -Base 96463c08e`.
- **Reajuste de precio de contratos** (`9e57559cc`) y su seguimiento (`fe01048d7`): `.\scripts\test-feature.ps1 -Base 3906398e6`
  e integraciones de ServiceContracts, `InAppBillingTests`, `BillingEmailTemplatesTests` y `ArchitectureTests`.
- **RRHH: cargo y lugar** (`4a4d5eec6`): suite completa (aviso «Cambio transversal»). **Cumpleaños** (`bc2502615`): todas.
- **Número de documento numérico** (`cd5cbcabc`, `931c795d0`): suite completa (aviso «Cambio transversal»; 9 módulos
  sin integración en el nivel rápido). `test-feature.ps1` quedó en verde sobre `11a128f34`; las correcciones sólo
  corrieron unitarias de Customers y Quotations.
- **Código postal en la entrega** (`1dfaaf3c4`): integración, `test-feature.ps1` y revisión de código.
- **Regalo en el pedido** (`230d8f6af`): integración completa de Quotations (se colgó en
  `WhatsAppChannelApiTests.ChannelsForOrdersAreFilteredByDocumentAndReadableWithOrderManage`) y `ArchitectureTests`.
- **Avisos de asignación de Soporte** (`1f91890ad`): `SupportAssigneeEventsApiTests` (nunca corrió),
  `SupportTaskNoticeSettingsApiTests`, `SupportTaskNoticeReaderTests`, `TaskNoticeEscalationTests`,
  `InAppSupportTaskTests`, `InAppNotificationEndpointsTests`, `MembershipPhoneApiTests`, `HrMemberPhonesTests`,
  `TaskNoticeTemplateProvisioningApiTests`, `ValkeyAppMemberPresenceTests` y `ArchitectureTests`.
- **Tareas internas** (`6d7f0bd66`): `SupportLayerTests`, `SupportEndpointPoliciesTests`, `SupportTaskAccessApiTests`,
  `SupportInternalTasksBackfillTests`, integración completa de Soporte (sobre todo `AnInternalTaskIsNeverHealedNorAlerted`),
  Notifications y Tenancy.
- **Campana** (`b7aaf8a73`): el resto de `Messaging.IntegrationTests`; primeros candidatos a fallar: `InAppExportsTests`,
  `InAppNotificationEndpointsTests`, `InAppWholesaleTests`, `ConversationAssignedOutboxTests`,
  `SupportUnassignedAlertSettingsApiTests`, `UnassignedAlertWorkerTests`, `InAppRetentionTests`, `OverdueDigestEventTests`.
- **Agenda de citas** (2026-11-22): todas las de Scheduling, `ArchitectureTests`, Mensajería, Clientes (fusión y
  borrado), Pipeline (fusión), `Bootstrapper.UnitTests`, Tenancy y Authorization.
- **Agentes IA** (`b73f6b4c`): `CopilotThreadsHandlersTests`, `CopilotRetentionSweeperTests`, `CopilotAnswerTextTests`,
  `CopilotSettingsHandlersTests`, `ConnectionSlotsLockTests`, `BotDelegationAdaptersTests`; integración de Ai,
  Automations y Messaging, y filtrada `CustomerMergeAcrossModulesApiTests`, `CustomerDeletionAcrossModulesTests`,
  `StoreSettingsApiTests`, `SeedStartupTests`, `TenantModulesApiTests`, `OperatorConsoleApiTests`.
- **Shopify: reglas de empresa, productos y onboarding** (`ead8cc25`): Companies, Quotations, Store, Shopify, Pos,
  Catalog, Storage, Integrations, Bootstrapper y `ArchitectureTests`.
- **Shopify: sincronizar ediciones** (`11e3f26c`): integración completa de Shopify y Quotations.
- **ReteICA por empresa** (`d4e4c39e`): integración de Companies, Store, Shopify, Reporting, Pos y Quotations.
- **Marcar cotización como enviada** (`a004b612`): `MarkQuotationSentHandlerTests` y `QuotationMarkSentApiTests` (con
  `$env:Seed__Enabled = "false"`).
- **Monitoreo de webhooks** (2026-11-16): las pruebas nuevas (lista en el Registro).
- **Mayorista** fase 2 (`f61772db`, `76c473e0`), fase 3 (`50e8ca9a`; la ruta del savepoint por producto nunca corrió
  contra Postgres, está en `~Linked`), categorías (`485587383`), listas que ocultan (`9df675975`) y compras fase 1
  (`e352beac`): integración completa de Wholesale, Purchases, Suppliers, Tenancy, Identity, Notifications y Catalog.
- **Plantilla con imagen en el encabezado** (`785a42fc`, `3a0df4d2`): `TemplateHeaderImageApiTests`,
  `TemplateManagement*ApiTests`, `SendTemplateApiTests`, `CampaignDispatchTests`, `ExternalSendApiTests`,
  `MessageTemplatePersistenceTests`, `TemplateHeaderImageReleaseTests`, `SystemOwnedFilesApiTests`,
  `TemplateHeaderImageSweepTests`.
- **Abrir la conversación de un cliente** (`40b5d800`): las 7 de `OpenCustomerConversationApiTests` (sin RED),
  `ArchitectureTests`, integración de Messaging y la revisión fuerte de la rama.
- **Soporte** fases 2 a 4b, PIN, documentos legales y variables: ninguna corrida (filtros en cada plan; primero
  `CompositionRootTests` y el trío de CORS de `SupportPublicSurfaceApiTests`).
- **Réplicas y Valkey** (`a81b5747`): desde el Bloque B de optimizaciones, ninguna (lista priorizada en el README).
- **Chatbots: multimedia** (`05e2ee3fb`): integración completa de Storage, Automations y Messaging.
- **Sinónimos de ciudades** (`32ec08b5`, `e2a8f9a7`): las escritas desde SC-10.
- **Categorías: administración y acceso rápido del POS** (`bd2637b13`): unitarias, arquitectura e integración de Catalog,
  POS y Bootstrapper.
- **RRHH fase 1** (`bef130da7`): integración de Storage, Tenancy y Geography.
- **Retenciones por UVT** (`5447ba56`): integración completa de Quotations.
- **Catálogo** (`76fecea8`, `6fb4baf4`): integración completa de Catalog.
- **Clases de integración de contactos de empresa** (`014c2872`): Customers, POS, Reporting, Quotations, Messaging
  (campañas) y Automations.
- **Fallas sin comparar contra `develop`:** `ForwardedHeadersApiTests` (4, Tenancy; también en el reporte del
  2026-11-25) y las dos de `ArchitectureTests` (`ConfigurationExampleTests`, `MultiReplicaInventoryTests`).

## Al desplegar lo que está en `main` sin despliegue registrado

Pasos y orden para lo que entró a `main` después de `6f56c7265`.

- **Cotizaciones de proyecto (`8603d6e9a`):** backend primero; prender `projects` en la consola del operador sólo cuando
  no quede un pod viejo (`TenantModuleKey.Parse` lanza con una clave que no conoce); el frontend después.
- **Anulación de facturas de Siigo (`70363e0d5`):** migración aditiva `AddSiigoInvoiceAnnulled`; el botón del frontend
  sale **después** de terminar el rollout (un pod viejo que lea una fila `Annulled` responde 500), y devolver el backend
  con filas `Annulled` deja esas pantallas en 500. **Sin verificar contra Siigo real:** qué responde la consulta de una
  factura anulada o borrada (`InvoiceNotFoundCodes` vacía: borrar en Siigo todavía no libera el pedido).
- **Roles por tenant (`5fdf11863`):** migración aditiva `AddRoleSeeds` (Authorization); **si la siembra falla al
  arrancar, el arranque falla**; un tenant registrado en un pod viejo después de la siembra queda sin los cinco roles
  hasta el siguiente arranque (aceptado en el spec).
- **Teléfono con indicativo (`da98b8e20`, frontend `494cbeed4`):** backend primero, con la migración aditiva
  `20261125232432_AddOrdersExportLayoutPhoneDialCode`. **Antes de publicar el frontend**, revisar en `qep-storefront`
  cómo pinta el WhatsApp de la tienda, que ahora se guarda con indicativo.
- **Entrega editable con Shopify (`d65658756`, frontend `b6d3b55f`):** el backend con `d65658756` sale antes o junto con
  el editor de entrega del frontend: sin él, un cambio de entrega en un pedido de Shopify no se valida ni se manda a
  Shopify, y una dirección de fuera de Colombia rompe la sincronización. Migración aditiva
  `20261125183720_AddCityCorrectionPostalCode`; durante el rollout un pod anterior no puede leer una proyección con
  dirección extranjera.
- **Siigo, flete en las tandas (`6d0e93c63`, `555832473`):** el frontend (`e666c6cb`) sale antes o junto con el
  backend; sin los mensajes de `siigo.invoice.freight_pending`, `freight_not_created` y `freight_unconfirmed` la fila
  muestra un error genérico y alguien podría crear la factura otra vez en Siigo. Sin migraciones.
- **Pedidos sin factura de Siigo (seguimiento en producción):** el arreglo del `due_date` de los pagos (`f2734f280`) está
  sin desplegar; los rechazados por redondeo están por reintentar; la causa de `documents_service` sigue sin conocerse.
- **WMS** (`c7e03f03f` … `a5f7ce066`): migraciones aditivas `AddTaskBoardIndexes` y `AddTaskEventRecordedAt` (relleno de
  `recorded_at`).
- **RRHH: cumpleaños** (`bc2502615`): backend primero.
- **Número de documento numérico** (`cd5cbcabc`, `931c795d0`): sin migraciones. Desde el despliegue, un NIT/CC/CE guardado
  con letras impide volver a guardar ese registro (pedido, cotización, cliente, invitación de distribuidor) hasta corregirlo.
  Conviene contar antes en producción cuántos hay (sólo el conteo).
- **Valkey `Permissions`:** en `main` sin desplegar; se prende un área por despliegue (Registro 2026-11-22 a 2026-11-26).

## Después de desplegar

### Pasos en producción sin confirmar

Pasos que vienen después de un despliegue ya registrado; git no dice si se hicieron.

- **Por confirmar:** cargar la UVT de Colombia del año en curso (operador), tras impuestos por tipo (Registro
  2026-11-16).
- **Por confirmar:** invalidar los PDF guardados tras el contenido configurable del PDF
  (`UPDATE quotations.quotation_pdfs SET content_fingerprint = NULL WHERE generated_at < '<fin del rollout>'`) y
  comprobar que `origen-botanico` conserva sus textos (Registro 2026-11-16).
- **Por confirmar:** pasar las tiendas de Shopify que tenían `company` a `shipping_address.company` y reintentar los
  pedidos que quedaron incompletos (ver [su sección](#shopify-fuente-del-documento-del-comprador)).
- **Por confirmar:** correr una vez `POST …/operator/cities/backfill` (sinónimos de ciudades).
- **Por confirmar:** el `PUT` manual del README que elige el canal de WhatsApp del tenant de Zenvia (envío por canal,
  plan 3), y la conexión de Zenvia de Origen Botánico (README § Plantilla de WhatsApp › En producción); después, borrar
  `QUOTATIONS_WHATSAPP_*` de `Backend-prod` y comprobar que el Secret vivo ya no las tenga.
- **Por confirmar:** `DATABASE_LISTENER_CONNECTION_STRING` en `Backend-prod` (canal en vivo de Mensajería; la misma
  cadena de `DATABASE_CONNECTION_STRING` con destino `pg-platform-rw.database.svc.cluster.local:5432`;
  `k8s/prod-secret.yaml` ya la mapea a `ConnectionStrings__QepDatabaseListener`). Después: el log de arranque debe decir
  `using connection string key QepDatabaseListener` y los WebSocket del ingress deben dejar de ser sólo saludo y pings.
- **Por confirmar:** `SHOPIFY_APP_CLIENT_ID` / `SHOPIFY_APP_CLIENT_SECRET` en el grupo `Backend-prod`.
- **Por confirmar:** monitoreo de webhooks: aplicar `feat/qep-webhook-alerts` de `migracion-k8s`, crear un silencio de
  7 días para `QepWebhookDiscarding` y confirmar en Explore `qep_webhook_last_received_seconds` y
  `qep_webhook_discarded_total`.
- **Por confirmar:** crear y aprobar en Meta la plantilla `qep_tarea_asignada` en la conexión elegida en
  `/settings/support` (avisos de asignación; sin conexión sólo hay campana y correo).
- **Por confirmar:** si un pod viejo radicó tickets durante el rollout de tareas internas, reejecutar una vez el
  `UPDATE` de relleno de `created_by_member_id`.
- **Por confirmar:** mirar en producción si hay eventos `Resolved` del bot sin `chatbotId` (contarían como de asesor en
  «Resueltas por chatbot»).
- **Por confirmar:** historial por coexistencia: conexiones de Clasic y Transportes CI creadas, URL del webhook de la app
  de Meta a QEP, los tres campos suscritos, y sólo entonces conectar el número de QCode.

### Migraciones de contrato pendientes

Columnas que el código ya no usa y que se dejaron en la base a propósito (expand/contract) para que el binario viejo
siguiera funcionando durante el despliegue. Se quitan en una **release posterior**, en rama aparte, cuando ya no quede
ningún pod ni frontend viejo en producción. Nunca en la misma release que las dejó sin uso.

- [ ] **`wholesale.price_lists` → `DROP TABLE IF EXISTS`** (spec `2026-11-22-listas-de-precios-por-canal-design.md` D-8;
  registrada 2026-11-24). La migración `MovePriceListsToCatalog` sólo quitó la FK y el índice de
  `distributors.price_list_id`; la tabla quedó sin uso porque los pods viejos del rollout aún la leen. Sus filas no se
  copiaron a `catalog.price_lists`, por decisión del owner: se pierden con el drop.
- [ ] **Retención fija → retenciones por tipo** (spec `2026-11-10-impuestos-por-tipo-design.md` §11.5; registrada
  2026-11-16). `MigrateRetentionToWithholdings` sólo copió, no borró. Siguen como propiedades sombra con `DEFAULT`:
  - `customers.customers.with_retention` (`CustomersDbContext.cs:214`)
  - `quotations.quotations.customer_with_retention` y `.party_with_retention` (`QuotationsDbContext.cs:189-195`)
  - `quotations.orders.customer_with_retention` y `.party_with_retention` (`QuotationsDbContext.cs:686-690`)

  **Antes:** que el backend y el frontend de impuestos por tipo estén en producción (backend desplegado el 2026-11-16).
  **Al hacerla:** quitar las propiedades sombra, generar la migración con `DropColumn`, quitar las columnas de los
  `INSERT` crudos de `ExportLoadSeeder.cs` y retirar los campos derivados de la API (`withRetention`,
  `billingWithRetention`, `client.withRetention`) **junto con el frontend que los deje de leer**. `retention_amount`
  **no** entra: sigue en el modelo como la suma de las retenciones.
- [ ] **Estados del pedido: históricos de `OrderStatusAxes`** (decisión del owner pendiente). Quedaron en
  `quotations.orders`, sin uso: `approved_at`, `approved_by`, `legacy_status`, `legacy_payment_status`. El owner pidió
  conservarlos (2026-11-11, «sin pérdida»), y el `Down` de `OrderStatusAxes` los usa para revertir. Quitarlos va sólo si
  el owner lo decide, con respaldo de la base antes.

## Decisiones del owner pendientes

- Descuento sin IVA por línea (2026-11-23): (1) las cotizaciones y pedidos ya guardados conservan el descuento con IVA;
  editar una línea de uno viejo deja el encabezado sumando valores con y sin IVA: decidir si se migra el dato; (2) la
  columna de descuento del Excel del ERP (`OrdersExportProcessor`) ahora sale sin IVA: confirmar que la importación lo
  admite; (3) el «Total utilidad» del PDF (`quotation.typ`) baja ~19 % junto a columnas con IVA: rotularlo «(sin IVA)» o
  no; (4) el carrito del storefront muestra un «Descuento» sin IVA junto a precios con IVA: decidir si sigue al POS.
- Parámetros legales (consola del operador): borrar una `(clave, fecha)` que no existe responde 404 `key_unknown`;
  con el área de Valkey apagada y más de una réplica, los otros pods pueden servir el valor viejo hasta 60 s
  (`MemoryLegalParameterCache` es `PerPodByDesign`); una liquidación simultánea a la edición no queda bloqueada por la
  guarda de uso, y dos altas simultáneas de la misma fila dan 500 en vez de 409.
- Campana de notificaciones (abiertas del spec): retención (90 días propuesta), estados que cuentan como «sin asignar»
  (`New`, `Open`, `WaitingInternal`), preferencias por tipo de aviso y cuándo quitar `notify` del `MessagingHub`.
- Avisos de asignación de Soporte: quien tiene `advisorship.manage` ve el teléfono de RR. HH. del miembro sin permiso
  `hr.*`.
- Agentes IA: presupuesto del eval con modelos reales, quién ve la actividad del agente (P-10/F-1), texto «Iniciar
  asistente» (F-7), bloque en Instagram (OB-2) y avisar o no que contesta una IA.
- Mayorista, catálogo por categoría: una categoría ajena, borrada o no permitida responde 422
  `wholesale.category.not_visible`; la alternativa era una página vacía.
- Estados del pedido: si el listado conserva «Todos menos archivados» o pasa a botones.
- Contactos de empresa: las decisiones marcadas `PENDING OWNER` en el spec `2026-11-10-contactos-de-empresa-backend-design.md`.
- Clientes duplicados y fusión: ratificar D-D1..D-D11 y D-F1..D-F15.
- Iniciar el bot desde el hilo: el aviso de 24 horas en conversaciones sin entrantes.
- Envío por canal: la revisión del spec, que sigue en borrador; DP-3 (ejemplo `total` de la plantilla de pedido).
- Soporte/PQRSF: que el abogado confirme lo decidido el 2026-11-15 sobre petición incompleta (Ley 1755, art. 17),
  reclamación directa (15 días hábiles, Ley 1480) y retención (10 años desde el cierre y luego anonimizar); siguen
  abiertos los puntos 5–9 y 11 del §13 del spec.
- Detalle del pedido del comprador: confirmar las interpretaciones tomadas: las líneas sin IVA bajo el excedente de IVA,
  sin fila de descuento, y `billing` oculto cuando `billedTo=Customer`.
- Gestor de plantillas: qué errores de Meta pausan la conexión.
- Flete: P1 (retención sin el envío) y P2 (giro del recaudo), con valor por defecto en el spec.
- Variantes: las del §11 del [traspaso](superpowers/plans/2026-10-20-variantes-handoff.md) (columna «Variante» en la
  variante por defecto, código de barras de una variante inactiva, borrado con `CASCADE` del historial, carrera de
  borrado, búsqueda por código de barras, `deletedVariantIds`).
- Onboarding (§6 del [spec](superpowers/specs/2026-10-21-onboarding-design.md)): D-1 sembrar IVA 19 %, 5 % y Exento al
  registrar un tenant de CO; D-2 landing en `/setup` mientras falte la meta principal; D-4 número de WhatsApp de
  soporte; D-5 en qué lugar de la cola entra.
- Onboarding de Shopify (§9.8 del mismo spec): **S-8**, app embebida o no.
- Shopify, documento del comprador: migrar el dato de las tiendas con `company` o avisarles; si el select de «Otro…»
  aplica también al tipo de documento; si se importa `billing_address`.
- Tipo de documento por país: qué hacer con una parte de facturación en ES con persona jurídica (la regla de NIT choca
  con la tabla de ES).
- Cuentas de comprador: los datos legales de la política de datos, el plazo de conservación del consentimiento
  (`buyers.consents` hoy no se purga nunca), y la dirección y el nombre del remitente de los correos del comprador (hoy
  sale sólo `Notifications:Infobip:SenderEmail`, sin nombre visible). `BuyerDataPolicy.Version` ya es `1.0.0`.

## Hotfixes y decisiones que no se ven en el código

- Cancelar un pedido del comprador (2026-11-11, merge `dd6ca631`, decisión del owner): `CancelAsync` resuelve los nombres
  de ciudad y departamento **antes** de cancelar, en el primer intento y en el reintento por concurrencia. Si Geography
  falla, el pedido no se cancela y el error se propaga; un pedido que ya no se puede cancelar responde 409 sin consultar
  Geography.
- Foto de la línea del pedido del comprador (2026-11-15, merge `e3131d5d`, decisión del owner): es la foto **actual** del
  catálogo (variante o, sin ella, la principal del producto), no una copia del momento de la compra. Si Catalog falla, el
  detalle falla (no se disfraza de foto nula).
- País del tenant de distribuidores ya aceptados (owner, 2026-11-22): sin backfill. Los que aceptaron antes de
  `ce4c0ed0c` con tenant nuevo siguen en `CO` aunque `wholesale.distributors.country` diga otro; el tenant puede
  cambiarlo en Ajustes.
- Contratos de servicio (owner, 2026-11-19): eliminar un contrato es borrado duro siempre, también con cobros pagados; el
  historial contable se pierde.
- Avisos de asignación (aceptado «por ahora» por el owner): el WhatsApp de aviso abre una conversación en la bandeja; el
  número del miembro lo ve quien lee Mensajería y un bot puede contestarle.
- Migraciones que nunca se revierten: el `Down` de `AddCustomerDataConsents` borra evidencia legal (Ley 1581); el de
  `AddCustomerMerge` falla si ya hubo fusiones con el mismo documento; el de `AllowPricelessPurchaseOrderLines` falla si
  hay líneas sin costo.
- USD sin IVA (hotfix 2026-10-14): sin backfill; fila de IVA oculta en el frontend; solo COP discrimina IVA. La
  generalización a otras monedas no se le consultó al owner.
- ImageSharp exige licencia en Release: `main` ya tiene la supresión (hotfix 2026-10-14).
- Pago parcial con redondeo (hotfix 2026-10-19, `fe3d1fe1`): `Order.RecalculatePaymentStatus` compara contra el neto
  redondeado a lo que la moneda muestra; `OrderTests` cubre 116.529 (completo) y 116.528 (parcial).

## Defectos conocidos

- `ValkeyGatewayTests.TheConnectedGaugeFollowsTheConnection` se cuelga una hora en la suite completa de
  `Bootstrapper.UnitTests` y cae con `TimeoutException` en `ValkeyTestServer.StartAsync` (dos de dos corridas el
  2026-11-26, sobre `c4df3c93e`); sola pasa. Causa sin identificar.
- `ConfigurationExampleTests.EveryBoundConfigurationKeyIsDocumentedInTheExample` sigue en rojo (reporte del
  2026-11-25). **Por confirmar:** `SupportReplyParityTests.TheRejectionReasonsMatchMessaging` (rojo desde `ea9cbba0`) ya
  no aparece entre las fallas de ese reporte.
- **Por confirmar:** las 17 pruebas que fallaban en `origin/develop` `a64a74d4` (2026-10-31: Catalog, Customers.UnitTests,
  Geography, Identity, Quotations) ya no aparecen en el reporte del 2026-11-25, salvo quizá
  `EditingTheBillingPartySavesItWithPersonTypeAndRecalculates` (Quotations.IntegrationTests tuvo 7 fallas sin nombrar aquí).
- Los harness de integración repiten dos problemas que se arreglaron en Messaging (`f7315f07`): exportan OpenTelemetry a
  `localhost:4317` y `RateLimitingMiddleware` no libera su `PartitionedRateLimiter` (Customers también se degrada).
  Seguimiento decidido por el owner: OTLP sólo con endpoint configurado y `RateLimiterRelease` en un helper compartido.
- Un registro de tenant que falla después de aprovisionar deja el usuario en `identity.users` sin membresía
  (`RegistrationEndpoints.cs:89-104`).
- Los harness de integración no fijan `Seed:Enabled`: con `Seed:Enabled=true` en los user-secrets del developer caen
  con `Seed:OwnerEmail is required`. Mientras tanto, se corren con `Seed__Enabled=false`.
- `dotnet format`: `WHITESPACE` en `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ReplyMarksReadApiTests.cs`,
  líneas 139 y 208 (comprobado el 2026-10-21).
- Datos del distribuidor al aceptar: la marca `acceptance_started_at` no se borra (un fallo posterior bloquea el documento
  hasta borrar y reinvitar), borrar y reinvitar con otro documento puede duplicar, y los vínculos a medias anteriores a
  `AddDistributorAcceptanceStartedAt` no se detectan hasta reintentarse.
- Mayorista, fase 2: dos aceptaciones simultáneas pueden chocar en el paso del proveedor (`TwoConcurrentAccepts`).
- Listas de precios que ocultan: el conteo del resumen no descuenta destinos borrados; `Total` cuenta productos sin precio
  en la moneda.
- Ocultar precios del mayorista: un borrador guardado con la lista visible conserva su costo hasta que se vuelva a guardar.
- Plantilla con imagen en el encabezado: posible doble auditoría `storage.file.deleted` si el outbox y el barrido liberan
  el mismo archivo; falta un índice para la consulta diaria del barrido.
- Retenciones por UVT: con el documento cerrado, `applies`/`reason` puede contradecir el `amount` guardado si la regla
  cambió después (gana la fila guardada); falta la prueba del refresco en la vista previa del pedido; una cotización de
  diciembre convertida en pedido en enero nace con la UVT desactualizada y su primer guardado la pasa al año nuevo
  (cambian los montos); «Nuevo pedido» con «Se factura a otros datos» no deja elegir retenciones (spec propio, cruza los
  dos repos).
- Costo de producto: un costo ≥ 1e17 responde 500 (igual que el precio).
- Acceso rápido del POS (riesgo aceptado): dos reemplazos simultáneos de la lista pueden dejar posiciones repetidas.
- Borrar productos: carreras de milisegundos (línea agregada a un borrador durante el DELETE; galería que re-agrega la
  foto entre la sonda y la purga), deadlock en el propio `SaveChanges` de Catalog sigue en 500, `CorrelationId` del
  evento no se liga al request.
- Contratos de servicio: el worker de recordatorios podría insertar un cobro entre la lectura y el commit de un borrado y
  dejarlo sin contrato; los archivos nuevos de esa rama quedaron en LF y el repo usa CRLF.
- Reporte de uso: la presencia es por proceso; con dos réplicas el listado de conectados deja de ser correcto.
- Foto en las líneas del pedido del comprador, menores de la revisión final:
  - La prueba de «una sola consulta con variantes distintas» usa dos variantes diferentes: no prueba la deduplicación.
  - Ninguna prueba afirma las fotos de la respuesta de cancelar tras un reintento por concurrencia.
  - Ninguna prueba de integración con un producto que tenga foto (sólo `imageUrl` null).
- Pedidos del comprador, seguimientos sin cerrar:
  - Dos clases se llaman `BuyerOrderErrors` (`Modules.Buyers.Application` y `Modules.Quotations.Domain`).
  - Las pruebas unitarias de los handlers Get/Cancel no afirman que tenant y cuenta llegan al puerto.
  - No hay prueba con base que `LinkAsync` con otro `tenantId` no enlace; lo garantiza el predicado del repositorio.
  - Cancelar el worker en medio de `LinkOrderAsync` deja el pedido sin enlazar y sin `Warning`.
- Pedidos de la tienda directos, seguimientos sin cerrar:
  - Reintento del checkout por las rutas de respaldo (`SubmitOrderRequest.cs`): si el pedido falla después de crear el
    cliente incompleto y el visitante reintenta con el mismo `clientRequestId`, se crea otro cliente incompleto huérfano
    por intento. Contradice R1 del spec.
  - Dos checkouts simultáneos sobre el mismo cliente (`StoreCustomerDirectory.cs`) pueden dar `412 concurrency.conflict`
    al visitante anónimo; el reintento pasa.
  - Todo rechazo del motor de pedidos sale como `line_unavailable` (`StoreOrdersAdapter.cs`).
  - `advisorName` en `GET /settings/store` también resuelve a un asesor suspendido o retirado
    (`StoreSettingsComposer.cs`).
  - D3: un visitante anónimo que conozca el documento de otra persona puede agregarle direcciones a ese cliente (nunca
    sobreescribe). Decisión del owner.
- Cuentas de comprador, seguimientos sin cerrar:
  - El correo de «cuenta eliminada» que agota `MaxAttempts` no anonimiza: las filas `buyers.*` de esa cuenta en
    `notifications.notifications` quedan con `recipient_id` y `recipient_address`.
  - El `failure_reason` del proveedor puede repetir la dirección del destinatario, y se guarda tal cual aunque la fila se
    anonimice.
  - `412 concurrency.conflict` no está en la tabla del contrato del comprador (README), y en `register` y
    `password/forgot` rompe la respuesta `202` anti-enumeración: un 412 revela que la cuenta existe.
  - Queda una ventana de milisegundos entre un login y un restablecer contraseña concurrentes.
  - `DeleteBuyerAccount` no reintenta ante un conflicto de concurrencia.

## Shopify: fuente del documento del comprador

La clave de «Atributo del documento» es una **ruta exacta** (decisión del owner): `shipping_address.company` lee el campo
«Empresa» de la dirección de envío; cualquier otra es un atributo del checkout. Si no existe en el pedido, no hace nada.
Backend `b7447106` y `1f0c7a4b`, frontend `7d976a1c`.

**Falta.** La prueba manual contra una tienda real.

**Por confirmar (después de desplegar).** Las tiendas que tenían `company` hay que pasarlas a `shipping_address.company`
(el `company` a secas ya no lee la dirección) y reintentar los pedidos que quedaron incompletos. Pendiente de decidir si
se migra el dato o se avisa.

**Fuera de este trabajo, sin decidir.** `billing_address` no se importa y el pedido siempre manda `Billing: null` (D-S7 y
D-S15). Propuesta sin respuesta: llenar la parte `Billing` cuando la dirección de facturación esté completa; cambiaría
D-S7 y D-S15.

## Registro

- 2026-11-27 — **Decisiones del owner** sobre las tarjetas de Siigo y del país de «Facturar a». (1) Tarjeta `OtqRAr9D`: aprueba
  el 409 `siigo.connection.account_changed` con `mismatches: [{kind, id}]` en vez de `label`, y que una forma de pago
  desactivada en la misma cuenta siga contando como diferencia. (2) Tarjeta `Mp4DWqN6`: el país de «Facturar a» deja de
  asumirse como Colombia y pasa a ser obligatorio, en la pantalla y en el backend (422). La tienda, Shopify y la API externa no
  fallan: el pedido entra sin país y queda por asignar. Lo ya guardado sin país no se migra: al editarlo hay que elegirlo, y la
  conversión copia el vacío. Mientras falte el país, sólo se bloquea la factura en Siigo (faltante en la tanda). **Sin construir:**
  tarjetas `PGAmC4DJ` (backend, «Por hacer») y `QolgF4Ma` (frontend). Hasta entonces rige lo mergeado (vacío = Colombia).
El registro de merges, decisiones y despliegues vive aparte, un archivo por mes: [`estado/registro-<AAAA-MM>.md`](estado/).
La entrada nueva va arriba del archivo del mes en curso; si el archivo no existe, se crea con el mismo encabezado que los demás.
Este archivo sólo dice cómo están las cosas hoy.
