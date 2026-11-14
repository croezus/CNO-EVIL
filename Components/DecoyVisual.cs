using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    /// <summary>
    /// Visual part of an active decoy (DecoyDispenserSetup prefab): the cartridge body tumbles, and a faint red beacon
    /// flashes while the decoy transmits (DecoyBeaconVisible). It also marks a RadarChaff as a decoy.
    /// </summary>
    public class DecoyVisual : MonoBehaviour
    {
        public Transform Tumble;
        public ParticleSystem Beacon;

        private float m_launchTime;
        private Vector3 m_spinAxis;
        private float m_spinRate;
        private bool m_beaconOn;

        private void OnEnable()
        {
            m_launchTime = Time.timeSinceLevelLoad;
            m_spinAxis = Random.onUnitSphere;
            m_spinRate = Random.Range(720.0f, 2160.0f);
            m_beaconOn = true;
            SetBeacon(false);
        }

        private void Update()
        {
            if (Tumble != null) Tumble.Rotate(m_spinAxis, m_spinRate * Time.deltaTime, Space.Self);

            float age = Time.timeSinceLevelLoad - m_launchTime;
            float arm = CnoEvilConfig.DecoyArmDelay_s.Value;
            bool transmitting = age >= arm && age < arm + CnoEvilConfig.DecoyActiveTime_s.Value;
            SetBeacon(transmitting && CnoEvilConfig.DecoyBeaconVisible.Value);
        }

        private void SetBeacon(bool on)
        {
            if (on == m_beaconOn || Beacon == null) return;
            m_beaconOn = on;
            // The beacon system keeps playing (vanilla RadarChaff destroys the object only while its particles play);
            // only the emission changes
            ParticleSystem.EmissionModule emission = Beacon.emission;
            emission.enabled = on;
        }
    }
}
