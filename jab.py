"""
jab.py: maneja programas Java (Swing/AWT) por el Java Access Bridge, sin tocar el mouse ni el
teclado del usuario. Es el equivalente de automate.exe para Java, que ignora los clics y teclas
enviados por mensajes de ventana.

Uso:
  python jab.py -p <pid|nombre> [-w "texto del título"] [-prof N] [-ocultos] [-v] <acciones...>

  -ocultos incluye los componentes no visibles (p. ej. las opciones de un menú sin desplegar,
  que se pueden invocar con -click sin abrir el menú).

Acciones (en orden, se pueden repetir):
  -ventanas                         lista las ventanas Java del proceso
  -info [prof]                      árbol de componentes: rol 'nombre' [acciones] (x,y,ancho,alto) estados
  -click <selector>                 ejecuta la acción principal (click/clic) del componente
  -accion <selector> "acción"       ejecuta una acción concreta (las de -info)
  -set <selector> "texto"           reemplaza el texto de un campo
  -focus <selector>                 le da el foco
  -select <selector> <índice>       selecciona el hijo N (listas, pestañas, árboles, tablas)
  -wait <ms>

Acciones por el agente Java (java\agente.jar, se carga en la JVM del programa con la Attach API
y crea los eventos dentro de Swing; tampoco toca el mouse ni el teclado del usuario). Sirven para lo
que la accesibilidad no ofrece: abrir con doble clic, menús contextuales, editar celdas de tablas.
  -raton <selector|x,y> [n]         n clics de ratón en el centro del componente (o en x,y de pantalla)
  -dobleclic <selector|x,y>         doble clic
  -derecho <selector|x,y>           clic derecho (menú contextual)
  -tecla <selector|foco> TECLA      tecla en formato KeyStroke de Java: ENTER, TAB, ESCAPE, "ctrl S", F2
  -escribir <selector|foco> "texto" escribe carácter a carácter (KEY_TYPED)

Selector: "nombre=OK", "rol=push button;nombre=OK", "nombre~=parte del nombre"; con "#N" al final
elige la N-ésima coincidencia (desde 1): "rol=text#2". -w cambia de ventana (diálogos) y se
mantiene para las acciones siguientes; se puede repetir entre acciones.

Requisitos: JAB activado (jabswitch -enable, y reiniciar el programa Java) y
pip install java-access-bridge-wrapper. Códigos: 0 bien, 2 error de uso, 1 error inesperado.
"""
import ctypes, glob, os, queue, sys, threading, time
from ctypes import byref, wintypes

sys.stdout.reconfigure(encoding='utf-8')


def buscar_dll():
    if os.environ.get('RC_JAVA_ACCESS_BRIDGE_DLL'):
        return
    candidatos = []
    for raiz in (os.environ.get('JAVA_HOME', ''), r'C:\Program Files\Java\*', r'C:\Program Files\Eclipse Adoptium\*',
                 r'C:\Program Files\Microsoft\jdk*', r'C:\Program Files (x86)\Java\*'):
        if raiz:
            candidatos += glob.glob(os.path.join(raiz, 'bin', 'WindowsAccessBridge-64.dll'))
    candidatos += glob.glob(r'C:\Windows\System32\WindowsAccessBridge-64.dll')
    if not candidatos:
        sys.exit('error: no se encontró WindowsAccessBridge-64.dll; instale un JDK o defina RC_JAVA_ACCESS_BRIDGE_DLL')
    os.environ['RC_JAVA_ACCESS_BRIDGE_DLL'] = sorted(candidatos)[-1]


buscar_dll()
from JABWrapper.jab_types import AccessibleActionsToDo, JavaObject  # noqa: E402
from JABWrapper.jab_wrapper import JavaAccessBridgeWrapper  # noqa: E402


class ErrorUso(Exception):
    pass


class Nodo:
    """Componente Java. Se recorre a mano: el ContextTree de JABWrapper aborta todo el árbol
    cuando un solo componente no expone su texto."""

    def __init__(self, puente, contexto, nivel, prof):
        self.puente, self.context, self.ancestry = puente, contexto, nivel
        self.context_info = ci = puente.get_context_info(contexto)
        self._acciones = None
        self.children = []
        if nivel < prof:
            for i in range(ci.childrenCount):
                try:
                    hijo = puente.get_child_context(contexto, i)
                    if hijo:
                        self.children.append(Nodo(puente, hijo, nivel + 1, prof))
                except Exception:
                    pass

    @property
    def rol(self):
        return self.context_info.role_en_US

    @property
    def visible(self):
        return 'showing' in self.context_info.states_en_US

    def get_actions(self):
        if self._acciones is None:
            self._acciones = {}
            if self.context_info.accessibleAction:
                try:
                    acc = self.puente.get_accessible_actions(self.context)
                    for i in range(acc.actionsCount):
                        self._acciones[acc.actionInfo[i].name.lower()] = acc.actionInfo[i]
                except Exception:
                    pass
        return list(self._acciones)

    def do_action(self, nombre):
        self.get_actions()
        if nombre not in self._acciones:
            raise ErrorUso(f"'{self.context_info.name}' no tiene la acción '{nombre}' (tiene: {', '.join(self._acciones) or 'ninguna'}).")
        todo = AccessibleActionsToDo(actionsCount=1)
        todo.actions[0] = self._acciones[nombre]
        self.puente.do_accessible_actions(self.context, todo)

    def insert_text(self, texto):
        self.puente.set_text_contents(self.context, texto)

    def request_focus(self):
        self.puente.request_focus(self.context)


def arrancar_puente():
    """El puente necesita un bucle de mensajes en su propio hilo."""
    tubo = queue.Queue()

    def bucle():
        try:
            puente = JavaAccessBridgeWrapper(ignore_callbacks=True)
            tubo.put(puente)
            msg = wintypes.MSG()
            while ctypes.windll.user32.GetMessageW(byref(msg), 0, 0, 0) > 0:
                ctypes.windll.user32.TranslateMessage(byref(msg))
                ctypes.windll.user32.DispatchMessageW(byref(msg))
        except Exception as e:
            tubo.put(e)

    threading.Thread(target=bucle, daemon=True).start()
    puente = tubo.get()
    if isinstance(puente, Exception):
        raise puente
    time.sleep(0.5)  # el puente descubre las JVM de forma asíncrona
    return puente


def resolver_pid(p):
    if p.isdigit():
        return int(p)
    import subprocess
    nombre = p if p.lower().endswith('.exe') else p + '.exe'
    salida = subprocess.run(['tasklist', '/FI', f'IMAGENAME eq {nombre}', '/FO', 'CSV', '/NH'],
                            capture_output=True, text=True).stdout
    for linea in salida.splitlines():
        partes = [x.strip('"') for x in linea.split('","')]
        if len(partes) > 1 and partes[1].isdigit():
            return int(partes[1])
    raise ErrorUso(f"No hay ningún proceso '{p}'.")


class Sesion:
    def __init__(self, puente, pid, prof, verbose):
        self.puente, self.pid, self.prof, self.v = puente, pid, prof, verbose
        self.titulo = None
        self.ocultos = False

    def ventanas(self):
        return [w for w in self.puente.get_windows() if w.pid == self.pid and w.title]

    def usar_ventana(self, texto=None):
        texto = texto if texto is not None else self.titulo
        cand = self.ventanas()
        if texto:
            cand = [w for w in cand if texto.lower() in w.title.lower()]
        # La visible primero: Swing deja ocultas (no destruidas) las ventanas cerradas con el mismo título
        visibles = [w for w in cand if ctypes.windll.user32.IsWindowVisible(w.hwnd)]
        cand = visibles or cand
        if not cand:
            raise ErrorUso(f"El proceso {self.pid} no tiene una ventana Java" + (f" cuyo título contenga '{texto}'." if texto else '.'))
        w = cand[0]
        p = self.puente
        p._hwnd = w.hwnd
        p._vmID = ctypes.c_long()
        p.context = JavaObject()
        p._wab.getAccessibleContextFromHWND(p._hwnd, byref(p._vmID), byref(p.context))
        if not p._vmID or not p.context:
            raise ErrorUso(f"La ventana '{w.title}' no expone accesibilidad (¿se arrancó el programa antes de activar el JAB?).")
        return w

    def arbol(self, prof=None):
        self.usar_ventana()
        return Nodo(self.puente, self.puente.context, 0, prof if prof is not None else self.prof)

    @staticmethod
    def nodos(raiz):
        pila = [raiz]
        while pila:
            n = pila.pop(0)
            yield n
            pila[0:0] = list(n.children)

    def buscar(self, selector):
        sel, _, idx = selector.partition('#')
        idx = int(idx) if idx else 1
        reglas = []
        for parte in sel.split(';'):
            parte = parte.strip()
            if not parte:
                continue
            for op in ('~=', '='):
                if op in parte:
                    k, val = parte.split(op, 1)
                    k = {'nombre': 'name', 'rol': 'role', 'descripcion': 'description', 'descripción': 'description'}.get(k.strip().lower(), k.strip().lower())
                    reglas.append((k, op, val.strip().lower()))
                    break
            else:
                raise ErrorUso(f"Selector mal formado: '{parte}'")
        hallados = []
        for n in self.nodos(self.arbol()):
            ci = n.context_info
            if not n.visible and not self.ocultos:
                continue
            ok = True
            for k, op, val in reglas:
                actual = (n.rol if k == 'role' else str(getattr(ci, k, '') or '')).strip().lower().replace('&', '')
                if (op == '=' and actual != val) or (op == '~=' and val not in actual):
                    ok = False
                    break
            if ok:
                hallados.append(n)
        if len(hallados) < idx:
            raise ErrorUso(f"No se encontró ningún componente con '{selector}' ({len(hallados)} coincidencias). Use -info para ver los componentes.")
        n = hallados[idx - 1]
        if self.v:
            ci = n.context_info
            print(f"    encontrado {ci.role} '{ci.name}' en ({ci.x},{ci.y}) {n.get_actions()}")
        return n

    def imprimir(self, prof):
        w = self.usar_ventana()
        print(f"Ventana : '{w.title}' (hwnd {w.hwnd:#x}, pid {w.pid})")
        def rec(n):
            if n.ancestry > 0 and not n.visible and not self.ocultos:
                return
            ci = n.context_info
            # Nodos de árbol sin desplegar: el puente los da sin rol, nombre ni posición
            if n.rol == 'unknown' and not ci.name and ci.x < 0:
                return
            acc = n.get_actions()
            nombre = (ci.name or '').replace('\n', ' ')[:80]
            if not nombre and ci.description:
                # Botones con solo icono: se seleccionan con descripcion=...
                nombre = f"' descripcion='{ci.description[:60]}"
            # Los campos de texto traen ~60 acciones de edición: basta con saber que admite -set
            if len(acc) > 6:
                acc = [a for a in acc if a in ('insert-content', 'select-all', 'notify-field-accept')] + [f'+{len(acc) - 3} de edición']
            extra = f" [{', '.join(acc)}]" if acc else ''
            # Sin estructura ni nombre no aporta nada: se imprime solo a sus hijos
            if n.rol in ('panel', 'filler', 'layered pane', 'root pane', 'viewport') and not nombre and not acc:
                for h in n.children:
                    rec(h)
                return
            print(f"{'  ' * n.ancestry}{n.rol} '{nombre}'{extra} ({ci.x},{ci.y},{ci.width}x{ci.height}) hijos={ci.childrenCount}"
                  f"{' [' + ci.states_en_US + ']' if self.v else ''}")
            for h in n.children:
                rec(h)
        rec(self.arbol(prof))


AGENTE = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'java', 'agente.jar')


def agente(s, orden, objetivo, *extra):
    """Ejecuta una orden del agente Java sobre el centro del componente (o un punto x,y o 'foco')."""
    import subprocess
    if not os.path.isfile(AGENTE):
        raise ErrorUso(f"No existe {AGENTE}; compílelo con java\\compilar.ps1")
    if orden in ('pulsar', 'arbol', 'texto'):
        punto = [objetivo] if objetivo is not None else []
    elif objetivo == 'foco':
        punto = ['foco', '-']
    elif objetivo and ',' in objetivo and objetivo.replace(',', '').replace('-', '').isdigit():
        punto = objetivo.split(',')
    else:
        ci = s.buscar(objetivo).context_info
        if ci.x < 0 or ci.width <= 0:
            raise ErrorUso(f"'{objetivo}' no está en pantalla (despliegue o desplace antes).")
        punto = [str(ci.x + ci.width // 2), str(ci.y + ci.height // 2)]
    import shutil
    java = os.path.join(os.path.dirname(os.environ['RC_JAVA_ACCESS_BRIDGE_DLL']), 'java.exe')
    if not os.path.isfile(java):
        # La DLL puede venir de System32; el lanzador necesita un JDK (módulo jdk.attach)
        jdks = sorted(glob.glob(r'C:\Program Files\Java\jdk*\bin\java.exe') + glob.glob(r'C:\Program Files\Eclipse Adoptium\jdk*\bin\java.exe'))
        java = jdks[-1] if jdks else shutil.which('java')
    titulo = s.usar_ventana().title
    r = subprocess.run([java, '-jar', AGENTE, str(s.pid), titulo, orden, *punto, *extra],
                       capture_output=True, text=True, encoding='utf-8', errors='replace')
    salida = (r.stdout + r.stderr).strip()
    if s.v:
        print('    agente: ' + salida.replace('\n', ' | '))
    if r.returncode != 0:
        raise ErrorUso(f"agente Java: {salida}")


def accion_principal(n):
    acc = n.get_actions()
    for preferida in ('click', 'clic', 'hacer clic', 'pulsar', 'press', 'toggleexpand'):
        if preferida in acc:
            return preferida
    if acc:
        return acc[0]
    raise ErrorUso(f"El componente '{n.context_info.name}' no tiene acciones; pruebe -focus o -select en su padre.")


def main(args):
    if '-p' not in args:
        raise ErrorUso(__doc__)
    i = args.index('-p')
    pid = resolver_pid(args[i + 1])
    del args[i:i + 2]
    prof, verbose = 25, False
    if '-prof' in args:
        i = args.index('-prof'); prof = int(args[i + 1]); del args[i:i + 2]
    if '-v' in args:
        args.remove('-v'); verbose = True

    s = Sesion(arrancar_puente(), pid, prof, verbose)
    if '-ocultos' in args:
        args.remove('-ocultos'); s.ocultos = True
    k = 0
    while k < len(args):
        a = args[k]
        sig = lambda n=1: args[k + n] if k + n < len(args) else None  # noqa: E731
        if verbose and a.startswith('-'):
            print(f"    {a} {' '.join(x for x in args[k + 1:k + 3] if x and not x.startswith('-'))}")
        if a == '-w':
            s.titulo = sig(); s.usar_ventana(); k += 2
        elif a == '-ventanas':
            for w in s.ventanas():
                vis = 'visible' if ctypes.windll.user32.IsWindowVisible(w.hwnd) else 'oculta'
                print(f"   {w.hwnd:#x} '{w.title}' ({vis})")
            k += 1
        elif a == '-info':
            p = sig()
            if p and p.isdigit():
                s.imprimir(int(p)); k += 2
            else:
                s.imprimir(s.prof); k += 1
        elif a == '-click':
            # Por el agente y no con la acción del puente: si el clic abre un diálogo modal, la
            # acción del puente no vuelve hasta que se cierra y deja trabado el puente entero
            n = s.buscar(sig()); ci = n.context_info
            if os.path.isfile(AGENTE) and ci.x >= 0 and ci.width > 0 and n.rol not in ('menu item', 'menu'):
                agente(s, 'clic', f"{ci.x + ci.width // 2},{ci.y + ci.height // 2}", '1')
            elif os.path.isfile(AGENTE) and ci.name:
                agente(s, 'pulsar', ci.name)
            else:
                n.do_action(accion_principal(n))
            k += 2
        elif a == '-accion':
            n = s.buscar(sig()); n.do_action(sig(2).lower()); k += 3
        elif a == '-set':
            n = s.buscar(sig()); n.insert_text(sig(2)); k += 3
        elif a == '-focus':
            n = s.buscar(sig()); n.request_focus(); k += 2
        elif a == '-select':
            n = s.buscar(sig())
            s.puente.clear_accessible_selection_from_context(n.context)
            s.puente.add_accessible_selection_from_context(n.context, int(sig(2))); k += 3
        elif a == '-raton':
            n = sig(2)
            if n and n.isdigit():
                agente(s, 'clic', sig(), n); k += 3
            else:
                agente(s, 'clic', sig(), '1'); k += 2
        elif a == '-dobleclic':
            agente(s, 'clic', sig(), '2'); k += 2
        elif a == '-derecho':
            agente(s, 'derecho', sig()); k += 2
        elif a == '-tecla':
            agente(s, 'tecla', sig(), sig(2)); k += 3
        elif a == '-escribir':
            agente(s, 'escribir', sig(), sig(2)); k += 3
        elif a == '-wait':
            time.sleep(int(sig()) / 1000); k += 2
        else:
            raise ErrorUso(f"Acción desconocida: {a}")
        time.sleep(0.2)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main(sys.argv[1:]))
    except ErrorUso as e:
        print(f"error: {e}", file=sys.stderr)
        sys.exit(2)
    except Exception as e:
        import traceback
        traceback.print_exc()
        sys.exit(1)
