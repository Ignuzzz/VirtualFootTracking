# 02 — Arquitetura de Software e Decisões Técnicas (ADRs)

## 1. Visão Geral Modular

A arquitetura separa completamente os contratos biomecânicos e de tracking (`VirtualFootTracking.Core`, sem dependência de APIs proprietárias da Meta) das implementações concretas ligadas ao Quest 3S (`OVRBody`, `OVRPlugin`, `MRUK`), permitindo substituir o provedor de rastreamento, o solver IK, o preditor de movimento ou o transporte de rede sem reconstruir a aplicação.

```mermaid
flowchart LR
    OVR["Meta OVRBody / OpenXR\n(FullBody + IOBT High)"] --> BTP["MetaOvrBodyTrackingProvider\n(IBodyTrackingProvider)"]
    MRUK["Meta MRUK / Scene API\n(FloorAnchors + Raycast)"] --> SMP["MrukSpatialMappingProvider\n(ISpatialMappingProvider)"]
    DEPTH["Meta Depth API\n(EnvironmentRaycastManager)"] --> DLD["EnvironmentDepthLegDetector\n(5-Layer Foreground + Voxel Bg)"]

    BTP --> RT["VirtualFootTrackingRuntime"]
    SMP --> RT
    DLD --> RT
    CAL["GuidedCalibrationProvider\n(ICalibrationProvider)"] --> RT

    RT --> SOLV["ProprietaryFootTrackingSolver + AnalyticLegIkSolver\n(Depth 3D + SDK + Stance Lock)"]
    SOLV --> PRED["AdaptiveKalmanMotionPredictor\n(ou NullMotionPredictor)"]

    PRED --> OUT["DiagnosticLogTrackerOutput\n(ITrackerOutput)"]
    PRED --> DIAG["RuntimeDiagnosticsProvider\n(IDiagnosticsProvider)"]
    PRED --> VIS["SkeletonAndTrackerVisualizer\n& VirtualFootTrackingVrUi + LaserPointer"]
```

---

## 2. Contratos Principais (`Assets/Scripts/Core/Interfaces/`)

| Interface | Responsabilidade | Implementação Atual (Fase 2) |
| :--- | :--- | :--- |
| `IBodyTrackingProvider` | Gerenciar permissão `BODY_TRACKING`, configurar `FullBody` + `High` IOBT e fornecer `BodySkeletonSnapshot`. | `MetaOvrBodyTrackingProvider` |
| `ISpatialMappingProvider` | Gerenciar permissão `USE_SCENE`, carregar sala do dispositivo via `MRUK`, expor polígono/altura do piso (`FloorPlaneData`) e raycast vertical no piso. | `MrukSpatialMappingProvider` |
| `IFootTrackingSolver` | Transformar o esqueleto + plano do piso + calibração nos quatro trackers virtuais (`VirtualTrackerFrame`). | `ProprietaryFootTrackingSolver` (com alternância A/B para `DiagnosticPassthroughFootSolver`) |
| `ICalibrationProvider` | Captura guiada multi-frame em pé (`2,5s`), ajustes manuais granulares e persistência JSON (`CalibrationProfile`). | `GuidedCalibrationProvider` |
| `ITrackerOutput` | Consumir quadros resolvidos (buffer de diagnóstico local; pronto para UDP/USB na Fase 3). | `DiagnosticLogTrackerOutput` |
| `IDiagnosticsProvider` | Medir FPS, frequência real de atualização de body tracking (Hz), latência do solver+Kalman (ms), modo ativo e erros. | `RuntimeDiagnosticsProvider` |

---

## 3. Modelo de Dados dos Trackers (`VirtualTrackerTypes.cs`)

Cada um dos quatro trackers (`LeftFoot`, `RightFoot`, `LeftKnee`, `RightKnee`) é emitido como um `VirtualTrackerSample` contendo:

- `Id`: `VirtualTrackerId` (`LeftFoot`, `RightFoot`, `LeftKnee`, `RightKnee`)
- `Position` (`Vector3`) e `Rotation` (`Quaternion`)
- `LinearVelocity` (`Vector3`) e `AngularVelocity` (`Vector3`)
- `TimestampSeconds` (`double`, monotonic `realtimeSinceStartupAsDouble`)
- `Validity`: `Invalid`, `Valid`, `Degraded`, `TemporarilyLost`
- `Source`: `Unavailable`, `SdkObserved`, `SdkEstimatedGenerativeLegs`, `EngineBiomechanicalFallback`, `EngineMotionPredicted`, `DepthObserved` (`DEPTH_3D`)
- `Confidence`: `0.0f` a `1.0f`
- `Space`: `UnityWorldSpace`, `TrackingRigLocalSpace` ou `HeadsetLocalSpace`
- `HeightAboveFloorMeters` (`float`), `IsGrounded` (`bool`), `FloorClamped` (`bool`)

---

## 4. Pipeline Explícito de Coordenadas

Para nunca misturar espaços de coordenadas sem transformação explícita:

1. **Espaço nativo OVR/OpenXR (mão direita):** `OVRPlugin.GetBodyState4` entrega `Posef.Position` e `Posef.Orientation` relativos ao Tracking Space com eixo Z invertido em relação à Unity.
2. **Espaço Tracking Rig da Unity (mão esquerda):** `MetaOvrBodyTrackingProvider` converte explicitamente com `OVRExtensions.FromFlippedZVector3f(...)` e `OVRExtensions.FromFlippedZQuatf(...)` -> armazenado em `BodyJointSample.TrackingSpacePosition/Rotation`.
3. **Espaço Global da Unity (`UnityWorldSpace`):** transformado por `OVRCameraRig.trackingSpace.TransformPoint(...)` e `trackingSpace.rotation * tsRot` -> armazenado em `BodyJointSample.WorldPosition/Rotation` e `VirtualTrackerSample` (`Space = CoordinateSpace.UnityWorldSpace`).
4. **Alinhamento com o Chão:** `OVRCameraRig` utiliza `OVRManager.TrackingOrigin.Stage`, e `MrukSpatialMappingProvider` lê `MRUKRoom.FloorAnchors` em coordenadas de mundo Unity (`anchor.transform.position.y` e `anchor.transform.TransformPoint(pt.x, pt.y, 0)`).

---

## 5. Decisões Arquiteturais (ADRs)

### ADR-001 — Classificação Honesta de Proveniência no Quest 3S
- O Meta Quest 3S possui câmeras voltadas para frente/lados/baixo para rastreio de mãos, torso e ambiente (IOBT — `BodyTrackingFidelity2.High`), **mas não vê diretamente os pés e tornozelos**.
- No `OVRPlugin.BoneId`, as juntas `70..83` (`FullBody_LeftUpperLeg` até `FullBody_RightFootBall`) são fornecidas pelo modelo **Generative Legs** da Meta.
- Portanto, `MetaOvrBodyTrackingProvider` classifica todas as juntas `70..83` como `TrackingDataSource.SdkEstimatedGenerativeLegs` (cor **Ciano** na cena diagnóstica), diferenciando-as das juntas superiores com `SpaceLocationFlags.PositionTracked` (`SdkObserved`, cor **Verde**).

### ADR-002 — Built-in Render Pipeline (BiRP) + Passthrough Underlay na Fase 1
- Utilizar BiRP + `UnityEngine.UI.Text` (`LegacyRuntime.ttf`) + `OVRPassthroughLayer` (`Underlay`) garante criação e compilação headless sem exigir importação manual de assets TMP/URP e permite ao usuário enxergar o **chão físico real** sob os trackers virtuais durante testes de chute e caminhada em Quest 3S.

### ADR-003 — Foot Tracking Engine Próprio + Modo A/B Diagnóstico (`v0.2.0`)
- `ProprietaryFootTrackingSolver` combina detecção de piso MRUK (`TryQueryFloorHeightAt`), histerese de contato (*Stance* vs *Swing* com *Foot Locking* elástico de `0,09m` para liberar chutes rápidos instantaneamente), `AnalyticLegIkSolver` (limites anatômicos `2°–148°` com polo anterior anti-hiperextensão) e `EngineBiomechanicalFallback` quando juntas inferiores estiverem inválidas.
- `AdaptiveKalmanMotionPredictor` escala o ruído de processo $Q$ dinamicamente entre repouso (`0,25`) e movimentos explosivos de combate (`320,0`), extrapolando perdas momentâneas por até `0,35s` com marcação explícita `EngineMotionPredicted` (Magenta).
- O usuário pode alternar em tempo real entre o Motor Próprio e o `DiagnosticPassthroughFootSolver` (botão `Y` ou UI Live Tracking) para comparação A/B direta no headset.

### ADR-004 — Blueprint de Comunicação PC / SteamVR (`TrackerPacketSchema.cs`)
- O schema `TrackerPacketFrame` (`Magic = "VFT1"`, versão `1`, `SequenceNumber`, `MonotonicTimestampNs`, `FloorHeightWorldY` e `4 × TrackerWireEntry`) já prepara o payload que será transmitido via UDP Wi-Fi (ou USB ADB forward) para o futuro driver C++ `openvr` (`vr::ITrackedDeviceServerDriver` registrado como `vr::TrackedDeviceClass_GenericTracker`) na Fase 4.

### ADR-009 — Detecção Física de Pernas e Chutes via Depth API em Tempo Real (`v0.3.0` – `v0.3.3`)
- Testes em hardware real mostraram que o `Generative Legs` da Meta (`v0.2.0`) estima bem passos e apoio no piso (~90% segundo o usuário), mas **não detecta chutes isolados no ar nem elevação estacionária de joelho**, pois não há câmeras dedicadas aos pés e o modelo reage apenas ao movimento do tronco/cabeça.
- Embora o Scene Mapping estático (Space Setup / âncoras de móveis) seja atualizado apenas por escaneamento manual e não sirva para membros dinâmicos, a **Depth API em tempo real** (`Meta.XR.EnvironmentRaycastManager` sobre OpenXR no Quest 3S) atualiza o mapa de profundidade a cada frame dentro do cone frontal das câmeras (`~±36°` H × `-54°..+4°` V na varredura grossa `9×7`, até `2,8 m`).
- `EnvironmentDepthLegDetector` isola apenas a perna em movimento aplicando filtragem multi-camada + **Sonda Fina 2-Pass (`v0.3.3`)**:
  1. **Base Corporal Bilateral (`v0.3.1`–`v0.3.3`):** calcula `pelvisRightFlat` diretamente de `RightUpperLeg - LeftUpperLeg` (fallback para braços/ombros superiores ou `headTransform.right`) e `pelvisForwardFlat = Cross(pelvisRightFlat, Up)`, com guarda contra inversão relativa à direção da cabeça — evitando `hips.WorldRotation * Vector3.forward`, que apontava lateralmente.
  2. **Varredura em 2 Passagens (`v0.3.3`):** Passo 1 (`9×7 = 63` raios) cobre o frustum completo; Passo 2 (`3×9 = 27` raios por perna ativa ou recém-rastreada) foca densidade vertical fina (`~4,2°`) ao longo do eixo coxa -> joelho -> canela -> pé para nunca perder chutes rápidos nem esticamentos parciais entre raios grossos.
  3. **Piso & Ambiente Estático Seguro (`v0.3.2`):** descarta piso (`y <= floorY + 0,10 m`), verifica exclusivamente planos de parede (`room.WallAnchors`, `ComponentType.Plane <= 0,12 m`) e volumes nominais de móveis (`TABLE | COUCH | BED | STORAGE | SCREEN | LAMP | PLANT`) sem `GLOBAL_MESH`, e aprende `_autoStaticVoxels` apenas além de `maxLegReach + 0,30 m`.
  4. **Cluster Sagital, Transição Contínua & Extensão Colinear (`v0.3.3`):** filtra por plano sagital (`±0,22 m`), interpola suavemente do joelho flexionado ao pé estendido (`extRatio` entre `1,05×` e `1,32×` da coxa) e aplica **Collinear Straight-Shin Extension** quando `joelho -> canela` está alinhado (`dot >= 0,82`).
- **Anti-Flicker no Solver & Kalman (`v0.3.3`):** `OneEuroFilter3D` (deadzone estacionária `1,2 cm` + `beta = 14`), histerese de entrada (`>= 2` frames ou `>= 5` pontos), *dropout hold* de `140 ms` com amortecimento exponencial no `ProprietaryFootTrackingSolver` e ruído `R` adaptativo para `DEPTH_3D` no `AdaptiveKalmanMotionPredictor`. Outros pontos fora do frustum retornam suavemente para `SdkEstimatedGenerativeLegs` + trava de piso.
