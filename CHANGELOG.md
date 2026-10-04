# KaceyTronic-RWR Changelog

## 1.5.0 (2026-10-04)

### Added
- **KR-33 Agni support** (jsonKey `1509_palafighter1`) — defaults to Rank 2, shows as `K33` at Rank 1 and above and `F` on Rank 0 scopes. Counts as a fighter for Threat Tier Organization. Its quality override is an advanced ConfigManager entry like the other modded aircraft.
- **F/A-18 audio pack** (RWR Audio Pack, Audio) — a fourth custom sound set. Like War Thunder, it has a Targeting loop (a radar source has you targeted) and a separate Tracking loop (a radar-guided missile in flight) that takes priority over it, plus its own new-air, new-ground and ping sounds. It has no Launch Warning sound yet.
- **RWR Audio Volume** (Audio) — a 1-100 slider for the volume of the custom audio packs. Doesn't affect the vanilla RWR/missile audio. Defaults to 50.
- **Use Vanilla IR Missile Warning** (Audio) — while custom audio is on, keeps the game's own IR missile warning tone audible and stops the custom Launch Warning from firing for IR missiles. Off by default.
- **Threat Tier Organization toggle** (General, Ranks 1-4 only) — an alternate placement mode that groups contacts by threat level instead of true range, same idea as a real F-16 RWR: Aware near the outer edge, Critical on the half-range ring, Lethal just outside the center reticle. Bearing is always real either way. Regular contacts default to Aware, upgrade to Critical if targeted or flying a designated fighter airframe (Revoker, Vortex, Ifrit, Vagrant, plus the Shrike/Eclipse/King Viper/Strike Raptor mods), and upgrade to Lethal if actively guiding a missile onto you. ARH missile icons are always Lethal.

### Changed
- All audio settings now live in their own **Audio** section, right under General: Use Custom Audio, RWR Audio Pack, RWR Audio Volume and Use Vanilla IR Missile Warning. The first three moved from General (and "Vanilla RWR/Missile Audio" became the inverted "Use Custom Audio"), so they reset to their defaults once.
- VTOLVR pack's Tracking cue is now a proper start/stop loop (a one-shot lead-in followed by a seamless loop body), instead of a one-shot fired per radar ping. A SARH launcher's continuous illumination doesn't generate a repeatable ping the way a search radar does, so the old ping-driven cue could fall silent for the entire duration it was meant to represent. SARH's loop starts/ends with the missile-warning lifecycle (its only signal); ARH's starts on any radar ping at all and ends when that ping goes stale (contact lost), since it has a much more direct signal available. Never layers for multiple simultaneous inbound missiles, and stops only once the last one ends. KaceyTronic's Tracking is untouched (still the original ping-driven one-shot).

### Fixed
- RWR audio (both vanilla and the new custom KaceyTronic set) continuing to play after ejection/death. The scope never unsubscribed from the disabled aircraft's own radar/missile events, so it kept reacting to pings against a plane the player no longer controlled.
- TGT and MSL Billboard lights sometimes sticking on after death -- both are derived live from contact/threat-tracking state that's now explicitly cleared the moment the aircraft is disabled, instead of only ever being cleared on a full mission restart.

### Added
- **Use Custom Audio toggle** (Audio) — mutes the game's own default radar warning sounds (contact blip, new-contact blip) and the missile lock warning audio loop, and plays the selected custom audio pack instead. Off by default, so the game's own audio plays until you turn it on.
- **Custom RWR audio packs** — a full 5-sound replacement set (new aircraft contact, new ground/naval contact, repeat ping, missile launch warning, missile tracking tone) that plays instead of the vanilla audio whenever "Use Custom Audio" above is turned on, driven entirely by the scope's own contact/threat tracking rather than the vanilla game's own audio triggers. **RWR Audio Pack** (Audio) picks which set — KaceyTronic (default), VTOLVR, or War Thunder, with more planned.
- **War Thunder audio pack** — the most involved pack yet, with two independent loop tones instead of one: a Targeting loop starts the moment a radar source gives you a "red ping" and stops if that same source stops targeting you or its contact goes stale, while a separate Tracking loop covers an actual radar-guided missile in flight (any ARH ping, or a confirmed SARH launch — the same signal VTOLVR's own loop uses). A SARH launch transfers Targeting straight into Tracking for that source rather than running both at once, and Tracking always takes priority over Targeting generally -- Targeting is muted for as long as Tracking is playing and automatically resumes afterward if it's still logically active, rather than the two ever overlapping. Launch Warning also plays twice per detection (an immediate copy plus one scheduled repeat), with a fresh missile detection before that repeat fires restarting the count from the top instead of stacking. New aircraft/ground contacts and the repeat ping are deliberately all the same sound for this pack specifically.

### Fixed
- The "VTOLVR" entry in the RWR Audio Pack dropdown showing as spaced-out individual letters ("V T O L V R") -- ConfigManager's own display-name formatter inserts a space before every uppercase letter, not just at a lowercase-to-uppercase boundary, so a normal name like WarThunder reads fine but an all-caps acronym doesn't. Fixed with a `[Description]` attribute on that enum value, which ConfigManager checks first and uses verbatim.

## 1.4.1 (2026-09-18)

### Added
- Added Support for Aryx's F-22E Strike Raptor

## 1.4.0 (2026-08-30)

### Changed
- Threat Panel is now referred to as **Billboard**.
- The diagonal divider on the Hi/Lo on the default Billboard now stays off until a Hi or Lo lamp is triggered.

### Fixed
- MSL warning being stuck on after dying, in some cases.

### Added
- **Compact Billboard Toggle** — a smaller and more stylized version of the Billboard, if you prefer that.
- **RWR and Billboard Scale settings** (ConfigManager) — everything scales smoothly... or should.
- **Hide Minimap Option** — I have no mouth but I must LARP.
- **Now supports Aryx's new airframe, the OA-27 Cavalier** (I still think it shoulda been called the Weevil...) — has a Rank 0 RWR like the Cricket. Considering adding a unique rank just for it, but we'll see.

## 1.3.0 (2026-08-16)

### Added
- **Warning panel** — a new, separate small panel with four annunciator-style lights, positionable independently of the scope:
  - **TGT** — spikes (3 flashes, hold, fade to off) whenever a radar specifically targets you.
  - **MSL** — lights while an actual missile threat is active (SARH guidance or an ARH seeker's own radar ping).
  - **SEEN** — spikes whenever any radar detects you at all (mirrors the minimap's grey/yellow/red ping coloring), with a refreshable 5s hold instead of a hard restart.
  - **HI/LO** — a diagonally-split box showing whether the current priority contact is above or below you.
  - Runs its own ~3.65s boot self-test on every respawn (independent of the scope's own splash screen), and immediately aborts that animation if a genuine TGT lock or missile threat fires mid-sequence.
  - All lights are black/off by default and only show color while actually active. New "Warning Panel Toggle" and Warning Panel X/Y position sliders in ConfigManager.
- **Rank 0 corner indicators** — four small round lamps in the old-style quadrant scope's corners: **A/I** (any aircraft ping), **NVL** (any ship ping), **R9** (SARH missile guided by a radar truck or the mobile radar container), **T9** (SARH missile guided by a Boltstrike/RadarSAM1).
- **Playable Ships mod support** — added RWR quality and ship-style designation/rendering for SmallKarrier, LandingKraft, PatrolBote, Korvette1, Frickate1, Destroyer1_Player, AssaultKarrier, and FleetKarrier.

### Fixed
- TGT and HI/LO indicators (both the warning panel and the underlying priority-contact system) now work correctly at Rank 0 — previously silently disabled there.
- "Best Font" toggle now also applies to the warning panel's labels (previously only the scope itself).

## 1.2.0 (2026-08-13)

### Added
- **ARH missile detection via radar pings** — ARH missiles are now picked up the moment they ping you on radar, not just once `MissileWarning` fires. SARH detection is unchanged. The connecting line from missile icon to scope center still only appears once a `MissileWarning` lock is confirmed (Rank 0-3; always shown on Rank 4).
- **Rank 0 IR missile warning ring** — a thin secondary ring outside the main quadrant ring, hidden by default, that flashes in the quadrant an inbound heat-seeking missile is approaching from.
- **Rank 4 IR missile warning ring** — same warning ring as Rank 0, now also on Rank 4, with 8 directional divisions instead of 4 for finer bearing resolution.
- **"Use Simple Ship Designators" toggle** (General) — swaps realistic naval hull codes (e.g. FFL, FS) for simpler class-based ones (e.g. ARG for Argus, SHD for Shard).
- **"Enable Notch line display for every Rank" toggle** (General) — extends the notch line (previously Rank 4 only) to Ranks 1-3 when targeted by an emitter.
- **"Best Font" toggle** (Secrets, advanced) — switches the RWR's typeface to Arial. Purely cosmetic.

### Fixed
- Background opacity slider now actually affects the background (previously baked into the panel sprite and unresponsive to the slider).
- Notch line now points to whichever of +90°/-90° is closer to the player's current heading, instead of always adding 90° (which could point the "correct" direction behind the aircraft).

### Changed
- Scope text now uses the game's own map-grid font instead of Unity's default font, matching the CruiseMissile Waypoints mod's labels.
- IR missile warning ring color now follows the "Threat Secondary Color" setting instead of a fixed yellow.
