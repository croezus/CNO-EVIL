using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    /// <summary>
    /// SNR bar one line below the vanilla max-G readout (GIndicators HUD app): the signal of the strongest hostile
    /// radar on the own aircraft, -20..+20 dB from its detection threshold (center tick). The pip runs along the line;
    /// at or above the threshold (detected) the pip, the tick and the line from tick to pip are amber, red when the
    /// radar guides a missile. Source: the own RWR receiver (detected pings and below-threshold paints, the same data
    /// as the EM scan ring), so a multiplayer client shows the mirrored receiver feed.
    /// </summary>
    public class HudSnrIndicator : MonoBehaviour
    {
        private const float MarginRangeDb = 20.0f;
        private const float PingMaxAgeSeconds = 1.0f;
        private const float EvaluateIntervalSeconds = 0.1f;
        private const float PipTimeConstant = 0.15f;
        private const float FadeSeconds = 0.5f;
        private const float LineThickness = 1.5f;

        private static readonly Color ColorAmber = new Color(1.0f, 0.82f, 0.25f, 0.95f);
        private static readonly Color ColorRed = new Color(1.0f, 0.32f, 0.22f, 0.98f);

        private GIndicators _app;
        private TextMeshProUGUI _source;
        private RectTransform _root;
        private Image _line;
        private Image _segment;
        private Image _tick;
        private Image _pip;
        private bool _shown;
        private TMP_Text[] _columnTexts;
        private bool _layoutDirty;

        private float _width;
        private float _targetMargin = -MarginRangeDb;
        private float _shownMargin = -MarginRangeDb;
        private bool _hasSignal;
        private bool _guidance;
        private float _pipAlpha;
        private float _nextEvaluate;

        public void Initialize(GIndicators app, TextMeshProUGUI gMaxLabel)
        {
            if (_root != null || gMaxLabel == null) return;
            _app = app;
            _source = gMaxLabel;

            GameObject rootObject = new GameObject("CnoEvil_SnrIndicator", typeof(RectTransform));
            _root = (RectTransform)rootObject.transform;
            _root.SetParent(gMaxLabel.transform.parent, false);

            _line = CreateImage("Line");
            _segment = CreateImage("Segment");
            _tick = CreateImage("Tick");
            _pip = CreateImage("Pip");

            // The G text column: the static "G" label and the two readouts
            _columnTexts = app != null ? app.GetComponentsInChildren<TMP_Text>(true) : new TMP_Text[0];

            _layoutDirty = true;
            SetShown(false);
        }

        private Image CreateImage(string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(_root, false);
            // Positions are measured from the left end of the line, on its center line
            rect.anchorMin = new Vector2(0.0f, 0.5f);
            rect.anchorMax = new Vector2(0.0f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            Image image = go.AddComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// The vanilla font size changed: place the bar again at the next refresh.
        /// </summary>
        public void SyncSize()
        {
            _layoutDirty = true;
        }

        /// <summary>
        /// Size and place from the drawn text (the label rects are wider than their glyphs): left edge on the
        /// G text column, right end a little past the max-G readout, center below the max-G glyphs.
        /// False while the max-G text has no glyphs yet.
        /// </summary>
        private bool Layout()
        {
            if (_root == null || _source == null) return false;
            Transform parent = _root.parent;
            float minX, maxX, minY, unusedMaxY;
            if (parent == null || !TryGetTextBounds(_source, parent, out minX, out maxX, out minY, out unusedMaxY)) return false;

            for (int i = 0; i < _columnTexts.Length; i++)
            {
                TMP_Text text = _columnTexts[i];
                float tMinX, tMaxX, tMinY, tMaxY;
                if (text == null || text == _source || !text.enabled) continue;
                if (!TryGetTextBounds(text, parent, out tMinX, out tMaxX, out tMinY, out tMaxY)) continue;
                minX = Mathf.Min(minX, tMinX);
                maxX = Mathf.Max(maxX, tMaxX);
            }

            float fontSize = Mathf.Max(8.0f, _source.fontSize);
            _width = maxX - minX + 0.8f * fontSize;

            _root.anchorMin = new Vector2(0.5f, 0.5f);
            _root.anchorMax = new Vector2(0.5f, 0.5f);
            _root.pivot = new Vector2(0.0f, 0.5f);
            _root.sizeDelta = new Vector2(_width, fontSize);
            _root.localPosition = new Vector3(minX, minY - 0.9f * fontSize, 0.0f);

            _line.rectTransform.sizeDelta = new Vector2(_width, LineThickness);
            _line.rectTransform.anchoredPosition = new Vector2(0.5f * _width, 0.0f);
            _tick.rectTransform.sizeDelta = new Vector2(2.0f, 0.45f * fontSize);
            _tick.rectTransform.anchoredPosition = new Vector2(0.5f * _width, 0.0f);
            _pip.rectTransform.sizeDelta = new Vector2(2.5f, 0.6f * fontSize);
            _segment.rectTransform.sizeDelta = new Vector2(0.0f, LineThickness);
            return true;
        }

        // Glyph bounds of a text in the space of the given transform
        private static bool TryGetTextBounds(TMP_Text text, Transform space, out float minX, out float maxX, out float minY, out float maxY)
        {
            minX = maxX = minY = maxY = 0.0f;
            if (string.IsNullOrEmpty(text.text)) return false;
            Bounds b = text.textBounds;
            if (b.size.x <= 0.0f) return false;
            Vector3 lo = space.InverseTransformPoint(text.transform.TransformPoint(b.min));
            Vector3 hi = space.InverseTransformPoint(text.transform.TransformPoint(b.max));
            minX = Mathf.Min(lo.x, hi.x);
            maxX = Mathf.Max(lo.x, hi.x);
            minY = Mathf.Min(lo.y, hi.y);
            maxY = Mathf.Max(lo.y, hi.y);
            return true;
        }

        public void Refresh(Aircraft aircraft)
        {
            if (_root == null) return;

            bool show = CnoEvilConfig.IsModActive &&
                        CnoEvilConfig.EnableHUDSnrIndicator != null && CnoEvilConfig.EnableHUDSnrIndicator.Value &&
                        aircraft != null && !aircraft.disabled;
            PhysRWRReceiver rwr = show ? PhysRWRReceiver.Get(aircraft) : null;
            if (rwr == null) show = false;
            if (show && _layoutDirty)
            {
                // Hidden until the max-G text has glyphs to place the bar from
                _layoutDirty = !Layout();
                if (_layoutDirty) show = false;
            }

            SetShown(show);
            if (!show) return;

            float now = Time.unscaledTime;
            if (now >= _nextEvaluate)
            {
                _nextEvaluate = now + EvaluateIntervalSeconds;
                float margin;
                bool guidance;
                _hasSignal = rwr.TryGetPeakRadarMargin(PingMaxAgeSeconds, out margin, out guidance);
                _targetMargin = _hasSignal ? Mathf.Clamp(margin, -MarginRangeDb, MarginRangeDb) : -MarginRangeDb;
                _guidance = _hasSignal && guidance;
            }

            // The pip runs to the measured margin; without a signal it runs to the left end and fades out
            float dt = Time.unscaledDeltaTime;
            _shownMargin += (_targetMargin - _shownMargin) * (1.0f - Mathf.Exp(-dt / PipTimeConstant));
            _pipAlpha = Mathf.MoveTowards(_pipAlpha, _hasSignal ? 1.0f : 0.0f, dt / FadeSeconds);

            float half = 0.5f * _width;
            float pipX = half + _shownMargin / MarginRangeDb * half;
            _pip.rectTransform.anchoredPosition = new Vector2(pipX, 0.0f);

            Color hud = _source.color;
            bool detected = _hasSignal && _shownMargin >= 0.0f;
            Color alert = _guidance ? ColorRed : ColorAmber;

            _line.color = new Color(hud.r, hud.g, hud.b, hud.a * 0.5f);
            _tick.color = detected ? alert : hud;
            Color pipColor = detected ? alert : hud;
            pipColor.a *= _pipAlpha;
            _pip.color = pipColor;

            bool segment = detected && pipX > half + 0.5f;
            if (_segment.enabled != segment) _segment.enabled = segment;
            if (segment)
            {
                _segment.rectTransform.sizeDelta = new Vector2(pipX - half, LineThickness);
                _segment.rectTransform.anchoredPosition = new Vector2(0.5f * (half + pipX), 0.0f);
                _segment.color = alert;
            }
        }

        private void SetShown(bool show)
        {
            if (show == _shown && _line != null && _line.enabled == show) return;
            _shown = show;
            _line.enabled = show;
            _tick.enabled = show;
            _pip.enabled = show;
            if (!show)
            {
                _segment.enabled = false;
                // Start from the left end the next time
                _shownMargin = -MarginRangeDb;
                _pipAlpha = 0.0f;
                _hasSignal = false;
            }
        }

        private void OnDestroy()
        {
            CnoEvil.Patches.GIndicatorsHUDPatches.Unregister(_app);
            if (_root != null) Destroy(_root.gameObject);
        }
    }
}
