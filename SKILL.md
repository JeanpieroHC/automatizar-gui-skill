---
name: automatizar-gui
description: Maneja programas de escritorio de Windows (.exe con interfaz gráfica) enviándoles clics, texto y teclas con automate.exe, sin mover el mouse ni usar el teclado reales del usuario, que puede seguir usando la PC. Úsala siempre que haya que abrir un .exe con ventanas y actuar sobre él, probar o verificar una app de escritorio (WPF, WinForms, Win32, UWP, Electron, Qt...), llenar formularios, pulsar botones, navegar menús, mandar atajos de teclado o cualquier tarea que requiera clics o entradas de teclado en un programa. No usar SendKeys, SendInput, SetForegroundWindow ni mover el cursor en su lugar.
---

# Automatizar programas con interfaz gráfica

`publish\automate.exe` (en esta carpeta de la skill) entrega los eventos **solo al proceso
objetivo**: el cursor y el teclado del usuario no se tocan y su ventana activa no cambia. Por eso
nunca se usan `SendKeys`, `SendInput`, `SetForegroundWindow`, `AppActivate` ni nada que mueva el
mouse o traiga ventanas al frente: interrumpen al usuario y escriben en la ventana que él esté usando.

En los comandos de abajo `<skill>` es la carpeta de esta skill (`%USERPROFILE%\.claude\skills\automatizar-gui`).

## Antes de empezar

- Si no existe `<skill>\publish\automate.exe`, compílalo con
  `powershell -ExecutionPolicy Bypass -File <skill>\compilar.ps1` (usa el SDK de .NET 8 o, si no está, Docker).
- **Antivirus**: Avast lo detecta como `IDP.Generic` (falso positivo por inyectar teclas en otro
  proceso) y lo pone en cuarentena. Si el .exe desaparece o no arranca, **díselo al usuario** para
  que lo restaure y agregue `<skill>\publish\*` a las excepciones. No intentes esquivar al antivirus
  (renombrar, ofuscar, empaquetar distinto).
- Si el programa usa datos reales (base de datos, archivos del usuario), pruébalo sobre una copia
  o un entorno de prueba, no sobre los datos del usuario.

## Flujo de trabajo

1. **Abrir y mirar**: `automate -p <ruta\programa.exe> -info`. Si el programa no está abierto, lo
   arranca y espera su ventana. Muestra la tecnología detectada, el método elegido y los controles
   con su `id`, su `nombre` y el `centro=(x,y)` en coordenadas del área cliente.
2. **Actuar** con `-v` para ver qué método se usó en cada paso. Prefiere los selectores en este
   orden: `id=`, luego `nombre=` y, solo si no hay otra cosa, coordenadas `x=,y=`.
3. **Verificar** sin traer la ventana al frente:
   `powershell -ExecutionPolicy Bypass -File <skill>\captura.ps1 -Proceso <nombre|pid> -Salida <png en el scratchpad>`
   y lee el PNG. También imprime qué programa está en primer plano y dónde está el cursor; compáralo
   antes y después para confirmar que no se tocó la entrada del usuario. `-info` sirve para releer
   el estado de los controles (textos, habilitado o deshabilitado).
4. **Cerrar** al terminar lo que tú abriste (`Stop-Process`), salvo que el usuario quiera dejarlo abierto.

## Programas que corren como administrador (instaladores, herramientas de sistema)

Windows (UIPI) no deja que un proceso normal haga clic, capture ni cierre uno elevado. Síntomas: el
clic "se envía" pero la ventana no cambia, la captura sale en blanco, `captura.ps1` imprime
`AVISO: ... corre como administrador`, `-info` dice `no se pudieron leer los módulos (Win32Exception)`
o `Stop-Process` da "Access is denied". En ese caso:

- Repite los pasos con `<skill>\elevado.ps1`, que relanza `automate` o `captura` como administrador.
  Windows muestra su ventana de UAC y **el usuario decide** si da el permiso; avísale antes en una
  línea ("te va a aparecer el aviso de UAC, dale Sí"). Nunca intentes saltarte el UAC.
- Cada llamada es un aviso de UAC, así que junta varios pasos en una llamada separándolos con `--`:
  ```
  powershell -ExecutionPolicy Bypass -File <skill>\elevado.ps1 captura -Proceso 7596 -Titulo Instalar -Salida C:\t\p1.png -- automate -p pid=7596 -v -click nombre=Siguiente -wait 1500 -- captura -Proceso 7596 -Titulo Instalar -Salida C:\t\p2.png
  ```
  Imprime la salida de cada paso (precedida por `> herramienta ...`) y termina con el código del último.
  Código 3: el usuario rechazó el UAC; no insistas, ofrécele hacerlo él a mano.
  Código 4: el proceso elevado no ejecutó nada (casi siempre el antivirus); el guion queda en
  `<skill>\pasos\` para revisarlo. Díselo al usuario; no cambies el script para esquivar al antivirus.
- `elevado.ps1` escribe los pasos en un `.ps1` legible en `<skill>\pasos\` y lo ejecuta con `-File`
  en una ventana minimizada. No lo cambies a `-EncodedCommand`, ventana oculta ni
  `-ExecutionPolicy Bypass`: Avast bloquea ese `powershell.exe` elevado por
  `IDP.HELU.PSE95 - Command line detection`, aunque la carpeta de la skill esté en excepciones.
- Para cerrar un proceso elevado no hay `Stop-Process` normal: usa el botón Cancelar/Salir del
  programa con `elevado.ps1 automate ...`.
- Si se prevé que el programa pedirá elevación (un instalador con `requireAdministrator` o Inno
  Setup que se relanza como admin), arráncalo directamente con `elevado.ps1 automate -p <ruta> -info`
  para ahorrar un intento fallido.

## Programas Java (Swing/AWT): `jab.py` y el agente Java

`automate` detecta Java (clases `SunAwtFrame`/`SunAwtDialog`, módulo `jvm.dll`) pero **Java ignora
sus clics y teclas**: AWT mira la ventana bajo el cursor *real* (`msg.pt`) y descarta el resto. Para
Java se usan, sin tocar tampoco el ratón ni el teclado del usuario:

- `python <skill>\jab.py`: Java Access Bridge (accesibilidad). Lista componentes y pone texto.
- `<skill>\java\agente.jar`: agente que `jab.py` carga en la JVM con la Attach API y que crea los
  eventos de ratón/teclado *dentro* de Swing. Lo usan `-click`, `-raton`, `-dobleclic`, `-derecho`,
  `-tecla`, `-escribir`. Se recompila con `powershell -File <skill>\java\compilar.ps1` (necesita
  un JDK con `javac`); el programa no hace falta reiniciarlo, cada llamada carga una copia nueva.

Preparación (una vez): `jabswitch -enable` (está en `bin\` del JDK; se revierte con `-disable`),
`pip install java-access-bridge-wrapper`, y **reiniciar el programa Java** después de activar el JAB.
Arranca el programa con `-WorkingDirectory` en su carpeta de instalación (algunos buscan ahí su
configuración y, si no, abren un diálogo pidiéndola).

```
python jab.py -p <pid|nombre> [-w "título"] [-ocultos] [-v] <acciones...>
  -ventanas | -info [prof]                 ventanas Java / árbol de componentes con sus acciones
  -click <selector>                        pulsa (por el agente: no se traba si abre un diálogo modal)
  -set <selector> "texto"                  reemplaza el texto de un campo
  -accion <selector> toggleexpand          acción del puente (desplegar nodos de árbol, etc.)
  -select <selector> <i>                   selecciona el hijo i (pestañas: rol=page tab list)
  -raton <selector|x,y> [n] | -dobleclic <selector|x,y> | -derecho <selector|x,y>
  -tecla <selector|foco> ENTER | -escribir <selector|foco> "texto"
Selector: "nombre=OK", "rol=push button;nombre=OK", "nombre~=parte", "descripcion=tooltip", "#N" al final.
```

Agente directo (sirve aunque el Access Bridge esté trabado; `""` como título = ventana de más arriba,
p. ej. un menú contextual):

```
java -jar agente.jar <pid> "<título>" arbol | pulsar "texto o tooltip" [n] | texto <n> "valor"
                                      combo <n> "parte de la opción" | clic|derecho <x> <y> [n]
```

Lo aprendido con PILAR (Swing con árboles y tablas propios):

- **Nunca uses la acción del puente para algo que abra un diálogo modal** (`-accion` en un botón o
  menú "Guardar..."): la acción no vuelve hasta que el diálogo se cierra y *traba el puente entero*
  (todas las consultas siguientes devuelven vacío). `-click` ya va por el agente. Si pasa, sigue con
  el agente directo (`arbol`, `pulsar`, `texto`) hasta cerrar el diálogo; el puente se destraba solo.
- Las opciones de menú cerradas se ven con `-ocultos` y se pulsan sin abrir el menú.
- Coordenadas: las del puente y las capturas son píxeles físicos; el agente las convierte a los
  lógicos de Swing según la escala de cada monitor.
- Los nodos de árbol se abren con `-dobleclic`; las casillas de un árbol se marcan con `-raton` en
  `x+14` del nodo; las celdas de tabla suelen editarse con `-derecho` (menú contextual) + agente `pulsar`.
- Algunos programas abren una ventana por **cada** evento de doble clic: revisa `-ventanas` tras abrir
  y cierra las copias. Cuidado: en PILAR, cerrar *sin guardar* una copia abierta antes de un cambio
  revierte el proyecto a ese estado. Guarda el proyecto a menudo.
- Cuando varias ventanas comparten título, `captura.ps1 -Handle 0x...` captura una concreta;
  `PostMessage(hwnd, WM_CLOSE)` cierra ventanas Java (Java sí atiende WM_CLOSE).

## Referencia de automate.exe

```
automate -p <programa.exe | ruta\programa.exe | pid=N> [opciones] <acciones...>

Opciones:  -w <texto del título>   -modo auto|mensajes|uia   -espera <ms> (10000)   -pausa <ms> (200)   -v

Acciones (en orden, se pueden repetir):
  -click x=4,y=3[,der|medio][,doble]          clic en coordenadas del área cliente
  -click id=btnGuardar | nombre=Guardar | tipo=Button   (se combinan con ';': "tipo=Edit;nombre=DNI")
  -focus <selector>                           da el foco a un control
  -type "texto"                               escribe en el control con foco o en el último usado (agrega al final)
  -set <selector> "texto"                     reemplaza el contenido de un control
  -key ENTER | TAB | ESC | F2 | ctrl+a | shift+END | alt+F4
  -wait <ms>
  -info [profundidad]
```

Ejemplos:
```
automate -p CondorVentas.exe -click nombre=Clientes
automate -p pid=345 -click x=142,y=456
automate -p CondorVentas.exe -set id=CampoProducto "cemento" -key ctrl+a -key DELETE -type "clavo" -key ENTER
automate -p charmap.exe -click x=254,y=396 -type "hola" -key ENTER
```

Códigos de salida: 0 bien, 2 error de uso (control no encontrado, control deshabilitado...), 1 error inesperado.

## Cómo elige el método (modo auto)

Detecta la tecnología por la clase de las ventanas y por los módulos cargados (`src/Deteccion.cs`)
y elige el método (`src/Estrategias.cs`):

| Tecnología | Método |
|---|---|
| Win32, WinForms, Qt, Java, Chromium/Electron | mensajes de ventana (`PostMessage` de `WM_LBUTTONDOWN`, `WM_CHAR`, `WM_KEYDOWN`) al control que hay en el punto |
| WPF, UWP/WinUI | UI Automation (Invoke, Toggle, Select, SetValue) para clics y texto; teclas por mensajes con un `WM_SETFOCUS` simulado, porque WPF descarta el teclado si su ventana no tiene el foco |
| Juegos (Unity, Unreal, DirectInput...) | mensajes, con aviso |

- **Ctrl, Shift o Alt**: se marcan solo en el estado de teclado que comparte el programa
  (`AttachThreadInput` + `SetKeyboardState`) durante 150 ms y luego se sueltan solo las que puso
  `automate`.
- **Robo de foco**: mientras corre, `automate` bloquea los cambios de primer plano
  (`LockSetForegroundWindow`) y, si aun así el programa se pone al frente, devuelve el foco a la ventana anterior.

## Si algo no responde

- Prueba el otro método con `-modo mensajes` o `-modo uia`.
- Los botones WPF suelen tener el texto en un hijo: `nombre=Guardar` encuentra el texto y sube
  solo hasta el botón que lo contiene.
- Lanzadores: algunos .exe solo abren otro proceso (`System32\notepad.exe` abre el Notepad de la
  Store, a veces como pestaña de uno ya abierto). `automate` acepta el proceso con el mismo nombre;
  si hay varias ventanas, usa `-p pid=N` o `-w`.
- Un clic que abre un diálogo modal: el diálogo es otra ventana. Selecciónala con `-w "<título>"`.
  Los menús desplegables también son ventanas aparte (en Qt, clase `QPopup`, con el nombre del
  proceso como título): ábrelo con un clic, mira `-info` para ver su título en "Otras ventanas" y
  haz clic dentro con `-w "<título>"` y coordenadas relativas a esa ventana. Para verlo:
  `captura.ps1 -Proceso <p> -Titulo "<título>"`.
- Controles sin nombre en `-info` (Qt 4, programas antiguos sin accesibilidad): trabaja por
  coordenadas. Sácalas de una captura: con `-Escala e`, el punto (cx, cy) de la imagen es
  aproximadamente x = cx/e - borde, y = cy/e - barra de título (normalmente borde ≈ 8 y barra ≈ 31-37 px).
  Compruébalo con `-v`: muestra a qué control (HWND) llegó el clic.
- Programa ejecutado como administrador: usa `elevado.ps1` (ver la sección de programas que corren como administrador).
- **Lo que no se puede resolver con este método**: programas que leen el hardware directamente
  (Raw Input, DirectInput, `GetAsyncKeyState`; casi todos los juegos) no ven los eventos simulados.
  Díselo al usuario. La única forma sin tocar su entrada es correr el programa en otra sesión
  (Windows Sandbox, VM, otro usuario por RDP). No uses entrada real sin su permiso.

## Soportar un tipo de programa nuevo

1. Agrega la regla de detección en `src/Deteccion.cs` (prefijo de clase de ventana o nombre de módulo).
2. Mapéala en `Estrategias.Elegir` a un método existente o a una clase nueva que implemente `IMetodo`
   (`Click`, `Escribir`, `Tecla`).
3. Recompila con `compilar.ps1` y pruébala con `-info` y `-v` más `captura.ps1`.
