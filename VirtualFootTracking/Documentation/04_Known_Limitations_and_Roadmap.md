# 04 — Limitações Conhecidas e Roadmap Técnico

## 1. Limitações Conhecidas de Hardware e SDK (Meta Quest 3S)

1. **Ausência de câmeras ópticas voltadas aos pés:**
   - O Quest 3S utiliza IOBT (Inside-Out Body Tracking) para torso, ombros, cotovelos, punhos e cabeça (`SdkObserved`), mas as juntas inferiores (`70..83`: quadris/coxas, joelhos, tornozelos e pontas dos pés) são inferidas pelo modelo **Generative Legs** da Meta (`SdkEstimatedGenerativeLegs`).
   - Movimentos explosivos de combate (chutes frontais altos, chutes laterais rápidos, cruzamento de pernas e giros fechados) não são observados diretamente pelas câmeras do headset e podem apresentar atraso, amplitude reduzida ou leve deriva vertical em relação ao chão real.

2. **Pré-requisito de Mapeamento de Cômodo (MRUK / Scene API):**
   - `MRUKRoom.FloorAnchors` só retorna âncoras reais se o usuário tiver executado o **Room/Space Setup** no Quest 3S e concedido `com.oculus.permission.USE_SCENE`.
   - Quando a sala não foi escaneada ou a permissão foi negada, o sistema entra explicitamente em `FloorDataSource.TrackingOriginFallback` (altura `trackingSpace.position.y`), indicado em âmbar no HUD e no piso virtual.

3. **Iluminação e Oclusão do IOBT:**
   - Rastreamento IOBT degrada em ambientes escuros, roupas muito largas ou quando os braços ocultam o torso das câmeras inferiores do headset.

4. **Limites de Predição e Fallback (`v0.2.0`):**
   - `AdaptiveKalmanMotionPredictor` extrapola perdas temporárias por no máximo `0,35s` com decaimento exponencial de velocidade (`EngineMotionPredicted`) e depois retorna `Invalid` para não simular dados inexistentes.
   - A transmissão externa via Wi-Fi UDP/USB para o driver SteamVR OpenVR (`GenericTracker`) pertence à Etapa 3.

---

## 2. Roadmap de Desenvolvimento

### Etapa 1 — Validação Diagnóstica no Quest 3S (**Concluída em `v0.1.0-diag`**)
- Compilado `VirtualFootTracking_v0.1_Diag.apk` (`46,46 MB`) e validado pelo usuário no Quest 3S em hardware real (esqueleto IOBT + Generative Legs em pé e sentado + piso/passthrough).

### Etapa 2 — Foot Tracking Engine Próprio (**Implementada em `v0.2.0-engine`**)
- **Solver Biomecânico & IK (`ProprietaryFootTrackingSolver` + `AnalyticLegIkSolver`):**
  - Reconstrução analítica 2-bone (`Quadril -> Joelho -> Tornozelo`) respeitando comprimentos calibrados de coxa/canela e limites anatômicos (`2°–148°`) sem inversão do joelho.
  - Detecção de fase *Stance* vs *Swing* com histerese e *Foot Locking* elástico ancorado no piso MRUK (evita penetração e deslizamento parado, soltando imediatamente em chutes rápidos).
- **Predição e Filtragem (`AdaptiveKalmanMotionPredictor`):**
  - Filtro de Kalman com ganho adaptativo por inovação/velocidade e extrapolação amortecida (até `0,35s`) marcada explicitamente como `TrackingDataSource.EngineMotionPredicted`.
- **Interface VR Completa (`VirtualFootTrackingVrUi` + `VrControllerLaserPointer`) & Calibração (`GuidedCalibrationProvider`):**
  - 5 painéis interativos por raio laser nos controles Touch Plus (Menu Principal, Calibração guiada `2,5s` + ajustes manuais, Live Tracking com alternância A/B, Configurações/Offsets e Diagnóstico completo).

### Etapa 3 — Streaming PC + Driver SteamVR OpenVR
- Implementar `UdpTrackerOutput` (Wi-Fi UDP baixa latência + fallback USB `adb forward`) serializando `TrackerPacketFrame` (`VFT1`).
- Sincronização de timestamps Quest ↔ PC e detecção de perda/reordenação de pacotes.
- Driver nativo C++ para SteamVR (OpenVR `IServerTrackedDeviceProvider`) expondo os quatro dispositivos como `TrackedDeviceClass_GenericTracker` (Pés e Joelhos) compatíveis com VRChat e jogos de combate PCVR.
