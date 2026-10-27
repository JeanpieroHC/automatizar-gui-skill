<#
.SYNOPSIS
    Compila agente.jar (agente Java + lanzador) con el JDK instalado. Lo usa jab.py para los
    clics, dobles clics y teclas que el Java Access Bridge no puede dar.
#>
$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
$jdk = if ($env:JAVA_HOME -and (Test-Path "$env:JAVA_HOME\bin\javac.exe")) { $env:JAVA_HOME } else {
    Get-ChildItem 'C:\Program Files\Java', 'C:\Program Files\Eclipse Adoptium', 'C:\Program Files\Microsoft' -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path "$($_.FullName)\bin\javac.exe" } | Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $jdk) { throw 'No hay un JDK con javac (el JRE solo no basta).' }
$clases = Join-Path $raiz 'clases'
Remove-Item -Recurse -Force $clases -ErrorAction SilentlyContinue
# --release 11: el agente se carga también en programas que traen un Java 11+ propio
& "$jdk\bin\javac.exe" --release 11 -encoding UTF-8 -d $clases (Get-ChildItem "$raiz\src\*.java").FullName
if ($LASTEXITCODE -ne 0) { throw 'Falló javac.' }
$manifiesto = Join-Path $raiz 'manifiesto.txt'
"Main-Class: Lanzador`r`nAgent-Class: AgenteArranque`r`n" | Set-Content -Encoding ascii $manifiesto
& "$jdk\bin\jar.exe" cfm (Join-Path $raiz 'agente.jar') $manifiesto -C $clases .
if ($LASTEXITCODE -ne 0) { throw 'Falló jar.' }
Remove-Item -Recurse -Force $clases, $manifiesto
Write-Host "Listo: $(Join-Path $raiz 'agente.jar') (JDK: $jdk)"
