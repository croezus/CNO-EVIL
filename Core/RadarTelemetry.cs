using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// What a radar or seeker does to the player aircraft (debug overlays only). The order is the display priority.
    /// </summary>
    public enum RadarIlluminationState
    {
        Search,           // Search scan, the echo is near the detection threshold
        SearchStrong,     // Search scan with at least 3 dB SNR margin (not a track: the RWR shows Search)
        HardLock,         // Single Target Track (STT) lock
        MissileGuidance   // CW / SARH illuminator or active ARH seeker
    }

    /// <summary>
    /// Whose receiver measured the SNR of an illumination row.
    /// </summary>
    public enum IlluminationSignalKind
    {
        RadarMonostatic,  // Echo of the player in the receiver of the radar itself
        SarhBistatic,     // Echo of the player in the passive receiver of a SARH missile seeker
        ArhSeeker         // Echo of the player in the active seeker of an ARH missile
    }

    public class ActiveRadarIlluminator
    {
        public int EmitterId;
        public string SourceName;
        public string PlatformType;
        public GlobalPosition EmitterGlobalPosition; // Global: the row lives up to 3.5 s, across floating-origin shifts
        public Vector3 EmitterForward;
        public float DistanceMeters;
        public float SNRdB;
        public float ReceivedPowerWatts;
        public RadarIlluminationState State;
        public IlluminationSignalKind SignalKind;
        public string Note;               // HOJ / CHAFF / DECOY, or null
        public float FirstSeenTime;
        public float LastPingTime;

        public Vector3 EmitterPosition
        {
            get { return EmitterGlobalPosition.ToLocalPosition(); }
        }

        /// <summary>
        /// Short label of whose SNR the row shows.
        /// </summary>
        public string SignalLabel
        {
            get
            {
                switch (SignalKind)
                {
                    case IlluminationSignalKind.SarhBistatic: return "seeker bistatic SNR";
                    case IlluminationSignalKind.ArhSeeker: return "seeker SNR";
                    default: return "radar echo SNR";
                }
            }
        }

        public Color GetColor()
        {
            switch (State)
            {
                case RadarIlluminationState.MissileGuidance:
                    bool flash = (Time.time * 8.0f) % 1.0f > 0.35f;
                    return flash ? new Color(1.0f, 0.0f, 0.35f, 1.0f) : new Color(1.0f, 0.2f, 0.9f, 1.0f);
                case RadarIlluminationState.HardLock:
                    return new Color(1.0f, 0.25f, 0.0f, 1.0f); // Vivid Orange-Red
                case RadarIlluminationState.SearchStrong:
                    return new Color(1.0f, 0.88f, 0.0f, 0.95f); // Vivid Yellow
                case RadarIlluminationState.Search:
                default:
                    return new Color(0.0f, 0.95f, 1.0f, 0.85f); // Cyan
            }
        }
    }

    /// <summary>
    /// Doppler filter result of the radar against the target, from the flags of DopplerClutterProcessor.
    /// </summary>
    public enum TelemetryNotchState
    {
        Clear,            // Outside the notch, no clutter in the target Doppler filter
        InNotchOpenSky,   // Inside the notch, no clutter background: the target is kept
        ClutterDegraded,  // Clutter power in the target Doppler filter (skirt, sidelobe band or sea)
        RotorFlash,       // HERM helicopter inside the notch: only the rotor blade flash passes
        Notched,          // TrackRejectedByNotch: the return is dropped
        HorizonOccluded   // 4/3 Earth horizon masks the path
    }

    /// <summary>
    /// Result of the scan: is the target in the detected list of the radar, and if not, why.
    /// </summary>
    public enum TelemetryDetection
    {
        Detected,
        BelowThreshold,
        Notched,
        HorizonMasked,
        DecoyCaptured
    }

    public struct RadarTelemetrySnapshot
    {
        public bool HasTarget;
        public string RadarName;
        public bool RadarIsOwn;            // The radar of the player aircraft (else a radar that scans the player)
        public string TargetName;
        public float SlantRangeMeters;
        public float EchoPowerWatts;       // After processing gain
        public float NoiseFloorWatts;
        public float ClutterPowerWatts;
        public float JammerPowerWatts;     // Self-screen + stand-off, after JammerBurnThroughRatio
        public float SelfJamPowerWatts;    // Self-screen part at the receiver
        public float StandoffJamPowerWatts;
        public float SNRdB;                // SINR: Pr / (Pn + Pc + Pj)
        public float RequiredSNRdB;
        public float DynamicRCS;           // Aspect RCS including stores and rotor flash
        public float FilteredRCS;          // RCS after the Doppler filter (the echo uses it)
        public float StoresPenaltyRCS;     // Part of DynamicRCS (0 when ExternalStoresRCSEnabled is off)
        public float RelativeRadialSpeed;
        public float GroundRadialSpeed;
        public float EffectiveNotch;       // Notch half-width with ECM widening
        public float ElevationDeg;
        public TelemetryNotchState NotchState;
        public TelemetryDetection Detection;
        public bool IsLookingDown;
        public bool HasClutterBackground;
        public float BurnThroughRangeMeters; // Self-screen jammer only
        public bool SelfJammerActive;
        public float EcmIntensity;           // Vanilla ECM intensity of the target
        public float SelfJamTransmitWatts;   // Jammer transmit power = intensity x EcmPowerPerIntensity_W
        public float LastUpdateTime;

        // Gizmo / Debug Raycast Vectors (local positions of this update)
        public Vector3 RadarPos;
        public Vector3 TargetPos;
        public Vector3 TargetVel;
        public Color RayColor;
    }

    public static class RadarTelemetry
    {
        public static RadarTelemetrySnapshot Latest = new RadarTelemetrySnapshot();
        public static int CurrentPriority = 0;
        public static float LastPriorityTime = 0.0f;

        public static readonly Dictionary<int, ActiveRadarIlluminator> ActiveIlluminators = new Dictionary<int, ActiveRadarIlluminator>();
        private static readonly List<ActiveRadarIlluminator> CachedIlluminatorList = new List<ActiveRadarIlluminator>();
        private static readonly List<int> _staleKeys = new List<int>();
        private static int _lastQueryFrame = -1;
        private static float _lastQueryMaxAge = -1.0f;

        private class IlluminatorPriorityComparer : IComparer<ActiveRadarIlluminator>
        {
            public int Compare(ActiveRadarIlluminator a, ActiveRadarIlluminator b)
            {
                if (ReferenceEquals(a, b)) return 0;
                if (a == null) return 1;
                if (b == null) return -1;
                int c = b.State.CompareTo(a.State);
                if (c != 0) return c;
                return a.DistanceMeters.CompareTo(b.DistanceMeters);
            }
        }
        private static readonly IlluminatorPriorityComparer _priorityComparer = new IlluminatorPriorityComparer();

        private static float _lastSelfPruneTime = 0.0f;

        public static void RegisterIllumination(
            int emitterId,
            string sourceName,
            string platformType,
            Vector3 emitterPos,
            Vector3 emitterFwd,
            float dist,
            float snrDb,
            float prWatts,
            RadarIlluminationState state,
            IlluminationSignalKind signalKind = IlluminationSignalKind.RadarMonostatic,
            string note = null)
        {
            float now = Time.time;

            if (now - _lastSelfPruneTime > 5.0f)
            {
                _lastSelfPruneTime = now;
                _staleKeys.Clear();
                foreach (var kvp in ActiveIlluminators)
                {
                    if (now - kvp.Value.LastPingTime > 5.0f)
                    {
                        _staleKeys.Add(kvp.Key);
                    }
                }
                for (int i = 0; i < _staleKeys.Count; i++)
                {
                    ActiveIlluminators.Remove(_staleKeys[i]);
                }
            }

            ActiveRadarIlluminator item;
            if (!ActiveIlluminators.TryGetValue(emitterId, out item))
            {
                item = new ActiveRadarIlluminator();
                item.EmitterId = emitterId;
                item.FirstSeenTime = now;
                ActiveIlluminators[emitterId] = item;
            }

            item.SourceName = !string.IsNullOrEmpty(sourceName) ? sourceName : "Radar Emitter";
            item.PlatformType = platformType;
            item.EmitterGlobalPosition = GlobalPositionExtensions.ToGlobalPosition(emitterPos);
            item.EmitterForward = emitterFwd;
            item.DistanceMeters = dist;
            item.SNRdB = snrDb;
            item.ReceivedPowerWatts = prWatts;
            item.State = state;
            item.SignalKind = signalKind;
            item.Note = note;
            item.LastPingTime = now;
        }

        public static List<ActiveRadarIlluminator> GetActiveIlluminators(float maxAgeSeconds = 3.5f)
        {
            // If already evaluated within this frame for the same age window, return the cached list
            if (Time.frameCount == _lastQueryFrame && maxAgeSeconds == _lastQueryMaxAge)
            {
                return CachedIlluminatorList;
            }
            _lastQueryFrame = Time.frameCount;
            _lastQueryMaxAge = maxAgeSeconds;

            float now = Time.time;
            CachedIlluminatorList.Clear();
            _staleKeys.Clear();

            foreach (var kvp in ActiveIlluminators)
            {
                if (now - kvp.Value.LastPingTime > maxAgeSeconds)
                {
                    _staleKeys.Add(kvp.Key);
                }
                else
                {
                    CachedIlluminatorList.Add(kvp.Value);
                }
            }

            for (int i = 0; i < _staleKeys.Count; i++)
            {
                ActiveIlluminators.Remove(_staleKeys[i]);
            }

            CachedIlluminatorList.Sort(_priorityComparer);

            return CachedIlluminatorList;
        }

        /// <summary>
        /// One scan of a radar against a target, with every term of the SINR (RadarSignalModel.EvaluateRadar) and the
        /// scan result. A higher priority holds the snapshot for 0.5 s.
        /// </summary>
        public static void UpdateTelemetry(
            string radarName,
            bool radarIsOwn,
            Vector3 radarPos,
            Vector3 targetPos,
            Vector3 targetVel,
            string targetName,
            float dist,
            ref RadarSkinSignal sig,
            bool detected,
            bool decoyCaptured,
            bool horizonOccluded,
            int priority = 0)
        {
            float now = Time.time;
            if (now - LastPriorityTime > 0.5f)
            {
                CurrentPriority = 0;
            }

            if (priority < CurrentPriority)
            {
                return;
            }

            CurrentPriority = priority;
            LastPriorityTime = now;

            float minSnr = CnoEvilConfig.EffectiveMinSNR;
            bool storesOn = CnoEvilConfig.ExternalStoresRCSEnabled.Value;

            Latest.HasTarget = true;
            Latest.RadarName = !string.IsNullOrEmpty(radarName) ? radarName : "Radar";
            Latest.RadarIsOwn = radarIsOwn;
            Latest.TargetName = !string.IsNullOrEmpty(targetName) ? targetName : "Unknown Target";
            Latest.SlantRangeMeters = dist;
            Latest.EchoPowerWatts = sig.EchoPower;
            Latest.NoiseFloorWatts = sig.NoiseFloor;
            Latest.ClutterPowerWatts = sig.ClutterPower;
            Latest.JammerPowerWatts = sig.JammerPower;
            Latest.StandoffJamPowerWatts = sig.StandoffJamPower;
            Latest.SelfJamPowerWatts = Mathf.Max(0.0f, sig.JammerPower - sig.StandoffJamPower);
            Latest.SNRdB = sig.SnrDb;
            Latest.RequiredSNRdB = minSnr;
            Latest.DynamicRCS = sig.DynamicRcs;
            Latest.FilteredRCS = sig.FilteredRcs;
            Latest.StoresPenaltyRCS = storesOn ? sig.StoresRcs : 0.0f;
            Latest.RelativeRadialSpeed = sig.Doppler.RelativeRadialVelocity;
            Latest.GroundRadialSpeed = sig.Doppler.TargetGroundRadialSpeed;
            Latest.EffectiveNotch = sig.Doppler.EffectiveNotch;
            Latest.ElevationDeg = sig.Doppler.ElevationAngleDeg;
            Latest.IsLookingDown = sig.Doppler.IsLookingDown;
            Latest.HasClutterBackground = sig.Doppler.HasClutterBackground;
            Latest.EcmIntensity = sig.EcmIntensity;
            Latest.SelfJammerActive = sig.EcmIntensity > 0.01f;
            Latest.SelfJamTransmitWatts = Latest.SelfJammerActive ? Mathf.Max(0.0f, sig.EcmIntensity) * JammerSpecs.WattsPerEcmIntensity : 0.0f;
            Latest.BurnThroughRangeMeters = Latest.SelfJammerActive ? sig.BurnRange : 0.0f;
            Latest.LastUpdateTime = now;

            Latest.RadarPos = radarPos;
            Latest.TargetPos = targetPos;
            Latest.TargetVel = targetVel;

            // Doppler filter state (the same flags the physics uses)
            if (horizonOccluded)
            {
                Latest.NotchState = TelemetryNotchState.HorizonOccluded;
            }
            else if (sig.Doppler.TrackRejectedByNotch)
            {
                Latest.NotchState = TelemetryNotchState.Notched;
            }
            else if (sig.Doppler.RotorModulation && sig.Doppler.IsInsideClutterNotch && sig.Doppler.HasClutterBackground)
            {
                Latest.NotchState = TelemetryNotchState.RotorFlash;
            }
            else if (sig.ClutterPower > 0.0f)
            {
                Latest.NotchState = TelemetryNotchState.ClutterDegraded;
            }
            else if (sig.Doppler.IsInsideClutterNotch)
            {
                Latest.NotchState = TelemetryNotchState.InNotchOpenSky;
            }
            else
            {
                Latest.NotchState = TelemetryNotchState.Clear;
            }

            // Scan result (same order as Radar_CanSeeRadarReturn_Patch)
            if (detected) Latest.Detection = TelemetryDetection.Detected;
            else if (horizonOccluded) Latest.Detection = TelemetryDetection.HorizonMasked;
            else if (sig.Doppler.TrackRejectedByNotch) Latest.Detection = TelemetryDetection.Notched;
            else if (decoyCaptured) Latest.Detection = TelemetryDetection.DecoyCaptured;
            else Latest.Detection = TelemetryDetection.BelowThreshold;

            Latest.RayColor = GetDetectionColor(Latest.Detection);
        }

        /// <summary>
        /// Ray / tag color of a scan result.
        /// </summary>
        public static Color GetDetectionColor(TelemetryDetection d)
        {
            switch (d)
            {
                case TelemetryDetection.Detected: return new Color(0.1f, 1.0f, 0.3f, 0.9f);       // Green
                case TelemetryDetection.DecoyCaptured: return new Color(1.0f, 0.3f, 0.9f, 0.9f);  // Magenta
                case TelemetryDetection.BelowThreshold: return new Color(1.0f, 0.85f, 0.1f, 0.9f); // Amber
                default: return new Color(1.0f, 0.2f, 0.2f, 0.9f);                                // Red: notch / horizon
            }
        }

        /// <summary>
        /// Short text of a scan result.
        /// </summary>
        public static string GetDetectionText(TelemetryDetection d)
        {
            switch (d)
            {
                case TelemetryDetection.Detected: return "DETECTED";
                case TelemetryDetection.DecoyCaptured: return "DECOY CAPTURE";
                case TelemetryDetection.BelowThreshold: return "BELOW THRESHOLD";
                case TelemetryDetection.Notched: return "NOTCHED";
                default: return "HORIZON MASKED";
            }
        }

        /// <summary>
        /// Short text of a Doppler filter state.
        /// </summary>
        public static string GetNotchText(TelemetryNotchState s)
        {
            switch (s)
            {
                case TelemetryNotchState.Notched: return "NOTCHED";
                case TelemetryNotchState.HorizonOccluded: return "MASKED";
                case TelemetryNotchState.RotorFlash: return "ROTOR FLASH";
                case TelemetryNotchState.ClutterDegraded: return "CLUTTER";
                case TelemetryNotchState.InNotchOpenSky: return "NOTCH, NO CLUTTER";
                default: return "CLEAR";
            }
        }

        /// <summary>
        /// Short text of an illumination state.
        /// </summary>
        public static string GetStateText(RadarIlluminationState s)
        {
            switch (s)
            {
                case RadarIlluminationState.MissileGuidance: return "MISSILE GUIDANCE";
                case RadarIlluminationState.HardLock: return "HARD LOCK";
                case RadarIlluminationState.SearchStrong: return "SEARCH +3dB";
                default: return "SEARCH";
            }
        }

        public static void ClearAll()
        {
            ActiveIlluminators.Clear();
            CachedIlluminatorList.Clear();
            _staleKeys.Clear();
            CurrentPriority = 0;
            LastPriorityTime = 0.0f;
            Latest = new RadarTelemetrySnapshot();
        }
    }
}
