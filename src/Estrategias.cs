namespace Automate;

/// <summary>
/// Qué método usa cada tecnología en modo "auto". Para soportar un tipo de programa nuevo:
/// 1) agregar su regla en <see cref="Detector"/>, 2) mapearlo aquí a un método existente o a
/// una clase nueva que implemente <see cref="IMetodo"/>.
/// </summary>
internal static class Estrategias
{
    public static IMetodo Elegir(Tecnologia tec) => tec switch
    {
        // WPF y UWP dibujan todos sus controles dentro de una sola ventana: se accionan por UIA.
        Tecnologia.Wpf or Tecnologia.Uwp => new MetodoUia(),
        // Cada control es un HWND (o la zona web es un HWND en Chromium): los mensajes llegan directo.
        _ => new MetodoMensajes(),
    };

    /// <summary>Advertencia para tecnologías que no se pueden manejar sin tocar la entrada real.</summary>
    public static string? Limitacion(Tecnologia tec) => tec switch
    {
        Tecnologia.Juego =>
            "parece un juego/motor gráfico: suelen leer el mouse y teclado con Raw Input/DirectInput, " +
            "que ignoran los mensajes. Se intentará igual; si no responde, la única forma sin tocar su " +
            "entrada real es correrlo en otra sesión (Windows Sandbox, VM o un usuario por RDP).",
        Tecnologia.Uwp =>
            "en apps UWP/WinUI los clics por coordenadas solo funcionan sobre controles accesibles; prefiera id= o nombre=.",
        _ => null,
    };

    public static IMetodo Forzar(string modo) => modo.ToLowerInvariant() switch
    {
        "mensajes" or "msg" or "messages" => new MetodoMensajes(),
        "uia" => new MetodoUia(),
        _ => throw new ErrorUsuario($"Modo desconocido '{modo}' (use auto, mensajes o uia)."),
    };
}
