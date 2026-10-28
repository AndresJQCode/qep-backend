# Variantes de producto — estado del proyecto

Archivo vivo: se actualiza al cerrar cada tarea o decisión. Las tareas detalladas y sus casillas están en el
[plan](2026-10-20-variantes-de-producto-backend.md); el porqué, en el [diseño](../specs/2026-10-20-variantes-de-producto-design.md).

## Estado general

| Fase | Estado | Referencia |
| --- | --- | --- |
| Diseño | Aprobado por el owner (2026-10-20), en `develop` local, sin push | `ea383771` |
| Plan backend | Escrito, en `develop` local, sin push; **ejecución: subagentes, en sesión nueva** | `e0d7756f` |
| Implementación backend | Hecha y mergeada a `develop` con ff-only (2026-10-21). Pruebas **ejecutadas y en verde** el 2026-10-25 (ver Registro) | `feature/variantes-producto`, desde `95dbdd0c` |
| Revisión independiente | Hecha (2026-10-21), sin Críticos; arreglos en `fix/variantes-revision-fable`, mergeados a `develop` con ff-only. Sus pruebas de integración pasan desde el 2026-10-25 | prompt en [`2026-10-20-variantes-revision-fable.md`](2026-10-20-variantes-revision-fable.md) |
| Plan y trabajo de frontend | Construido (VAR-01..13, cierre `22cde669`, en `origin/develop` del frontend); el contrato está en el [traspaso](2026-10-20-variantes-handoff.md) (sale en la misma ventana que el backend) | — |

## Tareas del plan (backend)

| # | Tarea | Estado |
| --- | --- | --- |
| 1 | Biblioteca de opciones (dominio) | Hecha (`e0506006`) |
| 2 | Variante, galería y reglas del agregado | Hecha (`31fb3098`, `04b26bfc`) |
| 3 | Persistencia y migración de datos de Catalog | Hecha (`ba326a59`) |
| 4 | Biblioteca de opciones: casos de uso y API | Hecha (`13d50f02`), pruebas escritas y sin ejecutar |
| 5 | Producto con variantes: crear, ver, listar, editar | Hecha (`852cd6c5`, `3f9497b9`), pruebas sin ejecutar |
| 6 | Borrar o desactivar variantes (puerto de uso) | Hecha (`5bd62b58`, `72e91421`), pruebas sin ejecutar |
| 7 | Historial de precios y copia de escalas por variante | Hecha (`f870b495`, `09072af1`), pruebas sin ejecutar |
| 8 | Excel de productos por variante | Hecha (`67f7bb6d`), pruebas sin ejecutar |
| 9 | Adaptadores hacia Quotations, Pos y Shopify | Hecha (`34944393`, conjunto T9–T12), pruebas sin ejecutar |
| 10 | Quotations: líneas por variante | Hecha (`34944393`), pruebas sin ejecutar |
| 11 | Pedidos de Shopify por variante | Hecha (`34944393`), pruebas sin ejecutar |
| 12 | Punto de venta por variante | Hecha (`34944393`), pruebas sin ejecutar |
| 13 | Reporte de cambios de precio | Hecha (`6bfc3ce6`, `3f07515c`), pruebas sin ejecutar |
| 14 | PDF y Excel de pedidos | Hecha (`62cbbf73`), pruebas sin ejecutar |
| 14b | Variantes vendibles en el listado para el selector (agregada, spec §6) | Hecha (`ca1146e1`), pruebas sin ejecutar |
| 15 | Siembras, documentación y traspaso al frontend | Hecha (`37045a68`, `27d037b5`), pruebas sin ejecutar |

Estados: Pendiente · En curso · Hecha (con SHA del commit) · Bloqueada (con motivo).

## Decisiones del owner

- 2026-10-20: decisiones 1, 2, 4 y 7 de §9 aprobadas tal cual.
- 2026-10-20: tope de **2.048** variantes por producto (el diseño decía 100).
- 2026-10-20: **galería** de hasta 250 fotos por producto; cada variante elige una (el diseño decía sin galería).
- 2026-10-20: una variante **se borra si no está en uso**, y si está en uso se desactiva (el diseño decía que nunca se borra).
- Reporte de ventas por producto/variante: fuera de este proyecto.

## Pendientes y riesgos abiertos

- **Revisión independiente (2026-10-21).** Arreglado: `OrderTests` indexaba la foto por producto (la única prueba
  unitaria roja); las pruebas de escalas por variante no detectaban la clave por producto (nuevas en las dos pasadas
  de recálculo y de punta a punta); `pricing: null` rechazaba escalas copiadas sin completar y una variante en uso
  con ellas no se podía desactivar por `deletedVariantIds`; el `Down` de Catalog devolvía una variante inactiva y su
  prueba no ejercitaba más de una variante por producto. Corrida la unitaria de Catalog, Quotations, Pos, Reporting,
  Shopify, Bootstrapper (sin el checker) y ArchitectureTests: todas verdes. Decididos por el owner el mismo día y hechos:
  el escáner prefiere un `Code` activo a un código de barras inactivo, y el listado muestra el `code` de la única
  activa de un producto sin opciones. El desempeño de `ShopifyProductLookup` y de la cuadrícula del POS (traen
  agregados completos) queda como requisito del proyecto 2, productos desde Shopify.
- **Correr las pruebas.** Hecho el 2026-10-25: los 14 proyectos en verde (ver Registro). Las 18 consultas LINQ de riesgo
  se habían verificado antes con `ToQueryString` y traducen.
- **Despliegue destructivo.** `AddProductVariants` borra columnas de `catalog.products`: backup, ventana de corte y orden
  de vuelta atrás en el [traspaso §13](2026-10-20-variantes-handoff.md). Backend y frontend salen juntos.
- **Decisiones que el owner debe confirmar** (detalle en el traspaso §11): columna «Variante» con nombre o vacía en la
  variante por defecto; borrar una variante borra su
  historial (CASCADE); carrera de borrado aceptada (`variant_id` huérfano); la búsqueda del listado también encuentra
  por código de barras; borrar es explícito con `deletedVariantIds`.
- Plan del frontend: no existe; sale del traspaso.
- Antes de desplegar: probar el escáner del POS con códigos de barras reales.
- Proyectos siguientes (orden acordado): 2 productos desde Shopify → 3 campanita → 4 alertas de la conexión Shopify.

## Registro

- 2026-10-20 — Diseño aprobado y mergeado a `develop`; plan escrito.
- 2026-10-20 — El owner elige ejecutar por subagentes en una sesión nueva.
- 2026-10-21 — Implementación por subagentes: tareas 1–15 y la 14b agregada (selector, spec §6); T9–T12 en un commit
  (`34944393`); revisión final con dos arreglos importantes (integrar `develop`, despliegue). Mergeada a `develop` local.
- 2026-10-25 — Pruebas corridas sobre `develop` `3472f01d`, con `Seed__Enabled=false`: build 0/0 y 3.647/3.647 en verde.
  Catalog 213 + 187, Quotations 1290 + 496, Pos 226 + 61, Reporting 116 + 101, Shopify 361 + 81, Identity 22 + 36
  (unitarias + integración), `Bootstrapper.UnitTests` 325, `ArchitectureTests` 132. Sin cambios de código.
