<#
.SYNOPSIS
    Captura la ventana principal de un programa sin traerla al frente (PrintWindow), aunque esté
    tapada por otras. También informa qué programa está en primer plano y dónde está el cursor,
    para comprobar que no se tocó la entrada del usuario.
.EXAMPLE
    .\captura.ps1 -Proceso CondorVentas -Salida C:\temp\paso1.png
    .\captura.ps1 -Proceso vppgde -Titulo vppgde -Salida C:\temp\menu.png   # un diálogo o menú del proceso
    .\captura.ps1 -Proceso 345            # solo informa primer plano y cursor
#>
param([Parameter(Mandatory)][string]$Proceso, [string]$Salida, [double]$Escala = 0.5,
      # Captura la ventana visible del proceso cuyo título contiene este texto (diálogos, menús).
      [string]$Titulo,
      # Captura esta ventana concreta (hex 0x... o decimal), p. ej. cuando varias comparten título.
      # (no se llama Hwnd: en PowerShell chocaría con la variable $hwnd del script)
      [string]$Handle)
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CapturaW {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int p);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public static IntPtr Buscar(int pid, string titulo) {
    IntPtr hallada = IntPtr.Zero;
    EnumWindows((h, l) => {
      int p; GetWindowThreadProcessId(h, out p);
      var sb = new System.Text.StringBuilder(512); GetWindowText(h, sb, 512);
      if (p == pid && IsWindowVisible(h) && sb.ToString().IndexOf(titulo, StringComparison.OrdinalIgnoreCase) >= 0) { hallada = h; return false; }
      return true;
    }, IntPtr.Zero);
    return hallada;
  }
  public struct RECT { public int L, T, R, B; }
  public struct POINT { public int X, Y; }
}
'@
[CapturaW]::SetProcessDPIAware() | Out-Null

$p = if ($Proceso -match '^\d+$') { Get-Process -Id $Proceso } else { Get-Process ($Proceso -replace '\.exe$', '') | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1 }
if (-not $p) { throw "No hay ningún proceso '$Proceso' con ventana." }
$hwnd = $p.MainWindowHandle
if ($Titulo) {
  $hwnd = [CapturaW]::Buscar($p.Id, $Titulo)
  if ($hwnd -eq [IntPtr]::Zero) { throw "El proceso no tiene una ventana visible con '$Titulo' en el título." }
}
if ($Handle) {
  $hwnd = [IntPtr][Convert]::ToInt64(($Handle -replace '^0x', ''), $(if ($Handle -match '^0x') { 16 } else { 10 }))
}

$fp = 0; [CapturaW]::GetWindowThreadProcessId([CapturaW]::GetForegroundWindow(), [ref]$fp) | Out-Null
$c = New-Object CapturaW+POINT; [CapturaW]::GetCursorPos([ref]$c) | Out-Null
"primer plano: $((Get-Process -Id $fp).ProcessName)  cursor: $($c.X),$($c.Y)"

# Un proceso elevado no deja leer su ruta ni capturarlo (sale en blanco) desde uno normal
$soyAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $soyAdmin -and -not $p.Path) {
  "AVISO: '$($p.ProcessName)' corre como administrador; la captura saldrá en blanco y los clics no llegarán. Use elevado.ps1."
}

if ($Salida) {
  $r = New-Object CapturaW+RECT; [CapturaW]::GetWindowRect($hwnd, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [CapturaW]::PrintWindow($hwnd, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT: también WPF/DirectX
  $g.ReleaseHdc($hdc)
  $chica = New-Object System.Drawing.Bitmap $bmp, ([int]($bmp.Width * $Escala)), ([int]($bmp.Height * $Escala))
  $chica.Save($Salida, [System.Drawing.Imaging.ImageFormat]::Png)
  "captura: $Salida"
}
