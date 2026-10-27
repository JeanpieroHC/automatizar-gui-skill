namespace Automate;

[Flags] internal enum Modificadores { Ninguno = 0, Ctrl = 1, Shift = 2, Alt = 4 }

/// <summary>Una tecla con sus modificadores, p. ej. "ctrl+shift+s" o "ENTER".</summary>
internal sealed record Combinacion(Modificadores Mods, ushort Vk, string Texto)
{
    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    static readonly Dictionary<string, ushort> Nombres = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTER"] = 0x0D, ["INTRO"] = 0x0D, ["RETURN"] = 0x0D,
        ["TAB"] = 0x09, ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B,
        ["SPACE"] = 0x20, ["ESPACIO"] = 0x20,
        ["BACKSPACE"] = 0x08, ["RETROCESO"] = 0x08,
        ["DELETE"] = 0x2E, ["DEL"] = 0x2E, ["SUPR"] = 0x2E, ["INSERT"] = 0x2D, ["INS"] = 0x2D,
        ["HOME"] = 0x24, ["INICIO"] = 0x24, ["END"] = 0x23, ["FIN"] = 0x23,
        ["PGUP"] = 0x21, ["PAGEUP"] = 0x21, ["PGDN"] = 0x22, ["PAGEDOWN"] = 0x22,
        ["LEFT"] = 0x25, ["IZQUIERDA"] = 0x25, ["UP"] = 0x26, ["ARRIBA"] = 0x26,
        ["RIGHT"] = 0x27, ["DERECHA"] = 0x27, ["DOWN"] = 0x28, ["ABAJO"] = 0x28,
    };

    public static Combinacion Parse(string texto)
    {
        var partes = texto.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length == 0) throw new ErrorUsuario("Tecla vacía.");
        var mods = Modificadores.Ninguno;
        foreach (var m in partes[..^1])
            mods |= m.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Modificadores.Ctrl,
                "shift" or "mayus" => Modificadores.Shift,
                "alt" => Modificadores.Alt,
                _ => throw new ErrorUsuario($"Modificador desconocido '{m}' (use ctrl, shift o alt)."),
            };

        var tecla = partes[^1];
        ushort vk;
        if (Nombres.TryGetValue(tecla, out var conocido)) vk = conocido;
        else if (tecla.Length > 1 && (tecla[0] is 'F' or 'f') && int.TryParse(tecla[1..], out var f) && f is >= 1 and <= 24)
            vk = (ushort)(0x70 + f - 1);
        else if (tecla.Length == 1)
        {
            var scan = Nativo.VkKeyScan(tecla[0]);
            if (scan == -1) throw new ErrorUsuario($"La tecla '{tecla}' no existe en el teclado actual.");
            vk = (ushort)(scan & 0xFF);
        }
        else throw new ErrorUsuario($"Tecla desconocida '{tecla}'.");
        return new Combinacion(mods, vk, texto);
    }
}
