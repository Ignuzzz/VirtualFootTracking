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

4. **Bloqueio de Instalação Automatizada na Fase 1:**
   - Nesta máquina, a instalação do Unity Editor `6000.0.85f2` via CLI sem privilégios administrativos foi bloqueada pelo UAC (`ELEVATION_FAILED`), e nenhuma conta Unity estava autenticada nem um Quest 3S conectado via USB. Portanto, **o primeiro build de APK e os testes em headset dependem das etapas manuais descritas em `03_Installation_and_Build_Guide.md`**.

---

## 2. Roadmap de Desenvolvimento

### Etapa 1 — Validação Diagnóstica no Quest 3S (Atual — pronta para build)
- Compilar `VirtualFootTracking_v0.1_Diag.apk` e rodar em hardware real no Quest 3S.
- Medir FPS, frequência real do `OVRBody.BodyState`, qualidade do `FloorAnchors` MRUK e comportamento cru do `Generative Legs` durante caminhada, agachamento e chutes (com o botão `Y` desligado e ligado).

### Etapa 2 — Foot Tracking Engine Próprio (Pós-validação de sensores)
- **Solver Biomecânico & IK (`IFootTrackingSolver`):**
  - Reconstrução analítica da cadeia `Quadril -> Joelho -> Tornozelo -> Pé` respeitando comprimentos calibrados de coxa/canela, limites anatômicos de flexão do joelho (0° a ~140°, sem hiperextensão) e plano coronal.
  - Detecção de fase de apoio (*stance*) vs balanço (*swing*) combinando velocidade linear e proximidade do plano de piso MRUK.
  - Prevenção suave de penetração no piso e estabilização de pé apoiado (*foot locking* sem deslizamento artificial).
- **Predição e Filtragem (`IMotionPredictor`):**
  - Substituir `NullMotionPredictor` por Filtro de Kalman / filtro adaptativo baseado em velocidade/aceleração (alta responsividade em picos de chute, baixa oscilação quando parado), marcando explicitamente amostras preditas como `TrackingDataSource.EngineMotionPredicted`.
- **Interface VR Completa:**
  - Telas dedicadas de Menu Principal, Assistente de Calibração (com ajuste manual de altura, coxa, canela, largura de base, distância dos pés e offsets individuais), Live Tracking 3D e Diagnóstico completo.

### Etapa 3 — Streaming PC + Driver SteamVR OpenVR
- Implementar `UdpTrackerOutput` (Wi-Fi UDP baixa latência + fallback USB `adb forward`) serializando `TrackerPacketFrame` (`VFT1`).
- Sincronização de timestamps Quest ↔ PC e detecção de perda/reordenação de pacotes.
- Driver nativo C++ para SteamVR (OpenVR `IServerTrackedDeviceProvider`) expondo os quatro dispositivos como `TrackedDeviceClass_GenericTracker` (Pés e Joelhos) compatíveis com VRChat e jogos de combate PCVR.
