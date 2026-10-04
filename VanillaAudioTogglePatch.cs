using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TraditionalRWR
{
    // Toggles the game's own default RWR-related audio (the inverse of "Use
    // Custom Audio" in ConfigManager, see Plugin.cs -- vanilla audio plays
    // unless custom audio is on) -- the contact/new-contact
    // blip the vanilla RadarWarning component plays on every radar ping,
    // and the missile-lock warning loop (plus its one-shot alert chime)
    // ThreatList's MissileAlarm plays while a missile is actively tracking
    // the player. Both are entirely vanilla systems this mod never
    // otherwise touches -- this only mutes their audio; the vanilla
    // directional warning icon and cockpit threat-list panel are untouched.
    // Players who'd rather rely on this scope's own visuals can opt out of
    // the redundant vanilla audio instead of losing the underlying warnings.
    internal static class VanillaAudioTogglePatch
    {
        // ThreatList.MissileAlarm is a private class nested inside a public
        // one -- its own AddMissile/ManageAlarmSound methods (where the
        // loop actually starts/keeps playing) can't be named with typeof()
        // from here, so they're resolved via AccessTools.Inner/Method and
        // patched manually from Plugin.Awake(), same trick KROP uses for
        // its own private nested types.
        internal static void ApplyManualPatch(Harmony harmony)
        {
            Type missileAlarmType = AccessTools.Inner(typeof(ThreatList), "MissileAlarm");

            MethodInfo addMissileMethod = AccessTools.Method(missileAlarmType, "AddMissile");
            harmony.Patch(addMissileMethod, prefix: new HarmonyMethod(typeof(VanillaAudioTogglePatch), nameof(SkipIfDisabled)));

            // AddMissile alone only blocks a NEW lock from ever starting the
            // loop -- if the player flips this toggle off while a missile is
            // already actively locked (loop already playing), the loop would
            // otherwise keep going until that missile's warning ends.
            // ManageAlarmSound runs every frame for every tracked alarm type
            // regardless (from ThreatList.Update()), so a postfix there is
            // the natural place to force an immediate stop instead, matching
            // this mod's usual "settings apply instantly" convention.
            MethodInfo manageAlarmSoundMethod = AccessTools.Method(missileAlarmType, "ManageAlarmSound");
            harmony.Patch(manageAlarmSoundMethod, postfix: new HarmonyMethod(typeof(VanillaAudioTogglePatch), nameof(StopIfDisabled)));
        }

        // Whether this alarm type's audio should play right now: all of them
        // while vanilla audio is on, otherwise only the IR one, and only if
        // "Use Vanilla IR Missile Warning" is enabled. MissileAlarm.seekerType
        // is the game's own per-type key ("IR", "ARH", "SARH", ...), matching
        // MissileSeeker.GetSeekerType().
        private static bool AlarmAllowed(string seekerType)
        {
            return RwrScopeController.VanillaAudioEnabled
                || (RwrScopeController.UseVanillaIrWarning && seekerType == "IR");
        }

        private static bool SkipIfDisabled(string ___seekerType)
        {
            return AlarmAllowed(___seekerType);
        }

        private static void StopIfDisabled(AudioSource ___alarmSource, string ___seekerType)
        {
            if (!AlarmAllowed(___seekerType) && ___alarmSource != null && ___alarmSource.isPlaying)
            {
                ___alarmSource.Stop();
            }
        }
    }

    // SoundManager.PlayRadarWarningOneShot is public, so this one uses the
    // normal attribute-based patch (found by harmony.PatchAll() in
    // Plugin.Awake()) instead of AccessTools -- its only caller is
    // RadarWarning's own private radar-ping handler, for exactly the
    // contact/new-contact blip.
    [HarmonyPatch(typeof(SoundManager), nameof(SoundManager.PlayRadarWarningOneShot))]
    internal static class RadarWarningBlipTogglePatch
    {
        private static bool Prefix()
        {
            return RwrScopeController.VanillaAudioEnabled;
        }
    }
}
