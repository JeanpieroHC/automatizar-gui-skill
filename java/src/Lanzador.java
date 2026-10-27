import com.sun.tools.attach.VirtualMachine;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;

/**
 * Carga el agente en la JVM indicada y muestra lo que escribió. Uso: Lanzador pid título orden args...
 * Carga una copia temporal del jar: la JVM objetivo la deja abierta mientras viva, y así el jar del
 * skill se puede recompilar. Las copias de llamadas anteriores se borran cuando ya no están en uso.
 */
public class Lanzador {
    public static void main(String[] args) throws Exception {
        if (args.length < 3) {
            System.err.println("uso: Lanzador <pid> <título de ventana o \"\"> <orden> [parámetros...]");
            System.exit(2);
        }
        Path jar = Paths.get(Lanzador.class.getProtectionDomain().getCodeSource().getLocation().toURI());
        Path tmp = Paths.get(System.getProperty("java.io.tmpdir"));
        File[] viejas = tmp.toFile().listFiles((d, n) -> n.startsWith("agente-gui-") && n.endsWith(".jar"));
        if (viejas != null) {
            for (File f : viejas) {
                f.delete();
            }
        }
        Path copia = Files.createTempFile("agente-gui-", ".jar");
        Files.copy(jar, copia, StandardCopyOption.REPLACE_EXISTING);
        Path salida = Files.createTempFile("agente", ".txt");
        StringBuilder a = new StringBuilder(copia.toString()).append('\u001E').append(salida);
        for (int i = 1; i < args.length; i++) {
            a.append('\u001F').append(args[i]);
        }
        VirtualMachine vm = VirtualMachine.attach(args[0]);
        try {
            vm.loadAgent(copia.toString(), a.toString());
        } finally {
            vm.detach();
        }
        String r = new String(Files.readAllBytes(salida), StandardCharsets.UTF_8);
        Files.deleteIfExists(salida);
        new java.io.PrintStream(System.out, true, "UTF-8").print(r);
        System.exit(r.contains("error:") ? 2 : 0);
    }
}
