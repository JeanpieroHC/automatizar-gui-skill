using System.Windows.Automation;

namespace Automate;

/// <summary>Estado compartido por las acciones de una ejecución.</summary>
internal sealed class Sesion
{
    public required Objetivo Objetivo { get; init; }
    public required IMetodo Metodo { get; init; }
    public bool Detallado { get; init; }
    public int EsperaMs { get; init; }
    /// <summary>Último control usado, para que -type/-key vayan al mismo sitio que el -click anterior.</summary>
    public AutomationElement? UltimoElemento { get; set; }
    public IntPtr UltimoHwnd { get; set; }

    public void Log(string texto) { if (Detallado) Console.Error.WriteLine($"    {texto}"); }
    public void Aviso(string texto) => Console.Error.WriteLine($"[!] {texto}");
}

internal static class Program
{
    const string Uso = """
        automate - envía clics y teclas a un programa sin mover el mouse ni usar el teclado real.

        Uso:
          automate -p <programa.exe | ruta\programa.exe | pid=N> [opciones] <acciones...>

        Opciones:
          -w <texto>         elige la ventana cuyo título contiene <texto>
          -modo <m>          auto (por defecto) | mensajes | uia
          -espera <ms>       cuánto esperar a que aparezcan ventanas/controles (10000)
          -pausa <ms>        pausa entre acciones (200)
          -v                 muestra qué hace en cada paso

        Acciones (se ejecutan en orden, se pueden repetir):
          -click x=4,y=3[,der|medio][,doble]   clic en coordenadas del área cliente de la ventana
          -click nombre=Guardar | id=btnGuardar | tipo=Button   clic en un control (UI Automation)
          -focus <selector>                    da el foco a un control
          -type "texto"                        escribe en el control con foco o el último usado
          -set <selector> "texto"              reemplaza el contenido de un control
          -key ENTER | TAB | ctrl+s | alt+F4   pulsa una tecla o combinación
          -wait <ms>                           espera
          -info [profundidad]                  muestra tecnología detectada, ventanas y controles

        Ejemplos:
          automate -p notepad.exe -type "hola" -key ENTER
          automate -p pid=345 -click x=120,y=80
          automate -p CondorVentas.exe -set id=TxtCodigo "A-001" -click nombre=Agregar
        """;

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { return Ejecutar(args); }
        catch (ErrorUsuario ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 2; }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex}"); return 1; }
    }

    static int Ejecutar(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "/?")) { Console.WriteLine(Uso); return 0; }

        string? proceso = null, titulo = null, modo = "auto";
        int espera = 10000, pausa = 200;
        bool detallado = false;
        var acciones = new List<(string Nombre, string[] Args)>();

        for (int i = 0; i < args.Length; i++)
        {
            string Sig() => i + 1 < args.Length ? args[++i] : throw new ErrorUsuario($"Falta el valor de {args[i]}.");
            switch (args[i].ToLowerInvariant())
            {
                case "-p": proceso = Sig(); break;
                case "-w": titulo = Sig(); break;
                case "-modo" or "-mode": modo = Sig(); break;
                case "-espera": espera = int.Parse(Sig()); break;
                case "-pausa": pausa = int.Parse(Sig()); break;
                case "-v": detallado = true; break;
                case "-click" or "-focus" or "-type" or "-key" or "-wait":
                    acciones.Add((args[i].ToLowerInvariant(), [Sig()])); break;
                case "-set":
                    { var sel = Sig(); acciones.Add(("-set", [sel, Sig()])); break; }
                case "-info":
                    {
                        var prof = i + 1 < args.Length && int.TryParse(args[i + 1], out _) ? args[++i] : "6";
                        acciones.Add(("-info", [prof])); break;
                    }
                default: throw new ErrorUsuario($"Argumento desconocido '{args[i]}'. Use -h para ver la ayuda.");
            }
        }
        if (proceso == null) throw new ErrorUsuario("Falta -p <programa.exe | pid=N>.");
        if (acciones.Count == 0) throw new ErrorUsuario("No se indicó ninguna acción (-click, -type, -key, -info...).");

        var objetivo = Objetivo.Resolver(proceso, titulo, espera);
        var deteccion = Detector.Detectar(objetivo);
        var metodo = modo.Equals("auto", StringComparison.OrdinalIgnoreCase) ? Estrategias.Elegir(deteccion.Tecnologia) : Estrategias.Forzar(modo);
        var s = new Sesion { Objetivo = objetivo, Metodo = metodo, Detallado = detallado, EsperaMs = espera };

        Console.Error.WriteLine($"[i] {objetivo.Proceso.ProcessName} (PID {objetivo.Proceso.Id}) · ventana '{Nativo.Titulo(objetivo.Ventana)}' · {deteccion.Tecnologia} → {metodo.Nombre}");
        if (Estrategias.Limitacion(deteccion.Tecnologia) is { } lim) s.Aviso(lim);
        if (Nativo.IsIconic(objetivo.Ventana)) s.Aviso("la ventana está minimizada; algunos programas no procesan clics así.");

        Nativo.GetCursorPos(out var cursorAntes);
        var frenteAntes = Nativo.GetForegroundWindow();
        // Mientras corre, nadie puede quitarle el primer plano a la ventana que usa el usuario.
        // Solo funciona si automate se lanzó desde la ventana activa, que es justo cuando el
        // programa objetivo heredaría el permiso de ponerse al frente.
        bool bloqueado = frenteAntes != objetivo.Ventana && Nativo.LockSetForegroundWindow(Nativo.LSFW_LOCK);
        s.Log(bloqueado ? "primer plano bloqueado mientras se ejecutan las acciones" : "no hizo falta bloquear el primer plano");
        try
        {
            foreach (var (nombre, a) in acciones)
            {
                s.Log($"{nombre} {string.Join(" ", a)}");
                switch (nombre)
                {
                    case "-click": Click(s, a[0]); break;
                    case "-focus":
                        {
                            var el = Uia.Buscar(s, a[0]);
                            s.Log($"UIA: SetFocus en {Uia.Describir(el)}");
                            el.SetFocus();
                            s.UltimoElemento = el;
                            break;
                        }
                    case "-type": metodo.Escribir(s, a[0]); break;
                    case "-set":
                        {
                            var el = Uia.Buscar(s, a[0]);
                            if (!MetodoUia.EscribirEn(s, el, a[1], reemplazar: true))
                                throw new ErrorUsuario($"{Uia.Describir(el)} no admite que se le escriba texto.");
                            break;
                        }
                    case "-key": metodo.Tecla(s, Combinacion.Parse(a[0])); break;
                    case "-wait": Thread.Sleep(int.Parse(a[0])); break;
                    case "-info": Info(objetivo, deteccion, metodo, int.Parse(a[0])); break;
                }
                Thread.Sleep(pausa);
                DevolverPrimerPlano(s, frenteAntes);
            }
        }
        finally
        {
            if (bloqueado) Nativo.LockSetForegroundWindow(Nativo.LSFW_UNLOCK);
        }

        if (detallado)
        {
            Nativo.GetCursorPos(out var cursorDespues);
            bool cursorIgual = cursorAntes.X == cursorDespues.X && cursorAntes.Y == cursorDespues.Y;
            Console.Error.WriteLine($"[i] cursor del usuario: {(cursorIgual ? "no se movió" : "cambió (lo movió usted o el programa)")}; " +
                $"ventana en primer plano: {(frenteAntes == Nativo.GetForegroundWindow() ? "la misma" : "cambió")}");
        }
        return 0;
    }

    /// <summary>
    /// Algunas acciones (SetFocus de UI Automation, un programa que llama a Focus()/Activate())
    /// traen la ventana objetivo al frente, porque Windows le pasa el permiso de primer plano a
    /// lo que se lanzó desde la terminal activa. Si pasó, se devuelve el frente a donde estaba.
    /// </summary>
    static void DevolverPrimerPlano(Sesion s, IntPtr frenteAntes)
    {
        if (frenteAntes == IntPtr.Zero || frenteAntes == s.Objetivo.Ventana) return;
        var ahora = Nativo.GetForegroundWindow();
        if (ahora == frenteAntes) return;
        Nativo.GetWindowThreadProcessId(ahora, out var pid);
        if (pid != s.Objetivo.Proceso.Id) return;
        bool ok = Nativo.SetForegroundWindow(frenteAntes);
        s.Log(ok ? "el programa se puso al frente; se devolvió el primer plano a su ventana anterior"
                 : "el programa se puso al frente y Windows no dejó devolver el primer plano");
    }

    static void Click(Sesion s, string spec)
    {
        int? x = null, y = null;
        var boton = Boton.Izquierdo;
        bool doble = false;
        foreach (var parte in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = parte.Split('=', 2);
            switch (kv[0].ToLowerInvariant())
            {
                case "x" when kv.Length == 2: x = int.Parse(kv[1]); break;
                case "y" when kv.Length == 2: y = int.Parse(kv[1]); break;
                case "der" or "derecho" or "right": boton = Boton.Derecho; break;
                case "medio" or "middle": boton = Boton.Medio; break;
                case "izq" or "izquierdo" or "left": boton = Boton.Izquierdo; break;
                case "doble" or "double": doble = true; break;
                default:
                    // No son coordenadas: es un selector de control.
                    var el = Uia.Buscar(s, spec);
                    s.Log($"UIA: encontrado {Uia.Describir(el)}");
                    if (boton == Boton.Izquierdo && !doble && MetodoUia.Accionar(s, el)) return;
                    var r = el.Current.BoundingRectangle;
                    var centro = new Nativo.POINT { X = (int)(r.X + r.Width / 2), Y = (int)(r.Y + r.Height / 2) };
                    MetodoMensajes.ClickEnPantalla(s, centro, boton, doble);
                    return;
            }
        }
        if (x == null || y == null) throw new ErrorUsuario($"Clic inválido '{spec}': use x=..,y=.. o nombre=/id=/tipo=.");
        s.Metodo.Click(s, x.Value, y.Value, boton, doble);
    }

    static void Info(Objetivo obj, Deteccion det, IMetodo metodo, int profundidad)
    {
        Nativo.GetClientRect(obj.Ventana, out var cr);
        Console.WriteLine($"Proceso   : {obj.Proceso.ProcessName} (PID {obj.Proceso.Id})");
        Console.WriteLine($"Ventana   : 0x{obj.Ventana:X} '{Nativo.Titulo(obj.Ventana)}' clase '{Nativo.Clase(obj.Ventana)}' área cliente {cr.Right}x{cr.Bottom}");
        Console.WriteLine($"Tecnología: {det.Tecnologia} → {metodo.Nombre}");
        foreach (var p in det.Pistas.Distinct().Take(12)) Console.WriteLine($"   · {p}");

        var otras = Objetivo.VentanasDe((uint)obj.Proceso.Id).Where(h => h != obj.Ventana && Nativo.IsWindowVisible(h)).ToList();
        if (otras.Count > 0)
        {
            Console.WriteLine("Otras ventanas visibles del proceso (elíjalas con -w):");
            foreach (var h in otras) Console.WriteLine($"   0x{h:X} '{Nativo.Titulo(h)}' clase '{Nativo.Clase(h)}'");
        }

        var origen = obj.APantalla(0, 0);
        var hijas = Nativo.Hijas(obj.Ventana).Where(Nativo.IsWindowVisible).ToList();
        Console.WriteLine($"Controles HWND visibles ({hijas.Count}):");
        foreach (var h in hijas.Take(60))
        {
            Nativo.GetWindowRect(h, out var r);
            Console.WriteLine($"   '{Nativo.Clase(h)}' '{Nativo.Titulo(h)}' centro=({(r.Left + r.Right) / 2 - origen.X},{(r.Top + r.Bottom) / 2 - origen.Y})");
        }
        Console.WriteLine("Controles UI Automation (centro en coordenadas para -click):");
        Uia.ImprimirArbol(obj, profundidad);
    }
}
