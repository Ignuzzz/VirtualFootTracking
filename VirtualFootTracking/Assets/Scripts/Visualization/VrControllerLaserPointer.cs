using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VirtualFootTracking.Visualization
{
    /// <summary>
    /// Lightweight ray pointer using Quest 3S controller anchors to interact with World-Space UI Canvases
    /// without relying on fragile EventSystem/InputModule configurations in OpenXR.
    /// </summary>
    public class VrControllerLaserPointer : MonoBehaviour
    {
        [SerializeField] private OVRCameraRig _cameraRig;
        [SerializeField] private float _maxRayDistance = 5.0f;

        private readonly List<Canvas> _registeredCanvases = new List<Canvas>();
        private LineRenderer _rayLine;
        private GameObject _reticle;
        private Material _reticleMat;

        private Button _hoveredButton;
        private Graphic _hoveredGraphic;
        private Color _hoveredBaseColor;
        private bool _usingLeftHand;
        private float _hapticEndTime;
        private OVRInput.Controller _hapticController = OVRInput.Controller.None;

        public void Initialize(OVRCameraRig rig)
        {
            _cameraRig = rig != null ? rig : FindAnyObjectByType<OVRCameraRig>();

            Shader lineShader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            GameObject lineGo = new GameObject("VFT_LaserRay");
            lineGo.transform.SetParent(transform, false);
            _rayLine = lineGo.AddComponent<LineRenderer>();
            _rayLine.useWorldSpace = true;
            _rayLine.positionCount = 2;
            _rayLine.startWidth = 0.004f;
            _rayLine.endWidth = 0.0015f;
            _rayLine.material = new Material(lineShader) { color = new Color(0.35f, 0.90f, 1.0f, 0.85f) };
            _rayLine.startColor = new Color(0.35f, 0.90f, 1.0f, 0.85f);
            _rayLine.endColor = new Color(0.35f, 0.90f, 1.0f, 0.35f);
            _rayLine.enabled = false;

            _reticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _reticle.name = "VFT_LaserReticle";
            _reticle.transform.SetParent(transform, false);
            _reticle.transform.localScale = Vector3.one * 0.018f;
            Collider col = _reticle.GetComponent<Collider>();
            if (col != null) Destroy(col);

            Shader unlit = Shader.Find("Unlit/Color") ?? lineShader;
            _reticleMat = new Material(unlit) { color = new Color(0.35f, 0.95f, 1.0f) };
            Renderer rend = _reticle.GetComponent<Renderer>();
            if (rend != null) rend.sharedMaterial = _reticleMat;
            _reticle.SetActive(false);
        }

        public void RegisterCanvas(Canvas canvas)
        {
            if (canvas != null && !_registeredCanvases.Contains(canvas))
            {
                _registeredCanvases.Add(canvas);
            }
        }

        private void Update()
        {
            if (_hapticController != OVRInput.Controller.None && Time.realtimeSinceStartup >= _hapticEndTime)
            {
                OVRInput.SetControllerVibration(0f, 0f, _hapticController);
                _hapticController = OVRInput.Controller.None;
            }

            if (OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger))
                _usingLeftHand = true;
            else if (OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger))
                _usingLeftHand = false;

            if (!TryGetPointerRay(out Ray ray))
            {
                ClearHover();
                if (_rayLine != null) _rayLine.enabled = false;
                if (_reticle != null) _reticle.SetActive(false);
                return;
            }

            bool hitAnyCanvas = false;
            float bestDist = _maxRayDistance;
            Vector3 bestPoint = Vector3.zero;
            Button bestButton = null;

            for (int i = 0; i < _registeredCanvases.Count; i++)
            {
                Canvas canvas = _registeredCanvases[i];
                if (canvas == null || !canvas.gameObject.activeInHierarchy)
                    continue;

                RectTransform canvasRect = canvas.transform as RectTransform;
                if (canvasRect == null)
                    continue;

                Plane plane = new Plane(canvasRect.forward, canvasRect.position);
                if (!plane.Raycast(ray, out float enter) || enter < 0.05f || enter >= bestDist)
                    continue;

                Vector3 hitWorld = ray.GetPoint(enter);
                Vector3 localCanvas = canvasRect.InverseTransformPoint(hitWorld);
                if (!canvasRect.rect.Contains(new Vector2(localCanvas.x, localCanvas.y)))
                    continue;

                hitAnyCanvas = true;
                bestDist = enter;
                bestPoint = hitWorld;
                bestButton = FindButtonAtWorldPoint(canvas, hitWorld);
            }

            if (hitAnyCanvas)
            {
                if (_rayLine != null)
                {
                    _rayLine.enabled = true;
                    _rayLine.SetPosition(0, ray.origin);
                    _rayLine.SetPosition(1, bestPoint);
                }

                if (_reticle != null)
                {
                    _reticle.SetActive(true);
                    _reticle.transform.position = bestPoint - ray.direction * 0.005f;
                    _reticleMat.color = bestButton != null
                        ? new Color(0.25f, 1.0f, 0.55f)
                        : new Color(0.35f, 0.90f, 1.0f);
                }

                UpdateHover(bestButton);

                bool triggerPressed = OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger) ||
                                      OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger) ||
                                      Input.GetMouseButtonDown(0);
                if (triggerPressed && bestButton != null && bestButton.interactable)
                {
                    bestButton.onClick.Invoke();
                    PulseHaptics(_usingLeftHand ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch);
                }
            }
            else
            {
                ClearHover();
                bool showIdleRay = _cameraRig != null &&
                                   (_cameraRig.rightControllerAnchor != null || _cameraRig.leftControllerAnchor != null);
                if (_rayLine != null)
                {
                    _rayLine.enabled = showIdleRay;
                    if (showIdleRay)
                    {
                        _rayLine.SetPosition(0, ray.origin);
                        _rayLine.SetPosition(1, ray.origin + ray.direction * 1.4f);
                    }
                }
                if (_reticle != null) _reticle.SetActive(false);
            }
        }

        private bool TryGetPointerRay(out Ray ray)
        {
            Transform anchor = null;
            if (_cameraRig != null)
            {
                anchor = _usingLeftHand ? _cameraRig.leftControllerAnchor : _cameraRig.rightControllerAnchor;
                if (anchor == null)
                    anchor = _usingLeftHand ? _cameraRig.leftHandAnchor : _cameraRig.rightHandAnchor;
            }

            if (anchor != null && (anchor.position.sqrMagnitude > 0.0001f || Application.platform == RuntimePlatform.Android))
            {
                ray = new Ray(anchor.position, anchor.forward);
                return true;
            }

            if (Camera.main != null)
            {
                ray = new Ray(Camera.main.transform.position, Camera.main.transform.forward);
                return true;
            }

            ray = default;
            return false;
        }

        private static Button FindButtonAtWorldPoint(Canvas canvas, Vector3 hitWorld)
        {
            Button[] buttons = canvas.GetComponentsInChildren<Button>(false);
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                Button btn = buttons[i];
                if (btn == null || !btn.interactable)
                    continue;

                RectTransform rt = btn.transform as RectTransform;
                if (rt == null)
                    continue;

                Vector3 localPt = rt.InverseTransformPoint(hitWorld);
                if (rt.rect.Contains(new Vector2(localPt.x, localPt.y)))
                {
                    return btn;
                }
            }
            return null;
        }

        private void UpdateHover(Button newHover)
        {
            if (_hoveredButton == newHover)
                return;

            ClearHover();
            _hoveredButton = newHover;

            if (_hoveredButton != null && _hoveredButton.targetGraphic != null)
            {
                _hoveredGraphic = _hoveredButton.targetGraphic;
                _hoveredBaseColor = _hoveredGraphic.color;
                _hoveredGraphic.color = Color.Lerp(_hoveredBaseColor, Color.white, 0.28f);
            }
        }

        private void ClearHover()
        {
            if (_hoveredGraphic != null)
            {
                _hoveredGraphic.color = _hoveredBaseColor;
                _hoveredGraphic = null;
            }
            _hoveredButton = null;
        }

        private void PulseHaptics(OVRInput.Controller controller)
        {
            OVRInput.SetControllerVibration(0.25f, 0.18f, controller);
            _hapticController = controller;
            _hapticEndTime = Time.realtimeSinceStartup + 0.045f;
        }

        private void OnDisable()
        {
            ClearHover();
            if (_hapticController != OVRInput.Controller.None)
            {
                OVRInput.SetControllerVibration(0f, 0f, _hapticController);
                _hapticController = OVRInput.Controller.None;
            }
        }
    }
}
