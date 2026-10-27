<#
.SYNOPSIS
    Ejecuta automate.exe y/o captura.ps1 como administrador, para manejar programas que corren
    elevados (instaladores, herramientas de sistema). Windows muestra su ventana de UAC y el usuario
    decide si da el permiso. Varios pasos separados por '--' van en una sola elevación (un solo aviso).
    Devuelve la salida de cada paso y el código de salida del último.
.EXAMPLE
    .\elevado.ps1 automate -p pid=7596 -click nombre=Siguiente
    .\elevado.ps1 captura -Proceso 7596 -Titulo Instalar -Salida C:\temp\paso1.png -- automate -p pid=7596 -click nombre=Siguiente -wait 1500 -- captura -Proceso 7596 -Titulo Instalar -Salida C:\temp\paso2.png
.NOTES
    Sin bloque param a propósito: así '-p', '-click', '-Proceso'... llegan tal cual a la herramienta.
    Los pasos se escriben en un .ps1 legible en pasos\ y el proceso elevado lo ejecuta con -File, en
    una ventana minimizada: nada de comandos codificados ni ventanas ocultas (Avast bloquea
    powershell.exe por "Command line detection" si la línea de comandos tiene esa forma).
    Código 3: el usuario rechazó el aviso de UAC.
#>
$raiz = $PSScriptRoot
$automate = Join-Path $raiz 'publish\automate.exe'
$captura = Join-Path $raiz 'captura.ps1'
# Los nombres de opción (-p, -click, -Proceso...) van tal cual; los valores, entre comillas simples
function Q([string]$s) { if ($s -match '^-[A-Za-z]\w*$') { $s } else { "'" + ($s -replace "'", "''") + "'" } }

# Separa los pasos por '--'
$pasos = @(); $actual = @()
foreach ($a in $args) {
    if ($a -eq '--') { if ($actual) { $pasos += , $actual }; $actual = @() } else { $actual += [string]$a }
}
if ($actual) { $pasos += , $actual }
if (-not $pasos) { throw 'Uso: elevado.ps1 automate|captura <argumentos> [-- automate|captura <argumentos>]...' }

$dir = Join-Path $raiz 'pasos'
New-Item -ItemType Directory -Force $dir | Out-Null
$id = [guid]::NewGuid().ToString('N').Substring(0, 8)
$guion = Join-Path $dir "pasos-$id.ps1"
$salida = Join-Path $dir "salida-$id.txt"

$cuerpo = @('# Generado por elevado.ps1; se borra al terminar.', '[Console]::OutputEncoding = [Text.Encoding]::UTF8', '$codigo = 0')
foreach ($paso in $pasos) {
    $herramienta, $resto = $paso
    $lista = ($resto | ForEach-Object { Q $_ }) -join ' '
    $comando = switch ($herramienta) {
        'automate' { "& $(Q $automate) $lista" }
        'captura'  { "& $(Q $captura) $lista" }
        default    { throw "Herramienta desconocida '$herramienta' (use automate o captura)." }
    }
    $cuerpo += "'> $herramienta $(($resto -join ' ') -replace "'", "''")' | Out-File -Append -Encoding utf8 $(Q $salida)"
    $cuerpo += "try { $comando 2>&1 | ForEach-Object { `"`$_`" } | Out-File -Append -Encoding utf8 $(Q $salida); `$codigo = `$LASTEXITCODE } catch { `"error: `$_`" | Out-File -Append -Encoding utf8 $(Q $salida); `$codigo = 1 }"
}
$cuerpo += 'exit $codigo'
# Con BOM: PowerShell 5.1 lee los .ps1 sin BOM como ANSI y rompe los acentos
[IO.File]::WriteAllText($guion, ($cuerpo -join "`r`n"), (New-Object Text.UTF8Encoding $true))

try {
    $p = Start-Process powershell.exe -Verb RunAs -WindowStyle Minimized -Wait -PassThru `
        -ArgumentList "-NoProfile -File `"$guion`""
} catch {
    Remove-Item $guion, $salida -ErrorAction SilentlyContinue
    Write-Host 'El usuario no concedió el permiso de administrador (UAC rechazado). No se ejecutó nada.'
    exit 3
}
if (Test-Path $salida) {
    Get-Content $salida -Encoding UTF8
    Remove-Item $guion, $salida -ErrorAction SilentlyContinue
} else {
    # Se deja el guion para revisarlo
    Write-Host "No hubo salida: el proceso elevado no llegó a ejecutar los pasos (¿lo bloqueó el antivirus?). Guion: $guion"
    exit 4
}
exit $p.ExitCode
