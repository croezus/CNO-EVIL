using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    public class FlightHUDEWIndicator : MonoBehaviour
    {
        private FlightHud _flightHud;
        private Aircraft _aircraft;
        private PhysRWRReceiver _rwr;

        private GameObject _indicatorObject;
        private RectTransform _indicatorRect;
        private TextMeshProUGUI _indicatorText;
        private TMP_FontAsset _hudFont;

        private float _flashTimer = 0.0f;
        private bool _flashState = true;
        private bool _isInitialized = false;

        private static readonly Color ColorHudGreen = new Color(0.28f, 1.0f, 0.42f, 0.95f);
        private static readonly Color ColorBrightGreen = new Color(0.35f, 1.0f, 0.5f, 1.0f);
        private static readonly Color ColorAmberCaution = new Color(1.0f, 0.82f, 0.25f, 0.95f);
        private static readonly Color ColorRedAlert = new Color(1.0f, 0.32f, 0.22f, 0.98f);
        private static readonly Color ColorDimGreen = new Color(0.28f, 1.0f, 0.42f, 0.55f);
        private static readonly Color ColorDatalinkCyan = new Color(0.2f, 0.85f, 1.0f, 0.95f);

        public void Initialize(FlightHud flightHud, Aircraft aircraft)
        {
            _flightHud = flightHud;
            _aircraft = aircraft;

            if (_flightHud == null || _aircraft == null) return;

            _rwr = _aircraft.GetComponent<PhysRWRReceiver>();
            if (_rwr == null)
            {
                _rwr = _aircraft.gameObject.AddComponent<PhysRWRReceiver>();
            }

            CreateIndicatorUI();
            _isInitialized = true;
        }

        private void OnDestroy()
        {
            if (_indicatorObject != null) Destroy(_indicatorObject);
        }

        private static readonly Func<FlightHud, RawImage> f_compass = FastReflection.CreateFieldGetter<FlightHud, RawImage>("compass");
        private static readonly Func<FlightHud, Transform> f_hudCenter = FastReflection.CreateFieldGetter<FlightHud, Transform>("HUDCenter");

        private void CreateIndicatorUI()
        {
            if (_indicatorObject != null) return;

            RawImage compassImage = f_compass != null ? f_compass(_flightHud) : null;
            Transform hudCenter = f_hudCenter != null ? f_hudCenter(_flightHud) : null;

            // Find parent: ideally compass parent or canvas
            Transform parentTransform = null;
            if (compassImage != null)
            {
                parentTransform = compassImage.transform.parent != null
                    ? compassImage.transform.parent
                    : compassImage.transform;
            }
            else if (hudCenter != null)
            {
                parentTransform = hudCenter;
            }
            else
            {
                parentTransform = _flightHud.transform;
            }

            _indicatorObject = new GameObject("CnoEvil_FlightHudEWIndicator");
            _indicatorObject.transform.SetParent(parentTransform, false);

            _indicatorRect = _indicatorObject.AddComponent<RectTransform>();
            _indicatorRect.anchorMin = new Vector2(0.5f, 1.0f);
            _indicatorRect.anchorMax = new Vector2(0.5f, 1.0f);
            _indicatorRect.pivot = new Vector2(0.5f, 0.5f);
            _indicatorRect.sizeDelta = new Vector2(260f, 32f);

            // Position right above the top heading compass tape so it never overlaps the 0-360 azimuth numbers
            if (compassImage != null)
            {
                RectTransform compassRect = compassImage.rectTransform;
                _indicatorRect.anchorMin = compassRect.anchorMin;
                _indicatorRect.anchorMax = compassRect.anchorMax;
                _indicatorRect.pivot = new Vector2(0.5f, 0.5f);
                Vector2 compPos = compassRect.anchoredPosition;
                // Place 28px above compass center (above the numbers tape)
                _indicatorRect.anchoredPosition = new Vector2(compPos.x, compPos.y + 28f);
            }
            else
            {
                _indicatorRect.anchoredPosition = new Vector2(0f, -25f);
            }

            // Find native HUD font
            _hudFont = HudFont.Get();

            _indicatorText = _indicatorObject.AddComponent<TextMeshProUGUI>();
            if (_hudFont != null)
            {
                _indicatorText.font = _hudFont;
            }
            _indicatorText.fontSize = 18f;
            _indicatorText.alignment = TextAlignmentOptions.Center;
            _indicatorText.color = ColorHudGreen;
            _indicatorText.raycastTarget = false;
            _indicatorText.text = "";
        }

        // "[ DL: MISSILE 3H ]" strings per clock position (0 = no clock) and flash phase, built once
        private static readonly string[] s_dlMissileText = BuildDatalinkMissileTexts();

        private static string[] BuildDatalinkMissileTexts()
        {
            string[] texts = new string[26];
            for (int clock = 0; clock <= 12; clock++)
            {
                string clockStr = clock > 0 ? string.Format("{0}H", clock) : "INBOUND";
                texts[clock * 2] = string.Format("[ DL: MISSILE {0} ]", clockStr);
                texts[clock * 2 + 1] = string.Format("[ > DL: MISSILE {0} < ]", clockStr);
            }
            return texts;
        }

        private static string GetDatalinkMissileText(int clock, bool flashOn)
        {
            clock = Mathf.Clamp(clock, 0, 12);
            return s_dlMissileText[clock * 2 + (flashOn ? 0 : 1)];
        }

        private void SetIndicator(string text, Color color)
        {
            if (!ReferenceEquals(_indicatorText.text, text)) _indicatorText.text = text;
            _indicatorText.color = color;
        }

        /// <summary>
        /// Physical burn-through range of the threat radar against our own jammer and our RCS toward it
        /// (same model as the radar / seeker detection code).
        /// </summary>
        private float EstimateBurnThroughRange(RwrThreatEntry threat, float ecmIntensity, Vector3 knownEmitterPos)
        {
            Unit emitter = threat.EmitterUnit;

            RadarSpecs specs;
            if (threat.IsActiveSeeker)
            {
                specs = RadarSpecs.CreateSeekerRadar();
            }
            else
            {
                Radar radar = emitter.radar as Radar;
                PhysRadarEmitter physEmitter = (radar != null) ? PhysRadarEmitter.Get(radar) : null;
                if (physEmitter != null && physEmitter.ThermalNoiseFloor > 0.0f)
                {
                    specs = physEmitter.Specs;
                }
                else
                {
                    RadarTemplate template;
                    float referenceRange;
                    specs = RadarSpecs.GetRadarSpecsForPlatform(emitter, radar, out template, out referenceRange);
                }
            }

            PhysRadarTarget ownTarget = PhysRadarTarget.Get(_aircraft);
            float ownRcs = (ownTarget != null) ? ownTarget.GetDynamicRCS(knownEmitterPos) : _aircraft.RCS;

            JammerSpecs jammer = JammerSpecs.CreateForIntensity(ecmIntensity);
            return ElectronicWarfare.CalculateBurnThroughRange(ownRcs, CnoEvilConfig.EffectiveMinSNR, ref jammer, ref specs, ElectronicWarfare.BurnThroughRatio);
        }

        /// <summary>
        /// Position of an emitter from the own faction's knowledge: the faction estimate of its track
        /// (contact, fresh fix, or last known position while in memory), or for a missile the datalink relay.
        /// Read-only: GetTrack would record a radar contact as a side effect.
        /// </summary>
        private bool TryGetKnownEmitterPosition(Unit emitter, out Vector3 knownPos)
        {
            knownPos = Vector3.zero;
            if (emitter == null) return false;

            FactionHQ hq = _aircraft.NetworkHQ;
            Dictionary<Unit, TriangulationTrack> tracks = (hq != null) ? RWRTriangulationProcessor.GetTrackDictionary(hq) : null;
            TriangulationTrack track;
            if (tracks != null && tracks.TryGetValue(emitter, out track) && track != null)
            {
                GlobalPosition estimate;
                float errorMeters;
                if (RWRTriangulationProcessor.TryGetFactionEstimate(track, emitter, out estimate, out errorMeters))
                {
                    knownPos = estimate.ToLocalPosition();
                    return true;
                }
            }

            // Missiles have no faction track; an allied sensor can relay them over the datalink
            DatalinkThreatEntry relay;
            if (emitter is Missile && _rwr != null && _rwr.TryGetDatalinkThreat(emitter, out relay))
            {
                knownPos = relay.EstimatedPosition;
                return true;
            }
            return false;
        }

        private const float EvaluateIntervalSeconds = 0.1f;
        private float _nextEvaluateTime;

        private void Update()
        {
            if (!CnoEvilConfig.IsModActive || !CnoEvilConfig.EnableHUDEWCues.Value)
            {
                if (_indicatorText != null && !string.IsNullOrEmpty(_indicatorText.text))
                {
                    _indicatorText.text = "";
                }
                return;
            }

            if (!_isInitialized || _aircraft == null || _aircraft.disabled || _indicatorText == null)
            {
                return;
            }

            _flashTimer += Time.deltaTime;
            if (_flashTimer >= 0.32f)
            {
                _flashTimer = 0.0f;
                _flashState = !_flashState;
            }

            // The cue is evaluated at EvaluateIntervalSeconds (the flash period is 0.32 s)
            float now = Time.unscaledTime;
            if (now < _nextEvaluateTime) return;
            _nextEvaluateTime = now + EvaluateIntervalSeconds;

            if (_rwr == null)
            {
                _rwr = PhysRWRReceiver.Get(_aircraft);
            }

            float ecmIntensity = _aircraft.GetECMIntensity();
            bool ecmActive = ecmIntensity > 0.05f;

            // Search for highest priority threat (MissileGuidance > Track > Search)
            RwrThreatEntry priorityThreat = null;
            if (_rwr != null && _rwr.ActiveThreats != null)
            {
                // The receiver already drops threats older than RWRStrobePersistence_s
                foreach (RwrThreatEntry threat in _rwr.ActiveThreats)
                {
                    if (threat == null || threat.EmitterUnit == null || threat.EmitterUnit.disabled) continue;

                    if (threat.State == RwrThreatState.MissileGuidance)
                    {
                        priorityThreat = threat;
                        break; // Top priority
                    }
                    else if (threat.State == RwrThreatState.Track)
                    {
                        if (priorityThreat == null || priorityThreat.State == RwrThreatState.Search)
                        {
                            priorityThreat = threat;
                        }
                    }
                    else if (threat.State == RwrThreatState.Search)
                    {
                        if (priorityThreat == null)
                        {
                            priorityThreat = threat;
                        }
                    }
                }
            }

            if (priorityThreat != null && priorityThreat.EmitterUnit != null)
            {
                Vector3 ownPos = _aircraft.transform.position;
                Vector3 ownVel = _aircraft.rb != null ? _aircraft.rb.velocity : Vector3.zero;
                // The RWR measures bearing only. Range comes from the faction estimate or the datalink relay;
                // without it the range is unknown and the range-dependent cues stay off.
                Vector3 knownEmitterPos;
                bool rangeKnown = TryGetKnownEmitterPosition(priorityThreat.EmitterUnit, out knownEmitterPos);
                float dist = rangeKnown ? Vector3.Distance(knownEmitterPos, ownPos) : 0.0f;

                DopplerResult doppler = default(DopplerResult);
                if (rangeKnown)
                {
                    // The notch computer places the emitter along the measured RWR bearing at the known range.
                    // The notch depends on ownship velocity against that line of sight, so its accuracy is the receiver's.
                    Vector3 threatPos = ownPos + priorityThreat.LastDirection * dist;

                    float ownAlt = _aircraft.radarAlt;
                    float notchThreshold = CnoEvilConfig.EffectiveNotchThreshold;
                    // Own rotor sidebands (HERM) stay outside the notch: the cue must not report a notch that radars do not see
                    PhysRadarTarget ownTarget = PhysRadarTarget.Get(_aircraft);
                    bool ownHERM = ownTarget != null && ownTarget.IsHelicopterWithHERM();
                    doppler = DopplerClutterProcessor.ProcessDopplerAndClutter(
                        threatPos,
                        Vector3.zero,
                        ownPos,
                        ownVel,
                        notchThreshold,
                        ownHERM,
                        ecmIntensity,
                        ownAlt
                    );
                    if (ownHERM) doppler.IsInsideClutterNotch = false;
                }

                bool isBurnThrough = ecmActive && rangeKnown && dist <= EstimateBurnThroughRange(priorityThreat, ecmIntensity, knownEmitterPos);

                if (isBurnThrough && priorityThreat.State == RwrThreatState.MissileGuidance)
                {
                    // Burn-through reached! Missile skin return pierces ECM noise!
                    _indicatorText.text = _flashState ? "[ BURN-THROUGH ]" : "[ > BURN-THROUGH < ]";
                    _indicatorText.color = ColorRedAlert;
                }
                else if (ecmActive && doppler.IsHOJVulnerable && priorityThreat.State == RwrThreatState.MissileGuidance)
                {
                    // Flying straight with ECM active during missile guidance: HOJ risk!
                    _indicatorText.text = _flashState ? "[ HOJ RISK ]" : "[ > HOJ RISK < ]";
                    _indicatorText.color = ColorRedAlert;
                }
                else if (priorityThreat.State == RwrThreatState.MissileGuidance)
                {
                    // Active Missile Terminal Alert
                    if (priorityThreat.IsActiveSeeker)
                    {
                        _indicatorText.text = _flashState ? "[ >> PITBULL << ]" : "[ PITBULL ]";
                    }
                    else
                    {
                        _indicatorText.text = _flashState ? "[ >> MISSILE << ]" : "[ MISSILE ]";
                    }
                    _indicatorText.color = ColorRedAlert;
                }
                else if (doppler.IsInsideClutterNotch)
                {
                    // Evaluate notch indicator based on the aircraft's EW computer capabilities
                    RwrCapabilities caps = _rwr != null ? _rwr.Capabilities : RWRTierDatabase.GetTier2Capabilities();

                    if (!caps.HasHUDNotchGuidance && !caps.CanClassifyThreats)
                    {
                        // Basic analog RWR (Cricket/Chicane): NO cheat notch indicator
                        _indicatorText.text = ecmActive ? "[ ECM ON ]" : "";
                        _indicatorText.color = ColorDimGreen;
                    }
                    else if (!caps.HasHUDNotchGuidance)
                    {
                        // Digital RWR without notch computer (Revoker/Compass): approximate beaming advisory
                        _indicatorText.text = "[ BEAMING ]";
                        _indicatorText.color = ColorAmberCaution;
                    }
                    else
                    {
                        // 5th-gen clutter notch computer (Vortex/Ifrit/Darkreach)
                        if (ecmActive)
                        {
                            _indicatorText.text = "[ ECM NOTCH ]";
                            _indicatorText.color = ColorBrightGreen;
                        }
                        else if (doppler.TrackRejectedByNotch)
                        {
                            _indicatorText.text = "[ NOTCH OK ]";
                            _indicatorText.color = ColorHudGreen;
                        }
                        else
                        {
                            _indicatorText.text = "[ NO CLUTTER ]";
                            _indicatorText.color = ColorAmberCaution;
                        }
                    }
                }
                else if (priorityThreat.State == RwrThreatState.Track)
                {
                    _indicatorText.text = "[ LOCK ]";
                    _indicatorText.color = ColorAmberCaution;
                }
                else if (ecmActive)
                {
                    _indicatorText.text = "[ ECM ON ]";
                    _indicatorText.color = ColorDimGreen;
                }
                else
                {
                    _indicatorText.text = "";
                }
            }
            else
            {
                // No onboard direct RWR lock. Check Allied Tactical Datalink!
                DatalinkThreatEntry dlThreat = _rwr != null ? _rwr.GetHighestDatalinkThreat() : null;
                if (dlThreat != null && dlThreat.IsMissile)
                {
                    // Allied radar warned us about an incoming missile!
                    SetIndicator(GetDatalinkMissileText(dlThreat.ClockPosition, _flashState), ColorDatalinkCyan);
                }
                else if (ecmActive)
                {
                    _indicatorText.text = "[ ECM ON ]";
                    _indicatorText.color = ColorDimGreen;
                }
                else
                {
                    _indicatorText.text = "";
                }
            }
        }
    }
}
