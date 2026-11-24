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
    "$dir/ProductApiTests.cs"       = "public sealed partial class ProductApiTests`n{`n    [Fact]`n    public void A() { }`n}"
    "$dir/ProductApiTests.Extra.cs" = "public sealed partial class ProductApiTests`n{`n    private void Help() { }`n}"
    "$dir/OrderBase.cs"             = "public abstract class OrderBase`n{`n}"
    "$dir/OrderReadTests.cs"        = "public sealed class OrderReadTests(Host h)`n    : OrderBase`n{`n    [Theory]`n    public void B() { }`n}"
    "$dir/CatalogTestDatabase.cs"   = "internal static class CatalogTestDatabase`n{`n}"
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
Assert-Equal "base abstracta: sin aviso" 0 @($p.Warnings).Count

$p = Plan @("$dir/ProductApiTests.cs", "$dir/OrderReadTests.cs")
Assert-Equal "dos clases" "FullyQualifiedName~.OrderReadTests.|FullyQualifiedName~.ProductApiTests." (FilterOf $p)

$p = Plan @("$dir/CatalogTestDatabase.cs")
Assert-Equal "harness: sin paso" "" (FilterOf $p)
Assert-Equal "harness: aviso" $true (($p.Warnings -join ";") -like "*Apoyo de pruebas cambiado*CatalogTestDatabase.cs*")

$p = Plan @("$dir/packages.lock.json")
Assert-Equal "lock file: sin aviso" 0 @($p.Warnings).Count

$p = Plan @("$dir/BorradaTests.cs")
Assert-Equal "borrado: sin paso" "" (FilterOf $p)

$p = Plan @(($dir -replace '/', '\') + "\ProductApiTests.cs")
Assert-Equal "barra invertida" "FullyQualifiedName~.ProductApiTests." (FilterOf $p)

$p = Plan @("src/Bootstrapper/X.cs")
Assert-Equal "bootstrapper" "ArchitectureTests,Bootstrapper.UnitTests" (Names $p)
Assert-Equal "bootstrapper: transversal" "Cambio transversal: conviene la corrida completa antes de seguir" ($p.Warnings -join ";")

$p = Plan @("src/Modules/Catalog/I/Persistence/Migrations/a.cs", "src/Modules/Pos/I/Persistence/Migrations/b.cs")
Assert-Equal "migraciones en dos modulos" $true ($p.Warnings -contains "Cambio transversal: conviene la corrida completa antes de seguir")

$p = Plan @("src/Modules/Catalog/I/Persistence/Migrations/a.cs")
Assert-Equal "migraciones en un modulo" $false ($p.Warnings -contains "Cambio transversal: conviene la corrida completa antes de seguir")

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

if ($script:failed) { Write-Host "$($script:failed) fallas"; exit 1 }
Write-Host "OK" -ForegroundColor Green
