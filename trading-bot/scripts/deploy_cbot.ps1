<#
.SYNOPSIS
    Copia el cBot y sus componentes compartidos a la carpeta que usa cTrader.

.DESCRIPTION
    cTrader compila cada robot desde Documentos\cAlgo\Sources\Robots\<Nombre>\<Nombre>\
    y no admite referencias a rutas de fuera. Por eso los componentes de
    shared_cbot\ se COPIAN en lugar de enlazarse.

    El repo es la fuente de verdad. Nunca editar los archivos copiados: los
    sobrescribe el siguiente despliegue.

.EXAMPLE
    .\scripts\deploy_cbot.ps1
    .\scripts\deploy_cbot.ps1 -Verificar
#>

[CmdletBinding()]
param(
    [string]$Bot = "PortfolioTrendBot",
    [string]$DestinoRaiz = "$env:USERPROFILE\OneDrive\Documentos\cAlgo\Sources\Robots",
    [switch]$Verificar
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# Componentes que necesita el bot. SessionEngine.cs no se copia: es para
# sistemas intradia y este es diario (ver CLAUDE.md).
$compartidos = @(
    "Signals.cs",
    "RiskSizer.cs",
    "CostTracker.cs",
    "TelemetryEmitter.cs"
)


# ---------------------------------------------------------------------------
# LINT DE CADENAS DE FORMATO
#
# El compilador de C# NO valida los especificadores de String.Format/Print: un
# {1,>10} compila y revienta en ejecucion con FormatException. Eso tumbo el
# OnStart del segundo backtest y con el todo el diagnostico de arranque.
#
# En C# la alineacion es {1,10} (derecha) y {1,-10} (izquierda). El '>' y el
# '<' son sintaxis de Python. Se bloquea el despliegue si aparecen.
# ---------------------------------------------------------------------------
function Test-CadenasDeFormato {
    param([string[]]$Archivos)

    $patron = '\{\s*\d+\s*,\s*[<>]'
    $malos = @()

    foreach ($f in $Archivos) {
        if (-not (Test-Path $f)) { continue }
        $n = 0
        foreach ($linea in (Get-Content $f)) {
            $n++
            # ignorar las lineas de comentario que documentan el propio bug
            if ($linea -match '^\s*//') { continue }
            if ($linea -match $patron) {
                $malos += "  $(Split-Path -Leaf $f):${n}: $($linea.Trim())"
            }
        }
    }

    if ($malos.Count -gt 0) {
        Write-Host ""
        Write-Host "DESPLIEGUE BLOQUEADO: alineacion de formato invalida en C#" -ForegroundColor Red
        Write-Host "En C# es {1,10} o {1,-10}. El '>' y el '<' lanzan FormatException." -ForegroundColor Red
        $malos | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        Write-Host ""
        throw "Corrige las cadenas de formato antes de desplegar."
    }
    Write-Host "  lint de formato: ok" -ForegroundColor DarkGray
}

$origenBot = Join-Path $repo "cbot\$Bot"
$destino   = Join-Path $DestinoRaiz "$Bot\$Bot"

if (-not (Test-Path $origenBot)) {
    throw "No existe el bot en $origenBot"
}

Write-Host "Bot ......... $Bot"
Write-Host "Origen ...... $origenBot"
Write-Host "Destino ..... $destino"
Write-Host ""

if ($Verificar) {
    Write-Host "Modo verificacion: comparando sin copiar." -ForegroundColor Cyan
    $difs = 0
    foreach ($f in @("$Bot.cs", "$Bot.csproj", "$Bot.sln") + $compartidos) {
        $src = if ($compartidos -contains $f) {
            Join-Path $repo "shared_cbot\$f"
        } else {
            Join-Path $origenBot $f
        }
        # el .sln va un nivel por encima, junto a la carpeta del proyecto
        $dst = if ($f -eq "$Bot.sln") {
            Join-Path $DestinoRaiz "$Bot\$f"
        } else {
            Join-Path $destino $f
        }
        if (-not (Test-Path $dst)) {
            Write-Host "  FALTA en destino: $f" -ForegroundColor Yellow
            $difs++
        } elseif ((Get-FileHash $src).Hash -ne (Get-FileHash $dst).Hash) {
            Write-Host "  DIFIERE: $f" -ForegroundColor Yellow
            $difs++
        } else {
            Write-Host "  ok: $f" -ForegroundColor DarkGray
        }
    }
    if ($difs -eq 0) { Write-Host "`nTodo sincronizado." -ForegroundColor Green }
    else { Write-Host "`n$difs archivo(s) por desplegar." -ForegroundColor Yellow }
    return
}

# Comprobar ANTES de copiar: no se despliega codigo que se sabe que falla.
Test-CadenasDeFormato -Archivos (
    @(Join-Path $origenBot "$Bot.cs") +
    ($compartidos | ForEach-Object { Join-Path $repo "shared_cbot\$_" })
)

New-Item -ItemType Directory -Force -Path $destino | Out-Null

# 1. el bot y su csproj
Copy-Item (Join-Path $origenBot "$Bot.cs")     $destino -Force
Copy-Item (Join-Path $origenBot "$Bot.csproj") $destino -Force
Write-Host "  copiado  $Bot.cs" -ForegroundColor Green
Write-Host "  copiado  $Bot.csproj" -ForegroundColor Green

# 2. los componentes compartidos
foreach ($f in $compartidos) {
    $src = Join-Path $repo "shared_cbot\$f"
    if (-not (Test-Path $src)) { throw "Falta el componente $src" }
    Copy-Item $src $destino -Force
    Write-Host "  copiado  $f" -ForegroundColor Green
}

# 3. la solucion. SIN ESTO cTRADER NO MUESTRA EL CODIGO FUENTE: compila el
#    .algo igual, pero el editor de Automate aparece vacio.
$slnOrigen = Join-Path $origenBot "$Bot.sln"
$slnDestino = Join-Path $DestinoRaiz "$Bot\$Bot.sln"
if (Test-Path $slnOrigen) {
    Copy-Item $slnOrigen $slnDestino -Force
    Write-Host "  copiado  $Bot.sln" -ForegroundColor Green
} else {
    Write-Host "  AVISO: falta $Bot.sln -- cTrader no mostrara el fuente" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Listo. Ahora en cTrader:" -ForegroundColor Cyan
Write-Host "  1. Automate -> el robot aparece como '$Bot'"
Write-Host "  2. Compilar (Ctrl+B)"
Write-Host "  3. Abrirlo sobre un grafico DIARIO de cualquiera de los simbolos"
Write-Host "  4. Revisar el parametro 'Simbolos' con los nombres EXACTOS de tu broker"
Write-Host ""
Write-Host "El bot pide AccessRights.FullAccess para escribir la telemetria." -ForegroundColor DarkGray
