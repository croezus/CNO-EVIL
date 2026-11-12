using System;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    /// <summary>
    /// Copies of the PortalMFD cockpit slots on the HMD (ADR-73), for small screens and low resolutions.
    /// Each window is a RawImage on the FlightHud canvas that shows the part of the TacScreen texture with one slot
    /// (TacScreenPortalMFD.TryGetSlotView). No page is built a second time: the page keys change the cockpit and the
    /// HMD together, and the cost is one UI quad per window. A slot that is turned in the texture (FS-41) is turned
    /// back upright. Only in the cockpit view. Nothing vanilla is changed, so turning off only hides the windows.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public class HMDPortalPages : MonoBehaviour
    {
        private static readonly Func<FlightHud, Canvas> f_canvas = FastReflection.CreateFieldGetter<FlightHud, Canvas>("canvas");

        private class Window
        {
            public GameObject Root;
            public RectTransform Rect;
            public CanvasGroup Group;
            public RectTransform ImageRect;
            public RawImage Image;
            // Layout inputs of the last placement; a change places the window again
            public int ScreenW, ScreenH;
            public float X, Y, Size, Aspect, Turn;
        }

        private FlightHud _flightHud;
        private Aircraft _aircraft;
        private bool _shown = true;
        private readonly Window[] _windows = new Window[CnoEvilConfig.HMDPortalWindowCount];

        public void Initialize(FlightHud flightHud, Aircraft aircraft)
        {
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

                float opacity = Mathf.Clamp01(CnoEvilConfig.HMDPortalPagesOpacity.Value);
                for (int i = 0; i < _windows.Length; i++)
                {
                    CnoEvilConfig.HMDPortalSlot role = CnoEvilConfig.HMDPortalPageSlot[i].Value;
                    Texture texture;
                    Rect uv;
                    float turn, aspect;
                    if (role == CnoEvilConfig.HMDPortalSlot.None ||
                        !portal.TryGetSlotView(role, out texture, out uv, out turn, out aspect))
                    {
                        SetActive(_windows[i], false);
                        continue;
                    }
                    Window w = _windows[i];
                    if (w == null || w.Root == null)
                    {
                        w = CreateWindow(canvas, i);
                        _windows[i] = w;
                    }
                    if (w.Image.texture != texture) w.Image.texture = texture;
                    if (w.Image.uvRect != uv) w.Image.uvRect = uv;
                    w.Group.alpha = opacity;
                    Place(w, i, aspect, turn, canvas, mainCam);
                    SetActive(w, true);
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
                if (_windows[i] != null && _windows[i].Root != null) Destroy(_windows[i].Root);
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
            if (w != null && w.Root != null && w.Root.activeSelf != active) w.Root.SetActive(active);
        }

        private Window CreateWindow(Canvas canvas, int index)
        {
            Window w = new Window();
            w.Root = new GameObject("CnoEvil_HMDPortalPage" + (index + 1), typeof(RectTransform), typeof(CanvasGroup));
            w.Root.transform.SetParent(canvas.transform, false);
            // First child: HUD and HMD symbols draw on top of the window
            w.Root.transform.SetAsFirstSibling();
            w.Rect = (RectTransform)w.Root.transform;
            w.Rect.pivot = new Vector2(0.5f, 0.5f);
            w.Group = w.Root.GetComponent<CanvasGroup>();
            w.Group.interactable = false;
            w.Group.blocksRaycasts = false;

            GameObject imageObject = new GameObject("Image", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            imageObject.transform.SetParent(w.Root.transform, false);
            w.ImageRect = (RectTransform)imageObject.transform;
            w.ImageRect.anchorMin = new Vector2(0.5f, 0.5f);
            w.ImageRect.anchorMax = new Vector2(0.5f, 0.5f);
            w.ImageRect.pivot = new Vector2(0.5f, 0.5f);
            w.ImageRect.anchoredPosition = Vector2.zero;
            w.Image = imageObject.GetComponent<RawImage>();
            w.Image.raycastTarget = false;
            w.Root.SetActive(false);
            w.ScreenW = -1;
            return w;
        }

        /// <summary>
        /// Places the window on the screen and turns the image upright. Runs again only when the screen, the config
        /// or the slot shape changes.
        /// </summary>
        private static void Place(Window w, int index, float aspect, float turn, Canvas canvas, Camera mainCam)
        {
            float x = CnoEvilConfig.HMDPortalPageX[index].Value;
            float y = CnoEvilConfig.HMDPortalPageY[index].Value;
            float size = Mathf.Clamp(CnoEvilConfig.HMDPortalPageSize[index].Value, 0.05f, 1.0f);
            int screenW = Screen.width;
            int screenH = Screen.height;
            if (screenW == w.ScreenW && screenH == w.ScreenH && x == w.X && y == w.Y && size == w.Size &&
                Mathf.Approximately(aspect, w.Aspect) && Mathf.Approximately(turn, w.Turn))
            {
                return;
            }

            RectTransform parentRect = w.Rect.parent as RectTransform;
            if (parentRect == null) return;
            float h = size * screenH;
            float wPx = Mathf.Min(h * Mathf.Max(0.1f, aspect), screenW);
            h = wPx / Mathf.Max(0.1f, aspect);
            Vector2 center = new Vector2(screenW * x, screenH * y);
            Vector2 screenMin = center - new Vector2(wPx, h) * 0.5f;
            Vector2 screenMax = center + new Vector2(wPx, h) * 0.5f;
            Camera uiCam = null;
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) uiCam = canvas.worldCamera != null ? canvas.worldCamera : mainCam;
            Vector2 localMin, localMax;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, screenMin, uiCam, out localMin) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, screenMax, uiCam, out localMax))
            {
                return;
            }

            w.ScreenW = screenW;
            w.ScreenH = screenH;
            w.X = x;
            w.Y = y;
            w.Size = size;
            w.Aspect = aspect;
            w.Turn = turn;

            // Anchored at the parent pivot, which is the origin of the local points
            Vector2 box = localMax - localMin;
            w.Rect.anchorMin = parentRect.pivot;
            w.Rect.anchorMax = parentRect.pivot;
            w.Rect.anchoredPosition = (localMin + localMax) * 0.5f;
            w.Rect.sizeDelta = box;

            // The image shows the texture rect around the slot; turned by -turn it is upright. At a quarter turn its
            // sides change places.
            bool quarter = Mathf.Abs(Mathf.Sin(turn * Mathf.Deg2Rad)) > 0.7071f;
            w.ImageRect.sizeDelta = quarter ? new Vector2(box.y, box.x) : box;
            w.ImageRect.localRotation = Quaternion.Euler(0f, 0f, -turn);
        }
    }
}
