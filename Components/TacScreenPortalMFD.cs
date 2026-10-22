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
    /// halves. Each half shows one page with its name in the top left corner. The content is
    /// 300 units high and as wide as the main screen allows (at least 690), so the halves use the whole screen.
    /// PortalMFDLeftPageKey / PortalMFDRightPageKey cycle the page of a half; with Shift they cycle its range.
    /// Vanilla keeps running below the cover. The EOTS page moves the vanilla camera screens into the page and puts
    /// them back when the display turns off (F10, other preset, destroy).
    /// </summary>
    public class TacScreenPortalMFD : MonoBehaviour
    {
        public const float MinContentWidth = 690f;
        public const float ContentHeight = 300f;
        private const float PageInset = 2f;
        private const float TickInterval = 0.1f;
        private const float RefWidth = 1024f;
        private const float RefHeight = 512f;
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

        // Main screen of each vanilla TacScreen prefab, in pixels of the 1024 x 512 canvas (origin bottom left).
        // The canvas renders into one texture: the cockpit maps the main screen and the small displays to different
        // parts of it. Values from the vanilla 'border' frames; SFB, fastBomber1, cricket and QuadVTOL1 have no frame
        // around the whole screen, their values come from the panel layout.
        private static readonly Dictionary<string, Rect> s_mainRects = new Dictionary<string, Rect>
        {
            { "tacScreen_CAS1", new Rect(0f, 149f, 1024f, 363f) },
            { "tacScreen_trainer", new Rect(0f, 149f, 1024f, 363f) },
            { "VTOLTrainer1_tacscreen", new Rect(0f, 149f, 1024f, 363f) },
            { "tacScreen_fighter1", new Rect(0f, 149f, 1024f, 363f) },
            { "tacScreen_smallFighter1", new Rect(0f, 149f, 1024f, 363f) },
            { "tacScreen_UFO", new Rect(0f, 149f, 1024f, 363f) },
            { "tacScreen_EW1", new Rect(0f, 129f, 1024f, 383f) },
            { "tacScreen_Multirole1", new Rect(0f, 128f, 768f, 384f) },
            { "tacScreen_AttackHelo1", new Rect(0f, 128f, 768f, 384f) },
            { "tacScreen_UtilityHelo1", new Rect(0f, 128f, 535f, 384f) },
            { "tacScreen_SFB", new Rect(0f, 146f, 1024f, 366f) },
            { "tacScreen_fastBomber1", new Rect(0f, 146f, 1024f, 366f) },
            { "tacScreen_cricket", new Rect(0f, 0f, 809f, 512f) },
            { "tacScreen_QuadVTOL1", new Rect(0f, 140f, 873f, 372f) }
        };

        private struct PhysicalScreen
        {
            public Rect Px;
            // Physical aspect of the panel / aspect of its pixel rect: the panel stretches the texture by this much horizontally
            public float Stretch;

            public PhysicalScreen(Rect px, float stretch)
            {
                Px = px;
                Stretch = stretch;
            }
        }

        // Prefabs whose main screen rect maps to two physical screens: each half of the portal goes on one of them.
        // tacScreen_EW1: UVs of the cockpit mesh EW1_targetScreen (outer screen u 0..0.5596, inner u 0.5555..0.9992,
        // v 0.2542..1); 2 px kept clear on each side of the overlap.
        private static readonly Dictionary<string, PhysicalScreen[]> s_splitScreens = new Dictionary<string, PhysicalScreen[]>
        {
            { "tacScreen_EW1", new[] { new PhysicalScreen(new Rect(0f, 130f, 567f, 382f), 1.06f), new PhysicalScreen(new Rect(571f, 130f, 453f, 382f), 1.04f) } }
        };

        private class Half
        {
            public int Index;
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
            public RectTransform NewParent;
            public Vector2 NewCenter, NewBox;
            public bool Applied;
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

        private Rect _mainPx;
        private float _contentWidth = MinContentWidth;
        private RectTransform _main;
        private RectTransform _backdrop;
        private RectTransform _content;
        private readonly Half[] _halves = new Half[2];
        private readonly Dictionary<CnoEvilConfig.PortalMFDPage, PortalPage> _pages = new Dictionary<CnoEvilConfig.PortalMFDPage, PortalPage>();
        private readonly List<MovedVanilla> _moved = new List<MovedVanilla>();

        private bool _built;
        private bool _engaged;
        private float _nextTick;

        private float _appliedRenderScale = 1f;
        private float _vanillaScaleFactor = 1f;
        private float _tsdRangeKm;
        private float _navRangeKm;
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

        public static bool IsPresetActive()
        {
            return CnoEvilConfig.IsModActive &&
                   CnoEvilConfig.TacScreenDisplayPreset != null &&
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

            ApplyRenderScale();
            _tsdRangeKm = CnoEvilConfig.PortalMFDTsdRange_km != null ? CnoEvilConfig.PortalMFDTsdRange_km.Value : 40f;
            _navRangeKm = CnoEvilConfig.PortalMFDNavRange_km != null ? CnoEvilConfig.PortalMFDNavRange_km.Value : 80f;

            Build();
            _built = true;
            SetEngaged(IsPresetActive());
        }

        #region Render scale

        /// <summary>
        /// PortalMFDRenderScale above 1: the TacScreen camera renders into a larger texture with mipmaps, and the canvas
        /// scale factor keeps the canvas at 1024 x 512 units, so the vanilla layout and the main screen rects stay the same.
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
            _canvasScaler.scaleFactor = scale;
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
            _renderTexture = _vanillaTexture;
            _hiResTexture.Release();
            Destroy(_hiResTexture);
            _hiResTexture = null;
        }

        private static float ConfigRenderScale()
        {
            return CnoEvilConfig.PortalMFDRenderScale != null ? CnoEvilConfig.PortalMFDRenderScale.Value : 1f;
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
            _mainPx = ResolveMainRect();
            _main = PortalMFDGraphics.Rect("PortalMFD_Main", _canvas.transform,
                new Vector2(_mainPx.xMin / RefWidth, _mainPx.yMin / RefHeight), new Vector2(_mainPx.xMax / RefWidth, _mainPx.yMax / RefHeight),
                new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            _backdrop = PortalMFDGraphics.Stretch("PortalMFD_Backdrop", _main, 0f);
            PortalMFDGraphics.AddImage(_backdrop, Color.black, null);

            PhysicalScreen[] split;
            s_splitScreens.TryGetValue(PrefabName(), out split);

            // Full height; the width follows the aspect of the main screen. A screen too narrow for the minimum
            // width scales the content down to fit it.
            float scale = _mainPx.height / ContentHeight;
            _contentWidth = _mainPx.width / scale;
            if (split != null)
            {
                // Both halves get the width of the narrower physical screen: the pages are built once and move between halves
                float halfWidth = float.MaxValue;
                for (int i = 0; i < split.Length; i++)
                    halfWidth = Mathf.Min(halfWidth, split[i].Px.width * split[i].Stretch / (split[i].Px.height / ContentHeight));
                _contentWidth = Mathf.Max(2f * halfWidth, MinContentWidth);
            }
            else if (_contentWidth < MinContentWidth)
            {
                _contentWidth = MinContentWidth;
                scale = Mathf.Min(_mainPx.width / MinContentWidth, scale);
            }
            // Parent of new pages until AttachPage moves them into a half
            _content = PortalMFDGraphics.At("PortalMFD", _main, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(_contentWidth, ContentHeight));
            _content.localScale = new Vector3(scale, scale, 1f);

            for (int i = 0; i < 2; i++)
            {
                if (split == null)
                {
                    _halves[i] = BuildHalf(i, _content, i == 0 ? -HalfWidth * 0.5f : HalfWidth * 0.5f);
                    continue;
                }
                // One half per physical screen, centered on it; the x scale undoes the stretch of the panel
                PhysicalScreen screen = split[i];
                float s = Mathf.Min(screen.Px.height / ContentHeight, screen.Px.width * screen.Stretch / HalfWidth);
                RectTransform holder = PortalMFDGraphics.At(i == 0 ? "PortalScreenLeft" : "PortalScreenRight", _main, new Vector2(0.5f, 0.5f),
                    screen.Px.center - _mainPx.center, new Vector2(HalfWidth, ContentHeight));
                holder.localScale = new Vector3(s / screen.Stretch, s, 1f);
                _halves[i] = BuildHalf(i, holder, 0f);
            }

            CnoEvilConfig.PortalMFDPage left = ConfigPage(0);
            CnoEvilConfig.PortalMFDPage right = ConfigPage(1);
            _halves[0].ConfigPage = left;
            _halves[1].ConfigPage = right;
            if (right == left) right = left == CnoEvilConfig.PortalMFDPage.TSD ? CnoEvilConfig.PortalMFDPage.RADAR : CnoEvilConfig.PortalMFDPage.TSD;
            SetPage(_halves[0], left);
            SetPage(_halves[1], right);

            // SetEngaged turns the cover on
            _main.gameObject.SetActive(false);
        }

        /// <summary>
        /// Main screen rect in canvas pixels: the table entry of the prefab, else the active 'border' frame, else the whole canvas.
        /// </summary>
        private Rect ResolveMainRect()
        {
            string prefab = PrefabName();
            Rect rect;
            string source;
            if (s_mainRects.TryGetValue(prefab, out rect))
            {
                source = "table";
            }
            else
            {
                rect = new Rect(0f, 0f, RefWidth, RefHeight);
                source = "whole canvas";
                Transform canvas = _canvas.transform;
                for (int i = 0; i < canvas.childCount; i++)
                {
                    RectTransform child = canvas.GetChild(i) as RectTransform;
                    if (child == null || !child.gameObject.activeSelf) continue;
                    if (!string.Equals(child.name, "border", StringComparison.OrdinalIgnoreCase)) continue;
                    Rect border = CanvasPixelRect(child);
                    if (border.width < 100f || border.height < 100f) continue;
                    rect = Rect.MinMaxRect(Mathf.Max(0f, border.xMin), Mathf.Max(0f, border.yMin), Mathf.Min(RefWidth, border.xMax), Mathf.Min(RefHeight, border.yMax));
                    source = "border";
                    break;
                }
            }
            if (CnoEvilPlugin.Log != null)
                CnoEvilPlugin.Log.LogInfo(string.Format("TacScreenPortalMFD: {0} main screen x {1:0} y {2:0} w {3:0} h {4:0} ({5}{6}).",
                    prefab, rect.x, rect.y, rect.width, rect.height, source, s_splitScreens.ContainsKey(prefab) ? ", split: 2 screens" : string.Empty));
            return rect;
        }

        private string PrefabName()
        {
            return _tacScreen.gameObject.name.Replace("(Clone)", string.Empty).Trim();
        }

        /// <summary>
        /// Rect of a direct canvas child in canvas pixels (origin bottom left), from its anchors: the canvas layout is
        /// not valid yet at spawn.
        /// </summary>
        private static Rect CanvasPixelRect(RectTransform rt)
        {
            Vector2 refSize = new Vector2(RefWidth, RefHeight);
            Vector2 size = Vector2.Scale(Vector2.Scale(rt.anchorMax - rt.anchorMin, refSize) + rt.sizeDelta, rt.localScale);
            Vector2 anchor = new Vector2(Mathf.Lerp(rt.anchorMin.x, rt.anchorMax.x, rt.pivot.x), Mathf.Lerp(rt.anchorMin.y, rt.anchorMax.y, rt.pivot.y));
            Vector2 pivotPos = Vector2.Scale(anchor, refSize) + rt.anchoredPosition;
            return new Rect(pivotPos - Vector2.Scale(rt.pivot, size), size);
        }

        private Half BuildHalf(int index, RectTransform parent, float x)
        {
            Half half = new Half();
            half.Index = index;
            half.Rect = PortalMFDGraphics.At(index == 0 ? "PortalHalfLeft" : "PortalHalfRight", parent, new Vector2(0.5f, 0.5f), new Vector2(x, 0f), new Vector2(HalfWidth, ContentHeight));

            Image frame = PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Frame", half.Rect, 1f), PortalMFDGraphics.QuarterFrame, PortalShape.Frame);
            frame.raycastTarget = false;

            half.PageArea = PortalMFDGraphics.At("PageArea", half.Rect, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(PageWidth, PageHeight));
            PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Border", half.PageArea, 0f), PortalMFDGraphics.PageBorder, PortalShape.Frame);

            // Page name in the top left corner, above the page (the page goes in as the first child of PageArea)
            half.Title = PortalMFDGraphics.Text("Title", half.PageArea, new Vector2(0f, 1f), new Vector2(31f, -14f), new Vector2(50f, 14f),
                string.Empty, 10.5f, PortalMFDGraphics.TextWhite, TextAlignmentOptions.TopLeft, _font);
            return half;
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

        private void SetPage(Half half, CnoEvilConfig.PortalMFDPage id)
        {
            Half other = _halves[1 - half.Index];
            PortalPage previous = half.Page;
            if (previous != null && previous.Id == id)
            {
                previous.SetVisible(true);
                return;
            }

            // A page shows on one half only: the other half takes this half's page
            if (other != null && other.Page != null && other.Page.Id == id)
            {
                if (previous != null) AttachPage(other, previous);
                else
                {
                    other.Page.SetVisible(false);
                    other.Page = null;
                }
            }

            if (previous != null && (other == null || other.Page != previous)) previous.SetVisible(false);

            AttachPage(half, GetPage(id));
            if (other != null) RefreshTitle(other);
        }

        private void AttachPage(Half half, PortalPage page)
        {
            half.Page = page;
            page.Root.SetParent(half.PageArea, false);
            page.Root.SetAsFirstSibling();
            page.Root.anchoredPosition = Vector2.zero;
            page.SetVisible(true);
            page.OnShow();
            page.Tick(Time.time);
            RefreshTitle(half);
        }

        private static void RefreshTitle(Half half)
        {
            half.Title.text = half.Page != null ? half.Page.Id.ToString() : string.Empty;
        }

        /// <summary>
        /// Next page of s_cycleOrder on this half; the page on the other half is skipped.
        /// </summary>
        private void CyclePage(Half half)
        {
            Half other = _halves[1 - half.Index];
            int start = half.Page != null ? Array.IndexOf(s_cycleOrder, half.Page.Id) : -1;
            for (int k = 1; k <= s_cycleOrder.Length; k++)
            {
                CnoEvilConfig.PortalMFDPage id = s_cycleOrder[(start + k + s_cycleOrder.Length) % s_cycleOrder.Length];
                if (other.Page != null && other.Page.Id == id) continue;
                // A manual change on the right half ends the automatic EOTS page
                if (half.Index == 1) _autoEotsActive = false;
                SetPage(half, id);
                return;
            }
        }

        private static CnoEvilConfig.PortalMFDPage ConfigPage(int index)
        {
            if (index == 0) return CnoEvilConfig.PortalMFDLeftPage != null ? CnoEvilConfig.PortalMFDLeftPage.Value : CnoEvilConfig.PortalMFDPage.RADAR;
            return CnoEvilConfig.PortalMFDRightPage != null ? CnoEvilConfig.PortalMFDRightPage.Value : CnoEvilConfig.PortalMFDPage.TSD;
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
            }
            else
            {
                // Put the vanilla panels back before the cover goes inactive, so they never go inactive
                for (int i = 0; i < _moved.Count; i++) RestoreMove(_moved[i]);
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

                ApplyLiveSettings();
                UpdateAutoEots();
                HandleKey(_halves[0], CnoEvilConfig.PortalMFDLeftPageKey != null ? CnoEvilConfig.PortalMFDLeftPageKey.Value : KeyCode.Semicolon);
                HandleKey(_halves[1], CnoEvilConfig.PortalMFDRightPageKey != null ? CnoEvilConfig.PortalMFDRightPageKey.Value : KeyCode.Quote);

            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TacScreenPortalMFD.Update", ex);
            }
            if (!_engaged) return;

            float now = Time.time;
            bool tick = now >= _nextTick;
            if (tick) _nextTick = now + TickInterval;
            // Each page on its own: an error on one page does not stop the other half
            for (int i = 0; i < 2; i++)
            {
                PortalPage page = _halves[i].Page;
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
        /// The key cycles the page of the half; with Shift it cycles the range of the page.
        /// </summary>
        private void HandleKey(Half half, KeyCode key)
        {
            if (key == KeyCode.None || !Input.GetKeyDown(key)) return;
            // Typing ; or ' in the chat or the editor does not change pages
            if (NuclearOption.MissionEditorScripts.InputFieldChecker.InsideInputField) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift)
            {
                if (half.Page != null) half.Page.CycleRange();
            }
            else CyclePage(half);
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

            for (int i = 0; i < 2; i++)
            {
                CnoEvilConfig.PortalMFDPage page = ConfigPage(i);
                if (page == _halves[i].ConfigPage) continue;
                _halves[i].ConfigPage = page;
                if (i == 1) _autoEotsActive = false;
                SetPage(_halves[i], page);
            }

            float tsd = CnoEvilConfig.PortalMFDTsdRange_km != null ? CnoEvilConfig.PortalMFDTsdRange_km.Value : 40f;
            if (!Mathf.Approximately(tsd, _tsdRangeKm))
            {
                _tsdRangeKm = tsd;
                SetPageRange(CnoEvilConfig.PortalMFDPage.TSD, tsd);
            }
            float nav = CnoEvilConfig.PortalMFDNavRange_km != null ? CnoEvilConfig.PortalMFDNavRange_km.Value : 80f;
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

            Half right = _halves[1];
            if (camActive)
            {
                // The setting only gates the switch to EOTS; a page switched before it was turned off is still restored
                if (CnoEvilConfig.PortalMFDAutoEots != null && !CnoEvilConfig.PortalMFDAutoEots.Value) return;
                if (IsShown(CnoEvilConfig.PortalMFDPage.EOTS)) return;
                _pageBeforeAutoEots = right.Page != null ? right.Page.Id : CnoEvilConfig.PortalMFDPage.TSD;
                SetPage(right, CnoEvilConfig.PortalMFDPage.EOTS);
                _autoEotsActive = true;
            }
            else if (_autoEotsActive)
            {
                _autoEotsActive = false;
                if (right.Page == null || right.Page.Id != CnoEvilConfig.PortalMFDPage.EOTS) return;
                // The left half took the previous page meanwhile: the right half shows the next free page
                Half left = _halves[0];
                if (left.Page != null && left.Page.Id == _pageBeforeAutoEots) CyclePage(right);
                else SetPage(right, _pageBeforeAutoEots);
            }
        }

        private bool IsShown(CnoEvilConfig.PortalMFDPage id)
        {
            for (int i = 0; i < 2; i++)
            {
                if (_halves[i].Page != null && _halves[i].Page.Id == id) return true;
            }
            return false;
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
