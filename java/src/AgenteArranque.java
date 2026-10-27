import java.io.File;
import java.lang.instrument.Instrumentation;
import java.net.URL;
import java.net.URLClassLoader;

/**
 * Punto de entrada del agente. La JVM objetivo deja cargada para siempre la clase Agent-Class (y
 * desde la primera copia del jar), así que esta clase solo carga Agente con un cargador nuevo, hijo
 * de la plataforma y no del sistema, desde la copia del jar de ESTA llamada, que llega delante de
 * los argumentos (separada por \u001E). Así una versión nueva del agente se usa sin reiniciar el programa.
 */
public class AgenteArranque {
    public static void agentmain(String args, Instrumentation inst) throws Exception {
        int corte = args.indexOf('\u001E');
        URL jar = new File(args.substring(0, corte)).toURI().toURL();
        URLClassLoader cl = new URLClassLoader(new URL[]{jar}, ClassLoader.getPlatformClassLoader());
        Class<?> c = cl.loadClass("Agente");
        c.getMethod("agentmain", String.class, Instrumentation.class).invoke(null, args.substring(corte + 1), inst);
    }
}
