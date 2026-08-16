# KaceyTronic-RWR Changelog

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
