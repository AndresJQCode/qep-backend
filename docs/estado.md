# Estado del proyecto `qep-backend`

Archivo vivo: se actualiza al cerrar un trabajo, tomar una decisión o desplegar. **No verificado contra el código al
momento de escribirlo** (2026-10-20): sale del historial de trabajo y de los specs. Lo marcado «Por verificar» no se
comprobó; ante cualquier duda gana el código ([CLAUDE.md](../CLAUDE.md)). La línea `Estado:` de cada spec dice si el
diseño se aprobó, no si se construyó.

## Cómo se lee

Estados: **Construido** (mergeado a `develop`) · **En curso** · **Aprobado, sin empezar** · **Por verificar**.
«Push» dice si el trabajo ya llegó a `origin`; donde no lo sé, lo digo.

## Trabajo en curso

| Trabajo | Estado | Dónde |
| --- | --- | --- |
| Variantes de producto | Diseño aprobado y plan escrito; implementación sin empezar | [estado propio](superpowers/plans/2026-10-20-variantes-estado.md) |
| Productos desde Shopify | En cola (después de variantes); sin diseño | memoria del proyecto |
| Centro de notificaciones (campanita) | En cola (después del anterior); sin diseño | memoria del proyecto |
| Alertas de salud de la conexión Shopify | En cola (depende de la campanita); sin diseño | memoria del proyecto |

Orden acordado con el owner: variantes → productos desde Shopify → campanita → alertas.

## Specs

| Spec | Estado | Notas |
| --- | --- | --- |
| 2026-10-07 módulo POS | Construido (Pos existe con sus pruebas de capas) | El spec aún dice «borrador» |
| 2026-10-07 módulos por tenant | Por verificar | |
| 2026-10-07 WhatsApp por tenant | Por verificar | |
| 2026-10-08 consola de operador | Por verificar | El spec no tiene línea de estado |
| 2026-10-08 integraciones | Construido (módulo Integrations) | |
| 2026-10-09 mensajería WhatsApp | Construido (módulo Messaging) | |
| 2026-10-10 mensajería: asignación y BSUID | Por verificar | La clave de conversación es el BSUID |
| 2026-10-11 contactos, plantillas y campañas | Por verificar | |
| 2026-10-11 notas de voz del asesor | Por verificar | |
| 2026-10-12 respuestas rápidas | Por verificar | |
| 2026-10-13 embudo de ventas | Construido (módulo Pipeline) | |
| 2026-10-14 gestor de plantillas de WhatsApp | Construido, mergeado a `develop` (`fb1734ea`) | Pendientes en [su plan](superpowers/plans/2026-10-15-gestor-de-plantillas-pendientes.md) |
| 2026-10-14 mensajería Instagram | Construido en el backend | Sin verificar contra Meta real: V-4 y V-8 |
| 2026-10-14 mensajería: marcar no leída | Por verificar | |
| 2026-10-14 versión desplegada | Por verificar | |
| 2026-10-15 integración Shopify (pedidos) | Construido; backend y frontend en `develop` local | Falta prueba contra Shopify real antes de `main` |
| 2026-10-15 mensajería: motivo de resolución | Por verificar | |
| 2026-10-15 pedidos sin cotización | Construido; backend (`1dc604d1`) y frontend (`93c5336`) | 10 menores diferidos, entre ellos `UpdateParties` |
| 2026-10-16 mensajería: leída al responder | Por verificar | Hay plan de backend |
| 2026-10-17 flete | Construido; backend (`07487ded`) y frontend (`5feba65`) en `develop` local | Pendientes del owner P1/P2 con valor por defecto en el spec |
| 2026-10-18 layout de documentos | Construido; backend y frontend en `develop` local | |
| 2026-10-20 variantes de producto | Aprobado, sin empezar | Ver «Trabajo en curso» |

Los specs `2026-09-*` que el README cita no están en la carpeta (p. ej. `README.md:555` y `:1622`).

## Antes de llevar `develop` a `main` (lo hace el owner)

- **Push pendiente.** Varios trabajos están en `develop` local de ambos repos «sin push»; confirmar con `git log
  origin/develop..develop` en cada repo. Backend primero, frontend después (el frontend de Shopify usa
  `cancelledAfterInvoice`, que debe llegar antes o a la vez).
- **Shopify:** `develop` no va a `main` sin el frontend de Shopify (`CLAUDE.md`, 2026-10-18). Falta la verificación
  manual contra una app y tienda de Shopify reales, y `SHOPIFY_APP_CLIENT_ID` / `SHOPIFY_APP_CLIENT_SECRET` en el grupo
  `Backend-prod`.
- **Variantes:** backend y frontend salen en la misma ventana (decisión del owner, 2026-10-20).
- **Pos:** antes de desplegar, comprobar que ningún tenant tenga un rol custom con la clave `cashier`.
- **`dotnet format`:** roto en `ReplyMarksReadApiTests.cs`.
- **Meta/Instagram:** V-4 y V-8 sin verificar contra Meta real.

## Decisiones del owner pendientes

- Gestor de plantillas: qué errores de Meta pausan la conexión.
- Flete: P1 (retención sin el envío) y P2 (giro del recaudo), con valor por defecto en el spec.
- Shopify: si se saca `shopify` de `DefaultForNewTenants` hasta que exista el frontend (alternativa a esperar).

## Hotfixes y decisiones que no se ven en el código

- USD sin IVA (hotfix 2026-10-14): sin backfill; fila de IVA oculta en el frontend; solo COP discrimina IVA. La
  generalización a otras monedas no se le consultó al owner.
- ImageSharp exige licencia en Release: `main` ya tiene la supresión (hotfix 2026-10-14).

## Defectos conocidos

- Un registro de tenant que falla después de aprovisionar deja el usuario en `identity.users` sin membresía
  (`RegistrationEndpoints.cs:89-104`).

## Registro

- 2026-10-20 — Se crea este archivo. Diseño y plan de variantes en `develop` local.
