# 03 — Guia de Instalação, Configuração e Compilação (Quest 3S)

## 1. Ambiente Preparado e APK Já Compilado

- **Android Platform-Tools (`adb` `37.0.3`):** instalado em `C:\Users\Bruno\AppData\Local\Android\platform-tools` e incluído no `PATH` do usuário.
- **Unity CLI (`1.0.1`) e Unity Hub (`4.0.1`):** instalados e configurados em `C:\Users\Bruno\Unity\Editors`.
- **Unity Editor `6000.0.85f2` LTS + Android SDK/NDK/OpenJDK 17:** instalado e autenticado (`Unity Personal`).
- **GitHub CLI (`gh`) + Repositório Git local (`main`):** preparado para publicar Releases do APK no GitHub via `Tools\publish_github_release.ps1`.
- **APK Depth + Foot Tracking Engine Quest 3S (`46,54 MB`):** compilado com sucesso em `d:\BodyTrackingVRX\VirtualFootTracking\Builds\VirtualFootTracking_v0.3.2_DepthFix2.apk` e validado via `aapt2`:
  - `package`: `com.virtualfoottracking.quest` (`versionName=0.3.2`, `versionCode=5`, `minSdkVersion=29`, `targetSdkVersion=36`, `arm64-v8a`)
  - `launchable-activity`: `com.unity3d.player.UnityPlayerGameActivity` (`enabled=true`, `com.oculus.intent.category.VR`)
  - Permissões: `com.oculus.permission.BODY_TRACKING`, `com.oculus.permission.USE_SCENE`, `com.oculus.permission.USE_ANCHOR_API`
  - Bibliotecas nativas (`lib/arm64-v8a/`): `libil2cpp.so`, `libOVRPlugin.so`, `libmrutilitykitshared.so`, `libUnityOpenXR.so`, `libopenxr_loader.so`.

---

## 2. Publicar o APK no GitHub para Download Direto (PC ou Quest)

O arquivo `.apk` não deve ser comitado diretamente no histórico Git por causa do tamanho binário; em vez disso, o script `Tools\publish_github_release.ps1` envia o código-fonte para o GitHub e anexa `VirtualFootTracking_v0.3.2_DepthFix2.apk` como asset de Release com link direto HTTPS:

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
   - **Saída:** `d:\BodyTrackingVRX\VirtualFootTracking\Builds\VirtualFootTracking_v0.3.2_DepthFix2.apk`
   - **Log completo:** `d:\BodyTrackingVRX\VirtualFootTracking\Logs\build_apk.log`

*(Ou pelo Unity Editor: abra `d:\BodyTrackingVRX\VirtualFootTracking` e execute `VirtualFootTracking > 3. Build Quest 3S Depth APK`.)*

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

Ao abrir o app (`v0.3.2`) no headset:
1. Aceite as permissões de **Rastreamento Corporal (`BODY_TRACKING`)** e **Dados Espaciais / Cena (`USE_SCENE`)**.
2. Navegue nos **5 painéis interativos** apontando o controle Touch Plus e apertando o **Gatilho (Trigger)** (`MENU`, `LIVE`, `CALIB`, `CONFIG`, `DIAG`):
   - **Menu Principal:** status ao vivo de Body Tracking, Scene & Depth 3D e os 4 Trackers.
   - **Calibração:** captura automática guiada em pé (`2,5s` + captura automática de fundo Depth `1,5s`), botão dedicado `CAPTURAR FUNDO DEPTH (1.5s)` e ajustes manuais.
   - **Live Tracking:** barra superior compacta com `MOTOR A/B`, `ESPELHO`, `TRAVA CHAO`, `KALMAN`, **`DEPTH LEG: ON/OFF`**, **`NUVEM 3D: ON/OFF`** e **`CAPTURAR FUNDO (1.5s)`**.
   - **Configurações:** toggles do engine + controle Depth (`Limpar Fundo Depth`) + ajuste fino de offsets individuais.
   - **Diagnóstico:** FPS, Hz, telemetria Depth (`Raw`, `Fg`, `L/R`, `Fundo vox`), matriz de coordenadas e legenda de cores (`VERDE=SDK_OBS`, **`AMARELO=DEPTH_3D`**, `CIANO=SDK_GEN`, `LARANJA=ENG_IK`, `MAGENTA=ENG_PRED`).
3. **Como testar chutes e elevação de joelho via Depth 3D no Quest 3S:**
   - Em pé em uma área livre (ou após clicar em **`CAPTURAR FUNDO (1.5s)`** / clicar o **Analógico Esquerdo** se houver sofás/cadeiras próximos), abra a tela **LIVE**.
   - Execute chutes frontais/diagonais ou eleve o joelho à frente (olhando ligeiramente para baixo ajuda o sensor frontal).
   - Observe as esferas amarelas da **Nuvem 3D** alinhadas à sua perna real e os trackers mudando de **Ciano (`SDK_GEN`)** para **Amarelo (`DEPTH_3D`)**.
4. Atalhos físicos no controle Touch Plus:
   - **`Menu / Start` (Esquerdo):** Alterna entre **Menu Principal** e **Live Tracking**.
   - **`A` (Direito):** Inicia calibração guiada em pé (`2,5s`) + captura de fundo Depth.
   - **`Click do Analógico Esquerdo`:** Inicia **Captura de Fundo Depth (`1,5s`)**.
   - **`B` (Direito):** Liga/desliga o espelho `1,35m` à frente.
   - **`X` (Esquerdo):** Recarrega a sala MRUK do dispositivo.
   - **`Y` (Esquerdo):** Alterna A/B entre **Motor Próprio (Depth 3D + IK + Kalman)** e **SDK Bruto**.
   - **Click do Analógico Direito:** Recentraliza a UI e o espelho.
