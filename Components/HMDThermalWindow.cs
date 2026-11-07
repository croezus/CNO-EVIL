using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    /// <summary>
    /// Thermal see-through window on the HMD.
    /// A copy of the vanilla target camera rig renders the view in front of the head into a RenderTexture,
    /// with the frustum of the main camera cropped to the window, so the image lines up with the world.
    /// The near clip plane removes the own airframe. IR look as the vanilla target camera IR mode
    /// (ColorAdjustments saturation override, exposure from ambient light, no fog); hot spots from IR sources and from the hulls
    /// of ground vehicles and ships (ThermalSignature), which carry no IR sources in vanilla.
    /// Passive IRST: a hostile aircraft engine, ground vehicle or ship in the window within the IRST range gives a bearing without range to the
    /// faction passive track (IrstSync -> RWRTriangulationProcessor.RegisterIrstBearing), marked by a green bracket.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public class HMDThermalWindow : MonoBehaviour
    {
        private const int HotSpotPoolSize = 24;
        private const int LosChecksPerFrame = 16;
        private const float LosRecheckSeconds = 0.25f;
        private const float ExposureIntervalSeconds = 1.0f;
        private const float PruneIntervalSeconds = 1.0f;
        private const float LosCacheResetSeconds = 10.0f;
        // Apparent radius (m) of a hot spot of intensity 1 (a fresh flare)
        private const float HotSpotRadiusMeters = 6.0f;
        // Width of the feathered edge as a fraction of the window height
        private const float EdgeSoftnessFraction = 0.12f;
        // Magnification steps cycled by the zoom key
        private static readonly float[] ZoomLevels = { 1.0f, 5.0f, 10.0f };
        private static readonly Color ZoomLabelColor = new Color(0.28f, 1.0f, 0.42f, 0.95f);
        // IRST: seconds between bearings to one aircraft, smallest bracket (canvas units)
        private const float IrstIntervalSeconds = 0.5f;
        private const float BracketMinSize = 18.0f;

        private static readonly Func<FlightHud, Canvas> f_canvas = FastReflection.CreateFieldGetter<FlightHud, Canvas>("canvas");
        private static readonly Func<TargetCam, Volume> f_screenVolume = FastReflection.CreateFieldGetter<TargetCam, Volume>("screenVolume");

        private struct LosState
        {
            public float NextCheckTime;
            public bool Visible;
        }

        private FlightHud _flightHud;
        private Aircraft _aircraft;
        private bool _open;
        private int _zoomIndex;
        private int _labelZoomIndex = -1;

        private GameObject _rigObject;
        private Camera _cam;
        private Volume _volume;
        private ColorAdjustments _colorAdjustments;
        private RenderTexture _rt;

        private GameObject _windowObject;
        private RectTransform _windowRect;
        private RectMask2D _windowMask;
        private CanvasGroup _windowGroup;
        private RawImage _windowImage;
        private TextMeshProUGUI _zoomLabel;
        private Image[] _hotSpots;
        private Sprite _hotSpotSprite;
        private Texture2D _hotSpotTexture;

        // Window on the screen, in main camera viewport coordinates
        private Rect _viewport;
        private int _layoutScreenW;
        private int _layoutScreenH;
        private float _layoutSize;
        private float _layoutAspect;
        private float _layoutOffsetY;
        private float _layoutScale;

        private float _nextExposureTime;
        private float _nextPruneTime;
        private float _nextLosCacheReset;
        // Keyed by IRSource (engines, flares) or by Unit (ground vehicles, ships)
        private readonly Dictionary<object, LosState> _los = new Dictionary<object, LosState>();
        private readonly List<Unit> _groundTargets = new List<Unit>();

        // Hot spot pass state, set at the start of UpdateHotSpots for DrawTarget
        private Camera _spotCam;
        private Vector3 _eye;
        private float _near;
        private float _maxRange;
        private float _tanHalfFov;
        private Vector2 _windowSize;
        private Rect _zoomed;
        private float _unitsPerViewport;
        private float _spotScale;
        private float _irstBaseRange;
        private int _losBudget;
        private int _spotsUsed;
        private int _bracketsUsed;
        // IRST: time of the next bearing per aircraft, aircraft bracketed in the current frame
        private readonly Dictionary<Unit, float> _irstNextTime = new Dictionary<Unit, float>();
        private readonly List<Unit> _irstUnitsThisFrame = new List<Unit>();
        private Image[] _brackets;
        private Sprite _bracketSprite;
        private Texture2D _bracketTexture;

        // State changed for the duration of our camera render, restored after it
        private bool _renderingOurs;
        private bool _fogBefore;
        private float _lodBiasBefore;
        private Volume _vanillaVolumeDisabled;

        public void Initialize(FlightHud flightHud, Aircraft aircraft)
        {
            _flightHud = flightHud;
            _aircraft = aircraft;
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            SetVisible(false);
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            if (_rigObject != null) Destroy(_rigObject);
            if (_windowObject != null) Destroy(_windowObject);
            if (_rt != null)
            {
                _rt.Release();
                Destroy(_rt);
            }
            if (_hotSpotSprite != null) Destroy(_hotSpotSprite);
            if (_hotSpotTexture != null) Destroy(_hotSpotTexture);
            if (_bracketSprite != null) Destroy(_bracketSprite);
            if (_bracketTexture != null) Destroy(_bracketTexture);
        }

        private float Zoom
        {
            get { return ZoomLevels[_zoomIndex]; }
        }

        private void Update()
        {
            if (NuclearOption.MissionEditorScripts.InputFieldChecker.InsideInputField) return;

            KeyCode key = CnoEvilConfig.HMDThermalWindowKey.Value;
            // Opening needs a window that can show now (mod on, target camera, cockpit view); closing always works
            if (key != KeyCode.None && Input.GetKeyDown(key) && (_open || CanShow(GetMainCamera())))
            {
                _open = !_open;
                // The window always opens at 1x
                if (!_open) _zoomIndex = 0;
            }

            KeyCode zoomKey = CnoEvilConfig.HMDThermalZoomKey.Value;
            if (_open && zoomKey != KeyCode.None && Input.GetKeyDown(zoomKey))
            {
                _zoomIndex = (_zoomIndex + 1) % ZoomLevels.Length;
            }
        }

        /// <summary>Part of the main camera viewport shown in the window: the window rect shrunk around its centre by the zoom.</summary>
        private Rect ZoomedViewport()
        {
            float zoom = Zoom;
            if (zoom <= 1.0f) return _viewport;
            Vector2 size = _viewport.size / zoom;
            return new Rect(_viewport.center - size * 0.5f, size);
        }

        private void LateUpdate()
        {
            try
            {
                Camera mainCam = GetMainCamera();
                if (!ShouldShow(mainCam))
                {
                    SetVisible(false);
                    return;
                }

                if (!EnsureCreated()) return;

                UpdateLayout(mainCam);
                _windowGroup.alpha = Mathf.Clamp01(CnoEvilConfig.HMDThermalWindowOpacity.Value);
                SetVisible(true);
                UpdateExposure();
                ApplyCameraPose(mainCam);
                UpdateHotSpots(mainCam);
                UpdateZoomLabel();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("HMDThermalWindow.LateUpdate", ex);
                SetVisible(false);
            }
        }

        private static Camera GetMainCamera()
        {
            return CameraStateManager.i != null && CameraStateManager.i.mainCamera != null ? CameraStateManager.i.mainCamera : Camera.main;
        }

        private bool ShouldShow(Camera mainCam)
        {
            return _open && CanShow(mainCam);
        }

        private bool CanShow(Camera mainCam)
        {
            if (!CnoEvilConfig.IsModActive) return false;
            if (!CnoEvilConfig.EnableHMDThermalWindow.Value) return false;
            if (_flightHud == null || _aircraft == null || _aircraft.disabled) return false;
            // Only aircraft with a working target camera (it is destroyed when its part detaches)
            if (_aircraft.targetCam == null) return false;
            if (mainCam == null) return false;
            CameraStateManager csm = CameraStateManager.i;
            return csm != null && csm.currentState != null && csm.currentState == csm.cockpitState;
        }

        private void SetVisible(bool visible)
        {
            if (_cam != null && _cam.enabled != visible) _cam.enabled = visible;
            if (_windowObject != null && _windowObject.activeSelf != visible) _windowObject.SetActive(visible);
        }

        private bool EnsureCreated()
        {
            if (_cam != null && _windowObject != null) return true;

            Canvas canvas = f_canvas != null ? f_canvas(_flightHud) : null;
            if (canvas == null) return false;
            GameAssets assets = GameAssets.i;
            if (assets == null || assets.targetCam == null) return false;

            if (_cam == null) CreateCamera(assets.targetCam);
            if (_windowObject == null) CreateWindow(canvas);
            return _cam != null && _windowObject != null;
        }

        private void CreateCamera(GameObject prefab)
        {
            if (_rigObject != null) Destroy(_rigObject);

            // Own copy of the vanilla target camera rig: same layers, renderer and IR volume as the TacScreen camera
            _rigObject = Instantiate(prefab);
            _rigObject.name = "CnoEvil_HMDThermalCam";
            Camera[] cams = _rigObject.GetComponentsInChildren<Camera>(true);
            if (cams.Length == 0)
            {
                Destroy(_rigObject);
                _rigObject = null;
                return;
            }
            _cam = cams[0];
            // The second camera draws the TacScreen UI overlay; not used here
            for (int i = 1; i < cams.Length; i++) cams[i].enabled = false;

            _cam.enabled = false;
            _cam.targetTexture = null;

            _volume = _cam.GetComponentInChildren<Volume>(true);
            _colorAdjustments = null;
            if (_volume != null)
            {
                // Volume.profile makes an instance copy, so the vanilla profile stays unchanged
                VolumeProfile profile = _volume.profile;
                if (profile != null && !profile.TryGet<ColorAdjustments>(out _colorAdjustments))
                {
                    _colorAdjustments = profile.Add<ColorAdjustments>(false);
                }
                // Enabled only while our camera renders, so it cannot change the TacScreen or the main view
                _volume.enabled = false;
            }

            if (_colorAdjustments != null)
            {
                _colorAdjustments.saturation.overrideState = true;
                _colorAdjustments.saturation.value = -100.0f;
                _colorAdjustments.contrast.overrideState = true;
                _colorAdjustments.postExposure.overrideState = true;
            }
            _nextExposureTime = 0.0f;
        }

        private void CreateWindow(Canvas canvas)
        {
            Transform parent = canvas.transform;

            // RectMask2D does not clip a graphic on its own object, so the image is a child: the mask softness feathers its edges
            _windowObject = new GameObject("CnoEvil_HMDThermalWindow", typeof(RectTransform), typeof(RectMask2D), typeof(CanvasGroup));
            _windowObject.transform.SetParent(parent, false);
            // First child: HUD and HMD symbols draw on top of the image
            _windowObject.transform.SetAsFirstSibling();
            _windowRect = (RectTransform)_windowObject.transform;
            _windowRect.pivot = new Vector2(0.5f, 0.5f);
            _windowMask = _windowObject.GetComponent<RectMask2D>();
            _windowGroup = _windowObject.GetComponent<CanvasGroup>();
            _windowGroup.interactable = false;
            _windowGroup.blocksRaycasts = false;

            GameObject imageObject = new GameObject("Image", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            imageObject.transform.SetParent(_windowObject.transform, false);
            RectTransform imageRect = (RectTransform)imageObject.transform;
            imageRect.anchorMin = Vector2.zero;
            imageRect.anchorMax = Vector2.one;
            imageRect.sizeDelta = Vector2.zero;
            imageRect.anchoredPosition = Vector2.zero;
            _windowImage = imageObject.GetComponent<RawImage>();
            _windowImage.color = Color.white;
            _windowImage.raycastTarget = false;

            _hotSpotTexture = BuildHotSpotTexture(64);
            _hotSpotSprite = Sprite.Create(_hotSpotTexture, new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f));
            _hotSpots = new Image[HotSpotPoolSize];
            for (int i = 0; i < HotSpotPoolSize; i++)
            {
                GameObject go = new GameObject("HotSpot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(_windowObject.transform, false);
                Image img = go.GetComponent<Image>();
                img.sprite = _hotSpotSprite;
                img.raycastTarget = false;
                img.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                img.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                go.SetActive(false);
                _hotSpots[i] = img;
            }

            // IRST brackets draw on top of the hot spots
            _bracketTexture = BuildBracketTexture(32);
            _bracketSprite = Sprite.Create(_bracketTexture, new Rect(0f, 0f, 32f, 32f), new Vector2(0.5f, 0.5f));
            _brackets = new Image[HotSpotPoolSize];
            for (int i = 0; i < HotSpotPoolSize; i++)
            {
                GameObject go = new GameObject("IrstBracket", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(_windowObject.transform, false);
                Image img = go.GetComponent<Image>();
                img.sprite = _bracketSprite;
                img.color = ZoomLabelColor;
                img.raycastTarget = false;
                img.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                img.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                go.SetActive(false);
                _brackets[i] = img;
            }

            // Last child: the zoom readout draws on top of the hot spots
            GameObject labelObject = new GameObject("ZoomLabel", typeof(RectTransform));
            labelObject.transform.SetParent(_windowObject.transform, false);
            RectTransform labelRect = (RectTransform)labelObject.transform;
            labelRect.anchorMin = new Vector2(1.0f, 0.0f);
            labelRect.anchorMax = new Vector2(1.0f, 0.0f);
            labelRect.pivot = new Vector2(1.0f, 0.0f);
            labelRect.sizeDelta = new Vector2(80.0f, 24.0f);
            labelRect.anchoredPosition = new Vector2(-10.0f, 8.0f);
            _zoomLabel = labelObject.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset font = HudFont.Get();
            if (font != null) _zoomLabel.font = font;
            _zoomLabel.fontSize = 16.0f;
            _zoomLabel.alignment = TextAlignmentOptions.BottomRight;
            _zoomLabel.color = ZoomLabelColor;
            _zoomLabel.raycastTarget = false;
            _zoomLabel.text = "";
            _labelZoomIndex = -1;

            _layoutScreenW = 0;
            _windowObject.SetActive(false);
        }

        private void UpdateZoomLabel()
        {
            if (_zoomLabel == null || _labelZoomIndex == _zoomIndex) return;
            _labelZoomIndex = _zoomIndex;
            float zoom = Zoom;
            _zoomLabel.text = zoom > 1.0f ? zoom.ToString("0") + "x" : "";
        }

        private static Texture2D BuildHotSpotTexture(int size)
        {
            Texture2D tex = SpriteFactory.NewTexture(size, size, string.Empty, TextureWrapMode.Clamp);
            float half = (size - 1) * 0.5f;
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
                    float a = (1.0f - r) * (1.0f - r);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255.0f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>Four corner brackets of a square, 2 px lines, each a quarter of the side long.</summary>
        private static Texture2D BuildBracketTexture(int size)
        {
            Texture2D tex = SpriteFactory.NewTexture(size, size, string.Empty, TextureWrapMode.Clamp);
            const int line = 2;
            int arm = size / 4;
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool nearX = x < line || x >= size - line;
                    bool nearY = y < line || y >= size - line;
                    bool armX = x < arm || x >= size - arm;
                    bool armY = y < arm || y >= size - arm;
                    bool on = (nearX && armY) || (nearY && armX);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(on ? 255 : 0));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>
        /// Places the window on the screen and sizes the RenderTexture. Runs again only when the screen or the config changes.
        /// </summary>
        private void UpdateLayout(Camera mainCam)
        {
            float size = Mathf.Clamp(CnoEvilConfig.HMDThermalWindowSize.Value, 0.1f, 1.0f);
            float aspect = Mathf.Clamp(CnoEvilConfig.HMDThermalWindowAspect.Value, 0.5f, 3.0f);
            float offsetY = CnoEvilConfig.HMDThermalWindowOffsetY.Value;
            float scale = Mathf.Clamp(CnoEvilConfig.HMDThermalResolutionScale.Value, 0.25f, 1.0f);
            int screenW = Screen.width;
            int screenH = Screen.height;
            if (screenW == _layoutScreenW && screenH == _layoutScreenH && size == _layoutSize &&
                aspect == _layoutAspect && offsetY == _layoutOffsetY && scale == _layoutScale && _rt != null)
            {
                return;
            }
            _layoutScreenW = screenW;
            _layoutScreenH = screenH;
            _layoutSize = size;
            _layoutAspect = aspect;
            _layoutOffsetY = offsetY;
            _layoutScale = scale;

            float h = size * screenH;
            float w = Mathf.Min(h * aspect, screenW);
            Vector2 center = new Vector2(screenW * 0.5f, screenH * (0.5f + offsetY));
            Vector2 screenMin = center - new Vector2(w, h) * 0.5f;
            Vector2 screenMax = center + new Vector2(w, h) * 0.5f;

            RectTransform parentRect = _windowRect.parent as RectTransform;
            Canvas canvas = _windowObject.GetComponentInParent<Canvas>();
            Camera uiCam = null;
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                uiCam = canvas.worldCamera != null ? canvas.worldCamera : mainCam;
            }
            Vector2 localMin;
            Vector2 localMax;
            if (parentRect != null &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, screenMin, uiCam, out localMin) &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, screenMax, uiCam, out localMax))
            {
                // Anchored at the parent pivot, which is the origin of the local points
                _windowRect.anchorMin = parentRect.pivot;
                _windowRect.anchorMax = parentRect.pivot;
                _windowRect.anchoredPosition = (localMin + localMax) * 0.5f;
                _windowRect.sizeDelta = localMax - localMin;
                int soft = Mathf.Max(0, Mathf.RoundToInt(EdgeSoftnessFraction * Mathf.Abs(_windowRect.sizeDelta.y)));
                _windowMask.softness = new Vector2Int(soft, soft);
            }

            Rect pixelRect = mainCam.pixelRect;
            _viewport = Rect.MinMaxRect(
                (screenMin.x - pixelRect.xMin) / pixelRect.width,
                (screenMin.y - pixelRect.yMin) / pixelRect.height,
                (screenMax.x - pixelRect.xMin) / pixelRect.width,
                (screenMax.y - pixelRect.yMin) / pixelRect.height);

            int rtW = Mathf.Max(64, Mathf.RoundToInt(w * scale));
            int rtH = Mathf.Max(64, Mathf.RoundToInt(h * scale));
            if (_rt == null || _rt.width != rtW || _rt.height != rtH)
            {
                if (_rt != null)
                {
                    _cam.targetTexture = null;
                    _rt.Release();
                    Destroy(_rt);
                }
                _rt = new RenderTexture(rtW, rtH, 24, RenderTextureFormat.Default);
                _rt.name = "CnoEvil_HMDThermalRT";
                _rt.filterMode = FilterMode.Bilinear;
                _rt.Create();
            }
            _cam.targetTexture = _rt;
            _windowImage.texture = _rt;
        }

        /// <summary>IR exposure from ambient light, the same curve as the vanilla target camera in IR mode.</summary>
        private void UpdateExposure()
        {
            if (_colorAdjustments == null) return;
            float now = Time.unscaledTime;
            if (now < _nextExposureTime) return;
            _nextExposureTime = now + ExposureIntervalSeconds;

            LevelInfo level = NetworkSceneSingleton<LevelInfo>.i;
            float light = level != null ? Mathf.InverseLerp(0.02f, 0.4f, level.GetAmbientLight()) : 1.0f;
            _colorAdjustments.postExposure.value = Mathf.Lerp(3.0f, -0.5f, light);
            _colorAdjustments.contrast.value = Mathf.Clamp(CnoEvilConfig.HMDThermalContrast.Value, -100.0f, 100.0f);
        }

        /// <summary>
        /// Eye position and rotation of the main camera, with its frustum cropped to the window (off-axis projection).
        /// Called again just before our camera renders, so the image has no lag behind the head.
        /// </summary>
        private void ApplyCameraPose(Camera mainCam)
        {
            Transform src = mainCam.transform;
            Transform dst = _cam.transform;
            dst.SetPositionAndRotation(src.position, src.rotation);

            float near = Mathf.Max(0.3f, CnoEvilConfig.HMDThermalNearClip_m.Value);
            float far = Mathf.Max(near + 1.0f, mainCam.farClipPlane);
            float top = near * Mathf.Tan(mainCam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float right = top * mainCam.aspect;

            Rect vp = ZoomedViewport();
            float l = -right + 2.0f * right * vp.xMin;
            float r = -right + 2.0f * right * vp.xMax;
            float b = -top + 2.0f * top * vp.yMin;
            float t = -top + 2.0f * top * vp.yMax;

            _cam.cullingMask = mainCam.cullingMask;
            _cam.nearClipPlane = near;
            _cam.farClipPlane = far;
            _cam.projectionMatrix = Matrix4x4.Frustum(l, r, b, t, near, far);
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_cam == null || camera != _cam) return;
            try
            {
                Camera mainCam = GetMainCamera();
                if (mainCam != null) ApplyCameraPose(mainCam);

                _renderingOurs = true;
                // Vanilla target camera IR mode: no fog
                _fogBefore = RenderSettings.fog;
                RenderSettings.fog = false;
                // Called before culling, so the lower LOD applies to our camera only.
                // LOD selection uses the camera field of view, not our zoomed projection, so the zoom scales the bias
                _lodBiasBefore = QualitySettings.lodBias;
                QualitySettings.lodBias = _lodBiasBefore * Mathf.Clamp(CnoEvilConfig.HMDThermalLodBias.Value, 0.1f, 1.0f) * Zoom;
                if (_volume != null) _volume.enabled = true;

                // The TacScreen camera volume shares the layer of ours; keep it out of this render
                _vanillaVolumeDisabled = null;
                if (_aircraft != null && _aircraft.targetCam != null && f_screenVolume != null)
                {
                    Volume vanilla = f_screenVolume(_aircraft.targetCam);
                    if (vanilla != null && vanilla != _volume && vanilla.enabled)
                    {
                        vanilla.enabled = false;
                        _vanillaVolumeDisabled = vanilla;
                    }
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("HMDThermalWindow.BeginCameraRendering", ex);
            }
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!_renderingOurs || camera != _cam) return;
            _renderingOurs = false;
            RenderSettings.fog = _fogBefore;
            QualitySettings.lodBias = _lodBiasBefore;
            if (_volume != null) _volume.enabled = false;
            if (_vanillaVolumeDisabled != null)
            {
                _vanillaVolumeDisabled.enabled = true;
                _vanillaVolumeDisabled = null;
            }
        }

        /// <summary>
        /// White hot spots on IR sources (engines, rocket motors, flares) and on ground vehicles and ships inside the window,
        /// with terrain line of sight.
        /// </summary>
        private void UpdateHotSpots(Camera mainCam)
        {
            float now = Time.unscaledTime;
            if (now >= _nextPruneTime)
            {
                _nextPruneTime = now + PruneIntervalSeconds;
                IRSourceRegistry.Prune();
                RefreshGroundTargets();
            }
            if (now >= _nextLosCacheReset)
            {
                _nextLosCacheReset = now + LosCacheResetSeconds;
                _los.Clear();
                _irstNextTime.Clear();
            }

            _spotScale = Mathf.Max(0.0f, CnoEvilConfig.HMDThermalHotSpotScale.Value);
            _spotsUsed = 0;
            _bracketsUsed = 0;
            bool irstOn = CnoEvilConfig.HMDThermalIrstEnabled.Value;
            if (_spotScale > 0.0f || irstOn)
            {
                _spotCam = mainCam;
                _eye = mainCam.transform.position;
                _near = Mathf.Max(0.3f, CnoEvilConfig.HMDThermalNearClip_m.Value);
                // The zoom shows targets farther out, so the hot spot range grows with it
                _maxRange = Mathf.Max(_near + 1.0f, CnoEvilConfig.HMDThermalMaxRange_m.Value) * Zoom;
                _tanHalfFov = Mathf.Tan(mainCam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                _windowSize = _windowRect.rect.size;
                _zoomed = ZoomedViewport();
                // Window height covers this many units of the viewport height
                _unitsPerViewport = _windowSize.y / Mathf.Max(0.0001f, _zoomed.height);
                _losBudget = LosChecksPerFrame;
                _irstBaseRange = irstOn
                    ? Mathf.Max(0.0f, CnoEvilConfig.HMDThermalIrstRange_m.Value) *
                      Mathf.Pow(Zoom, Mathf.Max(0.0f, CnoEvilConfig.HMDThermalIrstZoomExponent.Value))
                    : 0.0f;
                FactionHQ ownHq = _aircraft.NetworkHQ;

                List<IRSourceEntry> entries = IRSourceRegistry.Entries;
                for (int i = 0; i < entries.Count && !PoolsFull(); i++)
                {
                    IRSourceEntry e = entries[i];
                    IRSource src = e.Source;
                    if (src == null || e.Owner == null || src.transform == null) continue;
                    // Own engines are behind the near plane or out of view; own flares are shown
                    if (e.Owner == _aircraft && !src.flare) continue;
                    if (e.Owner.disabled && !src.flare) continue;
                    if (src.intensity <= 0.01f) continue;

                    // IRST: engines of hostile aircraft (flares and missiles give no bearing)
                    bool irstCandidate = irstOn && !src.flare && e.Owner is Aircraft && IsHostile(e.Owner, ownHq);
                    // Angular size of the plume: radius grows with the square root of the intensity
                    float radius = HotSpotRadiusMeters * Mathf.Sqrt(src.intensity);
                    DrawTarget(src, e.Owner, src.transform.position, src.intensity, radius, irstCandidate, now);
                }

                // Ground vehicles and ships carry no IR sources: heat from the hull
                for (int i = 0; i < _groundTargets.Count && !PoolsFull(); i++)
                {
                    Unit u = _groundTargets[i];
                    if (!ThermalSignature.IsGroundHeatTarget(u)) continue;
                    float intensity = ThermalSignature.GetHeatIntensity(u);
                    if (intensity <= 0.01f) continue;

                    bool irstCandidate = irstOn && IsHostile(u, ownHq);
                    DrawTarget(u, u, ThermalSignature.GetHeatPoint(u), intensity, ThermalSignature.GetHeatRadius(u), irstCandidate, now);
                }
            }
            _irstUnitsThisFrame.Clear();

            for (int i = _spotsUsed; i < HotSpotPoolSize; i++)
            {
                if (_hotSpots[i].gameObject.activeSelf) _hotSpots[i].gameObject.SetActive(false);
            }
            for (int i = _bracketsUsed; i < HotSpotPoolSize; i++)
            {
                if (_brackets[i].gameObject.activeSelf) _brackets[i].gameObject.SetActive(false);
            }
        }

        private bool PoolsFull()
        {
            return _spotsUsed >= HotSpotPoolSize && _bracketsUsed >= HotSpotPoolSize;
        }

        private void RefreshGroundTargets()
        {
            _groundTargets.Clear();
            List<Unit> all = UnitRegistry.allUnits;
            if (all == null || !CnoEvilConfig.HMDThermalGroundTargets.Value) return;
            for (int i = 0; i < all.Count; i++)
            {
                if (ThermalSignature.IsGroundHeatTarget(all[i])) _groundTargets.Add(all[i]);
            }
        }

        /// <summary>
        /// One heat source: a hot spot within the hot spot range, and for an IRST candidate within the IRST range a bracket
        /// and a bearing (one per unit, also with several engines). losKey: the IR source or the unit, for the LOS cache.
        /// </summary>
        private void DrawTarget(object losKey, Unit owner, Vector3 pos, float intensity, float radiusMeters, bool irstCandidate, float now)
        {
            float dist = Vector3.Distance(_eye, pos);
            if (dist < _near) return;

            float irstRange = _irstBaseRange * Mathf.Sqrt(intensity);
            bool irst = irstCandidate && dist <= irstRange && _bracketsUsed < HotSpotPoolSize && !_irstUnitsThisFrame.Contains(owner);
            bool spot = _spotScale > 0.0f && dist <= _maxRange && _spotsUsed < HotSpotPoolSize;
            if (!spot && !irst) return;

            Vector3 vp = _spotCam.WorldToViewportPoint(pos);
            if (vp.z <= 0.0f || !_zoomed.Contains(new Vector2(vp.x, vp.y))) return;

            LosState los;
            if (!_los.TryGetValue(losKey, out los) || now >= los.NextCheckTime)
            {
                if (_losBudget <= 0)
                {
                    // Not checked yet this frame: keep the last result, hide unknown ones
                    if (!_los.ContainsKey(losKey)) return;
                }
                else
                {
                    _losBudget--;
                    los.Visible = !Physics.Linecast(_eye, pos, PhysicsLayers.StaticsMask);
                    los.NextCheckTime = now + LosRecheckSeconds;
                    _los[losKey] = los;
                }
            }
            if (!los.Visible) return;

            float radiusViewport = radiusMeters / (dist * _tanHalfFov * 2.0f);
            float diameter = Mathf.Clamp(2.0f * radiusViewport * _unitsPerViewport * _spotScale, 6.0f, _windowSize.y * 0.15f);
            float u = (vp.x - _zoomed.xMin) / _zoomed.width - 0.5f;
            float v = (vp.y - _zoomed.yMin) / _zoomed.height - 0.5f;
            Vector2 windowPos = new Vector2(u * _windowSize.x, v * _windowSize.y);

            if (spot)
            {
                float fade = 1.0f - Mathf.Clamp01((dist - _near) / _maxRange);
                float alpha = Mathf.Clamp01(0.35f + 0.65f * intensity) * Mathf.Lerp(0.4f, 1.0f, fade);

                Image img = _hotSpots[_spotsUsed++];
                RectTransform rt = img.rectTransform;
                rt.anchoredPosition = windowPos;
                rt.sizeDelta = new Vector2(diameter, diameter);
                img.color = new Color(1.0f, 1.0f, 1.0f, alpha);
                if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);
            }

            if (irst)
            {
                _irstUnitsThisFrame.Add(owner);
                Image bracket = _brackets[_bracketsUsed++];
                float bracketSize = Mathf.Max(diameter * 1.6f, BracketMinSize);
                bracket.rectTransform.anchoredPosition = windowPos;
                bracket.rectTransform.sizeDelta = new Vector2(bracketSize, bracketSize);
                if (!bracket.gameObject.activeSelf) bracket.gameObject.SetActive(true);

                float next;
                if (!_irstNextTime.TryGetValue(owner, out next) || now >= next)
                {
                    _irstNextTime[owner] = now + IrstIntervalSeconds;
                    SendIrstBearing(owner, pos - _eye, Mathf.Clamp01(1.0f - dist / Mathf.Max(1.0f, irstRange)));
                }
            }
        }

        private static bool IsHostile(Unit unit, FactionHQ ownHq)
        {
            FactionHQ hq = DatalinkNetwork.GetUnitFactionHQ(unit);
            return hq != null && hq != ownHq;
        }

        /// <summary>
        /// Measured IRST bearing: the true direction turned by a Gaussian error of HMDThermalIrstBearingError_deg / zoom
        /// about a random axis normal to the line of sight.
        /// </summary>
        private void SendIrstBearing(Unit target, Vector3 trueDirection, float strength01)
        {
            Vector3 dir = trueDirection.normalized;
            float sigmaDeg = Mathf.Max(0.0f, CnoEvilConfig.HMDThermalIrstBearingError_deg.Value) / Zoom;
            Vector3 axis = Vector3.Cross(dir, UnityEngine.Random.onUnitSphere);
            if (axis.sqrMagnitude > 1e-6f && sigmaDeg > 0.0f)
            {
                // Box-Muller
                float u1 = Mathf.Max(1e-6f, UnityEngine.Random.value);
                float u2 = UnityEngine.Random.value;
                float gauss = Mathf.Sqrt(-2.0f * Mathf.Log(u1)) * Mathf.Cos(2.0f * Mathf.PI * u2);
                dir = Quaternion.AngleAxis(gauss * sigmaDeg, axis.normalized) * dir;
            }
            IrstSync.Submit(_aircraft, target, dir, strength01, Mathf.Max(0.0001f, sigmaDeg * Mathf.Deg2Rad));
        }
    }
}
