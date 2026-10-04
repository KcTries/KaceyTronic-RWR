using UnityEngine;

namespace TraditionalRWR
{
    // One RWR audio pack -- see RWRAudioLogic for the switchboard between
    // packs, and its header comment for how to add a new one. Five clips,
    // embedded directly in the DLL (Assets\*.wav, marked EmbeddedResource in
    // the .csproj) so a release stays a single file -- no loose audio files
    // to ship alongside it.
    internal static class KaceyTronicAudio
    {
        private const string ResourcePrefix = "TraditionalRWR.Assets.";

        private static AudioSource _source;
        private static AudioClip _newAir;
        private static AudioClip _newGround;
        private static AudioClip _ping;
        private static AudioClip _launchWarning;
        private static AudioClip _tracking;
        private static bool _loaded;

        // Called once from RWRAudioLogic.EnsureLoadedAll() -- host is the
        // scope's own persistent GameObject (added once at Plugin.Awake(),
        // never destroyed across mission restarts), so the AudioSource and
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

            _newAir = WavLoader.LoadEmbeddedClip(ResourcePrefix + "NewAir_KaceyTronic.wav", "KaceyTronic_NewAir");
            _newGround = WavLoader.LoadEmbeddedClip(ResourcePrefix + "NewGround_Kaceytronic.wav", "KaceyTronic_NewGround");
            _ping = WavLoader.LoadEmbeddedClip(ResourcePrefix + "Ping_KaceyTronic.wav", "KaceyTronic_Ping");
            _launchWarning = WavLoader.LoadEmbeddedClip(ResourcePrefix + "LaunchWarning_KaceyTronic.wav", "KaceyTronic_LaunchWarning");
            _tracking = WavLoader.LoadEmbeddedClip(ResourcePrefix + "Tracking_KaceyTronic.wav", "KaceyTronic_Tracking");
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

        internal static void PlayTracking()
        {
            Play(_tracking);
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
