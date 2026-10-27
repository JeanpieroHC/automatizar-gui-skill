namespace Automate;

/// <summary>Con qué está hecha la interfaz del programa objetivo.</summary>
internal enum Tecnologia { Desconocida, Win32, WinForms, Wpf, Uwp, Chromium, Qt, Java, Juego }

internal sealed record Deteccion(Tecnologia Tecnologia, List<string> Pistas);

/// <summary>
/// Reconoce la tecnología por las clases de ventana (lo más fiable) y por los módulos cargados.
/// Para soportar una tecnología nueva: agregar aquí su regla y en <see cref="Estrategias"/> cómo se maneja.
/// </summary>
internal static class Detector
{
    // (prefijo de clase de ventana, tecnología). Se revisa la ventana principal y sus hijas.
    static readonly (string Clase, Tecnologia Tec)[] ReglasClase =
    [
        ("HwndWrapper[", Tecnologia.Wpf),
        ("WindowsForms10.", Tecnologia.WinForms),
        ("ApplicationFrameWindow", Tecnologia.Uwp),
        ("Windows.UI.Core.CoreWindow", Tecnologia.Uwp),
        ("Microsoft.UI.Content.", Tecnologia.Uwp),          // WinUI 3
        ("WinUIDesktopWin32WindowClass", Tecnologia.Uwp),
        ("Chrome_WidgetWin_", Tecnologia.Chromium),          // Chrome, Edge, Electron, CEF
        ("Chrome_RenderWidgetHostHWND", Tecnologia.Chromium),
        ("Qt5QWindow", Tecnologia.Qt), ("Qt6QWindow", Tecnologia.Qt), ("QWidget", Tecnologia.Qt),
        ("QPopup", Tecnologia.Qt), ("QTool", Tecnologia.Qt),
        ("SunAwt", Tecnologia.Java),
        ("UnityWndClass", Tecnologia.Juego), ("UnrealWindow", Tecnologia.Juego), ("SDL_app", Tecnologia.Juego),
        ("GLFW30", Tecnologia.Juego),
    ];

    // (parte del nombre de un módulo cargado, tecnología).
    static readonly (string Modulo, Tecnologia Tec)[] ReglasModulo =
    [
        ("wpfgfx", Tecnologia.Wpf), ("PresentationNative", Tecnologia.Wpf), ("PresentationFramework", Tecnologia.Wpf),
        ("System.Windows.Forms", Tecnologia.WinForms),
        ("Windows.UI.Xaml", Tecnologia.Uwp), ("Microsoft.UI.Xaml", Tecnologia.Uwp),
        ("libcef", Tecnologia.Chromium), ("chrome_elf", Tecnologia.Chromium),
        ("QtCore4", Tecnologia.Qt), ("Qt5Core", Tecnologia.Qt), ("Qt6Core", Tecnologia.Qt),
        ("jvm.dll", Tecnologia.Java),
        ("UnityPlayer", Tecnologia.Juego), ("dinput8", Tecnologia.Juego), ("xinput1_", Tecnologia.Juego),
    ];

    public static Deteccion Detectar(Objetivo obj)
    {
        var pistas = new List<string>();
        var votos = new Dictionary<Tecnologia, int>();
        void Votar(Tecnologia t, int peso, string pista)
        {
            votos[t] = votos.GetValueOrDefault(t) + peso;
            pistas.Add(pista);
        }

        var ventanas = new List<IntPtr> { obj.Ventana };
        ventanas.AddRange(Nativo.Hijas(obj.Ventana));
        foreach (var clase in ventanas.Select(Nativo.Clase).Distinct())
            foreach (var (prefijo, tec) in ReglasClase)
                if (clase.StartsWith(prefijo, StringComparison.Ordinal))
                    Votar(tec, 10, $"clase de ventana '{clase}' → {tec}");

        try
        {
            foreach (System.Diagnostics.ProcessModule m in obj.Proceso.Modules)
                foreach (var (parte, tec) in ReglasModulo)
                    if (m.ModuleName.Contains(parte, StringComparison.OrdinalIgnoreCase))
                        Votar(tec, 3, $"módulo '{m.ModuleName}' → {tec}");
        }
        catch (Exception ex)
        {
            pistas.Add($"no se pudieron leer los módulos ({ex.GetType().Name}); solo se usan las clases de ventana");
        }

        if (votos.Count == 0)
        {
            // Sin pistas de un framework conocido: ventanas Win32 "puras" (MFC, Delphi, C++, etc.).
            pistas.Add("ninguna regla coincide → Win32 clásico");
            return new Deteccion(Tecnologia.Win32, pistas);
        }
        // Gana la de más puntos: una clase de ventana pesa más que un módulo suelto.
        var ganadora = votos.OrderByDescending(kv => kv.Value).First().Key;
        return new Deteccion(ganadora, pistas);
    }
}
