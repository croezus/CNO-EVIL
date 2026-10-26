using System;
using System.Globalization;
using UnityEngine;
using TMPro;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// Two more lines below the vanilla altitude readouts (Altitude HUD app):
    /// the best track that a hostile faction holds on the own aircraft ("ID NO / MEM / LOW / MED / HIGH / OPT"),
    /// and below it the frontal RCS of the own aircraft ("RCS 3.2", m²).
    /// The lines are clones of the vanilla absolute-altitude text, so font, color and theme are vanilla.
    /// Host / single player evaluate the faction tracks locally; a client shows the tier the host sends.
    /// The RCS is calculated locally on every machine.
    /// Refreshes in LateUpdate, after the vanilla app refresh of the frame, and follows the vanilla font size.
    /// </summary>
    public class DetectionStatusLine : MonoBehaviour
    {
        private TextMeshProUGUI _source;
        private Aircraft _aircraft;
        private float _syncedFontSize = -1.0f;
        private TextMeshProUGUI _text;
        private TextMeshProUGUI _rcsText;
        private DetectionTier _shownTier = (DetectionTier)255;
        private DetectionTier _localTier = DetectionTier.None;
        private Aircraft _localAircraft;
        private float _nextEvaluate;

        private Aircraft _rcsAircraft;
        private float _nextRcsEvaluate;
        private float _shownRcs = -1.0f;

        public void Initialize(TextMeshProUGUI radarAlt, TextMeshProUGUI absAlt)
        {
            if (_text != null || absAlt == null) return;
            _source = absAlt;

            // Lines below the radar altitude (which is below the absolute altitude), with the vanilla spacing of the two readouts
            RectTransform srcRect = absAlt.rectTransform;
            Vector2 step = Vector2.zero;
            float first = 1.0f;
            if (radarAlt != null)
            {
                step = radarAlt.rectTransform.anchoredPosition - srcRect.anchoredPosition;
                first = 2.0f;
            }
            if (step.sqrMagnitude < 1.0f)
            {
                step = new Vector2(0.0f, -absAlt.fontSize * 1.2f);
                first = 1.0f;
            }

            _text = CreateLine(absAlt, "CnoEvil_DetectionStatus", srcRect.anchoredPosition + first * step);
            _rcsText = CreateLine(absAlt, "CnoEvil_OwnRCS", srcRect.anchoredPosition + (first + 1.0f) * step);
        }

        private static TextMeshProUGUI CreateLine(TextMeshProUGUI source, string name, Vector2 position)
        {
            GameObject clone = Instantiate(source.gameObject, source.transform.parent);
            clone.name = name;
            TextMeshProUGUI text = clone.GetComponent<TextMeshProUGUI>();
            if (text == null)
            {
                Destroy(clone);
                return null;
            }
            text.raycastTarget = false;
            text.text = string.Empty;
            text.enabled = false;
            text.rectTransform.anchoredPosition = position;
            return text;
        }

        public void SetAircraft(Aircraft aircraft)
        {
            _aircraft = aircraft;
        }

        private void LateUpdate()
        {
            try
            {
                SyncFontSize();
                RefreshDetection(_aircraft);
                RefreshRcs(_aircraft);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("DetectionStatusLine.LateUpdate", ex);
            }
        }

        // The vanilla RefreshSettings sets the font size of the source text (HUD or HMD text size)
        private void SyncFontSize()
        {
            if (_source == null) return;
            float size = _source.fontSize;
            if (size == _syncedFontSize) return;
            _syncedFontSize = size;
            if (_text != null) _text.fontSize = size;
            if (_rcsText != null) _rcsText.fontSize = size;
        }

        private void RefreshDetection(Aircraft aircraft)
        {
            if (_text == null) return;

            bool show = DetectionStatus.IsEnabled && aircraft != null;
            DetectionTier tier = DetectionTier.None;
            if (show)
            {
                if (DetectionStatusNet.IsRemoteClient)
                {
                    // A host without the mod sends nothing: no line
                    show = DetectionStatusNet.HasServerData;
                    tier = DetectionStatusNet.ReceivedTier;
                }
                else
                {
                    float now = Time.unscaledTime;
                    if (aircraft != _localAircraft || now >= _nextEvaluate)
                    {
                        _localAircraft = aircraft;
                        _nextEvaluate = now + DetectionStatus.Interval;
                        _localTier = DetectionStatus.Evaluate(aircraft);
                    }
                    tier = _localTier;
                }
            }

            if (_text.enabled != show) _text.enabled = show;
            if (show && tier != _shownTier)
            {
                _shownTier = tier;
                _text.text = DetectionStatus.GetLabel(tier);
            }
        }

        private void RefreshRcs(Aircraft aircraft)
        {
            if (_rcsText == null) return;

            bool show = CnoEvilConfig.IsModActive &&
                        CnoEvilConfig.EnableHUDOwnRCS.Value &&
                        aircraft != null;
            PhysRadarTarget target = show ? PhysRadarTarget.Get(aircraft) : null;
            if (target == null) show = false;

            if (_rcsText.enabled != show) _rcsText.enabled = show;
            if (!show) return;

            float now = Time.unscaledTime;
            if (aircraft == _rcsAircraft && now < _nextRcsEvaluate) return;
            _rcsAircraft = aircraft;
            _nextRcsEvaluate = now + DetectionStatus.Interval;

            float rcs = RoundRcs(target.GetFrontalRCS());
            if (rcs == _shownRcs) return;
            _shownRcs = rcs;
            _rcsText.text = "RCS " + rcs.ToString(GetRcsFormat(rcs), CultureInfo.InvariantCulture);
        }

        // Two significant digits (one more below 0.1 m²), so the text changes only with the shown value
        private static float RoundRcs(float rcs)
        {
            string format = GetRcsFormat(rcs);
            float scale = format == "F0" ? 1.0f : format == "F1" ? 10.0f : format == "F2" ? 100.0f : 1000.0f;
            return Mathf.Round(rcs * scale) / scale;
        }

        private static string GetRcsFormat(float rcs)
        {
            if (rcs >= 10.0f) return "F0";
            if (rcs >= 1.0f) return "F1";
            if (rcs >= 0.1f) return "F2";
            return "F3";
        }

        private void OnDestroy()
        {
            if (_text != null) Destroy(_text.gameObject);
            if (_rcsText != null) Destroy(_rcsText.gameObject);
        }
    }
}
