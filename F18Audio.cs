using UnityEngine;

namespace TraditionalRWR
{
    // One RWR audio pack -- see RWRAudioLogic for the switchboard between
    // packs, and its header comment for how to add a new one. Clips are
    // embedded directly in the DLL (Assets\F18\*.wav, marked
    // EmbeddedResource in the .csproj) so a release stays a single file.
    //
    // Same two-loop shape as WarThunderAudio: a Targeting loop (a radar
    // source has you targeted, no missile in the air yet) and a separate
    // Tracking loop (a radar-guided missile in flight), with Tracking taking
    // priority over Targeting -- that muting/resume logic lives in
    // RWRAudioLogic, not here. Both loops are short seamless tones
    // (Targeting ~0.5s, Tracking ~0.16s) meant to repeat.
    //
    // No Launch Warning clip yet -- PlayLaunchWarning is a deliberate no-op
    // until one is added.
    internal static class F18Audio
    {
        private const string ResourcePrefix = "TraditionalRWR.Assets.F18.";

        private static AudioSource _source;
        private static AudioSource _targetingLoopSource;
        private static AudioSource _trackingLoopSource;
        private static AudioClip _newAir;
        private static AudioClip _newGround;
        private static AudioClip _ping;
        private static AudioClip _targetingLoop;
        private static AudioClip _trackingLoop;
        private static bool _loaded;

        // Called once from RWRAudioLogic.EnsureLoadedAll() -- host is the
        // scope's own persistent GameObject, so the AudioSources and decoded
        // clips are loaded exactly once for the plugin's lifetime.
        internal static void EnsureLoaded(GameObject host)
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;

            _source = AddSource(host, loop: false);
            _targetingLoopSource = AddSource(host, loop: true);
            _trackingLoopSource = AddSource(host, loop: true);

            _newAir = WavLoader.LoadEmbeddedClip(ResourcePrefix + "NewAir_F18.wav", "F18_NewAir");
            _newGround = WavLoader.LoadEmbeddedClip(ResourcePrefix + "NewGround_F18.wav", "F18_NewGround");
            _ping = WavLoader.LoadEmbeddedClip(ResourcePrefix + "Ping_F18.wav", "F18_Ping");
            _targetingLoop = WavLoader.LoadEmbeddedClip(ResourcePrefix + "TargetingLoop_F18.wav", "F18_TargetingLoop");
            _trackingLoop = WavLoader.LoadEmbeddedClip(ResourcePrefix + "TrackingLoop_F18.wav", "F18_TrackingLoop");
        }

        // 2D/non-positional -- matches vanilla's own radarWarningSource/
        // interfaceSource, which these clips are standing in for.
        private static AudioSource AddSource(GameObject host, bool loop)
        {
            AudioSource source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            return source;
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

        // No clip for this pack yet -- see the header comment.
        internal static void PlayLaunchWarning()
        {
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
