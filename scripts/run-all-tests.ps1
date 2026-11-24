<#
.SYNOPSIS
  Corre las pruebas de todos los proyectos, uno a la vez, y genera un reporte de fallas.

.PARAMETER Filter
  Texto que debe contener la ruta del proyecto (por ejemplo "Quotations"). Vacío = todos.

.PARAMETER Kind
  Unit, Integration o All (por defecto).

.PARAMETER MaxThreads
  Hilos de xUnit por proyecto. 4 es cómodo con Testcontainers.

.PARAMETER NoBuild
  Omite la compilación previa.

.OUTPUTS
  TestResults/report.md (resumen y fallas) y un .trx por proyecto en TestResults/<proyecto>/.
#>
param(
    [string]$Filter = "",
    [ValidateSet("Unit", "Integration", "All")][string]$Kind = "All",
    [int]$MaxThreads = 4,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Pre-requisitos: sin Docker las pruebas de integración fallan todas por conexión.
docker info *> $null
if ($LASTEXITCODE -ne 0 -and $Kind -ne "Unit") {
    throw "Docker no responde. Abre Docker Desktop y espera a que el daemon esté listo."
}
$running = Get-Process Api, testhost -ErrorAction SilentlyContinue
if ($running) { throw "Hay procesos Api/testhost corriendo (otra sesión o el API local). Espera o detenlos tú." }

$out = Join-Path $root "TestResults"
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null

if (-not $NoBuild) {
    dotnet build --no-restore -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "La compilación falló; no hay pruebas que correr." }
}

$projects = Get-ChildItem tests -Recurse -Filter *.csproj |
    Where-Object { $_.Name -ne "Modules.Ai.Eval.csproj" } |   # eval con IA real: se corre aparte
    Where-Object { $Kind -eq "All" -or $_.Name -like "*.$Kind*" } |
    Where-Object { $_.FullName -like "*$Filter*" } |
    Sort-Object { $_.Name -like "*Integration*" }, FullName   # unitarias primero

$rows = @(); $failures = @()
foreach ($p in $projects) {
    $name = $p.BaseName
    $dir = Join-Path $out $name
    Write-Host ">> $name" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    dotnet test $p.FullName --no-build -nologo --logger "trx;LogFileName=$name.trx" --results-directory $dir -- xUnit.MaxParallelThreads=$MaxThreads | Out-Null
    $sw.Stop()

    $trx = Get-ChildItem -LiteralPath $dir -Filter *.trx -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $trx) { $rows += [pscustomobject]@{ Project = $name; Total = "-"; Passed = "-"; Failed = "sin trx"; Secs = [int]$sw.Elapsed.TotalSeconds }; continue }
    [xml]$x = Get-Content -LiteralPath $trx.FullName -Raw
    $c = $x.TestRun.ResultSummary.Counters
    $rows += [pscustomobject]@{ Project = $name; Total = [int]$c.total; Passed = [int]$c.passed; Failed = [int]$c.failed; Secs = [int]$sw.Elapsed.TotalSeconds }
    foreach ($r in $x.TestRun.Results.UnitTestResult | Where-Object outcome -eq "Failed") {
        $msg = "$($r.Output.ErrorInfo.Message)".Trim()
        $first = ($msg -split "`r?`n")[0]
        $failures += [pscustomobject]@{ Project = $name; Test = $r.testName; Message = $first }
    }
}

$sb = [Text.StringBuilder]::new()
[void]$sb.AppendLine("# Reporte de pruebas — $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Proyecto | Total | Pasan | Fallan | Seg |")
[void]$sb.AppendLine("| --- | ---: | ---: | ---: | ---: |")
foreach ($r in $rows) { [void]$sb.AppendLine("| $($r.Project) | $($r.Total) | $($r.Passed) | $($r.Failed) | $($r.Secs) |") }
$sum = { param($f) ($rows | Where-Object { $_.$f -is [int] } | Measure-Object $f -Sum).Sum }
[void]$sb.AppendLine("| **Total** | **$(& $sum Total)** | **$(& $sum Passed)** | **$(& $sum Failed)** | **$(& $sum Secs)** |")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Causas más repetidas")
[void]$sb.AppendLine("")
foreach ($g in $failures | Group-Object Message | Sort-Object Count -Descending | Select-Object -First 15) {
    [void]$sb.AppendLine("- **$($g.Count)** × ``$($g.Name)``")
}
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Fallas por proyecto")
foreach ($g in $failures | Group-Object Project) {
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("### $($g.Name) ($($g.Count))")
    foreach ($f in $g.Group | Select-Object -First 50) { [void]$sb.AppendLine("- ``$($f.Test)`` — $($f.Message)") }
    if ($g.Count -gt 50) { [void]$sb.AppendLine("- … y $($g.Count - 50) más en el .trx") }
}
$report = Join-Path $out "report.md"
$sb.ToString() | Set-Content -LiteralPath $report -Encoding utf8
Write-Host "Reporte: $report" -ForegroundColor Green
