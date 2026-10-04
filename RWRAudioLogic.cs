using System;
using System.Collections.Generic;
using System.ComponentModel;
using UnityEngine;

namespace TraditionalRWR
{
    // Which pack "RWR Audio Pack" (Audio, see Plugin.cs) currently has
    // selected -- BepInEx's ConfigManager renders an enum as a dropdown
    // automatically. To add a new pack (e.g. F16, F18): clone an existing
    // pack class (KaceyTronicAudio.cs/VTOLVRAudio.cs) for the new one,
    // pointing its EnsureLoaded at its own EmbeddedResource files, add a
    // value here, and register it in RWRAudioLogic.AllPacks below. Nothing
    // else in this mod needs to change.
    //
    // ConfigManager's dropdown renders a value's name via its own
    // ToProperCase() (confirmed by decompiling ConfigurationManager.dll's
    // SettingFieldDrawer) -- it inserts a space before EVERY uppercase
    // letter, not just at a lowercase-to-uppercase boundary, so a plain
    // camelCase/PascalCase name like WarThunder reads fine ("War Thunder")
    // but an all-caps acronym like VTOLVR gets a space before every single
    // letter ("V T O L V R"). ConfigManager checks for a [Description]
    // attribute on the enum value FIRST and uses that verbatim instead if
    // present -- the correct way to override just the broken one, rather
    // than fighting ToProperCase for every future acronym-named pack.
    internal enum RwrAudioPack
    {
        KaceyTronic,
        [Description("VTOLVR")]
        VTOLVR,
        WarThunder,
        [Description("F/A-18")]
        F18,
    }

    // Switchboard between packs -- RwrScopeController only ever calls
    // through here, never a specific pack class directly, so adding a pack
    // never touches RwrScopeController itself.
    internal static class RWRAudioLogic
    {
        // Each pack decides for itself whether Tracking is a ping-driven
        // one-shot (PlayTracking, KaceyTronic's style) or a start/stop loop
        // tied to the missile-warning lifecycle instead (StartTracking/
        // StopTracking, VTOLVR's style -- see VTOLVRAudio's header comment
        // for why). Use WithOneShotTracking/WithLoopedTracking below rather
        // than the private constructor directly.
        private readonly struct PackHandlers
        {
            public readonly Action<GameObject> EnsureLoaded;
            public readonly Action PlayNewAir;
            public readonly Action PlayNewGround;
            public readonly Action PlayPing;
            public readonly Action PlayLaunchWarning;
            public readonly bool LoopsTracking;
            public readonly Action PlayTracking;
            public readonly Action StartTracking;
            public readonly Action StopTracking;
            // Targeting is a second, independent loop concept alongside
            // Tracking -- only WarThunder has one so far (a radar source
            // has you targeted/"red ping" but no missile is in the air yet,
            // distinct from an actual radar-guided missile in flight). Null
            // for any pack without one (KaceyTronic/VTOLVR); NotifyTargetingActive
            // is a no-op in that case, same "optional capability" shape
            // Tick below uses.
            public readonly Action StartTargeting;
            public readonly Action StopTargeting;
            // Optional per-frame hook, called from RwrScopeController's own
            // Update() via RWRAudioLogic.Tick() regardless of which pack is
            // selected -- null for a pack that doesn't need one (both
            // existing packs). WarThunder uses this to schedule
            // LaunchWarning's second play.
            public readonly Action Tick;

            private PackHandlers(
                Action<GameObject> ensureLoaded, Action playNewAir, Action playNewGround, Action playPing,
                Action playLaunchWarning, bool loopsTracking, Action playTracking, Action startTracking, Action stopTracking,
                Action startTargeting, Action stopTargeting, Action tick)
            {
                EnsureLoaded = ensureLoaded;
                PlayNewAir = playNewAir;
                PlayNewGround = playNewGround;
                PlayPing = playPing;
                PlayLaunchWarning = playLaunchWarning;
                LoopsTracking = loopsTracking;
                PlayTracking = playTracking;
                StartTracking = startTracking;
                StopTracking = stopTracking;
                StartTargeting = startTargeting;
                StopTargeting = stopTargeting;
                Tick = tick;
            }

            public static PackHandlers WithOneShotTracking(
                Action<GameObject> ensureLoaded, Action playNewAir, Action playNewGround,
                Action playPing, Action playLaunchWarning, Action playTracking,
                Action startTargeting = null, Action stopTargeting = null, Action tick = null)
            {
                return new PackHandlers(
                    ensureLoaded, playNewAir, playNewGround, playPing, playLaunchWarning,
                    loopsTracking: false, playTracking: playTracking, startTracking: null, stopTracking: null,
                    startTargeting: startTargeting, stopTargeting: stopTargeting, tick: tick);
            }

            public static PackHandlers WithLoopedTracking(
                Action<GameObject> ensureLoaded, Action playNewAir, Action playNewGround,
                Action playPing, Action playLaunchWarning, Action startTracking, Action stopTracking,
                Action startTargeting = null, Action stopTargeting = null, Action tick = null)
            {
                return new PackHandlers(
                    ensureLoaded, playNewAir, playNewGround, playPing, playLaunchWarning,
                    loopsTracking: true, playTracking: null, startTracking: startTracking, stopTracking: stopTracking,
                    startTargeting: startTargeting, stopTargeting: stopTargeting, tick: tick);
            }
        }

        // Add a new pack's line here once its class exists -- see this
        // file's header comment.
        private static readonly Dictionary<RwrAudioPack, PackHandlers> AllPacks = new Dictionary<RwrAudioPack, PackHandlers>
        {
            {
                RwrAudioPack.KaceyTronic,
                PackHandlers.WithOneShotTracking(
                    KaceyTronicAudio.EnsureLoaded, KaceyTronicAudio.PlayNewAir, KaceyTronicAudio.PlayNewGround,
                    KaceyTronicAudio.PlayPing, KaceyTronicAudio.PlayLaunchWarning, KaceyTronicAudio.PlayTracking)
            },
            {
                RwrAudioPack.VTOLVR,
                PackHandlers.WithLoopedTracking(
                    VTOLVRAudio.EnsureLoaded, VTOLVRAudio.PlayNewAir, VTOLVRAudio.PlayNewGround,
                    VTOLVRAudio.PlayPing, VTOLVRAudio.PlayLaunchWarning, VTOLVRAudio.StartTracking, VTOLVRAudio.StopTracking)
            },
            {
                RwrAudioPack.WarThunder,
                PackHandlers.WithLoopedTracking(
                    WarThunderAudio.EnsureLoaded, WarThunderAudio.PlayNewAir, WarThunderAudio.PlayNewGround,
                    WarThunderAudio.PlayPing, WarThunderAudio.PlayLaunchWarning, WarThunderAudio.StartTracking, WarThunderAudio.StopTracking,
                    startTargeting: WarThunderAudio.StartTargeting, stopTargeting: WarThunderAudio.StopTargeting, tick: WarThunderAudio.Tick)
            },
            {
                RwrAudioPack.F18,
                PackHandlers.WithLoopedTracking(
                    F18Audio.EnsureLoaded, F18Audio.PlayNewAir, F18Audio.PlayNewGround,
                    F18Audio.PlayPing, F18Audio.PlayLaunchWarning, F18Audio.StartTracking, F18Audio.StopTracking,
                    startTargeting: F18Audio.StartTargeting, stopTargeting: F18Audio.StopTargeting)
            },
        };

        private static RwrAudioPack _selectedAudioPack = RwrAudioPack.KaceyTronic;

        // Whether a radar-guided missile is currently tracking onto the
        // player -- set by RwrScopeController from the missile-warning
        // lifecycle (see NotifyTrackingActive), independent of which pack
        // is selected, so switching packs mid-threat starts/stops the new
        // pack's loop correctly instead of leaving it out of sync.
        private static bool _trackingActive;

        // Same idea as _trackingActive above, for the independent Targeting
        // loop (see PackHandlers.StartTargeting's own comment) -- currently
        // only meaningful for WarThunder, but tracked generically here the
        // same way Tracking is, so a future pack with its own Targeting
        // concept needs no changes to this switchboard.
        //
        // This is the LOGICAL state (is some radar source still targeting
        // the player), not necessarily what's actually audible -- Tracking
        // always takes priority over Targeting (a real radar-guided missile
        // in flight is a more urgent cue than "a radar has you targeted"),
        // so Targeting is muted for as long as Tracking is active and
        // automatically resumes once Tracking ends, if it's still logically
        // active at that point. See AudibleTargeting/ApplyTargetingAudibility.
        private static bool _targetingActive;

        // What SetPackTargetingActive should actually reflect right now,
        // given the priority rule above.
        private static bool AudibleTargeting => _targetingActive && !_trackingActive;

        // Only used while "Use Custom Audio" (Audio, see
        // Plugin.cs) is on -- see KaceyTronicAudio's header comment.
        internal static RwrAudioPack SelectedAudioPack
        {
            get => _selectedAudioPack;
            set
            {
                if (_selectedAudioPack == value)
                {
                    return;
                }

                SetPackTrackingActive(_selectedAudioPack, false);
                SetPackTargetingActive(_selectedAudioPack, false);
                _selectedAudioPack = value;
                SetPackTrackingActive(_selectedAudioPack, _trackingActive);
                SetPackTargetingActive(_selectedAudioPack, AudibleTargeting);
            }
        }

        // Every pack is loaded eagerly (a few hundred KB total at most), not
        // just the selected one -- switching packs in ConfigManager then
        // takes effect immediately, matching this mod's usual "settings
        // apply live" convention, with no reload/rebuild.
        internal static void EnsureLoadedAll(GameObject host)
        {
            _host = host;
            foreach (PackHandlers pack in AllPacks.Values)
            {
                pack.EnsureLoaded(host);
            }
            ApplyVolume();
        }

        // Master volume (0-1) for every custom pack's sound, set from the
        // "RWR Audio Volume" config (Audio). Every pack's AudioSources
        // live on the scope's own persistent host GameObject and nothing
        // else does, so setting each one's volume covers one-shots
        // (PlayOneShot scales by the source's volume) and loops alike --
        // no per-pack code. Doesn't affect the vanilla RWR audio, which
        // plays through the game's own sources.
        private static GameObject _host;
        private static float _volume = 1f;

        internal static void SetVolume(float volume)
        {
            _volume = Mathf.Clamp01(volume);
            ApplyVolume();
        }

        private static void ApplyVolume()
        {
            if (_host == null)
            {
                return;
            }
            foreach (AudioSource source in _host.GetComponents<AudioSource>())
            {
                source.volume = _volume;
            }
        }

        internal static void PlayNewAir()
        {
            AllPacks[SelectedAudioPack].PlayNewAir();
        }

        internal static void PlayNewGround()
        {
            AllPacks[SelectedAudioPack].PlayNewGround();
        }

        internal static void PlayPing()
        {
            AllPacks[SelectedAudioPack].PlayPing();
        }

        internal static void PlayLaunchWarning()
        {
            AllPacks[SelectedAudioPack].PlayLaunchWarning();
        }

        // Ping-driven one-shot Tracking, for a pack that doesn't loop --
        // a no-op for a looping pack, since NotifyTrackingActive drives that
        // one instead. RwrScopeController calls this exactly the same way
        // regardless of which pack is selected.
        internal static void PlayTracking()
        {
            PackHandlers handlers = AllPacks[SelectedAudioPack];
            if (!handlers.LoopsTracking)
            {
                handlers.PlayTracking();
            }
        }

        // Called by RwrScopeController off the missile-warning lifecycle
        // (ARH/SARH only, edge-triggered on the first radar-guided missile
        // appearing / the last one ending) -- not by individual radar
        // pings. See VTOLVRAudio's header comment for why a ping-driven
        // one-shot can't represent continuous SARH guidance. A no-op for a
        // pack that doesn't loop.
        internal static void NotifyTrackingActive(bool active)
        {
            if (_trackingActive == active)
            {
                return;
            }
            bool wasAudibleTargeting = AudibleTargeting;
            _trackingActive = active;
            SetPackTrackingActive(_selectedAudioPack, active);
            // Tracking starting mutes Targeting (priority); Tracking ending
            // restarts it if it's still logically active -- see
            // AudibleTargeting's comment.
            ApplyTargetingAudibility(wasAudibleTargeting);
        }

        private static void SetPackTrackingActive(RwrAudioPack pack, bool active)
        {
            PackHandlers handlers = AllPacks[pack];
            if (!handlers.LoopsTracking)
            {
                return;
            }
            if (active)
            {
                handlers.StartTracking();
            }
            else
            {
                handlers.StopTracking();
            }
        }

        // Called by RwrScopeController off its own Targeting-loop source set
        // (a radar source currently giving a "red ping" with no missile
        // launched yet -- see RwrScopeController.SetTargetingActive). This
        // is the LOGICAL signal, not necessarily what plays -- see
        // AudibleTargeting; Tracking playing at the same time mutes it. A
        // no-op for any pack without a Targeting concept.
        internal static void NotifyTargetingActive(bool active)
        {
            if (_targetingActive == active)
            {
                return;
            }
            bool wasAudibleTargeting = AudibleTargeting;
            _targetingActive = active;
            ApplyTargetingAudibility(wasAudibleTargeting);
        }

        // Starts/stops the pack's actual Targeting loop only when
        // AudibleTargeting's value has genuinely changed since wasAudible --
        // e.g. _targetingActive flipping true while Tracking is already
        // active shouldn't audibly start anything (still muted), but the
        // logical state is still recorded so it correctly resumes once
        // Tracking ends.
        private static void ApplyTargetingAudibility(bool wasAudible)
        {
            bool isAudible = AudibleTargeting;
            if (wasAudible == isAudible)
            {
                return;
            }
            SetPackTargetingActive(_selectedAudioPack, isAudible);
        }

        private static void SetPackTargetingActive(RwrAudioPack pack, bool active)
        {
            PackHandlers handlers = AllPacks[pack];
            if (handlers.StartTargeting == null || handlers.StopTargeting == null)
            {
                return;
            }
            if (active)
            {
                handlers.StartTargeting();
            }
            else
            {
                handlers.StopTargeting();
            }
        }

        // Called once a frame from RwrScopeController.Update(), regardless
        // of which pack is selected -- a no-op for a pack with no Tick
        // handler (both existing packs).
        internal static void Tick()
        {
            AllPacks[_selectedAudioPack].Tick?.Invoke();
        }
    }
}
