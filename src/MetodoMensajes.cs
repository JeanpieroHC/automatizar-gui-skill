namespace Automate;

internal enum Boton { Izquierdo, Derecho, Medio }

/// <summary>Cómo se entrega cada evento. Una implementación por familia de programas.</summary>
internal interface IMetodo
{
    string Nombre { get; }
    void Click(Sesion s, int x, int y, Boton boton, bool doble);
    void Escribir(Sesion s, string texto);
    void Tecla(Sesion s, Combinacion c);
}

/// <summary>
/// Publica mensajes de ventana (WM_LBUTTONDOWN, WM_CHAR, WM_KEYDOWN...) directamente en la cola
/// del control. No toca el cursor ni el teclado reales. Sirve para Win32, WinForms, Qt, Java y
/// para la zona web de Chromium/Electron. No sirve si el programa lee el hardware (GetCursorPos,
/// Raw Input, DirectInput): ese programa sigue viendo el mouse real.
/// </summary>
internal sealed class MetodoMensajes : IMetodo
{
    public string Nombre => "mensajes de ventana (PostMessage)";

    public void Click(Sesion s, int x, int y, Boton boton, bool doble)
    {
        var pantalla = s.Objetivo.APantalla(x, y);
        ClickEnPantalla(s, pantalla, boton, doble);
    }

    public static void ClickEnPantalla(Sesion s, Nativo.POINT pantalla, Boton boton, bool doble)
    {
        var hwnd = s.Objetivo.HwndEnPunto(pantalla);
        var local = pantalla;
        Nativo.ScreenToClient(hwnd, ref local);
        var lp = Nativo.MakeLParam(local.X, local.Y);
        var (abajo, arriba, mk) = boton switch
        {
            Boton.Derecho => (Nativo.WM_RBUTTONDOWN, Nativo.WM_RBUTTONUP, Nativo.MK_RBUTTON),
            Boton.Medio => (Nativo.WM_MBUTTONDOWN, Nativo.WM_MBUTTONUP, Nativo.MK_MBUTTON),
            _ => (Nativo.WM_LBUTTONDOWN, Nativo.WM_LBUTTONUP, Nativo.MK_LBUTTON),
        };
        s.Log($"click {boton.ToString().ToLowerInvariant()}{(doble ? " doble" : "")} → HWND 0x{hwnd:X} '{Nativo.Clase(hwnd)}' en ({local.X},{local.Y})");

        Nativo.PostMessage(hwnd, Nativo.WM_MOUSEMOVE, IntPtr.Zero, lp);
        Nativo.PostMessage(hwnd, abajo, mk, lp);
        Nativo.PostMessage(hwnd, arriba, IntPtr.Zero, lp);
        if (doble)
        {
            // Windows convierte el segundo "down" en DBLCLK solo con entrada real; aquí se envía tal cual.
            Nativo.PostMessage(hwnd, boton == Boton.Izquierdo ? Nativo.WM_LBUTTONDBLCLK : abajo, mk, lp);
            Nativo.PostMessage(hwnd, arriba, IntPtr.Zero, lp);
        }
        s.UltimoHwnd = hwnd;
    }

    public void Escribir(Sesion s, string texto) => EscribirEn(s, DestinoTeclado(s), texto);

    public static void EscribirEn(Sesion s, IntPtr hwnd, string texto, bool simularFoco = false)
    {
        if (simularFoco) SimularFoco(s, hwnd);
        s.Log($"escribir {texto.Length} caracteres → HWND 0x{hwnd:X} '{Nativo.Clase(hwnd)}'");
        foreach (var c in texto.Replace("\r\n", "\n"))
            Nativo.PostMessage(hwnd, Nativo.WM_CHAR, c == '\n' ? '\r' : c, (IntPtr)1);
    }

    public void Tecla(Sesion s, Combinacion c) => TeclaEn(s, DestinoTeclado(s), c);

    /// <summary>
    /// Con modificadores (ctrl+s), el programa pregunta a GetKeyState si Ctrl está pulsado, y eso
    /// mira el teclado real. Para engañarlo sin tocar el teclado del usuario se enlaza la cola de
    /// entrada de este hilo con la del hilo objetivo (AttachThreadInput) y se marca Ctrl como
    /// pulsado solo en ese estado compartido, mientras el programa procesa los mensajes. Si el
    /// programa está en primer plano, ese estado es el del teclado real durante esos 150 ms.
    /// </summary>
    public static void TeclaEn(Sesion s, IntPtr hwnd, Combinacion c, bool simularFoco = false)
    {
        if (simularFoco) SimularFoco(s, hwnd);
        s.Log($"tecla {c.Texto} → HWND 0x{hwnd:X} '{Nativo.Clase(hwnd)}'");
        var alt = c.Mods.HasFlag(Modificadores.Alt);
        var mods = new List<ushort>();
        if (c.Mods.HasFlag(Modificadores.Ctrl)) mods.Add(Combinacion.VK_CONTROL);
        if (c.Mods.HasFlag(Modificadores.Shift)) mods.Add(Combinacion.VK_SHIFT);
        if (alt) mods.Add(Combinacion.VK_MENU);

        uint miHilo = Nativo.GetCurrentThreadId();
        bool enlazado = false;
        var marcadas = new List<int>();   // teclas que puso automate y debe soltar al final
        if (mods.Count > 0)
        {
            // Un hilo de consola no tiene cola de entrada propia: sin ella no hay estado que compartir.
            if (Nativo.IsGUIThread(true) && Nativo.AttachThreadInput(miHilo, s.Objetivo.HiloUi, true))
            {
                enlazado = true;
                var estado = new byte[256];
                Nativo.GetKeyboardState(estado);
                // WPF pregunta por la tecla izquierda (VK_LCONTROL...), Win32 por la genérica: se marcan ambas.
                foreach (var vk in mods.SelectMany(m => new int[] { m, Izquierda(m) }))
                    if ((estado[vk] & 0x80) == 0) { estado[vk] |= 0x80; marcadas.Add(vk); }
                Nativo.SetKeyboardState(estado);
            }
            else s.Aviso("no se pudo enlazar la cola de entrada; puede que el programa ignore los modificadores.");
        }

        try
        {
            foreach (var m in mods) Enviar(hwnd, m, abajo: true, alt);
            Enviar(hwnd, c.Vk, abajo: true, alt);
            Enviar(hwnd, c.Vk, abajo: false, alt);
            foreach (var m in Enumerable.Reverse(mods)) Enviar(hwnd, m, abajo: false, alt);
            // Los mensajes publicados se procesan después; el estado debe seguir marcado hasta entonces.
            if (enlazado) Thread.Sleep(150);
        }
        finally
        {
            if (enlazado)
            {
                // Solo se sueltan las teclas que se marcaron aquí: si el programa está en primer plano,
                // este estado es también el del teclado real y el resto puede haber cambiado.
                var estado = new byte[256];
                Nativo.GetKeyboardState(estado);
                foreach (var vk in marcadas) estado[vk] &= 0x7F;
                Nativo.SetKeyboardState(estado);
                Nativo.AttachThreadInput(miHilo, s.Objetivo.HiloUi, false);
            }
        }
    }

    /// <summary>
    /// WPF descarta el teclado si su ventana no tiene el foco de Windows. Un WM_SETFOCUS enviado a
    /// mano le hace creer que lo tiene, sin activar la ventana ni quitarle el foco a la del usuario.
    /// </summary>
    static void SimularFoco(Sesion s, IntPtr hwnd)
    {
        if (Nativo.GetForegroundWindow() == Nativo.GetAncestor(hwnd, Nativo.GA_ROOT)) return;
        s.Log($"WM_SETFOCUS simulado → HWND 0x{hwnd:X}");
        Nativo.SendMessage(hwnd, Nativo.WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
    }

    static ushort Izquierda(ushort vk) => vk switch
    {
        Combinacion.VK_CONTROL => 0xA2,
        Combinacion.VK_SHIFT => 0xA0,
        _ => 0xA4,
    };

    static void Enviar(IntPtr hwnd, ushort vk, bool abajo, bool alt)
    {
        uint scan = Nativo.MapVirtualKey(vk, Nativo.MAPVK_VK_TO_VSC);
        long lp = 1 | (scan << 16);
        if (alt) lp |= 1L << 29;                       // bit de contexto: Alt pulsado
        if (!abajo) lp |= (1L << 30) | (1L << 31);     // tecla ya estaba abajo / se suelta
        uint msg = alt ? (abajo ? Nativo.WM_SYSKEYDOWN : Nativo.WM_SYSKEYUP) : (abajo ? Nativo.WM_KEYDOWN : Nativo.WM_KEYUP);
        Nativo.PostMessage(hwnd, msg, vk, (IntPtr)lp);
    }

    /// <summary>El control con foco dentro del programa, o el último que se clicó.</summary>
    static IntPtr DestinoTeclado(Sesion s)
    {
        var foco = s.Objetivo.HwndConFoco();
        if (foco != s.Objetivo.Ventana) return foco;
        return s.UltimoHwnd != IntPtr.Zero ? s.UltimoHwnd : foco;
    }
}
