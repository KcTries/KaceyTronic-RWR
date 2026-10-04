using UnityEngine;

namespace TraditionalRWR
{
    // One RWR audio pack -- see RWRAudioLogic for the switchboard between
    // packs. Clips embedded in Assets\WarThunder\*.wav.
    //
    // Unlike KaceyTronic/VTOLVR, this pack distinguishes TWO separate loop
    // states instead of one:
    // - Targeting: a radar source currently has you targeted ("red ping",
    //   RwrScopeController's e.isTarget) but no missile is in the air yet.
    //   Starts/stops per radar source; see RwrScopeController.SetTargetingActive.
    // - Tracking: an actual radar-guided missile (ARH or SARH) is in
    //   flight -- reuses the exact same signal VTOLVR's own loop already
    //   uses (RegisterRadarGuidedTracking/NotifyTrackingActive), since the
    //   trigger conditions are identical (any ARH ping, or a confirmed SARH
    //   launch).
    // A SARH launch transfers Targeting -> Tracking for that same source
    // (RwrScopeController turns Targeting off at the exact moment it
    // registers the new Tracking source, in the SARH branch of
    // OnMissileWarningReceived) -- from this pack's own perspective, that's
    // just an ordinary StopTargeting followed by a StartTracking.
    //
    // LaunchWarning also isn't a plain one-shot here -- it plays twice per
    // detection (immediate, then one scheduled repeat after the clip's own
    // length), and a fresh detection before that repeat fires cancels it
    // and restarts the sequence, rather than stacking. Driven by
    // RWRAudioLogic.Tick(), called every frame from RwrScopeController's
    // own Update() regardless of which pack is selected.
    internal static class WarThunderAudio
    {
        private const string ResourcePrefix = "TraditionalRWR.Assets.WarThunder.";

        private static AudioSource _source;
        private static AudioSource _targetingLoopSource;
        private static AudioSource _trackingLoopSource;
        private static AudioClip _ping;
        private static AudioClip _launchWarning;
        private static AudioClip _targetingLoop;
        private static AudioClip _trackingLoop;
        private static bool _loaded;

        private static bool _launchWarningRepeatPending;
        private static float _launchWarningRepeatTime;

        // Called once from RWRAudioLogic.EnsureLoadedAll() -- host is the
        // scope's own persistent GameObject (added once at Plugin.Awake(),
        // never destroyed across mission restarts), so the AudioSources and
        // decoded clips are loaded exactly once for the plugin's lifetime.
        internal static void EnsureLoaded(GameObject host)
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;

            _source = host.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            // 2D/non-positional -- matches vanilla's own radarWarningSource/
            // interfaceSource, which these clips are standing in for.
            _source.spatialBlend = 0f;
            _source.dopplerLevel = 0f;

            // Separate sources for each loop -- Targeting and Tracking can
            // in principle need to run independently (one stopping exactly
            // as the other starts, on a SARH launch transfer), and each
            // needs its own play/stop lifecycle distinct from _source's
            // one-shot PlayOneShot calls.
            _targetingLoopSource = host.AddComponent<AudioSource>();
            _targetingLoopSource.playOnAwake = false;
            _targetingLoopSource.loop = true;
            _targetingLoopSource.spatialBlend = 0f;
            _targetingLoopSource.dopplerLevel = 0f;

            _trackingLoopSource = host.AddComponent<AudioSource>();
            _trackingLoopSource.playOnAwake = false;
            _trackingLoopSource.loop = true;
            _trackingLoopSource.spatialBlend = 0f;
            _trackingLoopSource.dopplerLevel = 0f;

            _ping = WavLoader.LoadEmbeddedClip(ResourcePrefix + "WarThunder_Ping.wav", "WarThunder_Ping");
            _launchWarning = WavLoader.LoadEmbeddedClip(ResourcePrefix + "WarThunder_LaunchWarning.wav", "WarThunder_LaunchWarning");
            _targetingLoop = WavLoader.LoadEmbeddedClip(ResourcePrefix + "WarThunder_TargetingLoop.wav", "WarThunder_TargetingLoop");
            _trackingLoop = WavLoader.LoadEmbeddedClip(ResourcePrefix + "WarThunder_TrackingLoop.wav", "WarThunder_TrackingLoop");
        }

        // NewAir/NewGround/Ping are deliberately all the same sound for this
        // pack specifically (unlike KaceyTronic/VTOLVR, which use distinct
        // clips for each) -- there's no separate NewAir/NewGround clip to
        // load at all, everything just plays Ping.
        internal static void PlayNewAir()
        {
            Play(_ping);
        }

        internal static void PlayNewGround()
        {
            Play(_ping);
        }

        internal static void PlayPing()
        {
            Play(_ping);
        }

        // Plays immediately, then schedules exactly one repeat after the
        // clip's own length -- two total plays per "settled" detection. A
        // fresh call before that repeat fires overwrites the scheduled time
        // and plays another immediate copy, which both cancels the old
        // repeat (Tick only ever checks the CURRENT _launchWarningRepeatTime)
        // and restarts the sequence -- matching "if another missile is
        // detected while the loops are playing, the count resets."
        internal static void PlayLaunchWarning()
        {
            if (_source == null || _launchWarning == null)
            {
                return;
            }
            _source.PlayOneShot(_launchWarning);
            _launchWarningRepeatPending = true;
            _launchWarningRepeatTime = Time.unscaledTime + _launchWarning.length;
        }

        internal static void Tick()
        {
            if (_launchWarningRepeatPending && Time.unscaledTime >= _launchWarningRepeatTime)
            {
                _launchWarningRepeatPending = false;
                if (_source != null && _launchWarning != null)
                {
                    _source.PlayOneShot(_launchWarning);
                }
            }
        }

        internal static void StartTargeting()
        {
            if (_targetingLoopSource != null && _targetingLoop != null)
            {
                _targetingLoopSource.clip = _targetingLoop;
                _targetingLoopSource.Play();
            }
        }

        internal static void StopTargeting()
        {
            if (_targetingLoopSource != null)
            {
                _targetingLoopSource.Stop();
            }
        }

        internal static void StartTracking()
        {
            if (_trackingLoopSource != null && _trackingLoop != null)
            {
                _trackingLoopSource.clip = _trackingLoop;
                _trackingLoopSource.Play();
            }
        }

        internal static void StopTracking()
        {
            if (_trackingLoopSource != null)
            {
                _trackingLoopSource.Stop();
            }
        }

        private static void Play(AudioClip clip)
        {
            if (_source != null && clip != null)
            {
                _source.PlayOneShot(clip);
            }
        }
    }
}
