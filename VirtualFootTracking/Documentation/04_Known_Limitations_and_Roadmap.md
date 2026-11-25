# 04 — Limitações Conhecidas e Roadmap Técnico

## 1. Limitações Conhecidas de Hardware e SDK (Meta Quest 3S)

1. **Cobertura Física da Depth API vs. Oclusão fora do Frustum (`v0.3.3`):**
   - O Quest 3S possui câmeras de profundidade voltadas para a **frente** (e não câmeras 360° ou apontadas diretamente abaixo da vertical `-90°` quando o usuário olha para o horizonte).
   - O detector `EnvironmentDepthLegDetector` utiliza varredura em **2 passagens** (Passo 1 grosso `9×7` no frustum `-36°..+36°` H × `-54°..+4°` V + Passo 2 fino `3×9` focado nas pernas ativas, alcance anatômico `0,20 m .. coxa+canela+0,22 m` e aprendizado de fundo além de `+0,30 m` até `2,8 m`). Portanto:
     - **Detecta diretamente em 3D (`DEPTH_3D`):** chutes esticados frontais/diagonais, joelho erguido dobrado e pés elevados à frente em ambas as pernas (Esquerda e Direita).
     - **Não observa diretamente (`SdkEstimatedGenerativeLegs`):** chutes para trás das costas, aberturas laterais extremas fora do FOV das câmeras, ou pés diretamente sob a vertical ao olhar para o teto. Nesses momentos, a engine retorna suavemente (`~0,18 s`) para `Generative Legs + IK`.
   - Em cômodos apertados com paredes/móveis próximos (< `1,0 m`), o aprendizado contínuo de voxels estáticos, o filtro planar e o MRUK reduzem drasticamente detecções fantasmas; caso algum móvel apareça pela primeira vez já dentro do alcance da perna, usar **CAPTURAR FUNDO DEPTH (1,5s)** (ou clique no **Analógico Esquerdo**) fixa o ambiente imediatamente.

2. **Ausência de câmeras ópticas dedicadas aos pés (`OVRBody`):**
   - As juntas inferiores (`70..83`: quadris/coxas, joelhos, tornozelos e pontas dos pés) do `OVRBody` são inferidas pelo modelo **Generative Legs** da Meta (`SdkEstimatedGenerativeLegs`). O modo **Depth Leg Fusion (`v0.3.0`)** complementa essa limitação sempre que a perna entra no campo visual das câmeras frontais.

3. **Pré-requisito de Mapeamento de Cômodo (MRUK / Scene API):**
   - `MRUKRoom.FloorAnchors` e `EnvironmentRaycastManager` requerem `com.oculus.permission.USE_SCENE` (e, para âncoras de sala/móveis, **Room/Space Setup** concluído no Quest 3S).
   - Quando a sala não foi escaneada ou a permissão foi negada, o piso entra explicitamente em `FloorDataSource.TrackingOriginFallback` (altura `trackingSpace.position.y`), indicado em âmbar no HUD e no piso virtual.

4. **Iluminação e Oclusão:**
   - Tanto o IOBT quanto a reconstrução de profundidade perdem qualidade em ambientes muito escuros ou contra superfícies transparentes/espelhadas.

5. **Limites de Predição e Comunicação Externa (`v0.3.0`):**
   - `AdaptiveKalmanMotionPredictor` extrapola perdas temporárias por no máximo `0,35s` com decaimento exponencial de velocidade (`EngineMotionPredicted`) e depois retorna `Invalid` para não simular dados inexistentes.
   - A transmissão externa via Wi-Fi UDP/USB para o driver SteamVR OpenVR (`GenericTracker`) pertence à Etapa 4.

---

## 2. Roadmap de Desenvolvimento

### Etapa 1 — Validação Diagnóstica no Quest 3S (**Concluída em `v0.1.0-diag`**)
- Compilado `VirtualFootTracking_v0.1_Diag.apk` (`46,46 MB`) e validado pelo usuário no Quest 3S em hardware real (esqueleto IOBT + Generative Legs em pé e sentado + piso/passthrough).

### Etapa 2 — Foot Tracking Engine Próprio (**Concluída em `v0.2.0-engine`**)
- Compilado `VirtualFootTracking_v0.2_Engine.apk` (`46,51 MB`) e validado no Quest 3S (~90% de precisão em caminhada/apoio no piso, confirmando que `Generative Legs` não enxergava chutes nem elevação isolada de joelhos).
- **Solver Biomecânico & IK (`ProprietaryFootTrackingSolver` + `AnalyticLegIkSolver`) + Kalman Adaptativo + UI VR completa.**

### Etapa 3 — Detecção Física de Chutes & Joelhos via Depth API (**Concluída/Refinada em `v0.3.3-depth`**)
- **`EnvironmentDepthLegDetector` (`v0.3.3`):** Base bilateral sem rotação lateral de osso (`Left` e `Right` independentes), sonda fina 2-pass (`9×7 + 3×9`), proteção MRUK livre de `GLOBAL_MESH`, transição contínua entre joelho dobrado e estendido + **extensão colinear** da canela para chutes rápidos.
- **Anti-Flicker Multi-Estágio (`v0.3.3`):** `OneEuroFilter3D` + histerese de entrada + *dropout hold* de `140 ms` no `ProprietaryFootTrackingSolver` + `R` adaptativo para Depth no `AdaptiveKalmanMotionPredictor`.
- **Fusão Multi-Sensor no `ProprietaryFootTrackingSolver`:** destravamento automático do piso (`IsFootLocked = false`) e proveniência **`TrackingDataSource.DepthObserved` (`DEPTH_3D`, Amarelo)**.
- **Visualização & UI:** nuvem 3D ao vivo de pontos de profundidade nas pernas (`136` pontos) e botões de captura/limpeza de fundo estático no headset.

### Etapa 4 — Streaming PC + Driver SteamVR OpenVR
- Implementar `UdpTrackerOutput` (Wi-Fi UDP baixa latência + fallback USB `adb forward`) serializando `TrackerPacketFrame` (`VFT1`).
- Sincronização de timestamps Quest ↔ PC e detecção de perda/reordenação de pacotes.
- Driver nativo C++ para SteamVR (OpenVR `IServerTrackedDeviceProvider`) expondo os quatro dispositivos como `TrackedDeviceClass_GenericTracker` (Pés e Joelhos) compatíveis com VRChat e jogos de combate PCVR.
