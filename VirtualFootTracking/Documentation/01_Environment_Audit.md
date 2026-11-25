# 01 — Auditoria do Ambiente e Matriz de Compatibilidade

**Data da auditoria:** 2026-11-24  
**Workspace:** `d:\BodyTrackingVRX\VirtualFootTracking`

---

## 1. Hardware e Sistema Operacional (Máquina de Desenvolvimento)

| Item | Valor Observado |
| :--- | :--- |
| **Sistema Operacional** | Microsoft Windows 10 Pro 64-bit (`10.0.19045`) |
| **GPU** | AMD Radeon RX 6600 (Driver `32.0.21045.29003`) |
| **Espaço em disco — Unidade `C:`** | `292,84 GB` livres (`154,04 GB` usados) |
| **Espaço em disco — Unidade `D:`** | `37,27 GB` livres (`328,19 GB` usados) |
| **Privilégio do shell de execução** | Usuário padrão (`IsAdmin: False`) |

> **Decisão de armazenamento:** Como a unidade `D:` possui apenas `37,27 GB` livres e o Unity Editor `6000.0.85f2` + módulos Android ocupam ~`5,92 GB` em download (e > `15 GB` descompactados + caches), o diretório de instalação dos editores Unity foi apontado para `C:\Users\Bruno\Unity\Editors`, mantendo o projeto leve em `d:\BodyTrackingVRX\VirtualFootTracking`.

---

## 2. Inventário de Dependências (Antes vs. Depois da Preparação)

| Componente | Estado Inicial | Estado Atual | Localização / Versão | Observação |
| :--- | :--- | :--- | :--- | :--- |
| **Python** | Instalado | Instalado | `3.14.10` (`C:\Users\Bruno\AppData\Local\Programs\Python\Python314\python.exe`) | Pronto |
| **Git** | Instalado | Instalado | `2.56.1.windows.1` (`C:\Program Files\Git\cmd\git.exe`) | Pronto |
| **Meta Horizon Link** | Instalado | Instalado | `1.115.0` (`C:\Program Files\Meta Horizon\`) | Pronto |
| **Android Platform-Tools (`adb`)** | Ausente | **Instalado** | `37.0.3` (`C:\Users\Bruno\AppData\Local\Android\platform-tools\adb.exe`) | Adicionado ao `PATH` do usuário |
| **Unity CLI (`unity.exe`)** | Ausente | **Instalado** | `1.0.1` (`C:\Users\Bruno\AppData\Local\Unity\bin\unity.exe`) | Adicionado ao `PATH` do usuário |
| **Unity Hub** | Ausente | **Instalado** | `4.0.1.65535` (MSIX `UnityTechnologies.UnityHub`) | Instalado via `winget` |
| **Unity Editor `6000.0.85f2` LTS + Android SDK/NDK r27c/OpenJDK 17** | Ausente | **Instalado** | `C:\Users\Bruno\Unity\Editors\6000.0.85f2\Editor\Unity.exe` | Android SDK (`34`–`37.0`), Build-Tools `36.0.0`, NDK `27.2.12479018`, OpenJDK `17` |
| **Licença Unity (Unity ID)** | Ausente | **Ativa** | Conta Unity (`brunoviciobr@gmail.com`) | `Unity Personal` (`Assigned`) |
| **APK Diagnóstico Quest 3S (Fase 1)** | Ausente | **Validado (`v0.1.0`)** | `Builds\VirtualFootTracking_v0.1_Diag.apk` (`46,46 MB`) | Validado no Quest 3S real pelo usuário |
| **APK Foot Tracking Engine (Fase 2)** | Ausente | **Validado (`v0.2.0`)** | `Builds\VirtualFootTracking_v0.2_Engine.apk` (`46,51 MB`) | Validado no Quest 3S (~90% precisão piso/caminhada; chutes exigiram Depth API) |
| **APK Depth + Engine Fusion (Fase 3)** | Ausente | **Testado (`v0.3.0`) -> Corrigido (`v0.3.1`)** | `Builds\VirtualFootTracking_v0.3.1_DepthFix.apk` (`46,54 MB`) | `versionName=0.3.1`, `versionCode=4`, `arm64-v8a`, `BODY_TRACKING`, `USE_SCENE`, `UnityPlayerGameActivity` (`enabled=true`) |
| **Meta Quest 3S via USB/ADB** | Desconectado | **Não conectado** | `adb devices -l` -> lista vazia | Necessário conectar cabo USB, ativar Modo Desenvolvedor e autorizar depuração RSA no headset |

---

## 3. Matriz Oficial de Versões Compatíveis (Verificada em Novembro/2026)

Consultada diretamente via API oficial de releases da Unity (`services.api.unity.com`), registro UPM da Unity (`packages.unity.com`), registro scoped da Meta (`npm.developer.oculus.com`) e repositório oficial `oculus-samples/Unity-Movement`:

| Pacote / Ferramenta | Versão Alvo | Motivo Técnico da Escolha |
| :--- | :--- | :--- |
| **Unity Editor** | `6000.0.85f2` LTS (`b028303d8e7d`) | Meta XR SDK `209.0.0` declara explicitamente `"unity": "6000.0"` e `"unityRelease": "66f2"` (Unity 2022.3 foi descontinuado para recursos novos). |
| **Android Build Support + NDK + OpenJDK** | `android-ndk-r27c`, `OpenJDK 17.0.18+8`, `SDK Platforms 34–37`, `Build-Tools 36.0.0`, `CMake 3.22.1` | Dependências exatas resolvidas pelo módulo `android` da Unity `6000.0.85f2`. |
| **Meta XR Core SDK** (`com.meta.xr.sdk.core`) | `209.0.0` | Contém `OVRBody`, `OVRSkeleton`, `OVRPlugin.GetBodyState4`, `BodyJointSet.FullBody` (84 juntas), `BodyTrackingFidelity2.High` (IOBT), `OVRPermissionsRequester`, `OVRPassthroughLayer` e `MetaXRFeature`. |
| **Meta MR Utility Kit** (`com.meta.xr.mrutilitykit`) | `209.0.0` | Fornece `MRUK`, `MRUKRoom`, `MRUKAnchor`, `FloorAnchors`, `PlaneBoundary2D` e `Raycast` nativo para detecção do piso real. |
| **Unity OpenXR Plugin** (`com.unity.xr.openxr`) | `1.19.0` | Backend XR oficial exigido pelo `OVRBody` (que desabilita a si mesmo se `OVRPlugin.nativeXrApi != OpenXR`). |
| **XR Plugin Management** (`com.unity.xr.management`) | `4.7.0` | Gerenciamento do `OpenXRLoader` no target Android. |
| **XR Hands** (`com.unity.xr.hands`) | `1.7.2` | Dependência declarada em `com.meta.xr.sdk.core@209.0.0`. |
| **Unity Input System / uGUI** | `1.14.2` / `2.0.0` | Suporte de entrada OpenXR e Canvas World-Space sem dependência externa de fontes TMP. |

### Pacotes deliberadamente omitidos nesta Fase 1
- `com.unity.xr.oculus` (legado; substituído por OpenXR + Meta XR Core).
- `com.unity.xr.meta-openxr` (redundante/conflitante quando usado com `MetaXRFeature` do `com.meta.xr.sdk.core` 209.0.0).
- `com.meta.xr.sdk.interaction` e `com.meta.xr.sdk.movement` (não necessários para a cena diagnóstica de leitura crua de IOBT/FullBody e evitam peso extra de retargeting antes da validação em hardware).
