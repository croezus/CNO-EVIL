using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    /// <summary>
    /// A labelled box on the portal display (no mouse input: the +/- boxes show what Shift + the page key does).
    /// </summary>
    public class PortalButton
    {
        public RectTransform Rect;
        public TextMeshProUGUI Text;
        public Image Background;
    }

    /// <summary>
    /// TacScreenDisplayPreset = PortalMFD: the vanilla TacScreen content is covered by a portal display with two
    /// halves, or with one slot on each physical screen of the cockpit (one to four, s_layouts). Each slot shows one
    /// page with its name in the top left corner. The content is 300 units high and as wide as the main screen allows
    /// (at least 690), so the halves use the whole screen.
    /// PortalMFDLeftPageKey / PortalMFDRightPageKey / PortalMFDCenterPageKey / PortalMFDLowerPageKey cycle the page of
    /// a slot; with Shift they cycle its range.
    /// Vanilla keeps running below the cover. The EOTS page moves the vanilla camera screens into the page and puts
    /// them back when the display turns off (F10, other preset, destroy). Vanilla panels under the cover are hidden on
    /// the TacScreen canvas and on each other canvas that the TacScreen camera renders.
    /// </summary>
    public class TacScreenPortalMFD : MonoBehaviour
    {
        public const float MinContentWidth = 690f;
        public const float ContentHeight = 300f;
        private const float PageInset = 2f;
        private const float TickInterval = 0.1f;
        // Texture size the layout table values are written in; the table stores fractions of the texture
        private const float TableWidth = 1024f;
        private const float TableHeight = 512f;
        private const string HiResName = "PortalMFD_TacScreen";

        private static readonly Func<TacScreen, Canvas> s_getCanvas = FastReflection.CreateFieldGetter<TacScreen, Canvas>("canvas");
        private static readonly Func<TacScreen, Camera> s_getCam = FastReflection.CreateFieldGetter<TacScreen, Camera>("cam");
        private static readonly Func<TacScreen, RenderTexture> s_getRenderTexture = FastReflection.CreateFieldGetter<TacScreen, RenderTexture>("renderTexture");
        private static readonly Func<TacScreen, GameObject> s_getTargetCamDisplay = FastReflection.CreateFieldGetter<TacScreen, GameObject>("targetCamDisplay");
        private static readonly Func<TacScreen, GameObject> s_getLandingCamDisplay = FastReflection.CreateFieldGetter<TacScreen, GameObject>("landingCamDisplay");
        private static readonly Func<TacScreen, TextMeshProUGUI> s_getTimeDisplay = FastReflection.CreateFieldGetter<TacScreen, TextMeshProUGUI>("timeDisplay");
        private static readonly Func<TacScreen, Material> s_getScreenMaterial = FastReflection.CreateFieldGetter<TacScreen, Material>("screenMaterial");
        private static readonly Func<Cockpit, Renderer> s_getScreenRenderer = FastReflection.CreateFieldGetter<Cockpit, Renderer>("tacScreenRender");

        // Order of the page hotkeys
        private static readonly CnoEvilConfig.PortalMFDPage[] s_cycleOrder =
        {
            CnoEvilConfig.PortalMFDPage.TSD, CnoEvilConfig.PortalMFDPage.RADAR, CnoEvilConfig.PortalMFDPage.RWR,
            CnoEvilConfig.PortalMFDPage.EOTS, CnoEvilConfig.PortalMFDPage.SMS, CnoEvilConfig.PortalMFDPage.NAV,
            CnoEvilConfig.PortalMFDPage.HSI
        };

        // Position of a slot in the cockpit; it selects the page hotkey and the start page. The texture position of a
        // screen does not give it (F-16M: the right screen is between the left and the centre screen in the texture).
        // Lower: a screen below the centre screen (F-22E).
        private enum SlotRole
        {
            Left,
            Right,
            Center,
            Lower
        }

        private struct PhysicalScreen
        {
            // Fraction of the screen texture (origin bottom left)
            public Rect Uv;
            // Physical aspect of the panel / aspect of its pixel rect, as the pilot sees it: the panel stretches the
            // texture by this much horizontally
            public float Stretch;
            public SlotRole Role;
            // Degrees about z (multiple of 90) that the content turns to stand upright on the panel: the cockpit maps
            // the texture rotated onto the panel (FS-41: -90, like the panels of the mod)
            public float Rotation;

            public PhysicalScreen(Rect uv, float stretch, SlotRole role) : this(uv, stretch, role, 0f)
            {
            }

            public PhysicalScreen(Rect uv, float stretch, SlotRole role, float rotation)
            {
                Uv = uv;
                Stretch = stretch;
                Role = role;
                Rotation = rotation;
            }

            /// <summary>Size of the screen in canvas units as the pilot sees it: width and height swap at 90 degrees.</summary>
            public Vector2 View(Rect px)
            {
                return Mathf.Abs(Mathf.Repeat(Rotation, 180f) - 90f) < 1f ? new Vector2(px.height, px.width) : px.size;
            }
        }

        private class ScreenLayout
        {
            // Fraction of the screen texture (origin bottom left) the portal covers with one black backdrop.
            // Zero size: the screens are not adjacent, each screen gets its own backdrop.
            public Rect Main;
            // Not null: one slot on each physical screen (one to four; one: a single page on the full screen); null: two halves side by side on Main
            public PhysicalScreen[] Split;

            public ScreenLayout(Rect main, PhysicalScreen[] split)
            {
                Main = main;
                Split = split;
            }
        }

        /// <summary>Table value in pixels of a 1024 x 512 texture (origin bottom left) to a fraction of the texture.</summary>
        private static Rect Px(float x, float y, float width, float height)
        {
            return new Rect(x / TableWidth, y / TableHeight, width / TableWidth, height / TableHeight);
        }

        // Main screen of each TacScreen prefab. The canvas renders into one texture: the cockpit maps the main screen
        // and the small displays to different parts of it, and the portal covers only the main screen. Values are
        // fractions of the texture, so a canvas of any size uses them. Vanilla values from the 'border' frames; SFB,
        // fastBomber1 and QuadVTOL1 from the UV islands of the cockpit screen mesh; cricket from the panel layout.
        // Modded cockpits: from the UV islands of the cockpit screen mesh (Tools/inspect_modded_aircraft.py).
        private static readonly Dictionary<string, ScreenLayout> s_layouts = new Dictionary<string, ScreenLayout>
        {
            { "tacScreen_CAS1", new ScreenLayout(Px(0f, 149f, 1024f, 363f), null) },
            { "tacScreen_trainer", new ScreenLayout(Px(0f, 149f, 1024f, 363f), null) },
            { "VTOLTrainer1_tacscreen", new ScreenLayout(Px(0f, 149f, 1024f, 363f), null) },
            { "tacScreen_fighter1", new ScreenLayout(Px(0f, 149f, 1024f, 363f), null) },
            { "tacScreen_smallFighter1", new ScreenLayout(Px(0f, 149f, 1024f, 363f), null) },
            { "tacScreen_UFO", new ScreenLayout(Px(0f, 149f, 1024f, 363f), null) },
            // UVs of the cockpit mesh EW1_targetScreen (outer screen u 0..0.5596, inner u 0.5555..0.9992, v 0.2542..1);
            // 2 px kept clear on each side of the overlap
            { "tacScreen_EW1", new ScreenLayout(Px(0f, 129f, 1024f, 383f), new[]
                {
                    new PhysicalScreen(Px(0f, 130f, 567f, 382f), 1.06f, SlotRole.Left),
                    new PhysicalScreen(Px(571f, 130f, 453f, 382f), 1.04f, SlotRole.Right)
                }) },
            { "tacScreen_Multirole1", new ScreenLayout(Px(0f, 128f, 768f, 384f), null) },
            { "tacScreen_AttackHelo1", new ScreenLayout(Px(0f, 128f, 768f, 384f), null) },
            { "tacScreen_UtilityHelo1", new ScreenLayout(Px(0f, 128f, 535f, 384f), null) },
            // SFB-81: mesh sfb_tacscreen, side-by-side seats; the main MFD (MainPanel, TimePanel) u 0..588 v 144..512
            // shows again on the other seat. One page on the full screen (user choice). The engine, stores (turned
            // -90), flight instrument, status gauge and system screens keep their vanilla panels.
            { "tacScreen_SFB", new ScreenLayout(Px(0f, 144f, 588f, 368f), new[]
                {
                    new PhysicalScreen(Px(0f, 144f, 588f, 368f), 1.00f, SlotRole.Left)
                }) },
            // fastBomber1: mesh fastBomber1_tacscreen, side-by-side seats; the main MFD (MainPanel) u 3..586 v 146..488
            // shows again on the other seat. One page on the full screen (user choice). The stores, engine, flight
            // instrument, status gauge, system and threat light screens keep their vanilla panels.
            { "tacScreen_fastBomber1", new ScreenLayout(Px(3f, 146f, 583f, 342f), new[]
                {
                    new PhysicalScreen(Px(3f, 146f, 583f, 342f), 1.00f, SlotRole.Left)
                }) },
            { "tacScreen_cricket", new ScreenLayout(Px(0f, 0f, 809f, 512f), null) },
            // QuadVTOL1: mesh quadVTOL1_tacscreen, side-by-side seats; two MFDs, left (Buttons, Radar, cameras, time)
            // u 1..512 v 142..509 and centre (RightScreenBorder) u 512..901 v 228..511; roles from the pilot seat
            // (x -0.37, 0 m). The two MFDs touch in the texture: 2 px kept clear on each side. The four lower strips
            // and the system status keep their vanilla panels.
            { "tacScreen_QuadVTOL1", new ScreenLayout(Rect.zero, new[]
                {
                    new PhysicalScreen(Px(1f, 142f, 509f, 368f), 1.09f, SlotRole.Left),
                    new PhysicalScreen(Px(514f, 228f, 386f, 283f), 1.10f, SlotRole.Center)
                }) },
            // Aryx OA-27 Cavalier: mesh Aryx_PropAttacker1_TacScreens has 4 screens. Main (radar, flight instruments,
            // engine) u 6..748 v 199..509, panel aspect = pixel aspect. The status gauges, propeller panel and status
            // strip screens keep their vanilla panels.
            { "Aryx_PropAttacker1_TacScreen", new ScreenLayout(Px(6f, 199f, 742f, 310f), null) },
            // Aryx F-16M King Viper: mesh Aryx_F16M_Screen has three MFDs that are not adjacent in the texture, left
            // (engine) u 2..314, right (radar) u 317..629, centre (stores, cameras) u 632..1022 v 150..509; roles from
            // the mesh positions (x -0.235, +0.235, 0 m). The upper strip, the gauges and the systems panel keep their
            // vanilla panels.
            { "Aryx_F16M_TacScreen", new ScreenLayout(Rect.zero, new[]
                {
                    new PhysicalScreen(Px(2f, 1f, 312f, 291f), 1.00f, SlotRole.Left),
                    new PhysicalScreen(Px(317f, 1f, 312f, 291f), 1.00f, SlotRole.Right),
                    new PhysicalScreen(Px(632f, 150f, 390f, 359f), 1.00f, SlotRole.Center)
                }) },
            // Aryx F-22E Strike Raptor: mesh Aryx_KingRaptor_TacScreen has four MFDs, left (status gauges) u 3..257,
            // centre (radar, cameras) u 257..547, right (engines) u 549..803, all v 1..280, and the lower centre
            // (stores, time) u 767..1019 v 257..509; roles from the mesh positions (x -0.2, 0, +0.2 m; the lower centre
            // screen is 0.2 m below the centre screen). The three upper screens touch or almost touch in the texture:
            // 2 px kept clear on each side. The two upper small screens and the radio strips keep their vanilla panels.
            { "Aryx_KingRaptor_TacScreen", new ScreenLayout(Rect.zero, new[]
                {
                    new PhysicalScreen(Px(3f, 1f, 252f, 254f), 1.00f, SlotRole.Left),
                    new PhysicalScreen(Px(259f, 1f, 287f, 279f), 1.00f, SlotRole.Center),
                    new PhysicalScreen(Px(550f, 2f, 253f, 254f), 1.00f, SlotRole.Right),
                    new PhysicalScreen(Px(767f, 257f, 252f, 252f), 1.00f, SlotRole.Lower)
                }) },
            // Aryx F-99 Shrike: mesh Aryx_LightFighter1_TacScreens has three MFDs, centre (radar, cameras) u 1..538
            // v 1..374, left (kinematics, time) u 540..940 v 0..282, right (stores) u 540..939 v 288..508; roles from
            // the mesh positions (x 0, -0.25, +0.25 m). The lower status panel, the IR gauge, the FSS strips and the
            // radio strips keep their vanilla panels.
            { "Aryx_LightFighter1_TacScreen", new ScreenLayout(Rect.zero, new[]
                {
                    new PhysicalScreen(Px(540f, 0f, 400f, 282f), 1.00f, SlotRole.Left),
                    new PhysicalScreen(Px(1f, 1f, 537f, 373f), 1.00f, SlotRole.Center),
                    new PhysicalScreen(Px(540f, 288f, 399f, 219f), 1.00f, SlotRole.Right)
                }) },
            // Aryx FS-41 Eclipse: mesh Aryx_Interceptor1_TacScreen, tandem cockpit: each texture part shows on one
            // screen of the front and one of the rear cockpit. Two MFDs, left (radar, cameras) u 352..697, right
            // (flight instruments, engines) u 2..348, both v 3..509; roles from the front cockpit (x -0.15, +0.14 m).
            // The cockpit maps them rotated (texture up = pilot left), so the content turns -90 degrees like the
            // panels of the mod. The stores screen, the upper right strip and the systems tiles keep their vanilla panels.
            { "Aryx_Interceptor1_TacScreen", new ScreenLayout(Rect.zero, new[]
                {
                    new PhysicalScreen(Px(352f, 3f, 345f, 506f), 1.00f, SlotRole.Left, -90f),
                    new PhysicalScreen(Px(2f, 3f, 346f, 506f), 1.00f, SlotRole.Right, -90f)
                }) },
            // Aryx MC-260 Chimera: mesh Aryx_Cargo1_TacScreen, side-by-side cockpit with the pilot on the left: the
            // two pilot MFDs, outer (kinematics) u 327..648 and inner (radar, cameras) u 4..325, both v 256..510, show
            // again on the mirrored copilot MFDs; roles from the pilot seat (x -0.79, -0.48 m). The two MFDs touch in
            // the texture: 2 px kept clear on each side. The centre console screens (centre panel, stores), the
            // readouts, the system buttons and the overhead strip keep their vanilla panels (user choice).
            { "Aryx_MC260_Chimera_TacScreen", new ScreenLayout(Rect.zero, new[]
                {
                    new PhysicalScreen(Px(328f, 256f, 320f, 254f), 1.00f, SlotRole.Left),
                    new PhysicalScreen(Px(4f, 256f, 320f, 254f), 1.00f, SlotRole.Right)
                }) },
            // Aryx RAH-72 Knockout: mesh Aryx_LightHeli1_TacScreen has 4 screens. Main (radar, stores, cameras,
            // engines) u 43..983 v 36..303, panel aspect = pixel aspect. All panels are below one full canvas Image.
            // The two side screens (Left Panel, Right Panel) and the status strip (Top Panel) keep their vanilla panels.
            { "Aryx_LightHeli1_TacScreen", new ScreenLayout(Px(43f, 36f, 940f, 267f), null) },
            // FQ-106 Kestrel: mesh kestrel_targetScreen draws one screen (radar, stores, cameras, engine, flight
            // instruments) u 0..1024 v 151..512, stretch 0.96 like the vanilla fighters. The mesh has the vertices of
            // two lower screens without triangles: they are not drawn.
            { "kestrel_tacScreen", new ScreenLayout(Px(0f, 151f, 1024f, 361f), null) },
            // FS-3 Ternion: mesh tacScreens has 2 MFDs in an asymmetric cockpit. The stores MFD (WeaponPanel,
            // u 591..1019 v 260..508) is left of the radar MFD (Radar, Buttons, cameras, u 6..584 v 152..507), which is
            // right of centre. The radar MFD is the Center slot (user choice). The engine, flight instrument, IR / mass,
            // status, system and time strips keep their vanilla panels.
            { "tacScreen_P_Trisurface1", new ScreenLayout(Rect.zero, new[]
                {
                    new PhysicalScreen(Px(591f, 260f, 428f, 248f), 1.00f, SlotRole.Left),
                    new PhysicalScreen(Px(6f, 152f, 578f, 355f), 1.00f, SlotRole.Center)
                }) },
            // KR-33 Agni (mod 1509_PalaFighter1): mesh 1509_PalaFighter1_TacScreen has one main screen (radar, buttons,
            // engine, status gauges, cameras) u 0..767 v 129..512, the same geometry as tacScreen_Multirole1. The time
            // strip and the round fuel dial sample texels inside the main screen and show the portal there. The stores
            // screen (WeaponPanel), the flight instruments and the system status keep their vanilla panels.
            { "1509_tacScreen_PalaFighter1_Tacscreen", new ScreenLayout(Px(0f, 129f, 767f, 383f), null) }
        };

        // One page place of the portal: a half of the main screen, or one physical screen
        private class Slot
        {
            public int Index;
            public SlotRole Role;
            public RectTransform Rect;
            public RectTransform PageArea;
            public PortalPage Page;
            public TextMeshProUGUI Title;
            public CnoEvilConfig.PortalMFDPage ConfigPage;
        }

        private class MovedVanilla
        {
            public RectTransform Target;
            public Transform OriginalParent;
            public int OriginalSibling;
            public Vector2 AnchorMin, AnchorMax, Pivot, AnchoredPosition, SizeDelta;
            public Vector3 Scale;
            public Quaternion Rotation;
            public RectTransform NewParent;
            public Vector2 NewCenter, NewBox;
            public bool Applied;
        }

        private class HiddenPanel
        {
            public RectTransform Target;
            public CanvasGroup Group;
            public bool Added;
            public float Alpha;
            // Parts outside the backdrops: a CanvasGroup that ignores the hidden panel keeps them visible
            public readonly List<RectTransform> KeepTargets = new List<RectTransform>();
            public readonly List<CanvasGroup> KeepGroups = new List<CanvasGroup>();
        }

        // Another root canvas that the TacScreen camera renders into the same texture
        private class ExtraCanvas
        {
            public Canvas Canvas;
            public CanvasScaler Scaler;
            public Vector2 Size;
            public float VanillaScaleFactor;
        }

        private TacScreen _tacScreen;
        private Aircraft _aircraft;
        private Canvas _canvas;
        private Camera _screenCam;
        private CanvasScaler _canvasScaler;
        private Renderer _screenRenderer;
        private RenderTexture _renderTexture;
        private GameObject _targetCamDisplay;
        private GameObject _landingCamDisplay;
        private TMP_FontAsset _font;

        // Canvas size in units (vanilla 1024 x 512), the main rect and the black backdrops in canvas units
        private Vector2 _canvasSize = new Vector2(TableWidth, TableHeight);
        private Rect _mainPx;
        private PhysicalScreen[] _split;
        // Black backdrops as fractions of the screen texture, and in units of the TacScreen canvas
        private readonly List<Rect> _coverUv = new List<Rect>();
        private readonly List<Rect> _coverPx = new List<Rect>();
        private float _contentWidth = MinContentWidth;
        private RectTransform _main;
        private RectTransform _content;
        // Two to four slots, built in Build
        private Slot[] _slots = new Slot[0];
        // Slot of the automatic EOTS page: the screen with the vanilla target camera, else the right slot
        private Slot _autoEotsSlot;
        private readonly Dictionary<CnoEvilConfig.PortalMFDPage, PortalPage> _pages = new Dictionary<CnoEvilConfig.PortalMFDPage, PortalPage>();
        private readonly List<MovedVanilla> _moved = new List<MovedVanilla>();
        // Canvas panels under the cover: their TMP texts draw over the portal, so they are hidden while it is on
        private readonly List<HiddenPanel> _hidden = new List<HiddenPanel>();
        private readonly List<ExtraCanvas> _extraCanvases = new List<ExtraCanvas>();

        private bool _built;
        private bool _turnedSlots;
        private bool _engaged;
        private float _nextTick;

        private float _appliedRenderScale = 1f;
        private float _vanillaScaleFactor = 1f;
        private float _tsdRangeKm;
        private float _navRangeKm;
        // Set by SettingChanged of the live settings; applied on the next engaged frame
        private bool _liveSettingsDirty = true;
        private bool _subscribed;
        private RenderTexture _vanillaTexture;
        private RenderTexture _hiResTexture;
        private readonly List<KeyValuePair<Material, int>> _swappedTextures = new List<KeyValuePair<Material, int>>();

        private bool _camWasActive;
        private bool _autoEotsActive;
        private CnoEvilConfig.PortalMFDPage _pageBeforeAutoEots;

        public Aircraft Aircraft { get { return _aircraft; } }
        public TacScreen TacScreen { get { return _tacScreen; } }
        public TMP_FontAsset Font { get { return _font; } }
        public Transform CanvasTransform { get { return _canvas != null ? _canvas.transform : null; } }
        public GameObject TargetCamDisplay { get { return _targetCamDisplay; } }
        public GameObject LandingCamDisplay { get { return _landingCamDisplay; } }

        // Layout units; set in Build before the pages are created
        public float HalfWidth { get { return _contentWidth * 0.5f; } }
        public float PageWidth { get { return HalfWidth - 2f * PageInset; } }
        public float PageHeight { get { return ContentHeight - 2f * PageInset; } }
        // A slot turns with its screen (FS-41); set in Build before the pages are created
        public bool TurnedSlots { get { return _turnedSlots; } }

        public static bool IsPresetActive()
        {
            return CnoEvilConfig.IsModActive &&
                   CnoEvilConfig.TacScreenDisplayPreset.Value == CnoEvilConfig.TacScreenPreset.PortalMFD;
        }

        public void Initialize(TacScreen tacScreen, Aircraft aircraft, Cockpit cockpit)
        {
            _tacScreen = tacScreen;
            _aircraft = aircraft;
            if (_built) return;

            if (s_getCanvas != null) _canvas = s_getCanvas(tacScreen);
            if (s_getCam != null) _screenCam = s_getCam(tacScreen);
            if (s_getRenderTexture != null) _renderTexture = s_getRenderTexture(tacScreen);
            if (s_getTargetCamDisplay != null) _targetCamDisplay = s_getTargetCamDisplay(tacScreen);
            if (s_getLandingCamDisplay != null) _landingCamDisplay = s_getLandingCamDisplay(tacScreen);
            if (s_getScreenRenderer != null && cockpit != null) _screenRenderer = s_getScreenRenderer(cockpit);
            if (_canvas == null)
            {
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("TacScreenPortalMFD: TacScreen canvas not found.");
                return;
            }
            _canvasScaler = _canvas.GetComponent<CanvasScaler>();

            TextMeshProUGUI time = s_getTimeDisplay != null ? s_getTimeDisplay(tacScreen) : null;
            if (time != null) _font = time.font;

            // Before ApplyRenderScale: it changes the texture and the scale factor, not the canvas units
            _canvasSize = CanvasUnits(_canvasScaler);
            FindExtraCanvases();
            ApplyRenderScale();
            _tsdRangeKm = CnoEvilConfig.PortalMFDTsdRange_km.Value;
            _navRangeKm = CnoEvilConfig.PortalMFDNavRange_km.Value;

            Build();
            _built = true;
            SubscribeLiveSettings(true);
            SetEngaged(IsPresetActive());
        }

        #region Render scale

        /// <summary>
        /// PortalMFDRenderScale above 1: the TacScreen camera renders into a larger texture with mipmaps, and the canvas
        /// scale factor keeps the canvas size in units, so the vanilla layout and the main screen rects stay the same.
        /// The vanilla texture is a shared asset; the cockpit and screen materials get it back in RestoreRenderScale.
        /// </summary>
        private void ApplyRenderScale()
        {
            float scale = ConfigRenderScale();
            _appliedRenderScale = scale;
            if (scale <= 1.01f || _renderTexture == null || _screenCam == null || _canvasScaler == null) return;
            if (_canvasScaler.uiScaleMode != CanvasScaler.ScaleMode.ConstantPixelSize) return;

            RenderTextureDescriptor desc = _renderTexture.descriptor;
            desc.width = Mathf.RoundToInt(_renderTexture.width * scale);
            desc.height = Mathf.RoundToInt(_renderTexture.height * scale);
            desc.useMipMap = true;
            desc.autoGenerateMips = true;
            // A multisampled texture cannot have mipmaps
            desc.msaaSamples = 1;
            RenderTexture hiRes = new RenderTexture(desc);
            hiRes.name = HiResName;
            hiRes.filterMode = FilterMode.Trilinear;
            hiRes.anisoLevel = 8;
            hiRes.wrapMode = _renderTexture.wrapMode;
            if (!hiRes.Create())
            {
                Destroy(hiRes);
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("TacScreenPortalMFD: the larger screen texture could not be created.");
                return;
            }

            _vanillaTexture = _renderTexture;
            _hiResTexture = hiRes;
            SwapTexture(s_getScreenMaterial != null ? s_getScreenMaterial(_tacScreen) : null, _vanillaTexture, _hiResTexture);
            if (_screenRenderer != null)
            {
                Material[] materials = _screenRenderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++) SwapTexture(materials[i], _vanillaTexture, _hiResTexture);
            }
            _screenCam.targetTexture = _hiResTexture;
            _vanillaScaleFactor = _canvasScaler.scaleFactor;
            _canvasScaler.scaleFactor = _vanillaScaleFactor * scale;
            // A canvas that scales with the texture size keeps its units without a change
            for (int i = 0; i < _extraCanvases.Count; i++)
            {
                ExtraCanvas c = _extraCanvases[i];
                if (c.Scaler == null || c.Scaler.uiScaleMode != CanvasScaler.ScaleMode.ConstantPixelSize) continue;
                c.VanillaScaleFactor = c.Scaler.scaleFactor;
                c.Scaler.scaleFactor = c.VanillaScaleFactor * scale;
            }
            _renderTexture = _hiResTexture;
        }

        private void RestoreRenderScale()
        {
            if (_hiResTexture == null) return;
            for (int i = 0; i < _swappedTextures.Count; i++)
            {
                Material m = _swappedTextures[i].Key;
                if (m != null && m.GetTexture(_swappedTextures[i].Value) == _hiResTexture) m.SetTexture(_swappedTextures[i].Value, _vanillaTexture);
            }
            _swappedTextures.Clear();
            if (_screenCam != null && _screenCam.targetTexture == _hiResTexture) _screenCam.targetTexture = _vanillaTexture;
            if (_canvasScaler != null) _canvasScaler.scaleFactor = _vanillaScaleFactor;
            for (int i = 0; i < _extraCanvases.Count; i++)
            {
                ExtraCanvas c = _extraCanvases[i];
                if (c.Scaler != null && c.VanillaScaleFactor > 0f) c.Scaler.scaleFactor = c.VanillaScaleFactor;
                c.VanillaScaleFactor = 0f;
            }
            _renderTexture = _vanillaTexture;
            _hiResTexture.Release();
            Destroy(_hiResTexture);
            _hiResTexture = null;
        }

        private static float ConfigRenderScale()
        {
            return CnoEvilConfig.PortalMFDRenderScale.Value;
        }

        /// <summary>
        /// Replaces from with to on the material. A larger texture of an earlier TacScreen counts as from too: on a
        /// respawn the new screen can initialize before the old one is destroyed and restores its own texture.
        /// </summary>
        private void SwapTexture(Material m, Texture from, Texture to)
        {
            if (m == null) return;
            int[] ids = m.GetTexturePropertyNameIDs();
            for (int i = 0; i < ids.Length; i++)
            {
                Texture current = m.GetTexture(ids[i]);
                if (current == null || current == to) continue;
                if (current != from && !(current is RenderTexture && current.name == HiResName)) continue;
                m.SetTexture(ids[i], to);
                _swappedTextures.Add(new KeyValuePair<Material, int>(m, ids[i]));
            }
        }

        #endregion

        #region Build

        private void Build()
        {
            // Only the main screen part of the canvas is covered; the small displays keep their vanilla panels
            Rect mainUv = ResolveMainRect();
            PhysicalScreen[] split = _split;
            // Screens that are not adjacent: the root covers the whole canvas, each screen gets its own backdrop
            bool separate = split != null && (mainUv.width <= 0f || mainUv.height <= 0f);
            if (separate) mainUv = new Rect(0f, 0f, 1f, 1f);
            _mainPx = ToCanvas(mainUv);
            _main = PortalMFDGraphics.Rect("PortalMFD_Main", _canvas.transform, mainUv.min, mainUv.max,
                new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            _coverUv.Clear();
            if (separate)
            {
                for (int i = 0; i < split.Length; i++) _coverUv.Add(split[i].Uv);
            }
            else _coverUv.Add(mainUv);
            _coverPx.Clear();
            for (int i = 0; i < _coverUv.Count; i++)
            {
                _coverPx.Add(ToCanvas(_coverUv[i]));
                RectTransform backdrop = PortalMFDGraphics.At("PortalMFD_Backdrop", _main, new Vector2(0.5f, 0.5f),
                    _coverPx[i].center - _mainPx.center, _coverPx[i].size);
                PortalMFDGraphics.AddImage(backdrop, Color.black, null);
            }

            // Full height; the width follows the aspect of the main screen. A screen too narrow for the minimum
            // width scales the content down to fit it.
            float scale = _mainPx.height / ContentHeight;
            _contentWidth = _mainPx.width / scale;
            if (split != null)
            {
                // All slots get the width of the narrowest physical screen: the pages are built once and move between slots
                float halfWidth = float.MaxValue;
                for (int i = 0; i < split.Length; i++)
                {
                    Vector2 view = split[i].View(ToCanvas(split[i].Uv));
                    halfWidth = Mathf.Min(halfWidth, view.x * split[i].Stretch / (view.y / ContentHeight));
                }
                _contentWidth = Mathf.Max(2f * halfWidth, MinContentWidth);
            }
            else if (_contentWidth < MinContentWidth)
            {
                _contentWidth = MinContentWidth;
                scale = Mathf.Min(_mainPx.width / MinContentWidth, scale);
            }
            // Parent of new pages until AttachPage moves them into a slot
            _content = PortalMFDGraphics.At("PortalMFD", _main, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(_contentWidth, ContentHeight));
            _content.localScale = new Vector3(scale, scale, 1f);

            _slots = new Slot[split != null ? split.Length : 2];
            for (int i = 0; i < _slots.Length; i++)
            {
                if (split == null)
                {
                    _slots[i] = BuildSlot(i, i == 0 ? SlotRole.Left : SlotRole.Right, _content, i == 0 ? -HalfWidth * 0.5f : HalfWidth * 0.5f);
                    continue;
                }
                // One slot per physical screen, centered on it; the x scale undoes the stretch of the panel, the
                // rotation undoes the rotation of the texture on the panel
                PhysicalScreen screen = split[i];
                Rect screenPx = ToCanvas(screen.Uv);
                Vector2 view = screen.View(screenPx);
                float s = Mathf.Min(view.y / ContentHeight, view.x * screen.Stretch / HalfWidth);
                RectTransform holder = PortalMFDGraphics.At("PortalScreen" + screen.Role, _main, new Vector2(0.5f, 0.5f),
                    screenPx.center - _mainPx.center, new Vector2(HalfWidth, ContentHeight));
                holder.localScale = new Vector3(s / screen.Stretch, s, 1f);
                holder.localRotation = Quaternion.Euler(0f, 0f, screen.Rotation);
                if (screen.Rotation != 0f) _turnedSlots = true;
                _slots[i] = BuildSlot(i, screen.Role, holder, 0f);
            }

            // Start pages: a page that an earlier slot shows gives the first free page of the cycle order
            for (int i = 0; i < _slots.Length; i++)
            {
                CnoEvilConfig.PortalMFDPage page = ConfigPage(_slots[i].Role);
                _slots[i].ConfigPage = page;
                if (IsShown(page)) page = FirstFreePage();
                SetPage(_slots[i], page);
            }

            _autoEotsSlot = FindAutoEotsSlot();
            FindCoveredPanels();

            // SetEngaged turns the cover on
            _main.gameObject.SetActive(false);
        }

        /// <summary>
        /// Other root canvases below the TacScreen that its camera renders into the same texture (Screen Space Camera
        /// with the TacScreen camera). Each has its own CanvasScaler and size in units. A canvas in another mode does
        /// not draw on the screen texture and is only written to the log.
        /// </summary>
        private void FindExtraCanvases()
        {
            _extraCanvases.Clear();
            Canvas[] canvases = _tacScreen.GetComponentsInChildren<Canvas>(true);
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas c = canvases[i];
                if (c == _canvas || c.transform.IsChildOf(_canvas.transform)) continue;
                // A nested canvas goes with the panel of its root canvas
                Canvas parent = c.transform.parent != null ? c.transform.parent.GetComponentInParent<Canvas>(true) : null;
                if (parent != null && parent.transform.IsChildOf(_tacScreen.transform)) continue;
                bool renders = c.renderMode == RenderMode.ScreenSpaceCamera && _screenCam != null && c.worldCamera == _screenCam;
                if (CnoEvilPlugin.Log != null)
                {
                    CnoEvilPlugin.Log.LogInfo(string.Format("TacScreenPortalMFD: {0} other canvas {1}: {2}, {3} children, {4}.", PrefabName(),
                        c.name, c.renderMode, c.transform.childCount, renders ? "on the screen texture" : "not on the screen texture, ignored"));
                }
                if (!renders) continue;
                ExtraCanvas extra = new ExtraCanvas();
                extra.Canvas = c;
                extra.Scaler = c.GetComponent<CanvasScaler>();
                // Before ApplyRenderScale, like the TacScreen canvas
                extra.Size = CanvasUnits(extra.Scaler);
                _extraCanvases.Add(extra);
            }
        }

        /// <summary>
        /// Panels of the canvases on the screen texture with at least 90% of their area under one backdrop. The cover
        /// hides their images, but their TMP texts still draw over it (OA-27 BasicFlightInstrument), so SetEngaged
        /// hides the panels too, except the parts outside the backdrops (FindKeptParts). A node that is not covered
        /// but touches a backdrop is a container, and its children are examined at any depth (RAH-72: all panels
        /// below one full canvas Image).
        /// </summary>
        private void FindCoveredPanels()
        {
            _hidden.Clear();
            List<string> names = new List<string>();
            CollectCoveredPanels(_canvas.transform, _canvasSize, null, names);
            for (int i = 0; i < _extraCanvases.Count; i++)
            {
                ExtraCanvas c = _extraCanvases[i];
                if (c.Canvas != null) CollectCoveredPanels(c.Canvas.transform, c.Size, c.Canvas.name, names);
            }
            if (CnoEvilPlugin.Log != null && names.Count > 0)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("TacScreenPortalMFD: {0} hides under the portal: {1}.", PrefabName(), string.Join(", ", names.ToArray())));
            }
        }

        private void CollectCoveredPanels(Transform canvas, Vector2 canvasSize, string canvasName, List<string> names)
        {
            List<Rect> covers = new List<Rect>(_coverUv.Count);
            for (int k = 0; k < _coverUv.Count; k++)
            {
                Rect uv = _coverUv[k];
                covers.Add(new Rect(uv.x * canvasSize.x, uv.y * canvasSize.y, uv.width * canvasSize.x, uv.height * canvasSize.y));
            }
            // The canvas is the parent of its direct children: origin bottom left, one unit per unit
            CollectCoveredChildren(canvas, Vector2.zero, Vector2.zero, canvasSize, Vector2.right, Vector2.up, covers, canvasName, names);
        }

        /// <param name="parentPivot">Pivot of node (zero for the canvas)</param>
        /// <param name="prefix">Path of node below the canvas, or the name of an other canvas, or null</param>
        private void CollectCoveredChildren(Transform node, Vector2 parentPivot, Vector2 pivotPos, Vector2 size, Vector2 axisX, Vector2 axisY,
            List<Rect> covers, string prefix, List<string> names)
        {
            for (int i = 0; i < node.childCount; i++)
            {
                RectTransform child = node.GetChild(i) as RectTransform;
                if (child == null || child == _main) continue;
                // A vanilla camera display moves into the EOTS page (FS-41: directly on the canvas): hidden, the page would be empty
                if ((_targetCamDisplay != null && child.gameObject == _targetCamDisplay) ||
                    (_landingCamDisplay != null && child.gameObject == _landingCamDisplay)) continue;
                Vector2 childPos, childSize, childX, childY;
                ChildPlace(parentPivot, child, pivotPos, size, axisX, axisY, out childPos, out childSize, out childX, out childY);
                Rect r = Bounds(child.pivot, childPos, childSize, childX, childY);
                string path = prefix != null ? prefix + "/" + child.name : child.name;
                float area = r.width * r.height;
                bool covered = false, touches = false;
                for (int k = 0; k < covers.Count && !covered; k++)
                {
                    Rect cover = covers[k];
                    float w = Mathf.Min(r.xMax, cover.xMax) - Mathf.Max(r.xMin, cover.xMin);
                    float h = Mathf.Min(r.yMax, cover.yMax) - Mathf.Max(r.yMin, cover.yMin);
                    touches |= w > 0f && h > 0f;
                    covered = area >= 1f && w > 0f && h > 0f && w * h >= 0.9f * area;
                }
                if (!covered)
                {
                    // A container across the backdrops or of zero size: its children can be panels (RAH-72 Image).
                    // A LayoutGroup places its children at run time, their prefab places are wrong.
                    if ((touches || area < 1f) && child.GetComponent<LayoutGroup>() == null)
                        CollectCoveredChildren(child, child.pivot, childPos, childSize, childX, childY, covers, path, names);
                    continue;
                }
                HiddenPanel p = new HiddenPanel();
                p.Target = child;
                FindKeptParts(child, childPos, childSize, childX, childY, covers, p.KeepTargets);
                _hidden.Add(p);
                if (p.KeepTargets.Count > 0) path += string.Format(" ({0} parts outside stay)", p.KeepTargets.Count);
                names.Add(path);
            }
        }

        /// <summary>
        /// Subtrees of a hidden panel whose active graphics touch no backdrop: they draw on a small screen (F-99
        /// RadioControls: a small container under the centre screen, its texts on the radio strips; F-16M: the gun
        /// station box on the upper strip), so they stay visible. Children of a LayoutGroup are not examined: the
        /// group sets their places at run time.
        /// </summary>
        /// <param name="pivotPos">Canvas position of the pivot of node</param>
        /// <param name="size">Size of node in its own units</param>
        /// <param name="axisX">Canvas units per unit of node along its x axis (rotation and scale)</param>
        /// <param name="axisY">Canvas units per unit of node along its y axis</param>
        private void FindKeptParts(RectTransform node, Vector2 pivotPos, Vector2 size, Vector2 axisX, Vector2 axisY, List<Rect> covers, List<RectTransform> kept)
        {
            if (node.GetComponent<LayoutGroup>() != null) return;
            for (int i = 0; i < node.childCount; i++)
            {
                RectTransform rt = node.GetChild(i) as RectTransform;
                if (rt == null || !rt.gameObject.activeSelf) continue;
                Vector2 childPos, childSize, childX, childY;
                ChildPlace(node.pivot, rt, pivotPos, size, axisX, axisY, out childPos, out childSize, out childX, out childY);
                int total = 0, free = 0;
                CountFreeGraphics(rt, childPos, childSize, childX, childY, covers, ref total, ref free);
                if (free == 0) continue;
                bool camera = (_targetCamDisplay != null && rt.gameObject == _targetCamDisplay) ||
                              (_landingCamDisplay != null && rt.gameObject == _landingCamDisplay);
                if (free == total && !camera && rt.GetComponent<CanvasGroup>() == null) kept.Add(rt);
                else FindKeptParts(rt, childPos, childSize, childX, childY, covers, kept);
            }
        }

        /// <summary>
        /// Active graphics of the subtree, and those of them that touch no backdrop. A LayoutGroup counts as one graphic
        /// with its own rect: it places its children inside it at run time, their prefab places are wrong (FS-41
        /// KinematicInfo).
        /// </summary>
        private static void CountFreeGraphics(RectTransform node, Vector2 pivotPos, Vector2 size, Vector2 axisX, Vector2 axisY, List<Rect> covers, ref int total, ref int free)
        {
            bool layout = node.GetComponent<LayoutGroup>() != null;
            if (layout || node.GetComponent<Graphic>() != null)
            {
                total++;
                Rect r = Bounds(node.pivot, pivotPos, size, axisX, axisY);
                // A TMP text of zero size draws around its point (RAH-72 MissileWarningText): Overlaps is false for it
                bool touches = false;
                for (int k = 0; k < covers.Count && !touches; k++) touches = covers[k].Overlaps(r) || covers[k].Contains(r.center);
                if (!touches) free++;
            }
            if (layout) return;
            for (int i = 0; i < node.childCount; i++)
            {
                RectTransform rt = node.GetChild(i) as RectTransform;
                if (rt == null || !rt.gameObject.activeSelf) continue;
                Vector2 childPos, childSize, childX, childY;
                ChildPlace(node.pivot, rt, pivotPos, size, axisX, axisY, out childPos, out childSize, out childX, out childY);
                CountFreeGraphics(rt, childPos, childSize, childX, childY, covers, ref total, ref free);
            }
        }

        /// <summary>
        /// Canvas position of the pivot, own size and canvas axes of a child, from anchors, sizes, pivots, rotations
        /// and scales, because the layout is not valid at spawn (FS-41: panels turned -90 degrees).
        /// </summary>
        private static void ChildPlace(Vector2 parentPivot, RectTransform child, Vector2 pivotPos, Vector2 size, Vector2 axisX, Vector2 axisY,
            out Vector2 childPos, out Vector2 childSize, out Vector2 childX, out Vector2 childY)
        {
            childSize = Vector2.Scale(child.anchorMax - child.anchorMin, size) + child.sizeDelta;
            Vector2 anchor = new Vector2(Mathf.Lerp(child.anchorMin.x, child.anchorMax.x, child.pivot.x), Mathf.Lerp(child.anchorMin.y, child.anchorMax.y, child.pivot.y));
            Vector2 local = Vector2.Scale(anchor - parentPivot, size) + child.anchoredPosition;
            childPos = pivotPos + axisX * local.x + axisY * local.y;
            float a = child.localEulerAngles.z * Mathf.Deg2Rad;
            float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
            childX = (axisX * cos + axisY * sin) * child.localScale.x;
            childY = (axisY * cos - axisX * sin) * child.localScale.y;
        }

        /// <summary>Place of a direct canvas child: the canvas is the parent, origin bottom left, one unit per unit.</summary>
        private static void ChildPlace(RectTransform child, Vector2 canvasSize, out Vector2 pivotPos, out Vector2 size, out Vector2 axisX, out Vector2 axisY)
        {
            ChildPlace(Vector2.zero, child, Vector2.zero, canvasSize, Vector2.right, Vector2.up, out pivotPos, out size, out axisX, out axisY);
        }

        /// <summary>Axis-aligned canvas rect around a node of size and pivot, placed by pivotPos and its canvas axes.</summary>
        private static Rect Bounds(Vector2 pivot, Vector2 pivotPos, Vector2 size, Vector2 axisX, Vector2 axisY)
        {
            Vector2 x0 = axisX * (-pivot.x * size.x), x1 = axisX * ((1f - pivot.x) * size.x);
            Vector2 y0 = axisY * (-pivot.y * size.y), y1 = axisY * ((1f - pivot.y) * size.y);
            Vector2 a = pivotPos + x0 + y0, b = pivotPos + x1 + y0, c = pivotPos + x0 + y1, d = pivotPos + x1 + y1;
            return Rect.MinMaxRect(Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)), Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y)),
                Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)), Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y)));
        }

        /// <summary>
        /// Slot on the physical screen that shows the vanilla target camera (F-16M, F-22E: the centre screen): the
        /// panel that holds targetCamDisplay, its center in a screen rect. The panel is the highest node from the
        /// canvas down to the camera display that is not larger than 1.5 screens: a full canvas container (RAH-72
        /// Image) is not a panel. Two halves, or no screen holds it: the right slot.
        /// </summary>
        private Slot FindAutoEotsSlot()
        {
            Slot right = SlotOf(SlotRole.Right);
            if (_split == null || _targetCamDisplay == null || !_targetCamDisplay.transform.IsChildOf(_canvas.transform)) return right;
            List<RectTransform> chain = new List<RectTransform>();
            for (Transform t = _targetCamDisplay.transform; t != null && t != _canvas.transform; t = t.parent)
            {
                RectTransform rt = t as RectTransform;
                if (rt == null) return right;
                chain.Add(rt);
            }
            // Canvas place of each node, from the canvas down
            Vector2 parentPivot = Vector2.zero, pivotPos = Vector2.zero, size = _canvasSize, axisX = Vector2.right, axisY = Vector2.up;
            for (int c = chain.Count - 1; c >= 0; c--)
            {
                RectTransform rt = chain[c];
                Vector2 childPos, childSize, childX, childY;
                ChildPlace(parentPivot, rt, pivotPos, size, axisX, axisY, out childPos, out childSize, out childX, out childY);
                Rect r = Bounds(rt.pivot, childPos, childSize, childX, childY);
                for (int i = 0; i < _split.Length && i < _slots.Length; i++)
                {
                    Rect screen = ToCanvas(_split[i].Uv);
                    if (screen.Contains(r.center) && r.width * r.height <= 1.5f * screen.width * screen.height) return _slots[i];
                }
                parentPivot = rt.pivot;
                pivotPos = childPos;
                size = childSize;
                axisX = childX;
                axisY = childY;
            }
            return right;
        }

        private static void HidePanel(HiddenPanel p)
        {
            if (p.Target == null || p.Group != null) return;
            p.Group = p.Target.GetComponent<CanvasGroup>();
            p.Added = p.Group == null;
            if (p.Added) p.Group = p.Target.gameObject.AddComponent<CanvasGroup>();
            p.Alpha = p.Group.alpha;
            p.Group.alpha = 0f;
            for (int i = 0; i < p.KeepTargets.Count; i++)
            {
                RectTransform t = p.KeepTargets[i];
                if (t == null || t.GetComponent<CanvasGroup>() != null) continue;
                CanvasGroup keep = t.gameObject.AddComponent<CanvasGroup>();
                keep.ignoreParentGroups = true;
                p.KeepGroups.Add(keep);
            }
        }

        private static void ShowPanel(HiddenPanel p)
        {
            if (p.Group == null) return;
            for (int i = 0; i < p.KeepGroups.Count; i++)
            {
                if (p.KeepGroups[i] != null) Destroy(p.KeepGroups[i]);
            }
            p.KeepGroups.Clear();
            if (p.Added) Destroy(p.Group);
            else p.Group.alpha = p.Alpha;
            p.Group = null;
        }

        /// <summary>
        /// Main screen rect as a fraction of the canvas: the table entry of the prefab, else the active 'border' frame,
        /// else the whole canvas. Sets _split from the table entry.
        /// </summary>
        private Rect ResolveMainRect()
        {
            string prefab = PrefabName();
            Rect rect;
            string source;
            ScreenLayout layout;
            _split = null;
            string key = LayoutKey(prefab);
            if (key != null)
            {
                layout = s_layouts[key];
                rect = layout.Main;
                _split = layout.Split;
                source = key == prefab ? "table" : "table " + key;
            }
            else
            {
                rect = new Rect(0f, 0f, 1f, 1f);
                source = "whole canvas";
                Transform canvas = _canvas.transform;
                for (int i = 0; i < canvas.childCount; i++)
                {
                    RectTransform child = canvas.GetChild(i) as RectTransform;
                    if (child == null || !child.gameObject.activeSelf) continue;
                    if (!string.Equals(child.name, "border", StringComparison.OrdinalIgnoreCase)) continue;
                    Rect border = CanvasPixelRect(child, _canvasSize);
                    if (border.width < 100f || border.height < 100f) continue;
                    rect = Rect.MinMaxRect(Mathf.Max(0f, border.xMin / _canvasSize.x), Mathf.Max(0f, border.yMin / _canvasSize.y),
                        Mathf.Min(1f, border.xMax / _canvasSize.x), Mathf.Min(1f, border.yMax / _canvasSize.y));
                    source = "border";
                    break;
                }
            }
            if (CnoEvilPlugin.Log != null)
            {
                Rect px = ToCanvas(rect);
                string screens = string.Empty;
                if (_split != null)
                {
                    string[] parts = new string[_split.Length];
                    for (int i = 0; i < parts.Length; i++)
                    {
                        Rect s = ToCanvas(_split[i].Uv);
                        parts[i] = string.Format("{0} x {1:0} y {2:0} w {3:0} h {4:0}", _split[i].Role, s.x, s.y, s.width, s.height);
                        if (_split[i].Rotation != 0f) parts[i] += string.Format(" rot {0:0}", _split[i].Rotation);
                    }
                    screens = string.Format(", {0} screens: {1}", _split.Length, string.Join("; ", parts));
                }
                CnoEvilPlugin.Log.LogInfo(string.Format("TacScreenPortalMFD: {0} main screen x {1:0} y {2:0} w {3:0} h {4:0} of canvas {5:0} x {6:0} ({7}{8}).",
                    prefab, px.x, px.y, px.width, px.height, _canvasSize.x, _canvasSize.y, source, screens));
            }
            return rect;
        }

        private string PrefabName()
        {
            return _tacScreen.gameObject.name.Replace("(Clone)", string.Empty).Trim();
        }

        /// <summary>
        /// Table key of the TacScreen: the prefab name, else the longest key the name contains (a mod loader can give
        /// the instance another name than prefab + "(Clone)"); null when no key fits.
        /// </summary>
        private static string LayoutKey(string prefab)
        {
            if (s_layouts.ContainsKey(prefab)) return prefab;
            string best = null;
            foreach (string key in s_layouts.Keys)
            {
                if (prefab.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (best == null || key.Length > best.Length) best = key;
            }
            return best;
        }

        /// <summary>Fraction of the canvas to canvas units.</summary>
        private Rect ToCanvas(Rect uv)
        {
            return new Rect(uv.x * _canvasSize.x, uv.y * _canvasSize.y, uv.width * _canvasSize.x, uv.height * _canvasSize.y);
        }

        /// <summary>
        /// Size in units of a canvas that the TacScreen camera renders. The canvas fills the screen texture: texture
        /// pixels divided by the scale factor of the CanvasScaler mode. Without texture: 1024 x 512, the vanilla size.
        /// </summary>
        private Vector2 CanvasUnits(CanvasScaler scaler)
        {
            if (_renderTexture == null || _renderTexture.width <= 0 || _renderTexture.height <= 0) return new Vector2(TableWidth, TableHeight);
            Vector2 pixels = new Vector2(_renderTexture.width, _renderTexture.height);
            if (scaler == null) return pixels;
            float factor = 1f;
            switch (scaler.uiScaleMode)
            {
                case CanvasScaler.ScaleMode.ConstantPixelSize:
                    factor = scaler.scaleFactor;
                    break;
                case CanvasScaler.ScaleMode.ScaleWithScreenSize:
                    Vector2 reference = scaler.referenceResolution;
                    if (reference.x <= 0f || reference.y <= 0f) break;
                    float w = pixels.x / reference.x;
                    float h = pixels.y / reference.y;
                    if (scaler.screenMatchMode == CanvasScaler.ScreenMatchMode.Expand) factor = Mathf.Min(w, h);
                    else if (scaler.screenMatchMode == CanvasScaler.ScreenMatchMode.Shrink) factor = Mathf.Max(w, h);
                    else factor = Mathf.Pow(2f, Mathf.Lerp(Mathf.Log(w, 2f), Mathf.Log(h, 2f), scaler.matchWidthOrHeight));
                    break;
            }
            if (factor <= 0f) factor = 1f;
            return pixels / factor;
        }

        /// <summary>
        /// Rect of a direct canvas child in canvas units (origin bottom left), from its anchors: the canvas layout is
        /// not valid yet at spawn. A rotated child gives the rect around it.
        /// </summary>
        private static Rect CanvasPixelRect(RectTransform rt, Vector2 refSize)
        {
            Vector2 pivotPos, size, axisX, axisY;
            ChildPlace(rt, refSize, out pivotPos, out size, out axisX, out axisY);
            return Bounds(rt.pivot, pivotPos, size, axisX, axisY);
        }

        private Slot BuildSlot(int index, SlotRole role, RectTransform parent, float x)
        {
            Slot slot = new Slot();
            slot.Index = index;
            slot.Role = role;
            slot.Rect = PortalMFDGraphics.At("PortalHalf" + role, parent, new Vector2(0.5f, 0.5f), new Vector2(x, 0f), new Vector2(HalfWidth, ContentHeight));

            Image frame = PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Frame", slot.Rect, 1f), PortalMFDGraphics.QuarterFrame, PortalShape.Frame);
            frame.raycastTarget = false;

            slot.PageArea = PortalMFDGraphics.At("PageArea", slot.Rect, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(PageWidth, PageHeight));
            PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Border", slot.PageArea, 0f), PortalMFDGraphics.PageBorder, PortalShape.Frame);

            // Page name in the top left corner, above the page (the page goes in as the first child of PageArea)
            slot.Title = PortalMFDGraphics.Text("Title", slot.PageArea, new Vector2(0f, 1f), new Vector2(31f, -14f), new Vector2(50f, 14f),
                string.Empty, 10.5f, PortalMFDGraphics.TextWhite, TextAlignmentOptions.TopLeft, _font);
            return slot;
        }

        public PortalButton AddButton(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, string label, float fontSize)
        {
            PortalButton b = new PortalButton();
            b.Rect = PortalMFDGraphics.At("Btn_" + label, parent, anchor, pos, size);
            b.Background = PortalMFDGraphics.AddImage(b.Rect, PortalMFDGraphics.ButtonBox, null);
            b.Text = PortalMFDGraphics.Text("Text", b.Rect, new Vector2(0.5f, 0.5f), Vector2.zero, size, label, fontSize,
                PortalMFDGraphics.TextWhite, TextAlignmentOptions.Center, _font);
            return b;
        }

        private PortalPage GetPage(CnoEvilConfig.PortalMFDPage id)
        {
            PortalPage page;
            if (_pages.TryGetValue(id, out page)) return page;
            switch (id)
            {
                case CnoEvilConfig.PortalMFDPage.RADAR: page = new PortalPageRadar(); break;
                case CnoEvilConfig.PortalMFDPage.RWR: page = new PortalPageRwr(); break;
                case CnoEvilConfig.PortalMFDPage.SMS: page = new PortalPageSms(); break;
                case CnoEvilConfig.PortalMFDPage.EOTS: page = new PortalPageEots(); break;
                case CnoEvilConfig.PortalMFDPage.NAV: page = new PortalPageNav(); break;
                case CnoEvilConfig.PortalMFDPage.HSI: page = new PortalPageHsi(); break;
                default: page = new PortalPageTsd(); break;
            }
            page.Create(this, id, _content);
            _pages[id] = page;
            return page;
        }

        #endregion

        #region Pages

        private void SetPage(Slot slot, CnoEvilConfig.PortalMFDPage id)
        {
            PortalPage previous = slot.Page;
            if (previous != null && previous.Id == id)
            {
                previous.SetVisible(true);
                return;
            }

            // A page shows on one slot only: the slot that shows it takes this slot's page
            Slot other = SlotShowing(id);
            if (other != null)
            {
                if (previous != null) AttachPage(other, previous);
                else
                {
                    other.Page.SetVisible(false);
                    other.Page = null;
                }
            }

            if (previous != null && (other == null || other.Page != previous)) previous.SetVisible(false);

            AttachPage(slot, GetPage(id));
            if (other != null) RefreshTitle(other);
        }

        /// <summary>Slot that shows the page, or null.</summary>
        private Slot SlotShowing(CnoEvilConfig.PortalMFDPage id)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot s = _slots[i];
                if (s != null && s.Page != null && s.Page.Id == id) return s;
            }
            return null;
        }

        private bool IsShown(CnoEvilConfig.PortalMFDPage id)
        {
            return SlotShowing(id) != null;
        }

        /// <summary>First page of s_cycleOrder that no slot shows.</summary>
        private CnoEvilConfig.PortalMFDPage FirstFreePage()
        {
            for (int k = 0; k < s_cycleOrder.Length; k++)
            {
                if (!IsShown(s_cycleOrder[k])) return s_cycleOrder[k];
            }
            return s_cycleOrder[0];
        }

        private void AttachPage(Slot slot, PortalPage page)
        {
            slot.Page = page;
            page.Root.SetParent(slot.PageArea, false);
            page.Root.SetAsFirstSibling();
            page.Root.anchoredPosition = Vector2.zero;
            page.SetVisible(true);
            page.OnShow();
            page.Tick(Time.time);
            RefreshTitle(slot);
        }

        private static void RefreshTitle(Slot slot)
        {
            slot.Title.text = slot.Page != null ? slot.Page.Id.ToString() : string.Empty;
        }

        /// <summary>
        /// Next page of s_cycleOrder on this slot; the pages on the other slots are skipped.
        /// </summary>
        private void CyclePage(Slot slot)
        {
            int start = slot.Page != null ? Array.IndexOf(s_cycleOrder, slot.Page.Id) : -1;
            for (int k = 1; k <= s_cycleOrder.Length; k++)
            {
                CnoEvilConfig.PortalMFDPage id = s_cycleOrder[(start + k + s_cycleOrder.Length) % s_cycleOrder.Length];
                Slot other = SlotShowing(id);
                if (other != null && other != slot) continue;
                // A manual change on the automatic EOTS slot ends the automatic EOTS page
                if (slot == _autoEotsSlot) _autoEotsActive = false;
                SetPage(slot, id);
                return;
            }
        }

        private static CnoEvilConfig.PortalMFDPage ConfigPage(SlotRole role)
        {
            switch (role)
            {
                case SlotRole.Left: return CnoEvilConfig.PortalMFDLeftPage.Value;
                case SlotRole.Center: return CnoEvilConfig.PortalMFDCenterPage.Value;
                case SlotRole.Lower: return CnoEvilConfig.PortalMFDLowerPage.Value;
                default: return CnoEvilConfig.PortalMFDRightPage.Value;
            }
        }

        private static KeyCode PageKey(SlotRole role)
        {
            switch (role)
            {
                case SlotRole.Left: return CnoEvilConfig.PortalMFDLeftPageKey.Value;
                case SlotRole.Center: return CnoEvilConfig.PortalMFDCenterPageKey.Value;
                case SlotRole.Lower: return CnoEvilConfig.PortalMFDLowerPageKey.Value;
                default: return CnoEvilConfig.PortalMFDRightPageKey.Value;
            }
        }

        /// <summary>Slot of the role, or null (Center and Lower exist only on cockpits with those portal screens).</summary>
        private Slot SlotOf(SlotRole role)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] != null && _slots[i].Role == role) return _slots[i];
            }
            return null;
        }

        #endregion

        #region Vanilla panels

        /// <summary>
        /// Moves a vanilla panel into a page, centered on center and scaled to fit box. Vanilla still turns it on and off.
        /// The panel goes back to its place when the display turns off.
        /// </summary>
        public void MoveVanilla(RectTransform target, RectTransform newParent, Vector2 center, Vector2 box)
        {
            if (target == null || newParent == null) return;
            for (int i = 0; i < _moved.Count; i++)
            {
                if (_moved[i].Target == target) return;
            }
            MovedVanilla m = new MovedVanilla();
            m.Target = target;
            m.OriginalParent = target.parent;
            m.OriginalSibling = target.GetSiblingIndex();
            m.AnchorMin = target.anchorMin;
            m.AnchorMax = target.anchorMax;
            m.Pivot = target.pivot;
            m.AnchoredPosition = target.anchoredPosition;
            m.SizeDelta = target.sizeDelta;
            m.Scale = target.localScale;
            m.Rotation = target.localRotation;
            m.NewParent = newParent;
            m.NewCenter = center;
            m.NewBox = box;
            _moved.Add(m);
            if (_engaged) ApplyMove(m);
        }

        private static void ApplyMove(MovedVanilla m)
        {
            if (m.Applied || m.Target == null || m.NewParent == null) return;
            Vector2 size = m.Target.rect.size;
            if (size.x < 1f || size.y < 1f) size = m.NewBox;
            m.Target.SetParent(m.NewParent, false);
            m.Target.anchorMin = new Vector2(0.5f, 0.5f);
            m.Target.anchorMax = new Vector2(0.5f, 0.5f);
            m.Target.pivot = new Vector2(0.5f, 0.5f);
            m.Target.sizeDelta = size;
            m.Target.anchoredPosition = m.NewCenter;
            float s = Mathf.Min(m.NewBox.x / size.x, m.NewBox.y / size.y);
            m.Target.localScale = new Vector3(s, s, 1f);
            // The slot already turns with its screen (FS-41: the vanilla camera images are turned -90 degrees)
            m.Target.localRotation = Quaternion.identity;
            m.Applied = true;
        }

        private static void RestoreMove(MovedVanilla m)
        {
            if (!m.Applied || m.Target == null) return;
            if (m.OriginalParent != null)
            {
                m.Target.SetParent(m.OriginalParent, false);
                m.Target.SetSiblingIndex(Mathf.Min(m.OriginalSibling, m.OriginalParent.childCount - 1));
            }
            m.Target.anchorMin = m.AnchorMin;
            m.Target.anchorMax = m.AnchorMax;
            m.Target.pivot = m.Pivot;
            m.Target.sizeDelta = m.SizeDelta;
            m.Target.anchoredPosition = m.AnchoredPosition;
            m.Target.localScale = m.Scale;
            m.Target.localRotation = m.Rotation;
            m.Applied = false;
        }

        /// <summary>
        /// First RectTransform below the TacScreen canvas whose name starts with prefix (not inside an earlier match).
        /// </summary>
        public RectTransform FindVanilla(string prefix)
        {
            if (_canvas == null) return null;
            return FindRecursive(_canvas.transform, prefix);
        }

        private RectTransform FindRecursive(Transform parent, string prefix)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child == _main) continue;
                if (child.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return child as RectTransform;
            }
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child == _main) continue;
                RectTransform found = FindRecursive(child, prefix);
                if (found != null) return found;
            }
            return null;
        }

        #endregion

        #region Update

        private void SetEngaged(bool engaged)
        {
            if (!_built) return;
            if (_engaged == engaged) return;
            _engaged = engaged;
            if (engaged)
            {
                _main.gameObject.SetActive(true);
                _main.SetAsLastSibling();
                for (int i = 0; i < _moved.Count; i++) ApplyMove(_moved[i]);
                for (int i = 0; i < _hidden.Count; i++) HidePanel(_hidden[i]);
            }
            else
            {
                // Put the vanilla panels back before the cover goes inactive, so they never go inactive
                for (int i = 0; i < _moved.Count; i++) RestoreMove(_moved[i]);
                for (int i = 0; i < _hidden.Count; i++) ShowPanel(_hidden[i]);
                _main.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            if (!_built) return;
            try
            {
                SetEngaged(IsPresetActive() && _aircraft != null && !_aircraft.disabled);
                if (!_engaged) return;

                if (_liveSettingsDirty)
                {
                    ApplyLiveSettings();
                    _liveSettingsDirty = false;
                }
                UpdateAutoEots();
                for (int i = 0; i < _slots.Length; i++) HandleKey(_slots[i], PageKey(_slots[i].Role));
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TacScreenPortalMFD.Update", ex);
            }
            if (!_engaged) return;

            float now = Time.time;
            bool tick = now >= _nextTick;
            if (tick) _nextTick = now + TickInterval;
            // Each page on its own: an error on one page does not stop the other slots
            for (int i = 0; i < _slots.Length; i++)
            {
                PortalPage page = _slots[i].Page;
                if (page == null) continue;
                try
                {
                    if (tick) page.Tick(now);
                    page.FrameTick();
                }
                catch (Exception ex)
                {
                    PatchErrorLog.Once("PortalPage." + page.Id, ex);
                }
            }
        }

        /// <summary>
        /// The key cycles the page of the slot; with Shift it cycles the range of the page.
        /// </summary>
        private void HandleKey(Slot slot, KeyCode key)
        {
            if (key == KeyCode.None || !Input.GetKeyDown(key)) return;
            // Typing ; or ' in the chat or the editor does not change pages
            if (NuclearOption.MissionEditorScripts.InputFieldChecker.InsideInputField) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift)
            {
                if (slot.Page != null) slot.Page.CycleRange();
            }
            else CyclePage(slot);
        }

        private void SubscribeLiveSettings(bool subscribe)
        {
            if (subscribe == _subscribed) return;
            _subscribed = subscribe;
            Subscribe(CnoEvilConfig.PortalMFDRenderScale, subscribe);
            Subscribe(CnoEvilConfig.PortalMFDLeftPage, subscribe);
            Subscribe(CnoEvilConfig.PortalMFDRightPage, subscribe);
            Subscribe(CnoEvilConfig.PortalMFDCenterPage, subscribe);
            Subscribe(CnoEvilConfig.PortalMFDLowerPage, subscribe);
            Subscribe(CnoEvilConfig.PortalMFDTsdRange_km, subscribe);
            Subscribe(CnoEvilConfig.PortalMFDNavRange_km, subscribe);
        }

        private void Subscribe<T>(BepInEx.Configuration.ConfigEntry<T> entry, bool subscribe)
        {
            if (subscribe) entry.SettingChanged += OnLiveSettingChanged;
            else entry.SettingChanged -= OnLiveSettingChanged;
        }

        private void OnLiveSettingChanged(object sender, EventArgs e)
        {
            _liveSettingsDirty = true;
        }

        /// <summary>
        /// Settings that change in flight: render scale, start pages, page ranges.
        /// </summary>
        private void ApplyLiveSettings()
        {
            float scale = ConfigRenderScale();
            if (!Mathf.Approximately(scale, _appliedRenderScale))
            {
                RestoreRenderScale();
                ApplyRenderScale();
            }

            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                CnoEvilConfig.PortalMFDPage page = ConfigPage(slot.Role);
                if (page == slot.ConfigPage) continue;
                slot.ConfigPage = page;
                if (slot == _autoEotsSlot) _autoEotsActive = false;
                SetPage(slot, page);
            }

            float tsd = CnoEvilConfig.PortalMFDTsdRange_km.Value;
            if (!Mathf.Approximately(tsd, _tsdRangeKm))
            {
                _tsdRangeKm = tsd;
                SetPageRange(CnoEvilConfig.PortalMFDPage.TSD, tsd);
            }
            float nav = CnoEvilConfig.PortalMFDNavRange_km.Value;
            if (!Mathf.Approximately(nav, _navRangeKm))
            {
                _navRangeKm = nav;
                SetPageRange(CnoEvilConfig.PortalMFDPage.NAV, nav);
            }
        }

        private void SetPageRange(CnoEvilConfig.PortalMFDPage id, float km)
        {
            PortalPage page;
            PortalPagePlan plan = _pages.TryGetValue(id, out page) ? page as PortalPagePlan : null;
            if (plan != null) plan.SetRangeKm(km);
        }

        private void UpdateAutoEots()
        {
            bool camActive = (_targetCamDisplay != null && _targetCamDisplay.activeSelf) ||
                             (_landingCamDisplay != null && _landingCamDisplay.activeSelf);
            if (camActive == _camWasActive) return;
            _camWasActive = camActive;

            Slot slot = _autoEotsSlot;
            if (slot == null) return;
            if (camActive)
            {
                // The setting only gates the switch to EOTS; a page switched before it was turned off is still restored
                if (!CnoEvilConfig.PortalMFDAutoEots.Value) return;
                if (IsShown(CnoEvilConfig.PortalMFDPage.EOTS)) return;
                _pageBeforeAutoEots = slot.Page != null ? slot.Page.Id : CnoEvilConfig.PortalMFDPage.TSD;
                SetPage(slot, CnoEvilConfig.PortalMFDPage.EOTS);
                _autoEotsActive = true;
            }
            else if (_autoEotsActive)
            {
                _autoEotsActive = false;
                if (slot.Page == null || slot.Page.Id != CnoEvilConfig.PortalMFDPage.EOTS) return;
                // Another slot took the previous page meanwhile: this slot shows the next free page
                Slot holder = SlotShowing(_pageBeforeAutoEots);
                if (holder != null && holder != slot) CyclePage(slot);
                else SetPage(slot, _pageBeforeAutoEots);
            }
        }

        /// <summary>
        /// Puts the vanilla panels back and restores the screen texture. TacScreenPatches.ApplyPreset calls it before
        /// it destroys the component; OnDestroy does not move panels, because there the TacScreen can be going away too.
        /// </summary>
        public void Teardown()
        {
            try
            {
                for (int i = 0; i < _moved.Count; i++) RestoreMove(_moved[i]);
                _moved.Clear();
                RestoreRenderScale();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TacScreenPortalMFD.Teardown", ex);
            }
        }

        private void OnDestroy()
        {
            try
            {
                SubscribeLiveSettings(false);
                _moved.Clear();
                foreach (KeyValuePair<CnoEvilConfig.PortalMFDPage, PortalPage> kv in _pages) kv.Value.Dispose();
                _pages.Clear();
                RestoreRenderScale();
                if (_main != null) Destroy(_main.gameObject);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TacScreenPortalMFD.OnDestroy", ex);
            }
        }

        #endregion
    }
}
