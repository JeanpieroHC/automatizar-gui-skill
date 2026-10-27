import java.awt.*;
import java.awt.event.*;
import java.io.*;
import java.lang.instrument.Instrumentation;
import java.nio.charset.StandardCharsets;
import javax.swing.*;

/**
 * Agente que se carga en la JVM del programa (Attach API) y entrega eventos de mouse y teclado
 * directamente a la cola de eventos de AWT/Swing. No usa java.awt.Robot ni SendInput: el cursor y
 * el teclado del usuario no se tocan. Java descarta los clics enviados por mensajes de ventana
 * porque mira la ventana bajo el cursor real; aquí el evento ya nace dentro de Java.
 *
 * Argumentos (separados por \u001F): salida, título de la ventana (o "" para la de más arriba), orden, parámetros...
 *   clic x y n            n clics (n=2 doble clic) en coordenadas de pantalla de Java
 *   derecho x y           clic derecho (menú contextual)
 *   tecla x y TECLA       KEY_PRESSED/RELEASED; TECLA en formato KeyStroke ("ENTER", "ctrl S", "TAB")
 *   escribir x y texto    KEY_TYPED por carácter
 *   (x y = "foco" "-" para el componente con el foco)
 */
public class Agente {
    public static void agentmain(String args, Instrumentation inst) {
        String[] todos = args.split("\u001F", -1);
        String salida = todos[0];
        titulo = todos[1].toLowerCase();
        // a[1] es la orden y a[2..] sus parámetros
        String[] a = new String[todos.length - 1];
        a[0] = salida;
        System.arraycopy(todos, 2, a, 1, todos.length - 2);
        StringBuffer log = new StringBuffer();
        java.util.concurrent.CountDownLatch hecho = new java.util.concurrent.CountDownLatch(1);
        // invokeLater y no invokeAndWait: si el evento abre un diálogo modal, dispatchEvent no
        // vuelve hasta que el diálogo se cierra y el lanzador quedaría colgado
        SwingUtilities.invokeLater(() -> {
            try {
                ejecutar(a, log);
            } catch (Exception e) {
                log.append("error: ").append(e).append('\n');
            } finally {
                hecho.countDown();
            }
        });
        try {
            if (!hecho.await(1500, java.util.concurrent.TimeUnit.MILLISECONDS)) {
                log.append("ok (el evento sigue en curso: probablemente abrió un diálogo modal)\n");
            }
        } catch (InterruptedException ignored) {
        }
        try (Writer w = new OutputStreamWriter(new FileOutputStream(salida), StandardCharsets.UTF_8)) {
            w.write(log.toString());
        } catch (IOException ignored) {
        }
    }

    static void ejecutar(String[] a, StringBuffer log) throws Exception {
        String orden = a[1];
        // Órdenes por texto, sin coordenadas: funcionan aunque el Access Bridge esté trabado
        switch (orden) {
            case "arbol": {
                Window w = ventana();
                log.append("ventana: '").append(tituloDe(w)).append("' ").append(w.getClass().getName()).append('\n');
                volcar(w, 0, log);
                return;
            }
            case "pulsar": {
                // pulsar "texto" [n]: botón, casilla u opción de menú cuyo texto o tooltip es ese
                java.util.List<AbstractButton> bs = new java.util.ArrayList<>();
                buscarBotones(ventana(), a[2].toLowerCase(), bs);
                int n = a.length > 3 && !a[3].isEmpty() ? Integer.parseInt(a[3]) : 1;
                if (bs.size() < n) {
                    throw new IllegalStateException("no hay ningún botón o menú con el texto '" + a[2] + "' (" + bs.size() + ")");
                }
                AbstractButton b = bs.get(n - 1);
                log.append("pulsado: ").append(b.getClass().getSimpleName()).append(" '").append(b.getText()).append("'\n");
                // Más tarde y no aquí: si abre un diálogo modal, doClick no vuelve hasta cerrarlo
                SwingUtilities.invokeLater(b::doClick);
                return;
            }
            case "texto": {
                // texto n "valor": reemplaza el texto del n-ésimo campo editable visible de la ventana
                java.util.List<javax.swing.text.JTextComponent> ts = new java.util.ArrayList<>();
                buscarTextos(ventana(), ts);
                int n = Integer.parseInt(a[2]);
                if (ts.size() < n) {
                    throw new IllegalStateException("la ventana tiene " + ts.size() + " campos de texto editables");
                }
                ts.get(n - 1).setText(a[3]);
                log.append("texto ").append(n).append(" = '").append(a[3]).append("'\n");
                return;
            }
            case "combo": {
                // combo n "texto": elige en el n-ésimo JComboBox visible la primera opción que contiene el texto
                java.util.List<JComboBox<?>> cs = new java.util.ArrayList<>();
                buscarCombos(ventana(), cs);
                int n = Integer.parseInt(a[2]);
                if (cs.size() < n) {
                    throw new IllegalStateException("la ventana tiene " + cs.size() + " combos");
                }
                JComboBox<?> cb = cs.get(n - 1);
                StringBuilder ops = new StringBuilder();
                for (int i = 0; i < cb.getItemCount(); i++) {
                    String op = String.valueOf(cb.getItemAt(i));
                    if (op.toLowerCase().contains(a[3].toLowerCase())) {
                        cb.setSelectedIndex(i);
                        log.append("combo ").append(n).append(" = '").append(op).append("'\n");
                        return;
                    }
                    ops.append(" | ").append(op);
                }
                throw new IllegalStateException("el combo " + n + " no tiene '" + a[3] + "'; opciones:" + ops);
            }
            default:
        }
        Component c;
        Point p;
        if (a[2].equals("foco")) {
            c = KeyboardFocusManager.getCurrentKeyboardFocusManager().getFocusOwner();
            if (c == null) {
                throw new IllegalStateException("ningún componente tiene el foco");
            }
            p = new Point(c.getWidth() / 2, c.getHeight() / 2);
        } else {
            int[] l = aLogico(Integer.parseInt(a[2]), Integer.parseInt(a[3]));
            int x = l[0], y = l[1];
            Object[] cp = componenteEn(x, y);
            if (cp == null) {
                throw new IllegalStateException("no hay ninguna ventana Java visible en (" + x + "," + y + ")");
            }
            c = (Component) cp[0];
            p = (Point) cp[1];
        }
        log.append("componente: ").append(c.getClass().getName()).append(" en (").append(p.x).append(',').append(p.y).append(")\n");
        long t = System.currentTimeMillis();
        switch (orden) {
            case "clic": {
                int n = Integer.parseInt(a[4]);
                // Cada evento va en su propia tarea de la cola de Swing: lo que el programa encola al
                // procesar uno (p. ej. abrir otra ventana y ocultar esta en el PRESSED del doble clic)
                // se ejecuta antes del siguiente. Si el componente deja de verse, con un ratón real
                // los eventos que faltan ya no le llegarían: se dejan de enviar.
                int[] ids = {MouseEvent.MOUSE_PRESSED, MouseEvent.MOUSE_RELEASED, MouseEvent.MOUSE_CLICKED};
                java.util.List<int[]> eventos = new java.util.ArrayList<>();
                for (int i = 1; i <= n; i++) {
                    for (int id : ids) eventos.add(new int[]{id, i});
                }
                encadenar(c, p, t, eventos, 0);
                log.append("programados ").append(eventos.size()).append(" eventos\n");
                break;
            }
            case "evento": {
                // Diagnóstico: un solo evento de ratón (PRESSED, RELEASED o CLICKED) con clickCount n
                int id = a[4].equals("PRESSED") ? MouseEvent.MOUSE_PRESSED : a[4].equals("RELEASED") ? MouseEvent.MOUSE_RELEASED : MouseEvent.MOUSE_CLICKED;
                raton(c, id, t, id == MouseEvent.MOUSE_PRESSED ? InputEvent.BUTTON1_DOWN_MASK : 0, p, Integer.parseInt(a[5]), false, MouseEvent.BUTTON1);
                break;
            }
            case "derecho":
                raton(c, MouseEvent.MOUSE_PRESSED, t, InputEvent.BUTTON3_DOWN_MASK, p, 1, false, MouseEvent.BUTTON3);
                raton(c, MouseEvent.MOUSE_RELEASED, t, 0, p, 1, true, MouseEvent.BUTTON3);
                raton(c, MouseEvent.MOUSE_CLICKED, t, 0, p, 1, false, MouseEvent.BUTTON3);
                break;
            case "tecla": {
                KeyStroke ks = KeyStroke.getKeyStroke(a[4]);
                if (ks == null) {
                    throw new IllegalArgumentException("tecla no reconocida: " + a[4]);
                }
                Component destino = destinoTeclado(c);
                destino.dispatchEvent(new KeyEvent(destino, KeyEvent.KEY_PRESSED, t, ks.getModifiers(), ks.getKeyCode(), KeyEvent.CHAR_UNDEFINED));
                char ch = caracter(ks.getKeyCode());
                if (ch != KeyEvent.CHAR_UNDEFINED) {
                    destino.dispatchEvent(new KeyEvent(destino, KeyEvent.KEY_TYPED, t, ks.getModifiers(), KeyEvent.VK_UNDEFINED, ch));
                }
                destino.dispatchEvent(new KeyEvent(destino, KeyEvent.KEY_RELEASED, t, ks.getModifiers(), ks.getKeyCode(), KeyEvent.CHAR_UNDEFINED));
                break;
            }
            case "escribir": {
                Component destino = destinoTeclado(c);
                for (char ch : a[4].toCharArray()) {
                    destino.dispatchEvent(new KeyEvent(destino, KeyEvent.KEY_TYPED, t, 0, KeyEvent.VK_UNDEFINED, ch));
                }
                break;
            }
            default:
                throw new IllegalArgumentException("orden desconocida: " + orden);
        }
        log.append("ok\n");
    }

    static String titulo = "";

    /** La ventana visible cuyo título contiene `titulo` (la de más arriba si hay varias o no se dio título). */
    static Window ventana() {
        Window[] ws = Window.getWindows();
        for (int i = ws.length - 1; i >= 0; i--) {
            String t = tituloDe(ws[i]);
            if (ws[i].isShowing() && (titulo.isEmpty() || (t != null && t.toLowerCase().contains(titulo)))) {
                return ws[i];
            }
        }
        throw new IllegalStateException("no hay ninguna ventana visible cuyo título contenga '" + titulo + "'");
    }

    static String textoDe(Component c) {
        if (c instanceof AbstractButton) return ((AbstractButton) c).getText();
        if (c instanceof JLabel) return ((JLabel) c).getText();
        if (c instanceof javax.swing.text.JTextComponent) return ((javax.swing.text.JTextComponent) c).getText();
        if (c instanceof JComboBox) return String.valueOf(((JComboBox<?>) c).getSelectedItem());
        return null;
    }

    static void volcar(Component c, int nivel, StringBuffer log) {
        if (!c.isVisible()) return;
        String t = textoDe(c);
        String tip = c instanceof JComponent ? ((JComponent) c).getToolTipText() : null;
        boolean util = t != null && !t.isEmpty() || tip != null || c instanceof AbstractButton
                || c instanceof javax.swing.text.JTextComponent || c instanceof JTable || c instanceof JTree || c instanceof JList;
        if (util) {
            Point s = c.isShowing() ? c.getLocationOnScreen() : new Point(-1, -1);
            log.append("  ".repeat(Math.min(nivel, 12))).append(c.getClass().getSimpleName());
            if (t != null) log.append(" '").append(t.replace('\n', ' '), 0, Math.min(t.length(), 70)).append('\'');
            if (tip != null) log.append(" tip='").append(tip).append('\'');
            if (c instanceof JTable) log.append(" filas=").append(((JTable) c).getRowCount()).append(" columnas=").append(((JTable) c).getColumnCount());
            log.append(" (").append(s.x).append(',').append(s.y).append(' ').append(c.getWidth()).append('x').append(c.getHeight()).append(")\n");
        }
        if (c instanceof JMenu) {
            for (Component h : ((JMenu) c).getMenuComponents()) volcar(h, nivel + 1, log);
        } else if (c instanceof Container) {
            for (Component h : ((Container) c).getComponents()) volcar(h, nivel + 1, log);
        }
    }

    static void buscarBotones(Component c, String texto, java.util.List<AbstractButton> out) {
        if (c instanceof AbstractButton && c.isEnabled()) {
            AbstractButton b = (AbstractButton) c;
            String t = b.getText() == null ? "" : b.getText().toLowerCase();
            String tip = b.getToolTipText() == null ? "" : b.getToolTipText().toLowerCase();
            if (t.equals(texto) || tip.equals(texto)) out.add(b);
        }
        if (c instanceof JMenu) {
            for (Component h : ((JMenu) c).getMenuComponents()) buscarBotones(h, texto, out);
        } else if (c instanceof Container) {
            for (Component h : ((Container) c).getComponents()) buscarBotones(h, texto, out);
        }
    }

    static void buscarCombos(Component c, java.util.List<JComboBox<?>> out) {
        if (!c.isShowing()) return;
        if (c instanceof JComboBox) out.add((JComboBox<?>) c);
        if (c instanceof Container) {
            for (Component h : ((Container) c).getComponents()) buscarCombos(h, out);
        }
    }

    static void buscarTextos(Component c, java.util.List<javax.swing.text.JTextComponent> out) {
        if (!c.isShowing()) return;
        if (c instanceof javax.swing.text.JTextComponent && ((javax.swing.text.JTextComponent) c).isEditable()) {
            out.add((javax.swing.text.JTextComponent) c);
        }
        if (c instanceof Container) {
            for (Component h : ((Container) c).getComponents()) buscarTextos(h, out);
        }
    }

    /**
     * Las coordenadas llegan en píxeles físicos (las del Java Access Bridge y las capturas); Swing usa
     * píxeles lógicos cuando Windows escala la pantalla. En Windows cada monitor conserva su origen
     * físico y escala el resto: lógico = origen + (físico - origen) / escala.
     */
    static int[] aLogico(int px, int py) {
        for (GraphicsDevice d : GraphicsEnvironment.getLocalGraphicsEnvironment().getScreenDevices()) {
            GraphicsConfiguration gc = d.getDefaultConfiguration();
            Rectangle b = gc.getBounds();
            double sx = gc.getDefaultTransform().getScaleX(), sy = gc.getDefaultTransform().getScaleY();
            if (px >= b.x && py >= b.y && px < b.x + b.width * sx && py < b.y + b.height * sy) {
                return new int[]{(int) Math.round(b.x + (px - b.x) / sx), (int) Math.round(b.y + (py - b.y) / sy)};
            }
        }
        return new int[]{px, py};
    }

    static String tituloDe(Window w) {
        if (w instanceof Frame) return ((Frame) w).getTitle();
        if (w instanceof Dialog) return ((Dialog) w).getTitle();
        return "";
    }

    /** El componente más profundo bajo un punto de pantalla, en la ventana cuyo título contiene `titulo`. */
    static Object[] componenteEn(int x, int y) {
        Window[] ws = Window.getWindows();
        for (int i = ws.length - 1; i >= 0; i--) {
            Window w = ws[i];
            String t = tituloDe(w);
            if (!w.isShowing() || (!titulo.isEmpty() && (t == null || !t.toLowerCase().contains(titulo)))) {
                continue;
            }
            Point o = w.getLocationOnScreen();
            if (x < o.x || y < o.y || x >= o.x + w.getWidth() || y >= o.y + w.getHeight()) {
                continue;
            }
            Component c = SwingUtilities.getDeepestComponentAt(w, x - o.x, y - o.y);
            if (c == null) {
                c = w;
            }
            Point pc = new Point(x, y);
            SwingUtilities.convertPointFromScreen(pc, c);
            return new Object[]{c, pc};
        }
        return null;
    }

    static Component destinoTeclado(Component c) {
        if (c.isFocusable()) {
            c.requestFocusInWindow();
        }
        return c;
    }

    static char caracter(int vk) {
        switch (vk) {
            case KeyEvent.VK_ENTER: return '\n';
            case KeyEvent.VK_TAB: return '\t';
            case KeyEvent.VK_SPACE: return ' ';
            case KeyEvent.VK_BACK_SPACE: return '\b';
            case KeyEvent.VK_ESCAPE: return 27;
            default: return KeyEvent.CHAR_UNDEFINED;
        }
    }

    static void encadenar(Component c, Point p, long t, java.util.List<int[]> eventos, int k) {
        if (k >= eventos.size() || !c.isShowing()) return;
        int id = eventos.get(k)[0], clics = eventos.get(k)[1];
        raton(c, id, t, id == MouseEvent.MOUSE_PRESSED ? InputEvent.BUTTON1_DOWN_MASK : 0, p, clics, false, MouseEvent.BUTTON1);
        SwingUtilities.invokeLater(() -> encadenar(c, p, t, eventos, k + 1));
    }

    static void raton(Component c, int id, long t, int mods, Point p, int clics, boolean popup, int boton) {
        Point s = new Point(p);
        SwingUtilities.convertPointToScreen(s, c);
        c.dispatchEvent(new MouseEvent(c, id, t, mods, p.x, p.y, s.x, s.y, clics, popup, boton));
    }
}
