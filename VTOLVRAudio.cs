using UnityEngine;

namespace TraditionalRWR
{
    // One RWR audio pack -- see RWRAudioLogic for the switchboard between
    // packs, and its header comment for how to add a new one. Clips are
    // embedded directly in the DLL (Assets\VTOLVR\*.wav, marked
    // EmbeddedResource in the .csproj) so a release stays a single file --
    // no loose audio files to ship alongside it.
    //
    // Tracking is a loop, not a one-shot (see RWRAudioLogic.LoopsTracking)
    // -- Nuclear Option doesn't fire a repeatable "still being illuminated"
    // ping while a SARH launcher continuously guides a missile onto you the
    // way it does for an ordinary search radar, so a ping-driven one-shot
    // (like KaceyTronic's Tracking) would fall silent for exactly the
    // duration it's supposed to represent. RwrScopeController instead
    // starts/stops this loop directly off the missile-warning lifecycle
    // (ARH/SARH only) via RWRAudioLogic.NotifyTrackingActive.
    internal static class VTOLVRAudio
    {
        private const string ResourcePrefix = "TraditionalRWR.Assets.VTOLVR.";

        private static AudioSource _source;
        private static AudioSource _loopSource;
        private static AudioClip _newAir;
        private static AudioClip _newGround;
        private static AudioClip _ping;
        private static AudioClip _launchWarning;
        private static AudioClip _trackingLoopStart;
        private static AudioClip _trackingLoop;
        private static bool _loaded;

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

            // Separate source for the loop -- it needs its own independent
            // play/stop lifecycle (spanning many frames) instead of sharing
            // _source's one-shot PlayOneShot calls, which would have no way
            // to stop just the loop without also cutting off an unrelated
            // one-shot that happened to be playing at the same time.
            _loopSource = host.AddComponent<AudioSource>();
            _loopSource.playOnAwake = false;
            _loopSource.loop = true;
            _loopSource.spatialBlend = 0f;
            _loopSource.dopplerLevel = 0f;

            _newAir = WavLoader.LoadEmbeddedClip(ResourcePrefix + "NewAir_VTOLVR.wav", "VTOLVR_NewAir");
            _newGround = WavLoader.LoadEmbeddedClip(ResourcePrefix + "NewGround_VTOLVR.wav", "VTOLVR_NewGround");
            _ping = WavLoader.LoadEmbeddedClip(ResourcePrefix + "Ping_VTOLVR.wav", "VTOLVR_Ping");
            _launchWarning = WavLoader.LoadEmbeddedClip(ResourcePrefix + "LaunchWarning_VTOLVR.wav", "VTOLVR_LaunchWarning");
            _trackingLoopStart = WavLoader.LoadEmbeddedClip(ResourcePrefix + "TrackingLoopStart_VTOLVR.wav", "VTOLVR_TrackingLoopStart");
            _trackingLoop = WavLoader.LoadEmbeddedClip(ResourcePrefix + "TrackingLoop_VTOLVR.wav", "VTOLVR_TrackingLoop");
        }

        internal static void PlayNewAir()
        {
            Play(_newAir);
        }

        internal static void PlayNewGround()
        {
            Play(_newGround);
        }

        internal static void PlayPing()
        {
            Play(_ping);
        }

        internal static void PlayLaunchWarning()
        {
            Play(_launchWarning);
        }

        // Plays the one-shot lead-in once, then schedules the seamless
        // loop body to start exactly when it ends -- avoids looping the
        // start clip's own attack transient, and avoids a silent/overlapping
        // gap between the two.
        internal static void StartTracking()
        {
            float startDelay = 0f;
            if (_source != null && _trackingLoopStart != null)
            {
                _source.PlayOneShot(_trackingLoopStart);
                startDelay = _trackingLoopStart.length;
            }

            if (_loopSource != null && _trackingLoop != null)
            {
                _loopSource.clip = _trackingLoop;
                _loopSource.PlayDelayed(startDelay);
            }
        }

        internal static void StopTracking()
        {
            if (_loopSource != null)
            {
                _loopSource.Stop();
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
