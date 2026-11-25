# Pruebas en dos niveles: plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `scripts/test-feature.ps1` corre en minutos el nivel rápido de lo que cambió la rama; la corrida completa dice sobre qué commit corrió.

**Architecture:** La selección es una función pura en `scripts/TestSelection.psm1` (archivos cambiados → pasos y avisos). Los dos scripts sólo hacen efectos (git, `dotnet`, `.trx`) y comparten el módulo.

**Tech Stack:** Windows PowerShell 5.1, git, `dotnet test` (xunit.v3 por VSTest). Sin dependencias nuevas.

**Spec:** `docs/superpowers/specs/2026-11-24-pruebas-en-dos-niveles-design.md`

## Global Constraints

- Windows PowerShell 5.1: sin `&&`, `?:` ni `??`.
- Todo `.ps1`/`.psm1` se guarda en UTF-8 **con BOM** (sin él, 5.1 rompe las tildes).
- Sin Pester ni módulos externos.
- Nunca detener procesos ajenos.
- El nivel rápido nunca sale en 0 si algo falló, un proyecto no dejó `.trx` o un filtro no ejecutó pruebas.
- Mensajes en español colombiano, tuteando.
- Commits convencionales, sin atribución de IA.
- No se tocan los harness, `azure-pipelines.yml` ni `docs/estado.md` (este último sólo al mergear, en `develop`).

## Review Focus

- Archivo borrado o renombrado: no revienta y su clase no entra al filtro (Task 1).
- Rutas con `\` o `/`: seleccionan lo mismo (Task 1).
- Archivo `partial` sin `[Fact]` propio cambiado: entra su clase (Task 1).
- Filtro que no ejecuta ninguna prueba: la corrida falla (Task 2).
- `-Base` que no existe: error, no un plan vacío en verde (Task 2).

---

### Task 0: Traer la corrida completa a la rama

`scripts/run-all-tests.ps1` y sus 26 líneas en `README.md` están **sin commitear** en el checkout principal. Se copian; el checkout principal no se modifica.

**Files:**
- Create: `scripts/run-all-tests.ps1` (copia byte a byte)
- Modify: `README.md` (aplicar el diff)

- [ ] **Step 1: Copiar el script y el diff**

Desde el worktree, con `$main = "..\..\qep-backend"`:

```powershell
New-Item -ItemType Directory -Force scripts | Out-Null
Copy-Item "$main\scripts\run-all-tests.ps1" scripts\
git -C $main diff --output="$env:TEMP\readme-tests.patch" -- README.md
git apply "$env:TEMP\readme-tests.patch"
```

- [ ] **Step 2: Verificar**

Run: `git status --short`
Expected: sólo `README.md` modificado y `scripts/` nuevo. `git diff README.md` habla sólo de pruebas; si trae otra cosa, para y avisa.

- [ ] **Step 3: Commit** — `chore(tests): add the full test run script`

---

### Task 1: Selección de pruebas

**Files:**
- Create: `scripts/TestSelection.psm1`
- Test: `scripts/tests/TestSelection.Tests.ps1`

**Interfaces:**
- Produces: `Get-TestPlan -ChangedFiles <string[]> -Projects <string[]> -TestSources <hashtable>` → objeto con
  - `Steps`: `@([pscustomobject]@{ Project = <ruta .csproj relativa con '/'>; Filter = <string, '' = proyecto completo> })`, en orden: ArchitectureTests, unitarias, Bootstrapper, integración.
  - `Warnings`: `[string[]]`.
  - `TestSources`: ruta relativa → contenido de los `.cs` existentes bajo `*.IntegrationTests/`.

Reglas:

| Entrada | Efecto |
| --- | --- |
| Siempre | ArchitectureTests completo |
| `src/Modules/<M>/` o `tests/Modules/<M>/` | `Modules.<M>.UnitTests`, si está en `-Projects` |
| `src/Bootstrapper/`, `src/Api/`, `tests/Bootstrapper/` | `Bootstrapper.UnitTests` |
| `.cs` de integración con clase de prueba | filtro `FullyQualifiedName~.<Clase>.` unido con `\|`, un paso por proyecto |
| Clase `abstract` cambiada | sus derivadas, transitivas |
| Archivo de integración existente que no resuelve a una clase de prueba (harness, `.json`, `.csproj`) | aviso `Apoyo de pruebas cambiado, sus clases no se infieren: <ruta>` |
| Producción de `<M>` cambiada y sin paso de integración de `<M>` | aviso `<M>: cambió producción y no corre ninguna prueba de integración` |
| `src/BuildingBlocks/`, `src/Bootstrapper/`, `src/Api/`, `Directory.*.props`, o `/Migrations/` en más de un módulo | un solo aviso `Cambio transversal: conviene la corrida completa antes de seguir` |

Clase de prueba: declaración pública o interna que empieza en la columna 0 y cuyo nombre tiene `[Fact` o `[Theory` en alguno de sus archivos, o lo hereda. La base se lee de `class X<…>(…) : Base(…)`, aunque el `:` esté en otra línea.

- [ ] **Step 1: Escribir las pruebas**

```powershell
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\TestSelection.psm1") -Force
$script:failed = 0
function Assert-Equal($name, $expected, $actual) {
    if ("$expected" -ne "$actual") { $script:failed++; Write-Host "FALLA $name`n  esperado: $expected`n  obtenido: $actual" -ForegroundColor Red }
}

$arch = "tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj"
$boot = "tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj"
$unit = "tests/Modules/Catalog/Modules.Catalog.UnitTests/Modules.Catalog.UnitTests.csproj"
$int = "tests/Modules/Catalog/Modules.Catalog.IntegrationTests/Modules.Catalog.IntegrationTests.csproj"
$projects = @($arch, $boot, $unit, $int)
$dir = "tests/Modules/Catalog/Modules.Catalog.IntegrationTests"
$sources = @{
    "$dir/ProductApiTests.cs"        = "public sealed partial class ProductApiTests`n{`n    [Fact]`n    public void A() { }`n}"
    "$dir/ProductApiTests.Extra.cs"  = "public sealed partial class ProductApiTests`n{`n    private void Help() { }`n}"
    "$dir/OrderBase.cs"              = "public abstract class OrderBase`n{`n}"
    "$dir/OrderReadTests.cs"         = "public sealed class OrderReadTests(Host h)`n    : OrderBase`n{`n    [Theory]`n    public void B() { }`n}"
    "$dir/CatalogTestDatabase.cs"    = "internal static class CatalogTestDatabase`n{`n}"
}
function Plan([string[]]$files) { Get-TestPlan -ChangedFiles $files -Projects $projects -TestSources $sources }
function Names($p) { ($p.Steps | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Project) }) -join "," }
function FilterOf($p) { ($p.Steps | Where-Object Project -eq $int).Filter }

$p = Plan @()
Assert-Equal "vacio: solo arquitectura" "ArchitectureTests" (Names $p)
Assert-Equal "vacio: sin avisos" 0 @($p.Warnings).Count

$p = Plan @("src/Modules/Catalog/Modules.Catalog.Domain/Product.cs")
Assert-Equal "produccion: unitarias" "ArchitectureTests,Modules.Catalog.UnitTests" (Names $p)
Assert-Equal "produccion: aviso sin integracion" "Catalog: cambió producción y no corre ninguna prueba de integración" ($p.Warnings -join ";")

$p = Plan @("$dir/ProductApiTests.cs")
Assert-Equal "clase: filtro" "FullyQualifiedName~.ProductApiTests." (FilterOf $p)

$p = Plan @("$dir/ProductApiTests.Extra.cs")
Assert-Equal "partial sin Fact" "FullyQualifiedName~.ProductApiTests." (FilterOf $p)

$p = Plan @("$dir/OrderBase.cs")
Assert-Equal "base abstracta" "FullyQualifiedName~.OrderReadTests." (FilterOf $p)

$p = Plan @("$dir/ProductApiTests.cs", "$dir/OrderReadTests.cs")
Assert-Equal "dos clases" "FullyQualifiedName~.OrderReadTests.|FullyQualifiedName~.ProductApiTests." (FilterOf $p)

$p = Plan @("$dir/CatalogTestDatabase.cs")
Assert-Equal "harness: sin paso" "" (FilterOf $p)
Assert-Equal "harness: aviso" $true (($p.Warnings -join ";") -like "*Apoyo de pruebas cambiado*CatalogTestDatabase.cs*")

$p = Plan @("$dir/BorradaTests.cs")
Assert-Equal "borrado: sin paso" "" (FilterOf $p)

$p = Plan @(($dir -replace '/', '\') + "\ProductApiTests.cs")
Assert-Equal "barra invertida" "FullyQualifiedName~.ProductApiTests." (FilterOf $p)

$p = Plan @("src/Bootstrapper/X.cs")
Assert-Equal "bootstrapper" "ArchitectureTests,Bootstrapper.UnitTests" (Names $p)
Assert-Equal "bootstrapper: transversal" "Cambio transversal: conviene la corrida completa antes de seguir" ($p.Warnings -join ";")

$p = Plan @("src/Modules/Catalog/I/Persistence/Migrations/a.cs", "src/Modules/Pos/I/Persistence/Migrations/b.cs")
Assert-Equal "migraciones en dos modulos" $true ($p.Warnings -contains "Cambio transversal: conviene la corrida completa antes de seguir")

if ($script:failed) { Write-Host "$($script:failed) fallas"; exit 1 }
Write-Host "OK" -ForegroundColor Green
```

- [ ] **Step 2: RED**

Run: `powershell -NoProfile -File scripts\tests\TestSelection.Tests.ps1`
Expected: FAIL, el módulo `TestSelection.psm1` no existe.

- [ ] **Step 3: Implementar `Get-TestPlan` en `scripts/TestSelection.psm1`** (exportada con `Export-ModuleMember`). Normaliza `\` a `/` al entrar y ordena los nombres de clase del filtro.

- [ ] **Step 4: GREEN**

Run: el mismo comando. Expected: `OK`, exit 0.

- [ ] **Step 5: Commit** — `feat(tests): select the tests a branch change needs`

---

### Task 2: `scripts/test-feature.ps1`

**Files:**
- Create: `scripts/test-feature.ps1`
- Modify: `scripts/TestSelection.psm1`
- Test: `scripts/tests/TestSelection.Tests.ps1`

**Interfaces:**
- Consumes: `Get-TestPlan` (Task 1).
- Produces, en el módulo:
  - `Read-TrxResult -Directory <string>` → `$null` sin `.trx`; si no, `{ Total; Passed; Failed; Failures = @({ Test; Message }) }`. `Message` es la primera línea del error.
  - `Test-StepPassed -Result <objeto o $null> -ExitCode <int>` → `$true` sólo con resultado, `Total -gt 0`, `Failed -eq 0` y `ExitCode -eq 0`.
- Script: `param([string]$Base = "develop", [switch]$DryRun, [switch]$NoBuild)`.

Comportamiento:

- Corre desde la raíz del repo (`Split-Path -Parent $PSScriptRoot`).
- `git merge-base $Base HEAD`; si falla, `throw "No existe la base '<Base>'."`.
- Cambiados: `git -c core.quotepath=false diff --name-only --no-renames <mergeBase>` más `git -c core.quotepath=false ls-files --others --exclude-standard`.
- `-DryRun`: imprime pasos, filtros y avisos; no compila, no exige Docker ni toca `TestResults`.
- Sin `-NoBuild`: si hay proceso `Api`, `throw` explicando que bloquea la compilación; luego `dotnet build --no-restore -nologo -v q`.
- `testhost` corriendo: sólo aviso.
- Docker se exige (`docker info`) sólo si hay paso de Bootstrapper o de integración.
- Cada paso: `dotnet test <proyecto> --no-build -nologo [--filter <f>] --logger "trx;LogFileName=<nombre>.trx" --results-directory TestResults\feature\<nombre>`. Un paso fallido no frena los siguientes.
- Borra sólo `TestResults\feature`. Escribe `TestResults\feature\report.md`: sello (Task 3 lo agrega), tabla proyecto/total/pasan/fallan/segundos, fallas y avisos.
- Sale 1 si algún paso no pasa `Test-StepPassed`.

- [ ] **Step 1: Agregar pruebas** antes del bloque final:

```powershell
$ok = [pscustomobject]@{ Total = 3; Passed = 3; Failed = 0 }
Assert-Equal "paso verde" $true (Test-StepPassed -Result $ok -ExitCode 0)
Assert-Equal "sin trx" $false (Test-StepPassed -Result $null -ExitCode 0)
Assert-Equal "cero pruebas" $false (Test-StepPassed -Result ([pscustomobject]@{ Total = 0; Passed = 0; Failed = 0 }) -ExitCode 0)
Assert-Equal "con fallas" $false (Test-StepPassed -Result ([pscustomobject]@{ Total = 3; Passed = 2; Failed = 1 }) -ExitCode 1)
Assert-Equal "exit distinto de cero" $false (Test-StepPassed -Result $ok -ExitCode 1)

$tmp = Join-Path $env:TEMP ("trx-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
Assert-Equal "directorio sin trx" $true ($null -eq (Read-TrxResult -Directory $tmp))
@'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult testName="N.C.Falla" outcome="Failed"><Output><ErrorInfo><Message>boom
detalle</Message></ErrorInfo></Output></UnitTestResult></Results>
  <ResultSummary><Counters total="2" passed="1" failed="1" /></ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath (Join-Path $tmp "a.trx") -Encoding utf8
$r = Read-TrxResult -Directory $tmp
Assert-Equal "trx: conteos" "2,1,1" "$($r.Total),$($r.Passed),$($r.Failed)"
Assert-Equal "trx: falla" "N.C.Falla|boom" "$($r.Failures[0].Test)|$($r.Failures[0].Message)"
Remove-Item -LiteralPath $tmp -Recurse -Force
```

- [ ] **Step 2: RED** — mismo comando. Expected: FAIL, `Test-StepPassed` no se reconoce.
- [ ] **Step 3: Implementar** las dos funciones y el script.
- [ ] **Step 4: GREEN** — mismo comando: `OK`.
- [ ] **Step 5: Verificar el script**

```powershell
powershell -NoProfile -File scripts\test-feature.ps1 -DryRun
powershell -NoProfile -File scripts\test-feature.ps1 -Base no-existe -DryRun; $LASTEXITCODE
dotnet restore --locked-mode
powershell -NoProfile -File scripts\test-feature.ps1; $LASTEXITCODE
```

Expected: el primero lista sólo `ArchitectureTests` (la rama sólo toca docs y scripts); el segundo falla con `No existe la base` y código distinto de 0; el último compila, corre ArchitectureTests y sale 0. Mide y anota la duración.

- [ ] **Step 6: Commit** — `feat(tests): run the fast tier of a feature branch`

---

### Task 3: Sello en la corrida completa y documentación

**Files:**
- Modify: `scripts/TestSelection.psm1`, `scripts/run-all-tests.ps1`, `scripts/test-feature.ps1`
- Modify: `README.md` (§ Verificación), `CLAUDE.md`
- Test: `scripts/tests/TestSelection.Tests.ps1`

**Interfaces:**
- Produces: `Get-GitStamp` → string `Rama <rama> · commit <sha corto> · <árbol limpio | con cambios sin commitear>`.
- `run-all-tests.ps1` usa `Read-TrxResult` en vez de su lector propio; la tabla y el resto del reporte no cambian.

- [ ] **Step 1: Prueba**

```powershell
Assert-Equal "sello" $true ((Get-GitStamp) -match '^Rama \S+ · commit [0-9a-f]{7,} · (árbol limpio|con cambios sin commitear)$')
```

(El archivo de pruebas se guarda con BOM, o los `·` y las tildes no coinciden.)

- [ ] **Step 2: RED** — Expected: FAIL, `Get-GitStamp` no se reconoce.
- [ ] **Step 3: Implementar** `Get-GitStamp` y ponerlo bajo el título de ambos reportes; en `run-all-tests.ps1` se calcula **antes** de compilar.
- [ ] **Step 4: GREEN** — `OK`. Luego `powershell -NoProfile -File scripts\run-all-tests.ps1 -Kind Unit -Filter Audit` y revisar que `TestResults\report.md` trae el sello y la tabla.
- [ ] **Step 5: Documentar**
  - `README.md` § Verificación: los dos niveles, los parámetros de `test-feature.ps1` y cómo programar la completa de noche (el owner la registra):

    ```powershell
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -File `"$PWD\scripts\run-all-tests.ps1`""
    Register-ScheduledTask -TaskName "qep-backend-pruebas" -Action $action -Trigger (New-ScheduledTaskTrigger -Daily -At 1am)
    ```

    Con sus condiciones: equipo encendido, Docker Desktop abierto y sin `Api.exe` ni `testhost`; si no, no hay reporte nuevo, y eso se ve en la fecha y el sello.
  - `CLAUDE.md` del repo, en § Verificación: para mergear a `develop` basta `test-feature.ps1` en verde, leyendo sus avisos; la completa corre de noche o antes de `main`; `main` sólo recibe un SHA con reporte completo en verde.
- [ ] **Step 6: Commit** — `docs(tests): document the two test tiers`

---

## Al mergear (fuera de las tareas)

1. El checkout principal tiene `scripts/` sin seguimiento y `README.md` modificado: chocan con el merge. Con visto bueno del owner, y tras comprobar que son iguales a lo del commit de la Task 0, se descartan.
2. Actualizar `docs/estado.md` en `develop`.
3. En el `CLAUDE.md` del workspace (local, sin versionar), cambiar la viñeta «La suite completa corre sólo antes de mergear a `develop`» por: «Para mergear a `develop` corre `scripts/test-feature.ps1`; la suite completa corre de noche o antes de `main`».
