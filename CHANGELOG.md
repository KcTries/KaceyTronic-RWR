# KaceyTronic-RWR Changelog

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
