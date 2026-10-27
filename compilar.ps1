<#
.SYNOPSIS
    Compila automate.exe (un solo archivo, con .NET incluido) en la carpeta publish\ de esta skill.
    Usa el SDK de .NET 8 si está instalado; si no, compila dentro de Docker.
#>
$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
$opciones = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '-nologo', '-v', 'q')

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    & dotnet publish (Join-Path $raiz 'Automate.csproj') @opciones -o (Join-Path $raiz 'publish')
} else {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        $bin = @("$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin", "$env:ProgramFiles\Docker\Docker\resources\bin") |
            Where-Object { Test-Path (Join-Path $_ 'docker.exe') } | Select-Object -First 1
        if (-not $bin) { throw 'No hay SDK de .NET ni Docker para compilar.' }
        $env:PATH = "$bin;$env:PATH"
    }
    & docker run --rm -v "${raiz}:/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0 `
        dotnet publish Automate.csproj @opciones -o /src/publish
}
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación.' }
Remove-Item -Recurse -Force (Join-Path $raiz 'bin'), (Join-Path $raiz 'obj') -ErrorAction SilentlyContinue
$exe = Join-Path $raiz 'publish\automate.exe'
if (-not (Test-Path $exe)) { throw "No se generó $exe (¿lo puso en cuarentena el antivirus?)." }
Write-Host "Listo: $exe"
