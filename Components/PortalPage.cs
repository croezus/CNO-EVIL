using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Config;
using CnoEvil.Patches;

namespace CnoEvil.Components
{
    /// <summary>
    /// One page of the portal display (TacScreenPortalMFD). The page is built once in a W x H rect with a
    /// RectMask2D (a stencil Mask on turned slots) and moves between the two halves. W follows the main screen aspect (341 units at the minimum
    /// width), H is 296. The page name sits in the top left corner. Tick runs at 10 Hz while the page shows.
    /// </summary>
    public abstract class PortalPage
    {
        protected static readonly Vector2 Mid = new Vector2(0.5f, 0.5f);

        protected float W { get { return Host.PageWidth; } }
        protected float H { get { return Host.PageHeight; } }

        public CnoEvilConfig.PortalMFDPage Id;
        public RectTransform Root;
        protected TacScreenPortalMFD Host;

        protected Aircraft Aircraft { get { return Host.Aircraft; } }
        protected TMP_FontAsset Font { get { return Host.Font; } }

        public void Create(TacScreenPortalMFD host, CnoEvilConfig.PortalMFDPage id, Transform parent)
        {
            Host = host;
            Id = id;
            Root = PortalMFDGraphics.At("Page_" + id, parent, Mid, Vector2.zero, new Vector2(W, H));
            if (host.TurnedSlots)
            {
                // A RectMask2D clips in the space of the root canvas: on a turned slot its clip rect has a negative
                // height and the page stays black (FS-41). A stencil mask clips by its mesh; the graphic is opaque,
                // a transparent mesh is culled and would mask everything.
                Image maskGraphic = Root.gameObject.AddComponent<Image>();
                maskGraphic.raycastTarget = false;
                Root.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            }
            else Root.gameObject.AddComponent<RectMask2D>();
            Build();
            SetVisible(false);
        }

        /// <summary>
        /// The page holds vanilla panels. It hides with a CanvasGroup and never goes inactive, because deactivation
        /// stops the coroutines of the vanilla scripts on those panels.
        /// </summary>
        protected virtual bool HostsVanilla { get { return false; } }
        private CanvasGroup _group;

        public void SetVisible(bool on)
        {
            if (!HostsVanilla)
            {
                if (Root.gameObject.activeSelf != on) Root.gameObject.SetActive(on);
                return;
            }
            if (_group == null) _group = Root.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = on ? 1f : 0f;
            if (!Root.gameObject.activeSelf) Root.gameObject.SetActive(true);
        }

        protected abstract void Build();
        public virtual void Tick(float now) { }
        /// <summary>Every frame while the page shows (Tick runs at 10 Hz).</summary>
        public virtual void FrameTick() { }
        /// <summary>Range hotkey: the next longer range, the shortest after the longest.</summary>
        public virtual void CycleRange() { }
        public virtual void OnShow() { }
        public virtual void Dispose() { }

        #region Helpers

        protected Image Background(Color color)
        {
            Image bg = PortalMFDGraphics.AddImage(PortalMFDGraphics.Stretch("Bg", Root, 0f), color, null);
            bg.rectTransform.SetAsFirstSibling();
            return bg;
        }

        protected TextMeshProUGUI Label(Transform parent, Vector2 pos, Vector2 size, string text, float fontSize, Color color, TextAlignmentOptions align)
        {
            return PortalMFDGraphics.Text("Txt", parent, Mid, pos, size, text, fontSize, color, align, Font);
        }

        protected PortalButton Button(Vector2 pos, Vector2 size, string label)
        {
            return Host.AddButton(Root, Mid, pos, size, label, 9.5f);
        }

        protected static void Show(Component c, bool on)
        {
            if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
        }

        protected static void SetText(TextMeshProUGUI t, string s)
        {
            if (t != null && t.text != s) t.text = s;
        }

        /// <summary>Flat heading of the aircraft, degrees clockwise from north.</summary>
        protected static float HeadingDeg(Transform t)
        {
            Vector3 f = t.forward;
            if (f.x * f.x + f.z * f.z < 1e-6f) f = -t.up;
            return Mathf.Repeat(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, 360f);
        }

        /// <summary>World offset (east, north) to heading-up display units.</summary>
        protected static Vector2 HeadingUp(float east, float north, float headingRad, float scale)
        {
            float c = Mathf.Cos(headingRad);
            float s = Mathf.Sin(headingRad);
            return new Vector2((east * c - north * s) * scale, (east * s + north * c) * scale);
        }

        /// <summary>Index of the table entry nearest to km.</summary>
        protected static int NearestRange(float[] table, float km)
        {
            int best = 0;
            for (int i = 0; i < table.Length; i++)
            {
                if (Mathf.Abs(table[i] - km) < Mathf.Abs(table[best] - km)) best = i;
            }
            return best;
        }

        protected static Unit PrimaryTarget()
        {
            if (CombatHUD.i == null || !CombatHUD.i.HasTargets) return null;
            List<Unit> targets = CombatHUD.i.GetTargetList();
            return (targets != null && targets.Count > 0) ? targets[0] : null;
        }

        #endregion
    }

    /// <summary>
    /// What the player's faction knows about a unit, for the plan view pages (TSD, NAV).
    /// Hostile: located faction tracks only (UnitMapIcon_UpdateIcon_Patch.IsLocated), at the last known position.
    /// Friendly: own faction aircraft and ships at their live position (datalink).
    /// </summary>
    public struct PortalContact
    {
        public Unit Unit;
        public Vector3 WorldPos;
        public bool Hostile;
        public bool Live;
        public float UncertaintyMeters;
        public IdentTier Tier;
        public float Quality;
        /// <summary>Ground track (m/s) when known: hostile from a radar / visual contact in the last 10 s, friendly live.</summary>
        public Vector3 Velocity;
        public bool HasVelocity;
    }

    public static class PortalContacts
    {
        // Symbol limits of a plan view page, applied to the contacts on the page (PortalPagePlan.Tick)
        public const int MaxHostile = 48;
        public const int MaxFriendly = 32;
        // Reference TSD: the velocity vector shows above 10 m/s
        private const float MinVectorSpeedSq = 100f;
        private const float VelocityMaxAge = 10f;

        /// <summary>
        /// Hostile located tracks and friendly aircraft and ships; filter (optional) drops units the page does not show.
        /// No count limit: the page limits the symbols after its range test, so far contacts never hide near ones.
        /// </summary>
        public static void Collect(Aircraft own, List<PortalContact> list, Predicate<Unit> filter)
        {
            list.Clear();
            if (own == null) return;
            FactionHQ hq = own.NetworkHQ;

            FactionHQ playerHq = RWRTriangulationProcessor.GetPlayerFactionHQ();
            Dictionary<Unit, TriangulationTrack> tracks = playerHq != null ? RWRTriangulationProcessor.GetTrackDictionary(playerHq) : null;
            if (tracks != null)
            {
                foreach (KeyValuePair<Unit, TriangulationTrack> kv in tracks)
                {
                    Unit u = kv.Key;
                    TriangulationTrack track = kv.Value;
                    if (u == null || u.disabled || u is Missile || track == null) continue;
                    if (track.LastKnownTime <= 0f) continue;
                    if (!ThreatClassification.IsTrackedThreat(u, playerHq)) continue;
                    if (!UnitMapIcon_UpdateIcon_Patch.IsLocated(track)) continue;
                    if (filter != null && !filter(u)) continue;

                    PortalContact c = new PortalContact();
                    c.Unit = u;
                    c.WorldPos = track.LastKnownGlobalPosition.ToLocalPosition();
                    c.Hostile = true;
                    c.Live = track.HasRecentPreciseContact || track.IsActivelyDetected;
                    c.UncertaintyMeters = track.UncertaintyRadiusMeters;
                    c.Tier = TargetIdentification.GetTier(track);
                    c.Quality = track.TrackingQuality;
                    c.Velocity = new Vector3(track.LastPreciseVelocity.x, 0f, track.LastPreciseVelocity.z);
                    c.HasVelocity = track.LastPreciseVelocityTime > 0f &&
                                    Time.timeSinceLevelLoad - track.LastPreciseVelocityTime < VelocityMaxAge &&
                                    c.Velocity.sqrMagnitude > MinVectorSpeedSq;
                    list.Add(c);
                }
            }

            List<Unit> all = UnitRegistry.allUnits;
            if (hq == null || all == null) return;
            for (int i = 0; i < all.Count; i++)
            {
                Unit u = all[i];
                if (u == null || u == own || u.disabled || u.NetworkHQ != hq) continue;
                if (!(u is Aircraft) && !(u is Ship)) continue;
                if (filter != null && !filter(u)) continue;
                PortalContact c = new PortalContact();
                c.Unit = u;
                c.WorldPos = u.transform.position;
                c.Hostile = false;
                c.Live = true;
                c.Tier = IdentTier.High;
                c.Quality = 1f;
                Vector3 v = u.rb != null ? u.rb.velocity : u.transform.forward * u.speed;
                c.Velocity = new Vector3(v.x, 0f, v.z);
                c.HasVelocity = c.Velocity.sqrMagnitude > MinVectorSpeedSq;
                list.Add(c);
            }
        }
    }

    /// <summary>
    /// Reusable contact symbols: icon, optional ring (uncertainty or lock) and a label.
    /// </summary>
    public class PortalSymbolPool
    {
        public class Item
        {
            public RectTransform Root;
            public Image Icon;
            public Image Ring;
            public Image Lock;
            public Image Radar;
            public TextMeshProUGUI Label;
            public Unit Unit;
            public IdentTier Tier;
        }

        private readonly List<Item> _items = new List<Item>();
        private readonly Transform _parent;
        private readonly TMP_FontAsset _font;

        public PortalSymbolPool(Transform parent, TMP_FontAsset font)
        {
            _parent = parent;
            _font = font;
        }

        public Item Get(int index)
        {
            while (_items.Count <= index)
            {
                Item it = new Item();
                it.Root = PortalMFDGraphics.At("Sym", _parent, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1f, 1f));
                it.Ring = PortalMFDGraphics.Shape("Ring", it.Root, new Vector2(0.5f, 0.5f), Vector2.zero, 10f, PortalShape.RingFine, Color.white);
                it.Icon = PortalMFDGraphics.Shape("Icon", it.Root, new Vector2(0.5f, 0.5f), Vector2.zero, 10f, PortalShape.AirHat, Color.white);
                it.Lock = PortalMFDGraphics.Shape("Lock", it.Root, new Vector2(0.5f, 0.5f), Vector2.zero, 21.7f, PortalShape.RingThick, new Color(0.53f, 1f, 0.41f, 1f));
                // Emitter badge above the icon (reference radarIcon, 11 px at +6.3)
                it.Radar = PortalMFDGraphics.Shape("Radar", it.Root, new Vector2(0.5f, 0.5f), new Vector2(0f, 9f), 11f, PortalShape.RadarArc, Color.white);
                it.Radar.rectTransform.localEulerAngles = new Vector3(0f, 0f, 180f);
                it.Radar.gameObject.SetActive(false);
                it.Label = PortalMFDGraphics.Text("Label", it.Root, new Vector2(0.5f, 0.5f), new Vector2(34f, -7f), new Vector2(56f, 10f),
                    string.Empty, 7.5f, Color.white, TextAlignmentOptions.Left, _font);
                it.Root.gameObject.SetActive(false);
                _items.Add(it);
            }
            Item item = _items[index];
            if (!item.Root.gameObject.activeSelf) item.Root.gameObject.SetActive(true);
            return item;
        }

        public void HideFrom(int index)
        {
            for (int i = index; i < _items.Count; i++)
            {
                if (_items[i].Root.gameObject.activeSelf) _items[i].Root.gameObject.SetActive(false);
                _items[i].Unit = null;
            }
        }
    }
}
