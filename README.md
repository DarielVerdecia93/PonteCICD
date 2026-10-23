# ENSE CI

CI/CD local para desplegar `ENMC.BalcaoUnico.UI.WebApplication` por rama, sin tocar el repositorio
ni la base de datos (la BD la administra otra parte: aqui solo se configura su connection string).

Panel: **http://localhost:5080**

## Como funciona

- Cada **entorno** = una rama + un puerto (8090, 8091, ...) + una configuracion de build (`Desenvolvimento`, `Qualidade`...).
  La rama se elige en un selector visual (buscador, agrupada por `feature/`, `bugfix/`...), del **remoto** o de **tu repo local**.
- Deteccion de cambios:
  - remoto: `git ls-remote` cada `pollIntervalSec` (20 s); solo descarga si alguna rama se movio;
  - repo local (`localRepoPath`): se vigila `.git`, asi que un commit local (sin push) se detecta en un par de segundos.
- Con *auto-deploy*, cuando la rama cambia se espera `quietSec` (15 s) sin mas commits y se despliega **el ultimo**.
  Si un commit falla, no se reintenta hasta el siguiente commit o hasta que pulses *Desplegar*.
- El panel se actualiza en vivo (Server-Sent Events): paso actual del deploy, cuenta atras del auto-deploy,
  commits pendientes de desplegar y avisos al terminar cada ejecucion.
- La compilacion dinamica de ASP.NET se hace en `envs\<entorno>\aspnet-temp` en vez de `%TEMP%`, donde
  **Smart App Control** bloquea las DLL generadas (error 0x800711C7).
- Antivirus (Kaspersky): excluir esta carpeta, `service\EnseCI.exe` e `iisexpress.exe`, o bloqueara los procesos del CI.
- Cada deploy:
  1. exporta el commit con `git archive` (codigo limpio, el repo de trabajo no se toca);
  2. compila y publica con MSBuild (`/t:WebPublish`), sin `DBScripts` y con los ficheros extra de `includeInPublish`;
  3. aplica `Web.local.config` (connection string de la BD administrada, URLs...);
  4. para el sitio, **guarda la version actual en un zip**, sustituye los ficheros y arranca IIS Express;
  5. comprueba que responde; si no, **restaura automaticamente** el zip anterior.
- **Rollback**: restaura cualquier zip. Antes guarda tambien la version actual, asi que un rollback se puede deshacer.
- Se guardan los ultimos `keepBackups` zips por entorno. `UploadFiles` (datos de usuarios) no se toca ni entra en los zips.
- Los jobs se ejecutan de uno en uno; cada ejecucion queda en `runs\` (json + log), visible en el panel.

## Estructura

```
ci.json                    configuracion global + entornos (el panel la edita)
Web.local.config           (opcional) transformacion XDT comun a todos los entornos
engine\                    motor PowerShell: deploy.ps1, rollback.ps1, site.ps1
envs\<entorno>\site        sitio desplegado (lo sirve IIS Express)
envs\<entorno>\backups     zips <entorno>_<fecha>_<commit>_<motivo>.zip
envs\<entorno>\Web.local.config   (opcional) transformacion propia del entorno
mirror.git                 mirror del remoto
runs\                      historial de ejecuciones
service\                   EnseCI publicado (servicio de Windows)
src\EnseCI                 codigo del servicio (.NET 10)
```

## Base de datos administrada

Copia `engine\Web.local.config.example` como `Web.local.config` (comun) o como
`envs\<entorno>\Web.local.config` (por entorno) y pon la connection string. Se aplica en el siguiente deploy.

## Instalar como servicio

En PowerShell **como administrador**:

```powershell
.\install-service.ps1
```

Pide la contrasena de tu cuenta de Windows: el servicio corre con tu usuario para usar tus credenciales de git
(Azure DevOps) e IIS Express. Desinstalar: `.\install-service.ps1 -Uninstall`.

Sin instalarlo, tambien se puede ejecutar en una consola: `.\service\EnseCI.exe`.

## Actualizar el servicio tras cambiar su codigo

```powershell
Stop-Service EnseCI
dotnet publish src\EnseCI -c Release -o service
Start-Service EnseCI
```

## Uso desde consola (sin panel)

```powershell
.\engine\deploy.ps1   -Environment develop               # punta de la rama
.\engine\deploy.ps1   -Environment develop -Ref 1a2b3c4  # commit concreto
.\engine\rollback.ps1 -Environment develop               # ultimo backup
.\engine\site.ps1     -Environment develop -Action restart
```
