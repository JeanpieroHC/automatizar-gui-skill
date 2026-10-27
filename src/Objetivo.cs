using System.Diagnostics;
using System.IO;

namespace Automate;

/// <summary>Proceso objetivo y la ventana principal sobre la que se actúa.</summary>
internal sealed class Objetivo
{
    public required Process Proceso { get; init; }
    public required IntPtr Ventana { get; init; }
    public uint HiloUi { get; init; }

    /// <summary>
    /// Resuelve "-p": "pid=345", "345", "programa.exe" o la ruta de un .exe. Si es una ruta y
    /// el programa no está abierto, lo arranca y espera a que muestre su ventana.
    /// </summary>
    public static Objetivo Resolver(string spec, string? filtroTitulo, int esperaMs)
    {
        var procesos = BuscarProcesos(spec, out var rutaParaArrancar);
        if (procesos.Count == 0 && rutaParaArrancar != null)
        {
            Console.Error.WriteLine($"[i] {Path.GetFileName(rutaParaArrancar)} no está abierto; arrancándolo...");
            var p = Process.Start(new ProcessStartInfo(rutaParaArrancar)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(rutaParaArrancar)!,
            })!;
            procesos.Add(p);
        }
        if (procesos.Count == 0)
            throw new ErrorUsuario($"No hay ningún proceso que coincida con '{spec}'.");

        var limite = DateTime.Now.AddMilliseconds(esperaMs);
        while (true)
        {
            foreach (var p in procesos)
            {
                var hwnd = VentanaPrincipal((uint)p.Id, filtroTitulo);
                if (hwnd != IntPtr.Zero)
                    return new Objetivo
                    {
                        Proceso = p,
                        Ventana = hwnd,
                        HiloUi = Nativo.GetWindowThreadProcessId(hwnd, out _),
                    };
            }
            if (DateTime.Now > limite)
                throw new ErrorUsuario(filtroTitulo == null
                    ? $"El proceso '{spec}' no tiene ventanas visibles."
                    : $"El proceso '{spec}' no tiene una ventana visible cuyo título contenga '{filtroTitulo}'.");
            Thread.Sleep(250);
            // Un lanzador puede pasarle la ventana a otro proceso con el mismo nombre aunque esté en
            // otra ruta (System32\notepad.exe abre una pestaña en el Notepad de la Store ya abierto).
            if (rutaParaArrancar != null)
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(rutaParaArrancar)))
                    if (procesos.All(q => q.Id != p.Id)) procesos.Add(p);
        }
    }

    static List<Process> BuscarProcesos(string spec, out string? rutaParaArrancar)
    {
        rutaParaArrancar = null;
        var texto = spec.Trim();
        if (texto.StartsWith("pid=", StringComparison.OrdinalIgnoreCase)) texto = texto[4..];
        if (int.TryParse(texto, out var pid))
        {
            try { return [Process.GetProcessById(pid)]; }
            catch (ArgumentException) { throw new ErrorUsuario($"No existe el proceso con PID {pid}."); }
        }

        if (File.Exists(texto)) rutaParaArrancar = Path.GetFullPath(texto);
        var nombre = Path.GetFileNameWithoutExtension(texto);
        var lista = Process.GetProcessesByName(nombre).ToList();
        if (rutaParaArrancar is { } ruta)
        {
            // Con una ruta, solo cuentan los procesos de ese mismo .exe.
            lista = lista.Where(p =>
            {
                try { return string.Equals(p.MainModule?.FileName, ruta, StringComparison.OrdinalIgnoreCase); }
                catch { return true; }
            }).ToList();
        }
        return lista;
    }

    /// <summary>Ventana de nivel superior visible del proceso; si hay varias, la de mayor área.</summary>
    public static IntPtr VentanaPrincipal(uint pid, string? filtroTitulo)
    {
        var candidatas = VentanasDe(pid)
            .Where(h => Nativo.IsWindowVisible(h))
            .Where(h => filtroTitulo == null || Nativo.Titulo(h).Contains(filtroTitulo, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var conTitulo = candidatas.Where(h => Nativo.Titulo(h).Length > 0).ToList();
        if (conTitulo.Count > 0) candidatas = conTitulo;
        return candidatas
            .OrderByDescending(h => { Nativo.GetWindowRect(h, out var r); return (long)(r.Right - r.Left) * (r.Bottom - r.Top); })
            .FirstOrDefault();
    }

    public static List<IntPtr> VentanasDe(uint pid)
    {
        var lista = new List<IntPtr>();
        Nativo.EnumWindows((h, _) =>
        {
            Nativo.GetWindowThreadProcessId(h, out var p);
            if (p == pid) lista.Add(h);
            return true;
        }, IntPtr.Zero);
        return lista;
    }

    /// <summary>Pasa un punto del área cliente de la ventana principal a coordenadas de pantalla.</summary>
    public Nativo.POINT APantalla(int x, int y)
    {
        var p = new Nativo.POINT { X = x, Y = y };
        Nativo.ClientToScreen(Ventana, ref p);
        return p;
    }

    /// <summary>Control (HWND) más profundo y visible que hay bajo un punto de pantalla.</summary>
    public IntPtr HwndEnPunto(Nativo.POINT pantalla)
    {
        var actual = Ventana;
        while (true)
        {
            var local = pantalla;
            Nativo.ScreenToClient(actual, ref local);
            var hija = Nativo.ChildWindowFromPointEx(actual, local, Nativo.CWP_SKIPINVISIBLE | Nativo.CWP_SKIPTRANSPARENT);
            if (hija == IntPtr.Zero || hija == actual) return actual;
            actual = hija;
        }
    }

    /// <summary>Control con el foco del teclado dentro del hilo de la ventana (aunque no esté en primer plano).</summary>
    public IntPtr HwndConFoco()
    {
        var info = new Nativo.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Nativo.GUITHREADINFO>() };
        if (Nativo.GetGUIThreadInfo(HiloUi, ref info) && info.hwndFocus != IntPtr.Zero) return info.hwndFocus;
        return Ventana;
    }
}

/// <summary>Error que se le muestra al usuario sin traza.</summary>
internal sealed class ErrorUsuario(string mensaje) : Exception(mensaje);
