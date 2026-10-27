using System.Windows;
using System.Windows.Automation;

namespace Automate;

/// <summary>
/// Usa UI Automation: busca el control que hay en (x,y) o por nombre y lo acciona con su patrón
/// (Invoke, Toggle, Select, Value...). Es lo que necesitan WPF y UWP, que dibujan todo dentro de
/// una sola ventana y no siempre aceptan clics por mensajes. Si el control no tiene un patrón que
/// sirva (p. ej. clic derecho), cae a <see cref="MetodoMensajes"/> sobre ese mismo punto.
/// </summary>
internal sealed class MetodoUia : IMetodo
{
    public string Nombre => "UI Automation (con mensajes de respaldo)";

    public void Click(Sesion s, int x, int y, Boton boton, bool doble)
    {
        var pantalla = s.Objetivo.APantalla(x, y);
        var el = ElementoEn(AutomationElement.FromHandle(s.Objetivo.Ventana), new Point(pantalla.X, pantalla.Y));
        if (el == null)
        {
            s.Log($"UIA: no hay control en ({x},{y}); se usa mensajes");
            MetodoMensajes.ClickEnPantalla(s, pantalla, boton, doble);
            return;
        }
        s.Log($"UIA: en ({x},{y}) está {Uia.Describir(el)}");
        if (boton == Boton.Izquierdo && !doble && Accionar(s, el)) return;
        MetodoMensajes.ClickEnPantalla(s, pantalla, boton, doble);
        s.UltimoElemento = el;
    }

    /// <summary>Clic "semántico" sobre un control. Devuelve false si no tiene cómo accionarse.</summary>
    public static bool Accionar(Sesion s, AutomationElement el)
    {
        // El texto de un botón suele ser un hijo sin patrones: se acciona el botón que lo contiene.
        var walker = TreeWalker.ControlViewWalker;
        for (var e = el; e != null; e = walker.GetParent(e))
        {
            if (TryAccionar(s, e)) return true;
            if (e.Current.NativeWindowHandle != 0) break;
        }
        s.UltimoElemento = el;
        return false;
    }

    static bool TryAccionar(Sesion s, AutomationElement el)
    {
        s.UltimoElemento = el;
        if (!el.Current.IsEnabled && el.GetSupportedPatterns().Length > 0)
            throw new ErrorUsuario($"{Uia.Describir(el)} está deshabilitado en el programa.");
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
        {
            s.Log($"UIA: Invoke en {Uia.Describir(el)}");
            ((InvokePattern)p).Invoke();
            return true;
        }
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out p))
        {
            s.Log($"UIA: Toggle en {Uia.Describir(el)}");
            ((TogglePattern)p).Toggle();
            return true;
        }
        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p))
        {
            s.Log($"UIA: Select en {Uia.Describir(el)}");
            ((SelectionItemPattern)p).Select();
            return true;
        }
        if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p))
        {
            var ec = (ExpandCollapsePattern)p;
            s.Log($"UIA: Expand/Collapse en {Uia.Describir(el)}");
            if (ec.Current.ExpandCollapseState == ExpandCollapseState.Expanded) ec.Collapse(); else ec.Expand();
            return true;
        }
        if (el.Current.IsKeyboardFocusable)
        {
            // Un cuadro de texto: el clic solo sirve para darle el foco.
            s.Log($"UIA: SetFocus en {Uia.Describir(el)}");
            el.SetFocus();
            return true;
        }
        return false;
    }

    public void Escribir(Sesion s, string texto)
    {
        if (s.UltimoElemento != null && EscribirEn(s, s.UltimoElemento, texto, reemplazar: false)) return;
        MetodoMensajes.EscribirEn(s, s.Objetivo.HwndConFoco(), texto, simularFoco: true);
    }

    /// <summary>Escribe con ValuePattern. Devuelve false si el control no lo admite.</summary>
    public static bool EscribirEn(Sesion s, AutomationElement el, string texto, bool reemplazar)
    {
        if (!el.TryGetCurrentPattern(ValuePattern.Pattern, out var p)) return false;
        var vp = (ValuePattern)p;
        if (vp.Current.IsReadOnly) return false;
        string actual = "";
        if (!reemplazar)
            try { actual = vp.Current.Value; } catch { /* PasswordBox no deja leer */ }
        s.Log($"UIA: SetValue en {Uia.Describir(el)}");
        vp.SetValue(actual + texto);
        s.UltimoElemento = el;
        return true;
    }

    public void Tecla(Sesion s, Combinacion c)
    {
        // UI Automation no tiene teclas: van como mensajes a la ventana que contiene el control.
        var hwnd = s.UltimoElemento != null ? Uia.HwndDe(s.UltimoElemento) : IntPtr.Zero;
        MetodoMensajes.TeclaEn(s, hwnd != IntPtr.Zero ? hwnd : s.Objetivo.HwndConFoco(), c, simularFoco: true);
    }

    /// <summary>El control más interno cuyo rectángulo contiene el punto (sin importar qué ventana lo tape).</summary>
    static AutomationElement? ElementoEn(AutomationElement raiz, Point p)
    {
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? encontrado = null;
        var actual = raiz;
        while (actual != null)
        {
            AutomationElement? mejor = null;
            double mejorArea = double.MaxValue;
            for (var h = walker.GetFirstChild(actual); h != null; h = walker.GetNextSibling(h))
            {
                Rect r;
                try { r = h.Current.BoundingRectangle; } catch (ElementNotAvailableException) { continue; }
                if (r.IsEmpty || !r.Contains(p)) continue;
                double area = r.Width * r.Height;
                if (area <= mejorArea) { mejor = h; mejorArea = area; }
            }
            if (mejor == null) break;
            encontrado = mejor;
            actual = mejor;
        }
        return encontrado;
    }
}

/// <summary>Búsqueda y descripción de controles por UI Automation (sirve para cualquier tecnología).</summary>
internal static class Uia
{
    /// <summary>Selector "nombre=Guardar", "id=btnGuardar" o "tipo=Edit" (se pueden juntar con ';').</summary>
    public static AutomationElement Buscar(Sesion s, string selector)
    {
        string? nombre = null, id = null, tipo = null;
        foreach (var parte in selector.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = parte.IndexOf('=');
            if (i < 0) throw new ErrorUsuario($"Selector inválido '{parte}' (use nombre=..., id=... o tipo=...).");
            var valor = parte[(i + 1)..].Trim('"');
            switch (parte[..i].Trim().ToLowerInvariant())
            {
                case "nombre" or "name": nombre = valor; break;
                case "id": id = valor; break;
                case "tipo" or "type": tipo = valor; break;
                default: throw new ErrorUsuario($"Selector inválido '{parte}' (use nombre=..., id=... o tipo=...).");
            }
        }

        var raiz = AutomationElement.FromHandle(s.Objetivo.Ventana);
        var limite = DateTime.Now.AddMilliseconds(s.EsperaMs);
        while (true)
        {
            // Primero coincidencia exacta del nombre; si no, que lo contenga.
            AutomationElement? hallado = null;
            foreach (var exacto in new[] { true, false })
            {
                hallado = Recorrer(raiz).FirstOrDefault(e => Coincide(e, nombre, id, tipo, exacto));
                if (hallado != null) break;
            }
            if (hallado != null) return hallado;
            if (DateTime.Now > limite) throw new ErrorUsuario($"No se encontró ningún control con '{selector}'. Use -info para ver los controles.");
            Thread.Sleep(250);
        }
    }

    static bool Coincide(AutomationElement e, string? nombre, string? id, string? tipo, bool exacto)
    {
        try
        {
            var c = e.Current;
            if (id != null && !string.Equals(c.AutomationId, id, StringComparison.OrdinalIgnoreCase)) return false;
            if (tipo != null && !c.ControlType.ProgrammaticName.EndsWith("." + tipo, StringComparison.OrdinalIgnoreCase)) return false;
            if (nombre != null && !(exacto
                    ? string.Equals(c.Name, nombre, StringComparison.OrdinalIgnoreCase)
                    : c.Name.Contains(nombre, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }
        catch (ElementNotAvailableException) { return false; }
    }

    public static IEnumerable<AutomationElement> Recorrer(AutomationElement raiz, int profundidadMax = 40)
    {
        var walker = TreeWalker.ControlViewWalker;
        var pila = new Stack<(AutomationElement, int)>();
        pila.Push((raiz, 0));
        while (pila.Count > 0)
        {
            var (e, prof) = pila.Pop();
            if (prof > 0) yield return e;
            if (prof >= profundidadMax) continue;
            var hijos = new List<AutomationElement>();
            try { for (var h = walker.GetFirstChild(e); h != null; h = walker.GetNextSibling(h)) hijos.Add(h); }
            catch (ElementNotAvailableException) { }
            for (int i = hijos.Count - 1; i >= 0; i--) pila.Push((hijos[i], prof + 1));
        }
    }

    public static IntPtr HwndDe(AutomationElement el)
    {
        var walker = TreeWalker.RawViewWalker;
        for (var e = el; e != null; e = walker.GetParent(e))
        {
            try
            {
                if (e.Current.NativeWindowHandle != 0) return e.Current.NativeWindowHandle;
            }
            catch (ElementNotAvailableException) { }
        }
        return IntPtr.Zero;
    }

    public static string Describir(AutomationElement el)
    {
        try
        {
            var c = el.Current;
            var tipo = c.ControlType.ProgrammaticName.Replace("ControlType.", "");
            return $"[{tipo}] nombre='{c.Name}'" + (c.AutomationId.Length > 0 ? $" id='{c.AutomationId}'" : "");
        }
        catch (ElementNotAvailableException) { return "[control ya no disponible]"; }
    }

    /// <summary>Árbol de controles con el centro de cada uno en coordenadas del área cliente (para -info).</summary>
    public static void ImprimirArbol(Objetivo obj, int profundidadMax)
    {
        var origen = obj.APantalla(0, 0);
        void Rec(AutomationElement e, int prof)
        {
            var walker = TreeWalker.ControlViewWalker;
            for (var h = walker.GetFirstChild(e); h != null; h = walker.GetNextSibling(h))
            {
                string extra = "";
                try
                {
                    var r = h.Current.BoundingRectangle;
                    if (!r.IsEmpty)
                        extra = $" centro=({(int)(r.X + r.Width / 2) - origen.X},{(int)(r.Y + r.Height / 2) - origen.Y})";
                    var patrones = h.GetSupportedPatterns()
                        .Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", ""))
                        .Where(n => n is "Invoke" or "Toggle" or "SelectionItem" or "ExpandCollapse" or "Value");
                    if (patrones.Any()) extra += $" [{string.Join(",", patrones)}]";
                    if (!h.Current.IsEnabled) extra += " (deshabilitado)";
                }
                catch (ElementNotAvailableException) { continue; }
                Console.WriteLine($"{new string(' ', prof * 2)}{Describir(h)}{extra}");
                if (prof < profundidadMax) Rec(h, prof + 1);
            }
        }
        Rec(AutomationElement.FromHandle(obj.Ventana), 1);
    }
}
