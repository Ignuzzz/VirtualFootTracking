# 03 — Guia de Instalação, Configuração e Compilação (Quest 3S)

## 1. Ambiente Preparado e APK Já Compilado

- **Android Platform-Tools (`adb` `37.0.3`):** instalado em `C:\Users\Bruno\AppData\Local\Android\platform-tools` e incluído no `PATH` do usuário.
- **Unity CLI (`1.0.1`) e Unity Hub (`4.0.1`):** instalados e configurados em `C:\Users\Bruno\Unity\Editors`.
- **Unity Editor `6000.0.85f2` LTS + Android SDK/NDK/OpenJDK 17:** instalado e autenticado (`Unity Personal`).
- **GitHub CLI (`gh`) + Repositório Git local (`main`):** preparado para publicar Releases do APK no GitHub via `Tools\publish_github_release.ps1`.
- **APK Foot Tracking Engine Quest 3S (`46,51 MB`):** compilado com sucesso em `d:\BodyTrackingVRX\VirtualFootTracking\Builds\VirtualFootTracking_v0.2_Engine.apk` e validado via `aapt2`:
  - `package`: `com.virtualfoottracking.quest` (`versionName=0.2.0`, `versionCode=2`, `minSdkVersion=29`, `targetSdkVersion=36`, `arm64-v8a`)
  - `launchable-activity`: `com.unity3d.player.UnityPlayerGameActivity` (`enabled=true`, `com.oculus.intent.category.VR`)
  - Permissões: `com.oculus.permission.BODY_TRACKING`, `com.oculus.permission.USE_SCENE`, `com.oculus.permission.USE_ANCHOR_API`
  - Bibliotecas nativas (`lib/arm64-v8a/`): `libil2cpp.so`, `libOVRPlugin.so`, `libmrutilitykitshared.so`, `libUnityOpenXR.so`, `libopenxr_loader.so`.

---

## 2. Publicar o APK no GitHub para Download Direto (PC ou Quest)

O arquivo `.apk` não deve ser comitado diretamente no histórico Git por causa do tamanho binário; em vez disso, o script `Tools\publish_github_release.ps1` envia o código-fonte para o GitHub e anexa `VirtualFootTracking_v0.2_Engine.apk` como asset de Release com link direto HTTPS:

```powershell
gh auth login --web
cd d:\BodyTrackingVRX
.\VirtualFootTracking\Tools\publish_github_release.ps1
```

---

## 3. Recompilação Local (Opcional / Futura)

1. Para recompilar após alterar scripts:
   ```powershell
   cd d:\BodyTrackingVRX\VirtualFootTracking
   .\Tools\build_apk.ps1
   ```
   - **Saída:** `d:\BodyTrackingVRX\VirtualFootTracking\Builds\VirtualFootTracking_v0.2_Engine.apk`
   - **Log completo:** `d:\BodyTrackingVRX\VirtualFootTracking\Logs\build_apk.log`

*(Ou pelo Unity Editor: abra `d:\BodyTrackingVRX\VirtualFootTracking` e execute `VirtualFootTracking > 3. Build Quest 3S Engine APK`.)*

### Preparar o Meta Quest 3S antes de instalar
1. Ative o **Modo de Desenvolvedor** no aplicativo Meta Horizon do celular pareado ao Quest 3S.
2. No Quest 3S, vá em **Configurações > Ambiente Físico / Espaço (Space Setup)** e conclua o mapeamento do cômodo (garantindo que o **chão** esteja marcado corretamente para a Scene API / MRUK).
3. Para instalar via cabo USB, conecte o headset e aceite o prompt **Permitir depuração USB** dentro do Quest 3S (`adb devices -l`), ou baixe o APK pelo link da Release no navegador/SideQuest.

---

## 4. Instalar e Testar no Quest 3S

Com o Quest 3S conectado via USB e autorizado no `adb`:

```powershell
cd d:\BodyTrackingVRX\VirtualFootTracking
.\Tools\deploy_quest.ps1 -TailLogcat
```

Ao abrir o app (`v0.2.0`) no headset:
1. Aceite as permissões de **Rastreamento Corporal (`BODY_TRACKING`)** e **Dados Espaciais / Cena (`USE_SCENE`)**.
2. Navegue nos **5 painéis interativos** apontando o controle Touch Plus e apertando o **Gatilho (Trigger)** (`MENU`, `LIVE`, `CALIB`, `CONFIG`, `DIAG`):
   - **Menu Principal:** status ao vivo de Body Tracking, Scene Mapping e os 4 Trackers.
   - **Calibração:** captura automática guiada em pé (`2,5s`) + ajustes manuais de altura, coxa, canela, quadril, distância dos pés, largura da postura e yaw inicial.
   - **Live Tracking:** barra superior compacta com alternância A/B (`MOTOR: PROPRIO IK` vs `RAW SDK`), `ESPELHO`, `TRAVA CHAO (Foot Lock)` e `KALMAN`.
   - **Configurações:** toggles do engine + ajuste fino de offsets individuais (`-1cm/+1cm` em X/Y/Z para `L-FOOT`, `R-FOOT`, `L-KNEE`, `R-KNEE`).
   - **Diagnóstico:** FPS, frequência do tracking (Hz), latência (ms), matriz de posições/velocidades/altura do chão e flags `G` (Grounded), `L` (FootLocked), `I` (IK Solved), `C` (FloorClamped).
3. Atalhos físicos no controle Touch Plus:
   - **`Menu / Start` (Esquerdo):** Alterna entre **Menu Principal** e **Live Tracking**.
   - **`A` (Direito):** Inicia calibração guiada em pé (`2,5s`).
   - **`B` (Direito):** Liga/desliga o espelho `1,35m` à frente.
   - **`X` (Esquerdo):** Recarrega a sala MRUK do dispositivo.
   - **`Y` (Esquerdo):** Alterna A/B entre **Motor Próprio (IK + Kalman)** e **SDK Bruto**.
   - **Click do Analógico Direito:** Recentraliza a UI e o espelho.
