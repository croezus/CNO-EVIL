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
    /// The PortalMFD cockpit slots on the HMD (ADR-73), for small screens and low resolutions. Each window has one
    /// shape on every aircraft: the height is HMDPortalPageSize of the screen, the width HMDPortalPagesAspect times
    /// the height (ADR-79). The window shows its own copy of the page that the cockpit slot shows, built for the
    /// window shape. The copy is drawn off screen by an own camera into a texture of twice the window size with
    /// mipmaps, and the FlightHud shows that texture: the downsampling smooths the lines like the larger cockpit
    /// texture does (ADR-81; drawn straight on the HUD canvas the lines had pixel stairs). The copy follows the slot: the page keys
    /// change the cockpit and the HMD together, and the copy takes the range of the slot page. A page that holds the
    /// vanilla camera screens (EOTS) cannot have a copy: the window shows the slot part of the TacScreen texture,
    /// fitted into the window without stretch. Only in the cockpit view. Nothing vanilla is changed, so turning off
    /// only hides the windows.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public class HMDPortalPages : MonoBehaviour
    {
        private static readonly Func<FlightHud, Canvas> f_canvas = FastReflection.CreateFieldGetter<FlightHud, Canvas>("canvas");

        private const float TickInterval = 0.1f;
        // Content units of a window: the height of a cockpit slot, so the page text has the cockpit size
        private const float ContentHeight = TacScreenPortalMFD.ContentHeight;
        private const float Inset = TacScreenPortalMFD.PageInset;
        // Off-screen texture pixels per window pixel; the mipmaps sample it down (ADR-81)
        private const float Supersample = 2f;
        // Off-screen rig far below the world, so no other camera sees its canvas. The orthographic size keeps the
        // canvas some kilometres large, so the float steps of the far position stay far below one texel.
        private static readonly Vector3 RigPosition = new Vector3(0f, -1.0e6f, 0f);
        private const float RigOrthoSize = 1000f;
        private const float RigPlaneDistance = 100f;

        private class Window
        {
            public GameObject Root;
            public RectTransform Rect;
            public CanvasGroup Group;
            // HUD side: the off-screen texture
            public RawImage View;
            // Off-screen side: camera, canvas and its texture
            public GameObject Rig;
            public Camera Cam;
            public CanvasScaler Scaler;
            public RenderTexture Texture;
            // Window content in content units, scaled to the window
            public RectTransform Content;
            public RectTransform PageArea;
            public TextMeshProUGUI Title;
            public CnoEvilConfig.PortalMFDPage TitleId;
            public bool TitleShown;
            // Slot part of the TacScreen texture, for a page without a copy (EOTS)
            public RectTransform CutRect;
            public RawImage Cut;
            public float CutAspect, CutTurn;
            // Copies of the pages, built for ContentWidth by CopyHost
            public readonly Dictionary<CnoEvilConfig.PortalMFDPage, PortalPage> Copies = new Dictionary<CnoEvilConfig.PortalMFDPage, PortalPage>();
            public PortalPage Shown;
            public TacScreenPortalMFD CopyHost;
            public float ContentWidth;
            // Layout inputs of the last placement; a change places the window again
            public int ScreenW, ScreenH;
            public float X, Y, Size, Aspect;
        }

        private FlightHud _flightHud;
        private Aircraft _aircraft;
        private bool _shown = true;
        private float _nextTick;
        private readonly Window[] _windows = new Window[CnoEvilConfig.HMDPortalWindowCount];

        public void Initialize(FlightHud flightHud, Aircraft aircraft)
        {
            if (aircraft != _aircraft)
            {
                for (int i = 0; i < _windows.Length; i++) DisposeCopies(_windows[i]);
            }
            _flightHud = flightHud;
            _aircraft = aircraft;
        }

        private void Update()
        {
            KeyCode key = CnoEvilConfig.HMDPortalPagesKey.Value;
            if (key == KeyCode.None)
            {
                _shown = true;
                return;
            }
            if (NuclearOption.MissionEditorScripts.InputFieldChecker.InsideInputField) return;
            if (Input.GetKeyDown(key)) _shown = !_shown;
        }

        private void LateUpdate()
        {
            try
            {
                Camera mainCam = GetMainCamera();
                TacScreenPortalMFD portal = CanShow(mainCam) ? TacScreenPortalMFD.ForAircraft(_aircraft) : null;
                if (portal == null)
                {
                    HideAll();
                    return;
                }
                Canvas canvas = f_canvas != null ? f_canvas(_flightHud) : null;
                if (canvas == null)
                {
                    HideAll();
                    return;
                }

                float now = Time.time;
                bool tick = now >= _nextTick;
                if (tick) _nextTick = now + TickInterval;
                float opacity = Mathf.Clamp01(CnoEvilConfig.HMDPortalPagesOpacity.Value);
                for (int i = 0; i < _windows.Length; i++)
                {
                    CnoEvilConfig.HMDPortalSlot role = CnoEvilConfig.HMDPortalPageSlot[i].Value;
                    PortalPage slotPage;
                    if (role == CnoEvilConfig.HMDPortalSlot.None || !portal.TryGetSlotPage(role, out slotPage))
                    {
                        SetActive(_windows[i], false);
                        continue;
                    }
                    Window w = _windows[i];
                    if (w == null || w.Root == null)
                    {
                        DisposeCopies(w);
                        DestroyWindow(w);
                        w = CreateWindow(canvas, i, portal);
                        _windows[i] = w;
                    }
                    if (!Place(w, i, canvas, mainCam))
                    {
                        SetActive(w, false);
                        continue;
                    }
                    w.Group.alpha = opacity;
                    SetActive(w, true);
                    try
                    {
                        ShowPage(w, portal, role, slotPage, tick, now);
                    }
                    catch (Exception ex)
                    {
                        PatchErrorLog.Once("HMDPortalPages.ShowPage", ex);
                        ShowCopy(w, null);
                    }
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("HMDPortalPages.LateUpdate", ex);
                HideAll();
            }
        }

        private void OnDisable()
        {
            HideAll();
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _windows.Length; i++)
            {
                try
                {
                    DestroyWindow(_windows[i]);
                }
                catch (Exception ex)
                {
                    PatchErrorLog.Once("HMDPortalPages.OnDestroy", ex);
                }
                _windows[i] = null;
            }
        }

        private static Camera GetMainCamera()
        {
            return CameraStateManager.i != null && CameraStateManager.i.mainCamera != null ? CameraStateManager.i.mainCamera : Camera.main;
        }

        private bool CanShow(Camera mainCam)
        {
            if (!_shown || !CnoEvilConfig.EnableHMDPortalPages.Value) return false;
            if (!TacScreenPortalMFD.IsPresetActive()) return false;
            if (_flightHud == null || _aircraft == null || _aircraft.disabled || mainCam == null) return false;
            CameraStateManager csm = CameraStateManager.i;
            return csm != null && csm.currentState != null && csm.currentState == csm.cockpitState;
        }

        private void HideAll()
        {
            for (int i = 0; i < _windows.Length; i++) SetActive(_windows[i], false);
        }

        private static void SetActive(Window w, bool active)
        {
            if (w == null) return;
            if (w.Root != null && w.Root.activeSelf != active) w.Root.SetActive(active);
            // The camera renders only while the window shows
            if (w.Rig != null && w.Rig.activeSelf != active) w.Rig.SetActive(active);
        }

        private static void DestroyWindow(Window w)
        {
            if (w == null) return;
            DisposeCopies(w);
            if (w.Cam != null) w.Cam.targetTexture = null;
            if (w.Texture != null)
            {
                w.Texture.Release();
                Destroy(w.Texture);
                w.Texture = null;
            }
            if (w.Rig != null) Destroy(w.Rig);
            if (w.Root != null) Destroy(w.Root);
        }

        #region Pages

        /// <summary>
        /// Shows the copy of the slot page, or the texture part of the slot for a page without a copy. A copy takes
        /// the state of the slot page when it shows and when the range of the slot page changes.
        /// </summary>
        private void ShowPage(Window w, TacScreenPortalMFD portal, CnoEvilConfig.HMDPortalSlot role, PortalPage slotPage, bool tick, float now)
        {
            // Copies of a destroyed portal, or of another aircraft, are built again; the copy of a tandem seat stays
            if (w.Copies.Count > 0 && (w.CopyHost == null || w.CopyHost.Aircraft != _aircraft)) DisposeCopies(w);

            SetTitle(w, slotPage);
            if (slotPage == null)
            {
                ShowCopy(w, null);
                ShowCut(w, false);
                return;
            }

            PortalPage copy = slotPage.HoldsVanillaPanels ? null : GetCopy(w, portal, slotPage.Id);
            if (copy == null)
            {
                ShowCopy(w, null);
                PlaceCut(w, portal, role);
                return;
            }

            ShowCut(w, false);
            if (w.Shown != copy)
            {
                ShowCopy(w, null);
                copy.TakeState(slotPage);
                copy.SetVisible(true);
                copy.OnShow();
                copy.Tick(now);
                w.Shown = copy;
            }
            else if (copy.RangeState != slotPage.RangeState)
            {
                copy.TakeState(slotPage);
            }
            if (tick) copy.Tick(now);
            copy.FrameTick();
        }

        private PortalPage GetCopy(Window w, TacScreenPortalMFD portal, CnoEvilConfig.PortalMFDPage id)
        {
            PortalPage copy;
            if (w.Copies.TryGetValue(id, out copy)) return copy;
            copy = portal.CreateHmdPage(id, w.PageArea, w.ContentWidth - 2f * Inset, ContentHeight - 2f * Inset);
            if (copy == null) return null;
            copy.Root.SetAsFirstSibling();
            w.Copies[id] = copy;
            w.CopyHost = portal;
            return copy;
        }

        /// <summary>Hides the copy on the window and shows the given one; null hides only.</summary>
        private static void ShowCopy(Window w, PortalPage copy)
        {
            if (w.Shown != null && w.Shown != copy) w.Shown.SetVisible(false);
            w.Shown = copy;
            if (copy != null) copy.SetVisible(true);
        }

        private static void DisposeCopies(Window w)
        {
            if (w == null) return;
            foreach (KeyValuePair<CnoEvilConfig.PortalMFDPage, PortalPage> kv in w.Copies)
            {
                try
                {
                    kv.Value.Dispose();
                    if (kv.Value.Root != null) Destroy(kv.Value.Root.gameObject);
                }
                catch (Exception ex)
                {
                    PatchErrorLog.Once("HMDPortalPages.DisposeCopies", ex);
                }
            }
            w.Copies.Clear();
            w.Shown = null;
            w.CopyHost = null;
        }

        private static void SetTitle(Window w, PortalPage slotPage)
        {
            bool shown = slotPage != null;
            if (shown == w.TitleShown && (!shown || slotPage.Id == w.TitleId)) return;
            w.TitleShown = shown;
            if (shown) w.TitleId = slotPage.Id;
            w.Title.text = shown ? slotPage.Id.ToString() : string.Empty;
        }

        private static void ShowCut(Window w, bool on)
        {
            GameObject go = w.CutRect.gameObject;
            if (go.activeSelf != on) go.SetActive(on);
        }

        /// <summary>
        /// The slot part of the TacScreen texture, fitted into the page area without stretch and turned upright
        /// (FS-41 slots are turned in the texture). Placed again only when the slot shape changes.
        /// </summary>
        private static void PlaceCut(Window w, TacScreenPortalMFD portal, CnoEvilConfig.HMDPortalSlot role)
        {
            Texture texture;
            Rect uv;
            float turn, aspect;
            if (!portal.TryGetSlotView(role, out texture, out uv, out turn, out aspect))
            {
                ShowCut(w, false);
                return;
            }
            if (w.Cut.texture != texture) w.Cut.texture = texture;
            if (w.Cut.uvRect != uv) w.Cut.uvRect = uv;
            ShowCut(w, true);
            // Tolerances: float noise of the slot corners must not place the cut again each frame
            if (Mathf.Abs(aspect - w.CutAspect) < 1e-3f && Mathf.Abs(Mathf.DeltaAngle(turn, w.CutTurn)) < 0.5f) return;
            w.CutAspect = aspect;
            w.CutTurn = turn;

            aspect = Mathf.Max(0.1f, aspect);
            float areaW = w.ContentWidth - 2f * Inset;
            float areaH = ContentHeight - 2f * Inset;
            Vector2 fit = aspect > areaW / areaH ? new Vector2(areaW, areaW / aspect) : new Vector2(areaH * aspect, areaH);
            // The image shows the texture rect around the slot; turned by -turn it is upright. At a quarter turn its
            // sides change places.
            bool quarter = Mathf.Abs(Mathf.Sin(turn * Mathf.Deg2Rad)) > 0.7071f;
            w.CutRect.sizeDelta = quarter ? new Vector2(fit.y, fit.x) : fit;
            w.CutRect.localRotation = Quaternion.Euler(0f, 0f, -turn);
        }

        #endregion

        #region Window

        private Window CreateWindow(Canvas canvas, int index, TacScreenPortalMFD portal)
        {
            Window w = new Window();
            w.Root = new GameObject("CnoEvil_HMDPortalPage" + (index + 1), typeof(RectTransform), typeof(CanvasGroup));
            w.Root.layer = canvas.gameObject.layer;
            w.Root.transform.SetParent(canvas.transform, false);
            // First child: HUD and HMD symbols draw on top of the window
            w.Root.transform.SetAsFirstSibling();
            w.Rect = (RectTransform)w.Root.transform;
            w.Rect.pivot = new Vector2(0.5f, 0.5f);
            w.Group = w.Root.GetComponent<CanvasGroup>();
            w.Group.interactable = false;
            w.Group.blocksRaycasts = false;
            w.View = w.Root.AddComponent<RawImage>();
            w.View.raycastTarget = false;
            w.View.color = Color.white;

            // Off-screen rig (ADR-81): a camera on the TacScreen canvas layer, which the main camera does not draw,
            // renders a screen space canvas with the window content into the window texture
            int layer = portal.CanvasLayer;
            w.Rig = new GameObject("CnoEvil_HMDPortalRig" + (index + 1));
            w.Rig.layer = layer;
            w.Rig.transform.SetPositionAndRotation(RigPosition, Quaternion.identity);
            w.Cam = w.Rig.AddComponent<Camera>();
            w.Cam.clearFlags = CameraClearFlags.SolidColor;
            w.Cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
            w.Cam.cullingMask = 1 << layer;
            w.Cam.orthographic = true;
            w.Cam.orthographicSize = RigOrthoSize;
            w.Cam.nearClipPlane = 1f;
            w.Cam.farClipPlane = RigPlaneDistance * 2f;
            w.Cam.depth = -100f;
            w.Cam.allowHDR = false;
            w.Cam.allowMSAA = false;
            w.Cam.useOcclusionCulling = false;

            GameObject canvasGo = new GameObject("Canvas", typeof(RectTransform));
            canvasGo.layer = layer;
            canvasGo.transform.SetParent(w.Rig.transform, false);
            Canvas rigCanvas = canvasGo.AddComponent<Canvas>();
            rigCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            rigCanvas.worldCamera = w.Cam;
            rigCanvas.planeDistance = RigPlaneDistance;
            w.Scaler = canvasGo.AddComponent<CanvasScaler>();
            w.Scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            w.Scaler.scaleFactor = 1f;

            TMP_FontAsset font = portal.Font;
            Vector2 mid = new Vector2(0.5f, 0.5f);
            w.Content = PortalMFDGraphics.At("Content", canvasGo.transform, mid, Vector2.zero, new Vector2(ContentHeight, ContentHeight));
            // Black like the cockpit backdrop under a slot, and the slot frame (TacScreenPortalMFD.BuildSlot)
            PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Backdrop", w.Content, 0f), Color.black, null);
            PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Frame", w.Content, 1f), PortalMFDGraphics.QuarterFrame, PortalShape.Frame);
            w.PageArea = PortalMFDGraphics.Stretch("PageArea", w.Content, Inset);

            w.CutRect = PortalMFDGraphics.At("Cut", w.PageArea, mid, Vector2.zero, Vector2.one);
            w.Cut = w.CutRect.gameObject.AddComponent<RawImage>();
            w.Cut.raycastTarget = false;
            w.CutRect.gameObject.SetActive(false);

            PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Border", w.PageArea, 0f), PortalMFDGraphics.PageBorder, PortalShape.Frame);
            w.Title = PortalMFDGraphics.Text("Title", w.PageArea, new Vector2(0f, 1f), new Vector2(31f, -14f), new Vector2(50f, 14f),
                string.Empty, 10.5f, PortalMFDGraphics.TextWhite, TextAlignmentOptions.TopLeft, font);

            w.Root.SetActive(false);
            w.Rig.SetActive(false);
            w.ScreenW = -1;
            w.CutAspect = -1f;
            return w;
        }

        /// <summary>
        /// Places the window on the screen: height HMDPortalPageSize of the screen, width HMDPortalPagesAspect times
        /// the height (at most the screen width). The content is ContentHeight units high and as wide as the window
        /// shape; a new width builds the page copies again. Runs again only when the screen or the config changes.
        /// False while the canvas has no size yet.
        /// </summary>
        private static bool Place(Window w, int index, Canvas canvas, Camera mainCam)
        {
            float x = CnoEvilConfig.HMDPortalPageX[index].Value;
            float y = CnoEvilConfig.HMDPortalPageY[index].Value;
            float size = Mathf.Clamp(CnoEvilConfig.HMDPortalPageSize[index].Value, 0.05f, 1.0f);
            float aspect = Mathf.Clamp(CnoEvilConfig.HMDPortalPagesAspect.Value, 1.0f, 2.5f);
            int screenW = Screen.width;
            int screenH = Screen.height;
            if (screenW == w.ScreenW && screenH == w.ScreenH && x == w.X && y == w.Y && size == w.Size && aspect == w.Aspect) return true;

            RectTransform parentRect = w.Rect.parent as RectTransform;
            if (parentRect == null) return false;
            float h = size * screenH;
            float wPx = Mathf.Min(h * aspect, screenW);
            Vector2 center = new Vector2(screenW * x, screenH * y);
            Vector2 screenMin = center - new Vector2(wPx, h) * 0.5f;
            Vector2 screenMax = center + new Vector2(wPx, h) * 0.5f;
            Vector2 localMin, localMax;
            if (!ScreenToCanvasLocal(parentRect, canvas, screenMin, mainCam, out localMin) ||
                !ScreenToCanvasLocal(parentRect, canvas, screenMax, mainCam, out localMax))
            {
                return false;
            }
            Vector2 box = localMax - localMin;
            if (box.x <= 0f || box.y <= 0f) return false;
            // Without its texture the camera would draw on the screen
            if (!SizeTexture(w, wPx, h)) return false;

            w.ScreenW = screenW;
            w.ScreenH = screenH;
            w.X = x;
            w.Y = y;
            w.Size = size;
            w.Aspect = aspect;

            // Anchored at the parent pivot, which is the origin of the local points
            w.Rect.anchorMin = parentRect.pivot;
            w.Rect.anchorMax = parentRect.pivot;
            w.Rect.anchoredPosition = (localMin + localMax) * 0.5f;
            w.Rect.sizeDelta = box;

            // Whole units: a width change from float noise must not build the pages again
            float contentWidth = Mathf.Round(ContentHeight * box.x / box.y);
            if (Mathf.Abs(contentWidth - w.ContentWidth) > 0.5f)
            {
                DisposeCopies(w);
                w.ContentWidth = contentWidth;
                w.Content.sizeDelta = new Vector2(contentWidth, ContentHeight);
                w.CutAspect = -1f;
            }
            // The off-screen canvas is ContentHeight units high: the content fills the texture
            w.Scaler.scaleFactor = w.Texture.height / ContentHeight;
            w.Content.localScale = Vector3.one;
            return true;
        }

        /// <summary>
        /// The window texture at Supersample times the window pixel size, with mipmaps and trilinear filter, so the
        /// HUD samples it down smoothly. Made again only when the size changes. False if it cannot be made.
        /// </summary>
        private static bool SizeTexture(Window w, float widthPx, float heightPx)
        {
            int max = Mathf.Min(4096, SystemInfo.maxTextureSize);
            int rtW = Mathf.Clamp(Mathf.RoundToInt(widthPx * Supersample), 16, max);
            int rtH = Mathf.Clamp(Mathf.RoundToInt(heightPx * Supersample), 16, max);
            if (w.Texture != null && w.Texture.width == rtW && w.Texture.height == rtH && w.Texture.IsCreated()) return true;

            w.Cam.targetTexture = null;
            w.View.texture = null;
            if (w.Texture != null)
            {
                w.Texture.Release();
                Destroy(w.Texture);
                w.Texture = null;
            }
            RenderTextureDescriptor desc = new RenderTextureDescriptor(rtW, rtH, RenderTextureFormat.ARGB32, 0);
            desc.useMipMap = true;
            desc.autoGenerateMips = true;
            desc.msaaSamples = 1;
            RenderTexture rt = new RenderTexture(desc);
            rt.name = "CnoEvil_HMDPortalPageRT";
            rt.filterMode = FilterMode.Trilinear;
            rt.anisoLevel = 8;
            rt.wrapMode = TextureWrapMode.Clamp;
            if (!rt.Create())
            {
                Destroy(rt);
                PatchErrorLog.Once("HMDPortalPages.SizeTexture", new InvalidOperationException("window texture " + rtW + " x " + rtH + " could not be created"));
                return false;
            }
            w.Texture = rt;
            w.Cam.targetTexture = rt;
            w.View.texture = rt;
            return true;
        }

        /// <summary>
        /// Screen point to a local point of the root canvas rect (origin at its pivot). A screen space canvas covers
        /// the screen (Overlay) or the camera pixel rect (Camera), so the point follows from the rect alone: no camera
        /// ray onto the canvas plane, which misses for a frame when the camera and the canvas are out of step (floating
        /// origin shift). World space canvas: the ray. False while the canvas has no size yet.
        /// </summary>
        internal static bool ScreenToCanvasLocal(RectTransform parent, Canvas canvas, Vector2 screenPt, Camera mainCam, out Vector2 local)
        {
            local = Vector2.zero;
            if (parent == null) return false;
            if (canvas != null && canvas.renderMode == RenderMode.WorldSpace)
            {
                Camera uiCam = canvas.worldCamera != null ? canvas.worldCamera : mainCam;
                return RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenPt, uiCam, out local);
            }
            Rect rect = parent.rect;
            if (rect.width <= 0f || rect.height <= 0f) return false;
            Rect screen = new Rect(0f, 0f, Screen.width, Screen.height);
            if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceCamera)
            {
                Camera uiCam = canvas.worldCamera != null ? canvas.worldCamera : mainCam;
                if (uiCam != null) screen = uiCam.pixelRect;
            }
            if (screen.width <= 0f || screen.height <= 0f) return false;
            local = new Vector2(
                rect.xMin + (screenPt.x - screen.xMin) / screen.width * rect.width,
                rect.yMin + (screenPt.y - screen.yMin) / screen.height * rect.height);
            return true;
        }

        #endregion
    }
}
