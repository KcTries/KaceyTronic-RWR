using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace TraditionalRWR
{
    // Builds a scope (ring + reticle) parented under the game's own
    // HUDCanvas (found via CombatHUD.iconLayer's Canvas ancestor) so it
    // renders through the same Canvas as the game's own HUD icons.
    // iconLayer itself is a tiny 100x100 box pinned to screen center, not
    // screen-sized, so we anchor against the Canvas's full-screen rect
    // instead of iconLayer's own rect.
    public class RwrScopeController : MonoBehaviour
    {
        private const int ScopeDiameter = 220;
        private const int PanelSize = ScopeDiameter + 40;

        // Off by default. Both dev tools below (WriteDebug and
        // DumpUnitDefinitionsOnce) are single-flag-gated rather than
        // physically removed, so a user can flip this live via ConfigManager
        // ("Secrets" -> "Dev Logging", see Plugin.cs) to capture a debug log
        // for troubleshooting, instead of needing a special debug build.
        public static bool DevToolsEnabled;

        // BepInEx\plugins\rwrdebug\ -- not the Desktop, since this ships to
        // other players now, not just the dev machine. A hardcoded
        // C:\Users\<dev>\Desktop\... path (the old location) would silently
        // fail to write anywhere on anyone else's PC, which would quietly
        // break the whole point of "turn Dev Logging on and send it to the
        // developer" for exactly the people who'd need it. Paths.PluginPath
        // (from BepInEx itself) always resolves correctly regardless of
        // where the game/mod is installed. The folder is created on first
        // write since File.AppendAllText/WriteAllText don't create one.
        private static readonly string DebugFilePath = Path.Combine(Paths.PluginPath, "rwrdebug", "rwrdebug.txt");
        private static readonly string UnitDefinitionsDumpPath = Path.Combine(Paths.PluginPath, "rwrdebug", "UnitDefsDump.txt");
        private bool _built;
        private float _nextLogTime;
        private RectTransform _scopeRoot;
        private bool _definitionsDumped;

        // Dev-only plain-text log, read directly off disk during
        // development instead of the BepInEx console. No-op when
        // DevToolsEnabled is off, so every call site below stays in place
        // as free documentation of what used to be traced.
        private static void WriteDebug(string message)
        {
            if (!DevToolsEnabled)
            {
                return;
            }

            try
            {
                // AppendAllText doesn't create missing directories itself --
                // CreateDirectory is a no-op if "rwrdebug" already exists.
                Directory.CreateDirectory(Path.GetDirectoryName(DebugFilePath));
                File.AppendAllText(DebugFilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // best-effort debug output only
            }
        }

        // Dev-only: one-shot reflection dump of every UnitDefinition field,
        // used to find radar-related fields without guessing names via
        // dnSpy rather than assuming names. No-op when DevToolsEnabled is
        // off.
        private void DumpUnitDefinitionsOnce()
        {
            if (!DevToolsEnabled || _definitionsDumped)
            {
                return;
            }

            UnitDefinition[] definitions = Resources.FindObjectsOfTypeAll<UnitDefinition>();
            if (definitions.Length == 0)
            {
                return;
            }

            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"Found {definitions.Length} UnitDefinition assets at {DateTime.Now:HH:mm:ss.fff}");
                sb.AppendLine();

                FieldInfo[] fields = typeof(UnitDefinition).GetFields(BindingFlags.Public | BindingFlags.Instance);

                foreach (UnitDefinition def in definitions)
                {
                    sb.AppendLine($"=== {def.name} ===");
                    foreach (FieldInfo field in fields)
                    {
                        object value;
                        try
                        {
                            value = field.GetValue(def);
                        }
                        catch (Exception ex)
                        {
                            value = $"<error: {ex.Message}>";
                        }

                        sb.AppendLine($"  {field.Name} ({field.FieldType.Name}) = {value}");
                    }
                    sb.AppendLine();
                }

                // Own file, not the shared debug log -- this is a large
                // one-shot snapshot, not an ongoing trace, and overwrites
                // on each dump rather than accumulating like WriteDebug does.
                Directory.CreateDirectory(Path.GetDirectoryName(UnitDefinitionsDumpPath));
                File.WriteAllText(UnitDefinitionsDumpPath, sb.ToString());
                _definitionsDumped = true;
                WriteDebug($"Dumped {definitions.Length} UnitDefinitions to {UnitDefinitionsDumpPath}");
            }
            catch (Exception ex)
            {
                WriteDebug($"EXCEPTION in DumpUnitDefinitionsOnce: {ex}");
            }
        }

        private void Update()
        {
            try
            {
                if (DevToolsEnabled && Time.unscaledTime >= _nextLogTime)
                {
                    _nextLogTime = Time.unscaledTime + 5f;
                    CombatHUD hudCheck = SceneSingleton<CombatHUD>.i;
                    WriteDebug($"Update: built={_built} hudExists={hudCheck != null} subscribed={_subscribed} contacts={_contacts.Count}");
                }

                DumpUnitDefinitionsOnce();

                if (_built)
                {
                    if (_scopeRoot == null)
                    {
                        // The HUD Canvas we were parented to is gone --
                        // likely a mission restart/reload. Reset and fall
                        // through to rebuild against whatever's current.
                        WriteDebug("Scope root lost, resetting to rebuild.");
                        ResetState();
                    }
                    else
                    {
                        UpdateFunnyModeColors();
                        if (BestFontEnabled != _lastBestFontEnabled)
                        {
                            _lastBestFontEnabled = BestFontEnabled;
                            RefreshAllLabelFonts();
                        }
                        EnsureSubscribed();
                        EnsureMissileWarningSubscribed();
                        UpdateContacts();
                        UpdateArhMissileContacts();
                        UpdateWarningPanel();
                        UpdateRank0CornerIndicators();
                        UpdateRank4NotchLines();
                        CleanupStaleIrMissiles();
                        UpdateIrWarningRing(_rank0IrArcImages, 0, ref _rank0IrRingVisible);
                        UpdateIrWarningRing(_rank4IrArcImages, 4, ref _rank4IrRingVisible);
                        UpdateJamGhostContacts();
                        UpdateJamLineOfBearing();
                        UpdateSplashScreen();
                        UpdateThemedStaticElements();
                        UpdateScopePosition();
                        UpdateWarningPanelPosition();
                        return;
                    }
                }

                CombatHUD hud = SceneSingleton<CombatHUD>.i;
                if (hud == null || hud.iconLayer == null || !hud.iconLayer.gameObject.activeInHierarchy)
                {
                    return;
                }

                Canvas hudCanvas = hud.iconLayer.GetComponentInParent<Canvas>();
                if (hudCanvas == null)
                {
                    return;
                }

                BuildScope(hudCanvas.transform);
                _built = true;
                WriteDebug($"Scope built under canvas '{hudCanvas.name}'.");
            }
            catch (Exception ex)
            {
                WriteDebug($"EXCEPTION in Update: {ex}");
            }
        }

        private const float NormalRingThickness = 3f;
        private const float NormalHalfRingThickness = 1.5f;
        // Thin so labels near the outer edge (everything's quadrant-snapped
        // out there) don't get lost against a thick ring stroke.
        private const float Rank0RingThickness = 1f;

        // 8 ticks just inside the ring, pointing at center -- Rank 2's only
        // visual difference from Rank 1.
        private static readonly float[] Rank2TickBearings = { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };
        private const float Rank2TickLength = 8f;
        private const float Rank2TickThickness = 2f;
        private const float Rank2TickInset = 6f;

        // Ranks 0 and 4 both: a thin secondary ring just outside the main
        // ring, split into 4 quadrants (Rank 0) or 8 finer divisions (Rank
        // 4 -- sharper gear resolves direction more precisely, same reason
        // Rank 2's ticks are 8-way while Rank 0's quadrants are 4-way).
        // Hidden by default; whichever division an inbound IR (heat-
        // seeking) missile is approaching from flashes. IR missiles have no
        // radar of their own for onRadarWarning to pick up, so this is
        // driven by MissileWarning instead (same reasoning as SARH).
        private const float IrRingDiameter = ScopeDiameter + 12f;
        private const float IrRingThickness = 2f;
        private const float IrFlashInterval = 0.15f;
        // Tied to ThreatFlashColor ("Threat Secondary Color" in
        // ConfigManager) rather than a fixed hue -- same field the SARH
        // flash reads, so this also rides along with Funny Mode's rainbow
        // cycling like everything else that reads it does.
        private static Color IrWarningColor => WithOpacity(new Color(ThreatFlashColor.r, ThreatFlashColor.g, ThreatFlashColor.b, 0.95f));

        // Rank 0 corner lamps -- inset well clear of the background's own
        // rounded corners (18f radius) so they read as sitting neatly in
        // the corner rather than overlapping the curve.
        private const float Rank0IndicatorDiameter = 28f;
        private const float Rank0IndicatorThickness = 2f;
        private const float Rank0IndicatorInset = 28f;
        private const float Rank0IndicatorCornerOffset = (PanelSize / 2f) - Rank0IndicatorInset;
        // A/I and NVL only -- a fixed 3-second hold per ping (passed
        // explicitly to Rank0IndicatorColor()). R9/T9 pass 0f instead, since
        // they track a live SARH-threat state rather than a discrete ping.
        private const float Rank0IndicatorPingHoldSeconds = 3f;
        private const float Rank0IndicatorFadeSeconds = 1f;
        private static Color Rank0IndicatorActiveColor => Themed(0.9f);

        // Rank 4: dotted line from center to the ring, plus a small bar at
        // the ring, toward anything locking the player or any inbound
        // missile. Flashes yellow/orange. PERF: rebuilt from scratch every
        // frame rather than pooled -- cheap only because the threat count
        // is always small; revisit if that stops being true.
        private const float Rank4NotchLineLength = ScopeDiameter / 2f;
        private const float Rank4NotchLineThickness = 1.5f;
        private const float Rank4NotchDashLength = 4f;
        private const float Rank4NotchDashGap = 3f;
        private const float Rank4NotchArcLength = 12f;
        private const float Rank4NotchArcThickness = 4f;
        private const float Rank4NotchFlashInterval = 0.15f;
        private static Color Rank4NotchColorYellow => WithOpacity(new Color(NotchPrimaryColor.r, NotchPrimaryColor.g, NotchPrimaryColor.b, 0.95f));
        private static Color Rank4NotchColorOrange => WithOpacity(new Color(NotchSecondaryColor.r, NotchSecondaryColor.g, NotchSecondaryColor.b, 0.95f));

        // Rank 3+ while jammed: thick dotted line to the jammer, ending in
        // an X on the ring. No flash. PERF: rebuilt every frame like the
        // other dynamic overlays -- fine at the small dash counts involved.
        private const float JamLobLineLength = ScopeDiameter / 2f;
        private const float JamLobLineThickness = 3f;
        private const float JamLobDashLength = 6f;
        private const float JamLobDashGap = 4f;
        private const float JamLobXSize = 10f;
        private const float JamLobXThickness = 2.5f;
        private static Color JamLobColor => WithOpacity(new Color(JamLobBaseColor.r, JamLobBaseColor.g, JamLobBaseColor.b, 0.95f));

        // Startup splash: shown briefly whenever the player's aircraft
        // (re)spawns, with the half-range ring and center reticle hidden
        // underneath it so it doesn't compete for the same space.
        private const float SplashDisplaySeconds = 2.5f;
        private const float SplashFadeOutSeconds = 0.5f;
        // Rank 4's multi-line status reveals one line at a time at this
        // cadence, like a live boot sequence; every other rank's single
        // line just shows immediately (it's already "line 1 of 1").
        private const float SplashStatusLineIntervalSeconds = 0.6f;
        private static Color SplashTitleColor => Themed(0.95f);
        private static Color SplashSubtitleColor => Themed(0.85f);
        private static Color SplashVersionColor => Themed(0.7f);
        private static Color SplashStatusColor => Themed(0.7f);

        private class SplashContent
        {
            public string Title;
            public FontStyle TitleStyle;
            public string Subtitle;
            public string Version;
            public string[] StatusLines;
        }

        private static readonly SplashContent DefaultSplashContent = new SplashContent
        {
            Title = "Kaceytronic",
            TitleStyle = FontStyle.BoldAndItalic,
            Subtitle = "Enhanced On-Visor RWR",
            Version = "v 1.0.3",
            StatusLines = new[] { "System Booting..." },
        };

        // Per-rank overrides, keyed by RWR quality. Falls back to
        // DefaultSplashContent for anything not listed here.
        private static readonly Dictionary<int, SplashContent> SplashContentByRank = new Dictionary<int, SplashContent>
        {
            {
                0, new SplashContent
                {
                    Title = "Kaceytronic",
                    TitleStyle = FontStyle.Italic,
                    Subtitle = "On-Visor RWR",
                    Version = "v 2.1.6",
                    StatusLines = new[] { "Starting Up..." },
                }
            },
            {
                1, new SplashContent
                {
                    Title = "Kaceytronic",
                    TitleStyle = FontStyle.BoldAndItalic,
                    Subtitle = "On-Visor RWR",
                    Version = "v 1.3.1",
                    StatusLines = new[] { "Booting..." },
                }
            },
            {
                2, new SplashContent
                {
                    Title = "Kaceytronic",
                    TitleStyle = FontStyle.BoldAndItalic,
                    Subtitle = "On-Visor RWR",
                    Version = "v 1.9.5",
                    StatusLines = new string[0],
                }
            },
            {
                3, new SplashContent
                {
                    Title = "Kaceytronic",
                    TitleStyle = FontStyle.BoldAndItalic,
                    Subtitle = "Enhanced On-Visor RWR",
                    Version = "v 0.3.1",
                    StatusLines = new[] { "System Booting..." },
                }
            },
            {
                4, new SplashContent
                {
                    Title = "Kaceytronic",
                    TitleStyle = FontStyle.BoldAndItalic,
                    Subtitle = "Enhanced On-Visor RWR",
                    Version = "v 0.4.1a",
                    StatusLines = new[] { "Connected to Datalink...", "Computing... Done!", "System Booting... Done!" },
                }
            },
        };

        private SplashContent GetSplashContent()
        {
            if (SplashContentByRank.TryGetValue(_currentRwrQuality, out SplashContent content))
            {
                return content;
            }
            return DefaultSplashContent;
        }

        private RectTransform _normalOverlayRoot;
        private RectTransform _normalInnerElements;
        private RectTransform _rank0OverlayRoot;
        private RectTransform _rank0InnerElements;
        private RectTransform _rank2TicksOverlayRoot;
        // Built once, unlike contacts which get recolored every frame --
        // kept so UpdateThemedStaticElements() can retint these live when
        // the user changes RWR color/opacity in ConfigManager.
        private Image _backgroundImage;
        private Image _normalRingImage;
        private Image _normalHalfRingImage;
        private Image _normalReticleHorizontalImage;
        private Image _normalReticleVerticalImage;
        private Image _rank0RingImage;
        private Image _rank0HalfRingImage;
        private Image _rank0CrossHorizontalImage;
        private Image _rank0CrossVerticalImage;

        // Rank 0 only: four small "old style" round lamps in the panel's
        // corners -- A/I (Air Intercept, any aircraft ping), NVL (Naval,
        // any ship ping), R9 (either radar truck or the mobile radar
        // container), T9 (RadarSAM1/Boltstrike). Independent of the
        // TGT/MSL/SEEN/HI-LO warning panel -- own placement, own trigger
        // logic, own theme-colored (not threat-colored) look. Black by
        // default; each just tracks its own LastPing time and derives its
        // current color from that every frame (see UpdateRank0CornerIndicators()).
        private Image _airInterceptBorder;
        private Text _airInterceptLabel;
        private float _airInterceptLastPing = float.NegativeInfinity;
        private Image _navalBorder;
        private Text _navalLabel;
        private float _navalLastPing = float.NegativeInfinity;
        private Image _radarTruckBorder;
        private Text _radarTruckLabel;
        private float _radarTruckLastPing = float.NegativeInfinity;
        private Image _boltstrikeBorder;
        private Text _boltstrikeLabel;
        private float _boltstrikeLastPing = float.NegativeInfinity;
        // One shared arc sprite per ring (spans division 0's own slice),
        // reused across all instances of that ring via rotation -- same
        // sprite-reuse reasoning as the other cached shapes further down.
        // Rank 0 (4-way, 90 degrees/division) and Rank 4 (8-way, 45
        // degrees/division) need their own sprite each -- the arc's shape
        // itself differs, not just how many times it's rotated around.
        private Sprite _rank0IrArcSprite;
        private readonly Image[] _rank0IrArcImages = new Image[4];
        private Sprite _rank4IrArcSprite;
        private readonly Image[] _rank4IrArcImages = new Image[8];
        private RectTransform _rank4IrOverlayRoot;
        private readonly List<Image> _rank2TickImages = new List<Image>();
        private RectTransform _rank4NotchOverlayRoot;
        private readonly List<GameObject> _rank4NotchLines = new List<GameObject>();
        private RectTransform _jamLobOverlayRoot;
        private readonly List<GameObject> _jamLobLines = new List<GameObject>();
        // Every dynamic contact (real, ARH missile, jam ghost) parents here
        // instead of directly under _scopeRoot, so the whole layer can be
        // hidden in one shot (e.g. during the splash).
        private RectTransform _contactsOverlayRoot;
        // Rank 1+ only: hollow diamond that tracks whichever contact
        // currently has priority (see UpdatePriorityDiamond()).
        private Image _priorityDiamondImage;
        private RectTransform _splashOverlayRoot;
        private Text _splashTitleText;
        private Text _splashSubtitleText;
        private Text _splashVersionText;
        private Text _splashStatusText;
        private string[] _splashStatusLines;
        private bool _splashActive;
        private float _splashStartTime;

        // Separate small panel, same width as the scope, with four
        // annunciator-style lights: TGT (someone's radar has you as its
        // specific target), MSL (an actual missile threat -- SARH guidance
        // or an ARH seeker's own radar ping), SEEN (a radar ping detected
        // you at all, mirroring the minimap's grey/yellow/red ping colors),
        // and a HI/LO split box (whether the current priority contact is
        // above or below the player). Built and positioned independently of
        // _scopeRoot so it can be repositioned on its own. No fill on any
        // of them -- just a border+text pair, transparent interior so the
        // panel background shows through.
        private RectTransform _warningPanelRoot;
        private Image _warningPanelBackground;
        private Image _tgtLightBorder;
        private Text _tgtLightLabel;
        private Image _mslLightBorder;
        private Text _mslLightLabel;
        private Image _seenLightBorder;
        private Text _seenLightLabel;
        // Diagonal divider is a separate, always-idle-colored static
        // element (like a fixed reticle) -- HI and LO each get their own
        // independently-colorable L-shaped border piece instead, so they
        // can light up individually without fighting over a shared frame.
        private Image _hiLoDiagonal;
        private Image _hiBorder;
        private Text _hiLabel;
        private Image _loBorder;
        private Text _loLabel;
        // Set alongside the priority diamond in UpdatePriorityDiamond() --
        // the HI/LO indicator needs the actual Unit (for its world
        // position), not just the TrackedContact the diamond itself uses.
        private Unit _priorityEmitter;

        // One-shot boot self-test, independent of the scope's own splash
        // (own timing, not tied to SplashDisplaySeconds) -- retriggered
        // every respawn from EnsureSubscribed(), same as ShowSplashScreen().
        // All lights sit off (black), then each one is tested in turn (panel
        // reading order: TGT, MSL, SEEN, HI/LO together) by stepping through
        // every color it can actually display, ending back on off -- lights
        // not yet reached and lights already done both just sit off, so at
        // most one light shows a non-off color at any given moment.
        private enum WarningPanelStartupPhase { Black, TestingTgt, TestingMsl, TestingSeen, TestingHiLo, Done }
        private WarningPanelStartupPhase _startupPhase = WarningPanelStartupPhase.Done;
        private float _startupPhaseStartTime;
        private const float StartupBlackSeconds = 0.5f;
        private const float StartupColorStepSeconds = 0.35f;

        // TGT and SEEN are one-shot "spike" alerts rather than plain state
        // lights: triggered directly from OnRadarWarningReceived every time
        // a relevant ping arrives (see TriggerSpike()), not from a polled
        // state transition -- so there's no "cooldown" to wait out, a fresh
        // ping restarts the flash from scratch even mid-animation. Each
        // flashes theme/threat color a few times, holds solid threat color,
        // then hard-cuts back to the idle theme-colored look. Identical
        // shape for both, just a different active color and trigger
        // condition, so the state machine itself is shared.
        private enum SpikeLightPhase { Idle, Flashing, Holding }
        private struct SpikeLightState
        {
            public SpikeLightPhase Phase;
            public float PhaseStartTime;
        }
        private SpikeLightState _tgtLightState;
        private SpikeLightState _seenLightState;

        private void BuildScope(Transform canvasTransform)
        {
            // Every ring/reticle/tick/diamond built below bakes in a color
            // via Themed()/WithOpacity() at construction time -- without
            // this, they'd bake in whatever ThemeColor etc. happened to
            // still hold (stale hardcoded defaults on the very first build,
            // or the previous mission's colors on a rebuild), since
            // UpdateFunnyModeColors() otherwise only runs from the *next*
            // Update() tick onward, one frame after BuildScope() already ran.
            UpdateFunnyModeColors();

            _scopeRoot = BuildScopeRoot(canvasTransform);
            BuildBackground(_scopeRoot);
            BuildWarningPanel(canvasTransform);

            _normalOverlayRoot = BuildOverlayRoot(_scopeRoot, "NormalOverlay");
            _normalRingImage = BuildRing(_normalOverlayRoot, NormalRingThickness);
            _normalInnerElements = BuildOverlayRoot(_normalOverlayRoot, "NormalInnerElements");
            _normalHalfRingImage = BuildHalfRangeRing(_normalInnerElements, NormalHalfRingThickness);
            (_normalReticleHorizontalImage, _normalReticleVerticalImage) = BuildPlaneIcon(_normalInnerElements);

            _rank0OverlayRoot = BuildOverlayRoot(_scopeRoot, "Rank0Overlay");
            _rank0RingImage = BuildRing(_rank0OverlayRoot, Rank0RingThickness);
            _rank0InnerElements = BuildOverlayRoot(_rank0OverlayRoot, "Rank0InnerElements");
            _rank0HalfRingImage = BuildHalfRangeRing(_rank0InnerElements, Rank0RingThickness);
            (_rank0CrossHorizontalImage, _rank0CrossVerticalImage) = BuildFullCross(_rank0InnerElements);
            BuildIrWarningRing(_rank0OverlayRoot, ref _rank0IrArcSprite, _rank0IrArcImages, "Rank0IrArc");
            BuildRank0CornerIndicators(_rank0OverlayRoot);

            _rank2TicksOverlayRoot = BuildOverlayRoot(_scopeRoot, "Rank2TicksOverlay");
            BuildRank2Ticks(_rank2TicksOverlayRoot);

            // Rank 4 lines are built dynamically each frame, not here --
            // this is just the container they get parented under.
            _rank4NotchOverlayRoot = BuildOverlayRoot(_scopeRoot, "Rank4NotchOverlay");

            _rank4IrOverlayRoot = BuildOverlayRoot(_scopeRoot, "Rank4IrOverlay");
            BuildIrWarningRing(_rank4IrOverlayRoot, ref _rank4IrArcSprite, _rank4IrArcImages, "Rank4IrArc");

            // Also built dynamically each frame, not here -- just its container.
            _jamLobOverlayRoot = BuildOverlayRoot(_scopeRoot, "JamLobOverlay");

            _contactsOverlayRoot = BuildOverlayRoot(_scopeRoot, "ContactsOverlay");
            _priorityDiamondImage = BuildPriorityDiamond(_contactsOverlayRoot);

            _splashOverlayRoot = BuildOverlayRoot(_scopeRoot, "SplashOverlay");
            BuildSplashScreen(_splashOverlayRoot);
            SetSplashVisible(false);

            ApplyOverlayVisibility();
        }

        private void BuildRank2Ticks(RectTransform parent)
        {
            float radius = (ScopeDiameter / 2f) - Rank2TickInset;

            foreach (float bearing in Rank2TickBearings)
            {
                GameObject tickObject = new GameObject("Rank2Tick", typeof(RectTransform), typeof(Image));
                RectTransform rect = tickObject.GetComponent<RectTransform>();
                rect.SetParent(parent, false);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(Rank2TickThickness, Rank2TickLength);
                rect.anchoredPosition = BearingToDirection(bearing) * radius;
                rect.localRotation = Quaternion.Euler(0f, 0f, -bearing);

                Image image = tickObject.GetComponent<Image>();
                image.color = Themed(0.8f);
                image.raycastTarget = false;
                _rank2TickImages.Add(image);
            }
        }

        // Full-rect passthrough so children anchored (0.5,0.5) center on
        // the scope exactly as if parented directly to it, while still
        // letting us toggle an entire ring/reticle set on or off as a unit.
        private RectTransform BuildOverlayRoot(RectTransform parent, string name)
        {
            GameObject overlayObject = new GameObject(name, typeof(RectTransform));
            RectTransform rect = overlayObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        // Quality-dependent overlay sets are built once and swapped by
        // visibility rather than rebuilt, since RWR quality can change
        // mid-mission (aircraft swap).
        private void ApplyOverlayVisibility()
        {
            if (_normalOverlayRoot != null)
            {
                _normalOverlayRoot.gameObject.SetActive(_currentRwrQuality != 0);
            }
            if (_rank0OverlayRoot != null)
            {
                _rank0OverlayRoot.gameObject.SetActive(_currentRwrQuality == 0);
            }
            if (_rank2TicksOverlayRoot != null)
            {
                _rank2TicksOverlayRoot.gameObject.SetActive(_currentRwrQuality == 2);
            }
            if (_rank4NotchOverlayRoot != null)
            {
                _rank4NotchOverlayRoot.gameObject.SetActive(ShouldShowNotchLine(_currentRwrQuality));
            }
        }

        // Rings/reticle/ticks are built once and never touched again
        // elsewhere, so retint them here each frame -- otherwise a live
        // color/opacity change wouldn't show up until the next respawn.
        private void UpdateThemedStaticElements()
        {
            if (_backgroundImage != null)
            {
                _backgroundImage.color = WithOpacity(BackgroundBaseColor);
            }
            if (_normalRingImage != null)
            {
                _normalRingImage.color = Themed(0.8f);
            }
            if (_normalHalfRingImage != null)
            {
                _normalHalfRingImage.color = Themed(0.5f);
            }
            if (_normalReticleHorizontalImage != null)
            {
                _normalReticleHorizontalImage.color = Themed(0.9f);
            }
            if (_normalReticleVerticalImage != null)
            {
                _normalReticleVerticalImage.color = Themed(0.9f);
            }
            if (_rank0RingImage != null)
            {
                _rank0RingImage.color = Themed(0.8f);
            }
            if (_rank0HalfRingImage != null)
            {
                _rank0HalfRingImage.color = Themed(0.5f);
            }
            if (_rank0CrossHorizontalImage != null)
            {
                _rank0CrossHorizontalImage.color = Themed(0.9f);
            }
            if (_rank0CrossVerticalImage != null)
            {
                _rank0CrossVerticalImage.color = Themed(0.9f);
            }
            foreach (Image tick in _rank2TickImages)
            {
                if (tick != null)
                {
                    tick.color = Themed(0.8f);
                }
            }
            // Not colored here -- UpdatePriorityDiamond() now sets its color
            // every frame to match whatever contact it's currently over.
            if (_warningPanelBackground != null)
            {
                _warningPanelBackground.color = WithOpacity(BackgroundBaseColor);
            }
            // The HI/LO diagonal divider is never touched elsewhere -- it
            // has no active/inactive state of its own (that's on the HI/LO
            // borders+labels individually), so it just sits at the idle
            // look permanently and only needs live theme/opacity sync.
            if (_hiLoDiagonal != null)
            {
                _hiLoDiagonal.color = WarningLightIdleColor;
            }
            // TGT/MSL/SEEN border+text and HI/LO border+label colors are
            // not re-tinted here -- UpdateWarningPanel() (via
            // UpdateSpikeLight()/UpdateHiLoIndicator()/ApplyLightColor())
            // already recomputes them every frame from live state, and runs
            // earlier in Update() than this method -- touching them here
            // too would stomp that frame's animated color right back to a
            // static one.
        }

        private void BuildSplashScreen(RectTransform parent)
        {
            // Text here is just placeholder -- ShowSplashScreen() fills in
            // the real per-rank strings every time the splash appears.
            // This only establishes fonts/positions/styles once.
            _splashTitleText = CreateLabel(parent, DefaultSplashContent.Title, new Vector2(0f, 16f), 18, SplashTitleColor,
                FontStyle.BoldAndItalic, 190f, 24f);
            _splashSubtitleText = CreateLabel(parent, DefaultSplashContent.Subtitle, new Vector2(0f, -4f), 10, SplashSubtitleColor,
                FontStyle.Normal, 190f, 14f);
            _splashVersionText = CreateLabel(parent, DefaultSplashContent.Version, new Vector2(0f, -18f), 8, SplashVersionColor,
                FontStyle.Normal, 120f, 12f);
            // Tall enough to fit Rank 4's 3-line staged status without
            // clipping; single-line ranks just render centered within it.
            _splashStatusText = CreateLabel(parent, string.Empty, new Vector2(0f, -32f), 7, SplashStatusColor,
                FontStyle.Italic, 170f, 30f);
        }

        private void SetSplashVisible(bool visible)
        {
            if (_splashOverlayRoot != null)
            {
                _splashOverlayRoot.gameObject.SetActive(visible);
            }
        }

        private void SetInnerElementsVisible(bool visible)
        {
            if (_normalInnerElements != null)
            {
                _normalInnerElements.gameObject.SetActive(visible);
            }
            if (_rank0InnerElements != null)
            {
                _rank0InnerElements.gameObject.SetActive(visible);
            }
        }

        // Whenever the player's aircraft (re)spawns -- called from
        // EnsureSubscribed on every fresh subscribe, which already covers
        // both the first spawn and every respawn/aircraft swap after.
        private void ShowSplashScreen()
        {
            _splashActive = true;
            _splashStartTime = Time.unscaledTime;

            SplashContent content = GetSplashContent();
            if (_splashTitleText != null)
            {
                _splashTitleText.text = content.Title;
                _splashTitleText.fontStyle = content.TitleStyle;
            }
            if (_splashSubtitleText != null)
            {
                _splashSubtitleText.text = content.Subtitle;
            }
            _splashStatusLines = content.StatusLines;

            // Rank 4 only, rolled fresh per spawn. Rare easter egg takes
            // priority over the update-nag swap (both editing a clone, not
            // the shared static array in SplashContentByRank). Decided
            // before the version roll below, since the rare egg blanks the
            // version entirely -- its own roll still stays a separate,
            // independent Random call from the version roll, it just gets
            // final say over what the version field shows.
            bool rareEasterEggActive = false;
            if (_currentRwrQuality == 4 && _splashStatusLines.Length > 0)
            {
                if (UnityEngine.Random.value < 0.01f)
                {
                    _splashStatusLines = new[] { "Pa pa...", "Tu Tu... Tu Tu... Tu", "Wa wa!" };
                    rareEasterEggActive = true;
                }
                else if (UnityEngine.Random.value < 0.25f)
                {
                    string[] lines = (string[])_splashStatusLines.Clone();
                    lines[0] = "SW Update Required! Contact Maintainer";
                    _splashStatusLines = lines;
                }
            }

            if (_splashVersionText != null)
            {
                _splashVersionText.text = rareEasterEggActive ? string.Empty : RollSplashVersionText(content.Version);
            }

            UpdateSplashStatusText(0f);

            SetSplashVisible(true);
            SetInnerElementsVisible(false);
            SetContactsVisible(false);
            ApplySplashAlpha(1f);
        }

        private static readonly string[] JokeSplashVersions =
        {
            "Unrecognized Version -- Contact Maintainer",
            "v69.67.420",
            "v3.2.02",
        };

        // Every rank, rolled fresh per spawn: 5% chance the real version
        // string is swapped for one of three joke alternates instead.
        private static string RollSplashVersionText(string realVersion)
        {
            if (UnityEngine.Random.value < 0.05f)
            {
                return JokeSplashVersions[UnityEngine.Random.Range(0, JokeSplashVersions.Length)];
            }
            return realVersion;
        }

        // Rank 4's status reveals one line at a time (a staged boot
        // sequence); every other rank's single line is just "line 1 of 1",
        // shown immediately -- same code path either way.
        private void UpdateSplashStatusText(float age)
        {
            if (_splashStatusText == null)
            {
                return;
            }

            int totalLines = _splashStatusLines != null ? _splashStatusLines.Length : 0;
            if (totalLines == 0)
            {
                _splashStatusText.text = string.Empty;
                return;
            }

            int linesToShow = Mathf.Min(totalLines, 1 + Mathf.FloorToInt(age / SplashStatusLineIntervalSeconds));
            _splashStatusText.text = string.Join("\n", _splashStatusLines, 0, linesToShow);
        }

        // Also covers the Rank 4 notch line and jamming line-of-bearing --
        // both run full-radius from center to the ring, so they'd cross
        // straight through the splash text if left showing.
        private void SetContactsVisible(bool visible)
        {
            if (_contactsOverlayRoot != null)
            {
                _contactsOverlayRoot.gameObject.SetActive(visible);
            }
            if (_rank4NotchOverlayRoot != null)
            {
                // Only re-enable if the current rank would actually show it --
                // ApplyOverlayVisibility already gates this per-rank, so
                // hiding unconditionally is safe but re-showing must respect it.
                _rank4NotchOverlayRoot.gameObject.SetActive(visible && ShouldShowNotchLine(_currentRwrQuality));
            }
            if (_jamLobOverlayRoot != null)
            {
                _jamLobOverlayRoot.gameObject.SetActive(visible);
            }
        }

        private void UpdateSplashScreen()
        {
            if (!_splashActive)
            {
                return;
            }

            float age = Time.unscaledTime - _splashStartTime;
            float totalDuration = SplashDisplaySeconds + SplashFadeOutSeconds;

            if (age > totalDuration)
            {
                _splashActive = false;
                SetSplashVisible(false);
                SetInnerElementsVisible(true);
                SetContactsVisible(true);
                return;
            }

            UpdateSplashStatusText(age);

            float alpha = age > SplashDisplaySeconds
                ? Mathf.Clamp01(1f - ((age - SplashDisplaySeconds) / SplashFadeOutSeconds))
                : 1f;
            ApplySplashAlpha(alpha);
        }

        private void ApplySplashAlpha(float alpha)
        {
            if (_splashTitleText != null)
            {
                Color color = SplashTitleColor;
                color.a *= alpha;
                _splashTitleText.color = color;
            }
            if (_splashSubtitleText != null)
            {
                Color color = SplashSubtitleColor;
                color.a *= alpha;
                _splashSubtitleText.color = color;
            }
            if (_splashVersionText != null)
            {
                Color color = SplashVersionColor;
                color.a *= alpha;
                _splashVersionText.color = color;
            }
            if (_splashStatusText != null)
            {
                Color color = SplashStatusColor;
                color.a *= alpha;
                _splashStatusText.color = color;
            }
        }

        // Base color/alpha before ThemeOpacity is applied -- was previously
        // baked straight into the sprite's own texture (see
        // CreateRoundedRectSprite's fillColor param), which is why the
        // opacity slider never touched it: every other shape here (rings,
        // reticle, diamond) bakes a plain white mask and drives its actual
        // color through Image.color instead, which is the only thing
        // UpdateThemedStaticElements() can retint live.
        private static readonly Color BackgroundBaseColor = new Color(0.02f, 0.05f, 0.03f, 0.5f);

        private void BuildBackground(RectTransform parent)
        {
            GameObject backgroundObject = new GameObject("Background", typeof(RectTransform), typeof(Image));
            RectTransform rect = backgroundObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Image image = backgroundObject.GetComponent<Image>();
            image.sprite = CreateRoundedRectSprite(PanelSize, PanelSize, 18f, Color.white);
            image.color = WithOpacity(BackgroundBaseColor);
            image.raycastTarget = false;
            _backgroundImage = image;
        }

        // Set from Plugin.Awake() ("RWR Position" section) and live-updated
        // via UpdateScopePosition(). Defaults match the original hardcoded
        // position, so an untouched install looks unchanged.
        public static float ScopePositionX = 0f;
        public static float ScopePositionY = 446f;

        // Set from Plugin.Awake() ("Warning Panel Position" section) and
        // live-updated via UpdateWarningPanelPosition(). Defaults stack the
        // panel directly above the scope (ScopePositionY's default +
        // PanelSize + a small gap).
        public static float WarningPanelPositionX = 0f;
        public static float WarningPanelPositionY = 716f;

        // Set from Plugin.Awake() ("General" section), live-updated in
        // ConfigManager. Panel is always built; this just toggles it active.
        public static bool ExtraPanelEnabled = true;

        private RectTransform BuildScopeRoot(Transform parent)
        {
            GameObject rootObject = new GameObject("ScopeRoot", typeof(RectTransform));
            RectTransform rect = rootObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);

            // Bottom-left pivot so X/Y are plain screen coordinates. Default
            // (0, 446) sits flush against the left edge, just above the
            // minimap's measured top edge (y=426.4).
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.sizeDelta = new Vector2(PanelSize, PanelSize);
            rect.anchoredPosition = new Vector2(ScopePositionX, ScopePositionY);

            return rect;
        }

        // Shared by every panel's own position-update method so Dealer Mode
        // (Secrets, see Plugin.cs) bounces all of them in sync rather than
        // just the scope -- both panels are the same width (PanelSize) and
        // share the bottom-left pivot convention, so the same scale/
        // X-compensation applies unchanged to each.
        private static void ComputeDealerModeSquish(out float scaleX, out float scaleY)
        {
            scaleX = 1f;
            scaleY = 1f;
            if (!DealerModeEnabled)
            {
                return;
            }

            // One full squish-and-release cycle per beat. Cosine-based so
            // it starts and ends each cycle at squishT=0 (full height)
            // rather than jumping straight into the squish.
            float bounceHz = DealerModeBpm / 60f;
            float squishT = (1f - Mathf.Cos(Time.unscaledTime * bounceHz * 2f * Mathf.PI)) / 2f;

            scaleY = Mathf.Lerp(1f, DealerModeMinScaleY, squishT);
            scaleX = Mathf.Lerp(1f, DealerModeMaxScaleX, squishT);
        }

        private void UpdateScopePosition()
        {
            if (_scopeRoot == null)
            {
                return;
            }

            ComputeDealerModeSquish(out float scaleX, out float scaleY);

            // _scopeRoot's pivot is bottom-left (see BuildScopeRoot), so
            // scaling Y already keeps the bottom edge fixed for free --
            // only the top comes down. X needs a compensating shift,
            // though (zero when Dealer Mode is off, since scaleX is then
            // exactly 1), or widening would only grow the panel rightward
            // off the left edge instead of bulging out symmetrically
            // around its horizontal center.
            float compensatedX = ScopePositionX + (PanelSize / 2f) * (1f - scaleX);

            _scopeRoot.anchoredPosition = new Vector2(compensatedX, ScopePositionY);
            _scopeRoot.localScale = new Vector3(scaleX, scaleY, 1f);
        }

        private const float WarningPanelHeight = 110f;
        private const float WarningLightWidth = 90f;
        private const float WarningLightHeight = 42f;
        private const float WarningLightGap = 6f;
        private const float WarningLightCornerRadius = 4f;
        private const float WarningLightBorderThickness = 2f;
        private const float WarningLightInsetX = 16f;
        // Left pair (TGT/MSL) stacked and vertically centered against the
        // left edge; right pair (SEEN/HI-LO) mirrors the same inset against
        // the right edge instead.
        private const float WarningLightOffsetX = -(PanelSize / 2f) + WarningLightInsetX + (WarningLightWidth / 2f);
        private const float WarningLightOffsetY = (WarningLightGap / 2f) + (WarningLightHeight / 2f);
        private const float WarningLightRightOffsetX = -WarningLightOffsetX;

        // Resting/inactive look for every indicator's border+text -- fully
        // off (black) rather than dimly lit, so a light only ever shows
        // color while its own event is actually firing.
        private static Color WarningLightOffColor => WithOpacity(Color.black);
        // Theme color, used only by the HI/LO diagonal divider -- that's a
        // static structural element (like the panel background), not an
        // indicator with its own on/off state, so it keeps the old dim
        // theme-colored look instead of going black with the rest.
        private static Color WarningLightIdleColor => Themed(0.7f);
        // SEEN's active color -- same shape as TargetedColor, but off
        // Threat Secondary instead of Threat Primary.
        private static Color SeenColor => WithOpacity(new Color(ThreatFlashColor.r, ThreatFlashColor.g, ThreatFlashColor.b, 0.95f));

        private const float TgtFlashInterval = 0.15f;
        private const int TgtFlashCount = 3;
        // Solid hold after the flashes finish, then a hard cut (no fade)
        // straight back to WarningLightOffColor.
        private const float TgtHoldSeconds = 3.5f;
        // SEEN's hold is refreshed rather than restarted by pings that
        // arrive mid-hold -- see TriggerSeenPing().
        private const float SeenHoldSeconds = 5f;

        private const float HiLoDiagonalThickness = 2f;
        // How far each border half pulls back from the diagonal centerline
        // -- see CreateHiLoBorderHalfSprite.
        private const float HiLoGapMargin = 3f;
        // Quarter-offset into each triangular half so the label sits
        // roughly centered in its own half rather than the shared box center.
        private const float HiLoLabelOffsetX = WarningLightWidth / 4f;
        private const float HiLoLabelOffsetY = WarningLightHeight / 4f;

        private void BuildWarningPanel(Transform canvasTransform)
        {
            GameObject rootObject = new GameObject("WarningPanelRoot", typeof(RectTransform));
            RectTransform rect = rootObject.GetComponent<RectTransform>();
            rect.SetParent(canvasTransform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.sizeDelta = new Vector2(PanelSize, WarningPanelHeight);
            rect.anchoredPosition = new Vector2(WarningPanelPositionX, WarningPanelPositionY);
            _warningPanelRoot = rect;

            GameObject backgroundObject = new GameObject("Background", typeof(RectTransform), typeof(Image));
            RectTransform backgroundRect = backgroundObject.GetComponent<RectTransform>();
            backgroundRect.SetParent(_warningPanelRoot, false);
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;
            _warningPanelBackground = backgroundObject.GetComponent<Image>();
            _warningPanelBackground.sprite = CreateRoundedRectSprite(PanelSize, Mathf.RoundToInt(WarningPanelHeight), 18f, Color.white);
            _warningPanelBackground.color = WithOpacity(BackgroundBaseColor);
            _warningPanelBackground.raycastTarget = false;

            (_tgtLightBorder, _tgtLightLabel) = BuildWarningLight(_warningPanelRoot, "TGT", new Vector2(WarningLightOffsetX, WarningLightOffsetY));
            (_mslLightBorder, _mslLightLabel) = BuildWarningLight(_warningPanelRoot, "MSL", new Vector2(WarningLightOffsetX, -WarningLightOffsetY));
            (_seenLightBorder, _seenLightLabel) = BuildWarningLight(_warningPanelRoot, "SEEN", new Vector2(WarningLightRightOffsetX, WarningLightOffsetY));
            BuildHiLoIndicator(_warningPanelRoot, new Vector2(WarningLightRightOffsetX, -WarningLightOffsetY));
        }

        private void BuildHiLoIndicator(RectTransform parent, Vector2 position)
        {
            GameObject diagonalObject = new GameObject("HiLoDiagonal", typeof(RectTransform), typeof(Image));
            RectTransform diagonalRect = diagonalObject.GetComponent<RectTransform>();
            diagonalRect.SetParent(parent, false);
            diagonalRect.anchorMin = new Vector2(0.5f, 0.5f);
            diagonalRect.anchorMax = new Vector2(0.5f, 0.5f);
            diagonalRect.pivot = new Vector2(0.5f, 0.5f);
            diagonalRect.sizeDelta = new Vector2(WarningLightWidth, WarningLightHeight);
            diagonalRect.anchoredPosition = position;
            _hiLoDiagonal = diagonalObject.GetComponent<Image>();
            _hiLoDiagonal.sprite = CreateHiLoDiagonalSprite(Mathf.RoundToInt(WarningLightWidth), Mathf.RoundToInt(WarningLightHeight), HiLoDiagonalThickness);
            _hiLoDiagonal.color = WarningLightIdleColor;
            _hiLoDiagonal.raycastTarget = false;

            // Diagonal runs top-left to bottom-right, splitting the box
            // into a top-right triangle (HI) and bottom-left triangle (LO)
            // -- each half gets its own border piece (see
            // CreateHiLoBorderHalfSprite) so it can light up independently,
            // and its label nudged well clear of the diagonal into its half.
            _hiBorder = BuildHiLoBorderHalf(parent, "HiBorder", position, upperRightHalf: true);
            _loBorder = BuildHiLoBorderHalf(parent, "LoBorder", position, upperRightHalf: false);

            _hiLabel = CreateLabel(parent, "HI", position + new Vector2(HiLoLabelOffsetX, HiLoLabelOffsetY), 14, WarningLightOffColor, FontStyle.Bold, WarningLightWidth / 2f, WarningLightHeight / 2f);
            _loLabel = CreateLabel(parent, "LO", position + new Vector2(-HiLoLabelOffsetX, -HiLoLabelOffsetY), 14, WarningLightOffColor, FontStyle.Bold, WarningLightWidth / 2f, WarningLightHeight / 2f);
        }

        private Image BuildHiLoBorderHalf(RectTransform parent, string name, Vector2 position, bool upperRightHalf)
        {
            GameObject borderObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            RectTransform borderRect = borderObject.GetComponent<RectTransform>();
            borderRect.SetParent(parent, false);
            borderRect.anchorMin = new Vector2(0.5f, 0.5f);
            borderRect.anchorMax = new Vector2(0.5f, 0.5f);
            borderRect.pivot = new Vector2(0.5f, 0.5f);
            borderRect.sizeDelta = new Vector2(WarningLightWidth, WarningLightHeight);
            borderRect.anchoredPosition = position;
            Image borderImage = borderObject.GetComponent<Image>();
            borderImage.sprite = CreateHiLoBorderHalfSprite(Mathf.RoundToInt(WarningLightWidth), Mathf.RoundToInt(WarningLightHeight), WarningLightCornerRadius, WarningLightBorderThickness, upperRightHalf);
            borderImage.color = WarningLightOffColor;
            borderImage.raycastTarget = false;
            return borderImage;
        }

        private (Image border, Text label) BuildWarningLight(RectTransform parent, string text, Vector2 position)
        {
            GameObject borderObject = new GameObject(text + "Border", typeof(RectTransform), typeof(Image));
            RectTransform borderRect = borderObject.GetComponent<RectTransform>();
            borderRect.SetParent(parent, false);
            borderRect.anchorMin = new Vector2(0.5f, 0.5f);
            borderRect.anchorMax = new Vector2(0.5f, 0.5f);
            borderRect.pivot = new Vector2(0.5f, 0.5f);
            borderRect.sizeDelta = new Vector2(WarningLightWidth, WarningLightHeight);
            borderRect.anchoredPosition = position;
            Image borderImage = borderObject.GetComponent<Image>();
            borderImage.sprite = CreateRectFrameSprite(Mathf.RoundToInt(WarningLightWidth), Mathf.RoundToInt(WarningLightHeight), WarningLightCornerRadius, WarningLightBorderThickness);
            borderImage.color = WarningLightOffColor;
            borderImage.raycastTarget = false;

            Text label = CreateLabel(parent, text, position, 20, WarningLightOffColor, FontStyle.Bold, WarningLightWidth, WarningLightHeight);

            return (borderImage, label);
        }

        private void UpdateWarningPanelPosition()
        {
            if (_warningPanelRoot == null)
            {
                return;
            }

            ComputeDealerModeSquish(out float scaleX, out float scaleY);
            float compensatedX = WarningPanelPositionX + (PanelSize / 2f) * (1f - scaleX);

            _warningPanelRoot.anchoredPosition = new Vector2(compensatedX, WarningPanelPositionY);
            _warningPanelRoot.localScale = new Vector3(scaleX, scaleY, 1f);
        }

        private void StartWarningPanelStartupSequence()
        {
            _startupPhase = WarningPanelStartupPhase.Black;
            _startupPhaseStartTime = Time.unscaledTime;
        }

        // Picks the color for whichever step `elapsedInPhase` currently
        // falls in, holding on the last step once the phase's own elapsed
        // check (in UpdateWarningPanelStartup()) is about to advance it.
        private static Color StartupStepColor(float elapsedInPhase, Color[] steps)
        {
            int index = Mathf.Clamp(Mathf.FloorToInt(elapsedInPhase / StartupColorStepSeconds), 0, steps.Length - 1);
            return steps[index];
        }

        // Returns true while the startup sequence owns the panel's colors
        // this frame (caller should skip its own normal-state logic), false
        // once it's finished and normal per-light logic should resume.
        private bool UpdateWarningPanelStartup()
        {
            if (_startupPhase == WarningPanelStartupPhase.Done)
            {
                return false;
            }

            float elapsed = Time.unscaledTime - _startupPhaseStartTime;

            switch (_startupPhase)
            {
                case WarningPanelStartupPhase.Black:
                    ApplyLightColor(_tgtLightBorder, _tgtLightLabel, WarningLightOffColor);
                    ApplyLightColor(_mslLightBorder, _mslLightLabel, WarningLightOffColor);
                    ApplyLightColor(_seenLightBorder, _seenLightLabel, WarningLightOffColor);
                    ApplyLightColor(_hiBorder, _hiLabel, WarningLightOffColor);
                    ApplyLightColor(_loBorder, _loLabel, WarningLightOffColor);
                    if (elapsed >= StartupBlackSeconds)
                    {
                        _startupPhase = WarningPanelStartupPhase.TestingTgt;
                        _startupPhaseStartTime = Time.unscaledTime;
                    }
                    break;

                case WarningPanelStartupPhase.TestingTgt:
                {
                    Color[] steps = { TargetedColor, WarningLightOffColor };
                    ApplyLightColor(_tgtLightBorder, _tgtLightLabel, StartupStepColor(elapsed, steps));
                    ApplyLightColor(_mslLightBorder, _mslLightLabel, WarningLightOffColor);
                    ApplyLightColor(_seenLightBorder, _seenLightLabel, WarningLightOffColor);
                    ApplyLightColor(_hiBorder, _hiLabel, WarningLightOffColor);
                    ApplyLightColor(_loBorder, _loLabel, WarningLightOffColor);
                    if (elapsed >= steps.Length * StartupColorStepSeconds)
                    {
                        _startupPhase = WarningPanelStartupPhase.TestingMsl;
                        _startupPhaseStartTime = Time.unscaledTime;
                    }
                    break;
                }

                case WarningPanelStartupPhase.TestingMsl:
                {
                    Color[] steps = { TargetedColor, SeenColor, WarningLightOffColor };
                    ApplyLightColor(_tgtLightBorder, _tgtLightLabel, WarningLightOffColor);
                    ApplyLightColor(_mslLightBorder, _mslLightLabel, StartupStepColor(elapsed, steps));
                    ApplyLightColor(_seenLightBorder, _seenLightLabel, WarningLightOffColor);
                    ApplyLightColor(_hiBorder, _hiLabel, WarningLightOffColor);
                    ApplyLightColor(_loBorder, _loLabel, WarningLightOffColor);
                    if (elapsed >= steps.Length * StartupColorStepSeconds)
                    {
                        _startupPhase = WarningPanelStartupPhase.TestingSeen;
                        _startupPhaseStartTime = Time.unscaledTime;
                    }
                    break;
                }

                case WarningPanelStartupPhase.TestingSeen:
                {
                    Color[] steps = { SeenColor, WarningLightOffColor };
                    ApplyLightColor(_tgtLightBorder, _tgtLightLabel, WarningLightOffColor);
                    ApplyLightColor(_mslLightBorder, _mslLightLabel, WarningLightOffColor);
                    ApplyLightColor(_seenLightBorder, _seenLightLabel, StartupStepColor(elapsed, steps));
                    ApplyLightColor(_hiBorder, _hiLabel, WarningLightOffColor);
                    ApplyLightColor(_loBorder, _loLabel, WarningLightOffColor);
                    if (elapsed >= steps.Length * StartupColorStepSeconds)
                    {
                        _startupPhase = WarningPanelStartupPhase.TestingHiLo;
                        _startupPhaseStartTime = Time.unscaledTime;
                    }
                    break;
                }

                case WarningPanelStartupPhase.TestingHiLo:
                {
                    Color[] steps = { TargetedColor, WarningLightOffColor };
                    Color hiLoColor = StartupStepColor(elapsed, steps);
                    ApplyLightColor(_tgtLightBorder, _tgtLightLabel, WarningLightOffColor);
                    ApplyLightColor(_mslLightBorder, _mslLightLabel, WarningLightOffColor);
                    ApplyLightColor(_seenLightBorder, _seenLightLabel, WarningLightOffColor);
                    ApplyLightColor(_hiBorder, _hiLabel, hiLoColor);
                    ApplyLightColor(_loBorder, _loLabel, hiLoColor);
                    if (elapsed >= steps.Length * StartupColorStepSeconds)
                    {
                        _startupPhase = WarningPanelStartupPhase.Done;
                    }
                    break;
                }
            }

            return true;
        }

        // TGT: any current radar contact has the player specifically
        // targeted (the exact same signal that turns that contact's icon
        // TargetedColor on the scope) -- drives the TGT spike. SEEN: any
        // radar ping detected the player at all (mirrors the minimap's
        // grey/yellow/red ping coloring -- see OnRadarWarningReceived).
        // MSL: an actual missile threat is in effect -- a SARH launcher
        // actively guiding one (_sarhThreatCounts) or an ARH missile's own
        // seeker radar currently pinging (_arhMissileContacts, already
        // pruned to non-stale entries by UpdateArhMissileContacts() earlier
        // this same frame). HI/LO: whether the current priority contact
        // (see UpdatePriorityDiamond(), which sets _priorityEmitter) sits
        // above or below the player. All four go dark during the splash,
        // same as every other live overlay -- spike states are forced back
        // to Idle rather than left running so they don't silently keep
        // counting down underneath it.
        private void UpdateWarningPanel()
        {
            if (_warningPanelRoot == null)
            {
                return;
            }

            _warningPanelRoot.gameObject.SetActive(ExtraPanelEnabled);
            if (!ExtraPanelEnabled)
            {
                return;
            }

            // A genuine targeting lock or missile threat firing mid-animation
            // takes priority over the cosmetic boot sequence -- the trigger
            // methods set _tgtLightState/_sarhThreatCounts/etc. unconditionally
            // regardless of startup state, but nothing advances or displays
            // that state until the startup sequence actually finishes, so
            // without this check a threat that appears during the first
            // ~3.65s after respawn would go unseen until it's already stale.
            // Deliberately scoped to TGT/MSL only (not SEEN or HI/LO, which
            // fire on routine radar activity / any nearby contact) -- those
            // are common enough in a populated mission that including them
            // would abort the animation almost every single respawn.
            if (_startupPhase != WarningPanelStartupPhase.Done
                && (_tgtLightState.Phase != SpikeLightPhase.Idle || _sarhThreatCounts.Count > 0 || _arhMissileContacts.Count > 0))
            {
                _startupPhase = WarningPanelStartupPhase.Done;
            }

            if (UpdateWarningPanelStartup())
            {
                return;
            }

            if (_splashActive)
            {
                _tgtLightState.Phase = SpikeLightPhase.Idle;
                _seenLightState.Phase = SpikeLightPhase.Idle;
                ApplyLightColor(_tgtLightBorder, _tgtLightLabel, WarningLightOffColor);
                ApplyLightColor(_mslLightBorder, _mslLightLabel, WarningLightOffColor);
                ApplyLightColor(_seenLightBorder, _seenLightLabel, WarningLightOffColor);
                ApplyLightColor(_hiBorder, _hiLabel, WarningLightOffColor);
                ApplyLightColor(_loBorder, _loLabel, WarningLightOffColor);
                return;
            }

            ApplyLightColor(_tgtLightBorder, _tgtLightLabel, UpdateSpikeLight(ref _tgtLightState, TargetedColor, TgtHoldSeconds));
            ApplyLightColor(_seenLightBorder, _seenLightLabel, UpdateSpikeLight(ref _seenLightState, SeenColor, SeenHoldSeconds));

            bool missileThreat = _sarhThreatCounts.Count > 0 || _arhMissileContacts.Count > 0;
            Color mslColor;
            if (missileThreat)
            {
                bool useColorA = Mathf.Repeat(Time.unscaledTime, SarhFlashInterval * 2f) < SarhFlashInterval;
                mslColor = useColorA ? SarhFlashColorA : SarhFlashColorB;
            }
            else
            {
                mslColor = WarningLightOffColor;
            }
            ApplyLightColor(_mslLightBorder, _mslLightLabel, mslColor);

            UpdateHiLoIndicator();
        }

        private void UpdateHiLoIndicator()
        {
            bool priorityAbove = false;
            bool priorityBelow = false;
            if (_priorityEmitter != null && _playerAircraft != null)
            {
                float deltaY = _priorityEmitter.transform.position.y - _playerAircraft.transform.position.y;
                priorityAbove = deltaY > 0f;
                priorityBelow = deltaY < 0f;
            }

            ApplyLightColor(_hiBorder, _hiLabel, priorityAbove ? TargetedColor : WarningLightOffColor);
            ApplyLightColor(_loBorder, _loLabel, priorityBelow ? TargetedColor : WarningLightOffColor);
        }

        // Called directly from OnRadarWarningReceived on every ping that
        // targets the player. Unconditionally restarts the flash phase,
        // even mid-animation, so there's no "cooldown" gap needed before it
        // can trigger again.
        private static void TriggerSpike(ref SpikeLightState state)
        {
            state.Phase = SpikeLightPhase.Flashing;
            state.PhaseStartTime = Time.unscaledTime;
        }

        // Called directly from OnRadarWarningReceived on every ping that
        // detects the player at all. Unlike TriggerSpike(), a ping doesn't
        // unconditionally restart the flash: from Idle it starts the flash
        // like normal, but a ping arriving mid-hold just refreshes the hold
        // timer (extending how long it stays lit) instead of replaying the
        // flash from scratch. A ping arriving mid-flash is left alone --
        // the in-progress flash just runs out into Holding on its own.
        private static void TriggerSeenPing(ref SpikeLightState state)
        {
            if (state.Phase == SpikeLightPhase.Idle)
            {
                state.Phase = SpikeLightPhase.Flashing;
                state.PhaseStartTime = Time.unscaledTime;
            }
            else if (state.Phase == SpikeLightPhase.Holding)
            {
                state.PhaseStartTime = Time.unscaledTime;
            }
        }

        // Just phase progression + color output -- triggering itself
        // happens in TriggerSpike()/TriggerSeenPing(), called from
        // OnRadarWarningReceived.
        private static Color UpdateSpikeLight(ref SpikeLightState state, Color activeColor, float holdSeconds)
        {
            float now = Time.unscaledTime;

            Color color;
            switch (state.Phase)
            {
                case SpikeLightPhase.Flashing:
                {
                    float elapsed = now - state.PhaseStartTime;
                    float flashDuration = TgtFlashCount * TgtFlashInterval * 2f;
                    if (elapsed >= flashDuration)
                    {
                        state.Phase = SpikeLightPhase.Holding;
                        state.PhaseStartTime = now;
                        color = activeColor;
                    }
                    else
                    {
                        bool onHalf = Mathf.Repeat(elapsed, TgtFlashInterval * 2f) < TgtFlashInterval;
                        color = onHalf ? activeColor : WarningLightOffColor;
                    }
                    break;
                }
                case SpikeLightPhase.Holding:
                {
                    if (now - state.PhaseStartTime >= holdSeconds)
                    {
                        state.Phase = SpikeLightPhase.Idle;
                    }
                    color = activeColor;
                    break;
                }
                default:
                    color = WarningLightOffColor;
                    break;
            }

            return color;
        }

        private static void ApplyLightColor(Image border, Text label, Color color)
        {
            if (border != null)
            {
                border.color = color;
            }
            if (label != null)
            {
                label.color = color;
            }
        }

        private Image BuildRing(RectTransform parent, float thickness)
        {
            GameObject ringObject = new GameObject("Ring", typeof(RectTransform), typeof(Image));
            RectTransform rect = ringObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(ScopeDiameter, ScopeDiameter);
            rect.anchoredPosition = Vector2.zero;

            Image image = ringObject.GetComponent<Image>();
            image.sprite = CreateRingSprite(ScopeDiameter, thickness);
            image.color = Themed(0.8f);
            image.raycastTarget = false;
            return image;
        }

        private Image BuildHalfRangeRing(RectTransform parent, float thickness)
        {
            int diameter = ScopeDiameter / 2;

            GameObject ringObject = new GameObject("HalfRangeRing", typeof(RectTransform), typeof(Image));
            RectTransform rect = ringObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(diameter, diameter);
            rect.anchoredPosition = Vector2.zero;

            Image image = ringObject.GetComponent<Image>();
            image.sprite = CreateRingSprite(diameter, thickness);
            image.color = Themed(0.5f);
            image.raycastTarget = false;
            return image;
        }

        private const int PriorityDiamondSize = 28;
        private const float PriorityDiamondThickness = 2f;

        // Starts inactive -- UpdatePriorityDiamond() only activates it once
        // there's an actual contact to point at (Rank 1+).
        private Image BuildPriorityDiamond(RectTransform parent)
        {
            GameObject diamondObject = new GameObject("PriorityDiamond", typeof(RectTransform), typeof(Image));
            RectTransform rect = diamondObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(PriorityDiamondSize, PriorityDiamondSize);
            rect.anchoredPosition = Vector2.zero;

            Image image = diamondObject.GetComponent<Image>();
            image.sprite = CreateDiamondOutlineSprite(PriorityDiamondSize, PriorityDiamondThickness);
            image.color = Themed(0.9f);
            image.raycastTarget = false;
            diamondObject.SetActive(false);
            return image;
        }

        private (Image Horizontal, Image Vertical) BuildPlaneIcon(RectTransform parent)
        {
            Image horizontal = CreateCrossBar(parent, "ReticleHorizontal", new Vector2(20f, 2f));
            Image vertical = CreateCrossBar(parent, "ReticleVertical", new Vector2(2f, 20f));
            return (horizontal, vertical);
        }

        // Rank 0: the cross expands all the way out to the outer ring
        // instead of a small center reticle, visually dividing the scope
        // into the four quadrants contacts are snapped to.
        private (Image Horizontal, Image Vertical) BuildFullCross(RectTransform parent)
        {
            Image horizontal = CreateCrossBar(parent, "Rank0ReticleHorizontal", new Vector2(ScopeDiameter, 2f));
            Image vertical = CreateCrossBar(parent, "Rank0ReticleVertical", new Vector2(2f, ScopeDiameter));
            return (horizontal, vertical);
        }

        // N instances (N = arcImages.Length -- 4 for Rank 0, 8 for Rank 4)
        // of one shared arc sprite spanning a single division's own slice,
        // each rotated to cover a different division -- same
        // -degrees-per-index rotation convention as everywhere else
        // bearings get turned into a Z euler angle in this file. All start
        // inactive; UpdateIrWarningRing() activates only the division(s)
        // an inbound IR missile currently occupies.
        private void BuildIrWarningRing(RectTransform parent, ref Sprite cachedSprite, Image[] arcImages, string namePrefix)
        {
            int divisionCount = arcImages.Length;
            if (cachedSprite == null)
            {
                cachedSprite = CreateArcSegmentSprite(Mathf.RoundToInt(IrRingDiameter), IrRingThickness, divisionCount);
            }

            float divisionSpan = 360f / divisionCount;
            for (int division = 0; division < divisionCount; division++)
            {
                GameObject arcObject = new GameObject(namePrefix + division, typeof(RectTransform), typeof(Image));
                RectTransform rect = arcObject.GetComponent<RectTransform>();
                rect.SetParent(parent, false);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(IrRingDiameter, IrRingDiameter);
                rect.anchoredPosition = Vector2.zero;
                rect.localRotation = Quaternion.Euler(0f, 0f, -divisionSpan * division);

                Image image = arcObject.GetComponent<Image>();
                image.sprite = cachedSprite;
                image.color = IrWarningColor;
                image.raycastTarget = false;
                arcObject.SetActive(false);

                arcImages[division] = image;
            }
        }

        private void BuildRank0CornerIndicators(RectTransform parent)
        {
            float corner = Rank0IndicatorCornerOffset;
            (_airInterceptBorder, _airInterceptLabel) = BuildRank0CornerIndicator(parent, "A/I", new Vector2(-corner, corner));
            (_navalBorder, _navalLabel) = BuildRank0CornerIndicator(parent, "NVL", new Vector2(corner, corner));
            (_radarTruckBorder, _radarTruckLabel) = BuildRank0CornerIndicator(parent, "R9", new Vector2(-corner, -corner));
            (_boltstrikeBorder, _boltstrikeLabel) = BuildRank0CornerIndicator(parent, "T9", new Vector2(corner, -corner));
        }

        private (Image border, Text label) BuildRank0CornerIndicator(RectTransform parent, string text, Vector2 position)
        {
            GameObject borderObject = new GameObject(text + "Indicator", typeof(RectTransform), typeof(Image));
            RectTransform borderRect = borderObject.GetComponent<RectTransform>();
            borderRect.SetParent(parent, false);
            borderRect.anchorMin = new Vector2(0.5f, 0.5f);
            borderRect.anchorMax = new Vector2(0.5f, 0.5f);
            borderRect.pivot = new Vector2(0.5f, 0.5f);
            borderRect.sizeDelta = new Vector2(Rank0IndicatorDiameter, Rank0IndicatorDiameter);
            borderRect.anchoredPosition = position;
            Image borderImage = borderObject.GetComponent<Image>();
            borderImage.sprite = CreateRingSprite(Mathf.RoundToInt(Rank0IndicatorDiameter), Rank0IndicatorThickness);
            borderImage.color = WarningLightOffColor;
            borderImage.raycastTarget = false;

            Text label = CreateLabel(parent, text, position, 9, WarningLightOffColor, FontStyle.Bold, Rank0IndicatorDiameter, Rank0IndicatorDiameter - 4f);

            return (borderImage, label);
        }

        private Image CreateCrossBar(RectTransform parent, string name, Vector2 size)
        {
            GameObject barObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            RectTransform rect = barObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;

            Image image = barObject.GetComponent<Image>();
            image.color = Themed(0.9f);
            image.raycastTarget = false;
            return image;
        }

        private static Sprite CreateRingSprite(int size, float thickness)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            float radius = size / 2f;
            Vector2 center = new Vector2(radius, radius);
            float innerRadius = radius - thickness;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float alpha = 0f;

                    if (dist <= radius && dist >= innerRadius)
                    {
                        alpha = 1f;
                        if (dist > radius - 1f)
                        {
                            alpha = Mathf.Clamp01(radius - dist);
                        }
                        if (dist < innerRadius + 1f)
                        {
                            alpha = Mathf.Min(alpha, Mathf.Clamp01(dist - innerRadius));
                        }
                    }

                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        }

        // Same ring-with-anti-aliased-edges technique as CreateRingSprite,
        // but only fills in division 0's own slice (0 to 360/divisionCount
        // degrees -- for divisionCount=4 that's quadrant 0/NE, matching
        // GetQuadrantIndex's own convention) -- the other divisions reuse
        // this same sprite rotated around rather than each getting their
        // own texture. Same Atan2(x, y) bearing convention as
        // BearingToDirection (0 degrees = up).
        private static Sprite CreateArcSegmentSprite(int size, float thickness, int divisionCount)
        {
            float divisionSpan = 360f / divisionCount;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            float radius = size / 2f;
            Vector2 center = new Vector2(radius, radius);
            float innerRadius = radius - thickness;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = (x + 0.5f) - center.x;
                    float py = (y + 0.5f) - center.y;
                    float dist = Mathf.Sqrt(px * px + py * py);
                    float alpha = 0f;

                    if (dist <= radius && dist >= innerRadius)
                    {
                        float bearing = Mathf.Atan2(px, py) * Mathf.Rad2Deg;
                        float normalizedBearing = ((bearing % 360f) + 360f) % 360f;

                        if (normalizedBearing >= 0f && normalizedBearing < divisionSpan)
                        {
                            alpha = 1f;
                            if (dist > radius - 1f)
                            {
                                alpha = Mathf.Clamp01(radius - dist);
                            }
                            if (dist < innerRadius + 1f)
                            {
                                alpha = Mathf.Min(alpha, Mathf.Clamp01(dist - innerRadius));
                            }
                        }
                    }

                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        }

        private static Sprite CreateRoundedRectSprite(int width, int height, float cornerRadius, Color fillColor)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            float halfW = width / 2f;
            float halfH = height / 2f;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float px = (x + 0.5f) - halfW;
                    float py = (y + 0.5f) - halfH;

                    float qx = Mathf.Abs(px) - (halfW - cornerRadius);
                    float qy = Mathf.Abs(py) - (halfH - cornerRadius);

                    float outsideDist = Mathf.Sqrt(Mathf.Pow(Mathf.Max(qx, 0f), 2f) + Mathf.Pow(Mathf.Max(qy, 0f), 2f));
                    float dist = outsideDist + Mathf.Min(Mathf.Max(qx, qy), 0f) - cornerRadius;

                    float alpha = Mathf.Clamp01(0.5f - dist);
                    texture.SetPixel(x, y, new Color(fillColor.r, fillColor.g, fillColor.b, fillColor.a * alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f));
        }

        // Shared by CreateRectFrameSprite and CreateHiLoBorderHalfSprite --
        // signed distance from (px, py) (pixel center relative to the box's
        // own center) to a rounded rect's outline, converted to a border-band
        // alpha. Kept as one function so a future tweak to the border
        // formula/anti-aliasing can't update one call site and miss the other.
        private static float RoundedRectBorderAlpha(float px, float py, float halfW, float halfH, float cornerRadius, float thickness)
        {
            float qx = Mathf.Abs(px) - (halfW - cornerRadius);
            float qy = Mathf.Abs(py) - (halfH - cornerRadius);

            float outsideDist = Mathf.Sqrt(Mathf.Pow(Mathf.Max(qx, 0f), 2f) + Mathf.Pow(Mathf.Max(qy, 0f), 2f));
            float dist = outsideDist + Mathf.Min(Mathf.Max(qx, qy), 0f) - cornerRadius;

            float band = Mathf.Abs(dist) - (thickness / 2f);
            return Mathf.Clamp01(0.5f - band);
        }

        // Hollow rounded-rect outline -- same signed-distance shape as
        // CreateRoundedRectSprite, but alpha comes from a band around the
        // zero-distance contour (|dist| within half the border thickness)
        // instead of "inside the shape", so only the border rasterizes.
        private static Sprite CreateRectFrameSprite(int width, int height, float cornerRadius, float thickness)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            float halfW = width / 2f;
            float halfH = height / 2f;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float alpha = RoundedRectBorderAlpha((x + 0.5f) - halfW, (y + 0.5f) - halfH, halfW, halfH, cornerRadius, thickness);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f));
        }

        // The HI/LO diagonal runs top-left to bottom-right ("\"), through
        // origin point (0, height) with direction (width, -height) --
        // splitting the box into a top-right triangle (containing corner
        // (width, height)) and a bottom-left triangle (containing corner
        // (0, 0)). The signed 2D cross product of a pixel's offset from
        // that origin against the (normalized) diagonal direction is
        // negative on the top-right side and positive on the bottom-left
        // side (checked directly against both corners) -- both sprite
        // generators below share that same sign convention so the two
        // border halves and the label placement in BuildHiLoIndicator all
        // agree on which side is which.
        private static Vector2 HiLoDiagonalDirection(int width, int height)
        {
            return new Vector2(width, -height).normalized;
        }

        private static float HiLoSignedDiagonalDistance(float x, float y, int height, Vector2 diagonalDir)
        {
            float relX = x - 0f;
            float relY = y - height;
            return relX * diagonalDir.y - relY * diagonalDir.x;
        }

        // Deliberately hard-thresholded (no smoothing) and Point-filtered --
        // every other sprite in this file uses a soft/anti-aliased edge,
        // but that made this specific line read as blurry rather than
        // crisp, so it gets the opposite (aliased) treatment instead.
        private static Sprite CreateHiLoDiagonalSprite(int width, int height, float thickness)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            Vector2 diagonalDir = HiLoDiagonalDirection(width, height);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float signedDist = HiLoSignedDiagonalDistance(x + 0.5f, y + 0.5f, height, diagonalDir);
                    float alpha = Mathf.Abs(signedDist) <= thickness / 2f ? 1f : 0f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f));
        }

        // Same rounded-rect border shape as CreateRectFrameSprite, but only
        // rasterized on whichever side of the diagonal this half owns, and
        // pulled back an extra HiLoGapMargin from the centerline so the two
        // halves don't touch -- the two halves' sprites still tile together
        // into roughly the original combined outline, just with a visible
        // gap along the shared diagonal edge instead of a seam. That edge
        // itself is deliberately NOT included here at all (see
        // CreateHiLoDiagonalSprite) so it stays a single neutral static
        // element regardless of which half is currently lit.
        private static Sprite CreateHiLoBorderHalfSprite(int width, int height, float cornerRadius, float thickness, bool upperRightHalf)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            float halfW = width / 2f;
            float halfH = height / 2f;
            Vector2 diagonalDir = HiLoDiagonalDirection(width, height);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float borderAlpha = RoundedRectBorderAlpha((x + 0.5f) - halfW, (y + 0.5f) - halfH, halfW, halfH, cornerRadius, thickness);

                    float signedDist = HiLoSignedDiagonalDistance(x + 0.5f, y + 0.5f, height, diagonalDir);
                    bool included = upperRightHalf ? signedDist < -HiLoGapMargin : signedDist > HiLoGapMargin;

                    float alpha = included ? borderAlpha : 0f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f));
        }

        // --- Live contacts, driven by Aircraft.onRadarWarning ---------------------
        // Plotted on an invisible -50..+50 grid centered on the scope,
        // scaled to scope units and clamped so nothing renders past the
        // ring regardless of actual range. A contact appears the instant we
        // hear from it, regardless of detected/isTarget -- isTarget only
        // changes its color (actively targeted vs. just illuminated).

        private const float GridExtent = 50f;
        private const float MaxContactRadius = (ScopeDiameter / 2f) - 14f;
        private const float SymbolToLabelOffset = -16f;
        // The dome symbol is only an open arc (top half of its box), so its
        // visual weight sits noticeably higher than the other, more evenly
        // balanced symbols -- nudge it down to line up with them.
        private const float DomeSymbolVerticalOffset = -5f;
        private const float ShipSymbolVerticalOffset = -3f;

        // Set from Plugin.Awake() via the MaxRangeKm config entry (default
        // 50km), and live-updated if the user changes it in ConfigManager.
        public static float MaxDisplayRangeMeters = 50000f;

        // Set from Plugin.Awake() ("General" section), live-updated in
        // ConfigManager. Swaps ship designators from ShipCodeOverrides
        // (real-world-style hull codes, e.g. FFL/CVE) to
        // SimpleShipCodeOverrides (simpler class-name-based codes, e.g.
        // ARG/CSR) wherever a ship's designation is looked up.
        public static bool UseSimpleShipDesignators;

        // Set from Plugin.Awake() ("General" section), live-updated in
        // ConfigManager. Extends the notch line (normally Rank 4 only) down
        // to ranks 1-3 as well -- see ShouldShowNotchLine.
        public static bool NotchLineOnAllRanks;

        // Set from Plugin.Awake() ("RWR Appearance" section), live-updated
        // in ConfigManager. ThemeColor's own alpha is ignored -- opacity is
        // ThemeOpacity alone. Base scope elements (rings, reticle, normal
        // contacts/labels, splash text) take ThemeColor's hue via Themed();
        // status colors below keep their own hue (so recoloring the theme
        // never changes what a warning means) but still scale with
        // ThemeOpacity via WithOpacity().
        //
        // These six are the *effective* colors everything else in the file
        // reads -- recomputed once a frame by UpdateFunnyModeColors() below
        // from the User*Color fields (the real ConfigManager values Plugin.cs
        // writes to). Normally that's just a passthrough; while Funny Mode
        // is on it overwrites them with rotating rainbow values instead,
        // without disturbing what's actually saved to disk.
        public static Color ThemeColor = new Color(0.2f, 1f, 0.4f, 1f);
        public static float ThemeOpacity = 1f;
        public static Color ThreatColor = new Color(1f, 0.2f, 0.15f, 1f);
        public static Color ThreatFlashColor = new Color(1f, 0.9f, 0.1f, 1f);
        public static Color JamLobBaseColor = new Color(1f, 0.45f, 0f, 1f);
        public static Color NotchPrimaryColor = new Color(1f, 0.9f, 0.1f, 1f);
        public static Color NotchSecondaryColor = new Color(1f, 0.55f, 0.05f, 1f);

        public static Color UserThemeColor = new Color(0.2f, 1f, 0.4f, 1f);
        public static Color UserThreatColor = new Color(1f, 0.2f, 0.15f, 1f);
        public static Color UserThreatFlashColor = new Color(1f, 0.9f, 0.1f, 1f);
        public static Color UserJamLobBaseColor = new Color(1f, 0.45f, 0f, 1f);
        public static Color UserNotchPrimaryColor = new Color(1f, 0.9f, 0.1f, 1f);
        public static Color UserNotchSecondaryColor = new Color(1f, 0.55f, 0.05f, 1f);

        // "Secrets" section (see Plugin.cs), advanced-only.
        public static bool FunnyModeEnabled;
        public static bool DealerModeEnabled;
        public static int DealerModeBpm = 130;
        public static bool BestFontEnabled;
        private bool _lastBestFontEnabled;

        private const float FunnyModeCycleSeconds = 6f;
        // Peak squish: top edge comes down to half the panel's height, and
        // the panel widens noticeably (not the full 2x a strict area-
        // preserving squash would need at 0.5x height -- that looked too
        // extreme on a HUD-sized element).
        private const float DealerModeMinScaleY = 0.5f;
        private const float DealerModeMaxScaleX = 1.3f;

        // Normally just copies the real user colors through unchanged.
        // While Funny Mode is on, all six get a rotating hue instead --
        // evenly spaced 1/6 of the color wheel apart so none of them can
        // ever land on the same color at the same time.
        private static void UpdateFunnyModeColors()
        {
            if (!FunnyModeEnabled)
            {
                ThemeColor = UserThemeColor;
                ThreatColor = UserThreatColor;
                ThreatFlashColor = UserThreatFlashColor;
                JamLobBaseColor = UserJamLobBaseColor;
                NotchPrimaryColor = UserNotchPrimaryColor;
                NotchSecondaryColor = UserNotchSecondaryColor;
                return;
            }

            float t = Time.unscaledTime / FunnyModeCycleSeconds;
            ThemeColor = RainbowColor(t, 0f / 6f);
            ThreatColor = RainbowColor(t, 1f / 6f);
            ThreatFlashColor = RainbowColor(t, 2f / 6f);
            JamLobBaseColor = RainbowColor(t, 3f / 6f);
            NotchPrimaryColor = RainbowColor(t, 4f / 6f);
            NotchSecondaryColor = RainbowColor(t, 5f / 6f);
        }

        private static Color RainbowColor(float cyclePosition, float hueOffset)
        {
            Color color = Color.HSVToRGB(Mathf.Repeat(cyclePosition + hueOffset, 1f), 1f, 1f);
            color.a = 1f;
            return color;
        }

        private static Color Themed(float baseAlpha)
        {
            return new Color(ThemeColor.r, ThemeColor.g, ThemeColor.b, baseAlpha * ThemeOpacity);
        }

        private static Color WithOpacity(Color baseColor)
        {
            baseColor.a *= ThemeOpacity;
            return baseColor;
        }

        // Set from Plugin.Awake() ("RWR Quality Overrides" section), live-
        // updated in ConfigManager. FallbackRwrQuality always applies (it's
        // for aircraft with no table entry at all, e.g. from another mod --
        // not really an "override"). OverwriteRwrSettings only gates
        // AircraftRwrQualityOverrides, which only holds entries the user
        // actually set (-1 means "no override for this aircraft").
        public static bool OverwriteRwrSettings;
        public static int FallbackRwrQuality = DefaultRwrQuality;
        public static readonly Dictionary<string, int> AircraftRwrQualityOverrides = new Dictionary<string, int>();
        // Lifecycle since the last ping: bright, then a dimmed "stale"
        // hold, then a quick fade to fully gone.
        private const float ContactBrightSeconds = 5f;
        private const float ContactDarkSeconds = 5f;
        private const float ContactFadeOutSeconds = 0.5f;
        private const float DarkenedAlphaFactor = 0.35f;
        private static Color ContactColor => Themed(0.95f);
        private static Color TargetedColor => WithOpacity(new Color(ThreatColor.r, ThreatColor.g, ThreatColor.b, 0.95f));

        private class TrackedContact
        {
            public RectTransform Group;
            public Image[] SymbolImages;
            public Text Label;
            public float LastSeenTime;
            public Color BaseColor;
            // Explicit flag, not a BaseColor == TargetedColor comparison --
            // TargetedColor is theme-dependent now, so a stale color
            // captured on an earlier ping would stop matching (and silently
            // break) if the user changes the theme in between.
            public bool IsTargeted;

            // Rank 0 only: which quadrant this contact currently occupies,
            // and when it arrived there -- used to decide priority when
            // multiple contacts land in the same quadrant.
            public int Rank0Quadrant = -1;
            public float Rank0QuadrantEnteredTime;
        }

        private readonly Dictionary<Unit, TrackedContact> _contacts = new Dictionary<Unit, TrackedContact>();
        private Aircraft _playerAircraft;
        private bool _subscribed;

        // --- RWR quality (0=Poor .. 4=Excellent), keyed by the flown
        // aircraft's UnitDefinition.code. Determines the whole scope's
        // behavior for the current flight. Defaults to 2 ("what we have
        // now") for anything not explicitly assigned yet.
        private const int DefaultRwrQuality = 2;
        private static readonly Dictionary<string, int> AircraftRwrQuality = new Dictionary<string, int>
        {
            { "CI-22", 0 },   // Cricket
            { "VL-49", 1 },   // Tarantula
            { "UH-90", 1 },   // Ibis
            { "SAH-46", 2 },  // Chicane
            { "A-19", 2 },    // Brawler
            { "FS-12", 2 },   // Revoker
            { "SFB-81", 2 },  // Darkreach
            { "KR-67", 3 },   // Ifrit
            { "FS-20", 3 },   // Vortex
            { "EW-25", 4 },   // Medusa

            // Blueprinter addon aircraft (keyed by jsonKey, no useful code).
            { "Aryx_MiG-15", 0 },           // MiG-15 (no radar)
            { "Aryx_LightHelicopter1", 4 }, // RAH-72 Knockout (no radar)
            { "Aryx_LightFighter1", 1 },    // F-99 Shrike
            { "Aryx_F16M_KingViper", 2 },   // F-16M King Viper
            { "P_Trisurface1", 3 },         // FS-3 Ternion
            { "Aryx_CargoPlane1", 3 },      // MC-260 Chimera
            { "Aryx_Interceptor1", 4 },     // FS-41 Eclipse

            // Playable Ships addon -- makes ship classes flyable, so they're
            // technically Aircraft here and need their own quality entries
            // like any other aircraft. See ShipTypeOverrideJsonKeys below
            // for the part that makes them still render/designate as ships.
            { "SmallKarrier", 4 },          // Cursor-class equivalent
            { "LandingKraft", 3 },          // no radar
            { "PatrolBote", 3 },
            { "Korvette1", 4 },             // Shard-class equivalent
            { "Frickate1", 4 },             // Argus-class equivalent
            { "Destroyer1_Player", 4 },     // Dynamo-class equivalent
            { "AssaultKarrier", 4 },        // Annex-class equivalent
            { "FleetKarrier", 4 },          // Hyperion-class equivalent
        };

        // These three airframes don't have a single fixed quality -- it's
        // rerolled every time you spawn in a new instance of them.
        private static readonly Dictionary<string, (int Quality, float Weight)[]> ProbabilisticRwrQuality =
            new Dictionary<string, (int, float)[]>
        {
            { "T/A-30", new (int, float)[] { (1, 0.25f), (2, 0.75f) } }, // Compass
            { "AB-4", new (int, float)[] { (3, 0.75f), (4, 0.25f) } },   // Alkyon
            { "VT-7", new (int, float)[] { (1, 0.5f), (2, 0.5f) } },     // Vagrant
        };

        private static int RollWeightedQuality((int Quality, float Weight)[] options)
        {
            float total = 0f;
            foreach ((int Quality, float Weight) option in options)
            {
                total += option.Weight;
            }

            float roll = UnityEngine.Random.value * total;
            float cumulative = 0f;
            foreach ((int Quality, float Weight) option in options)
            {
                cumulative += option.Weight;
                if (roll <= cumulative)
                {
                    return option.Quality;
                }
            }

            return options[options.Length - 1].Quality;
        }

        // For ConfigManager's per-aircraft override descriptions (Plugin.cs)
        // -- aircraftCode here is the exact dictionary key (a UnitDefinition
        // code or jsonKey), not a UnitDefinition, so this is a direct lookup
        // rather than going through TryGetByCodeOrJsonKey.
        public static string DescribeDefaultQuality(string aircraftCode)
        {
            if (ProbabilisticRwrQuality.TryGetValue(aircraftCode, out (int Quality, float Weight)[] options))
            {
                float total = 0f;
                foreach ((int Quality, float Weight) option in options)
                {
                    total += option.Weight;
                }

                StringBuilder builder = new StringBuilder("Default: ");
                for (int i = 0; i < options.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }
                    builder.Append(Mathf.RoundToInt(options[i].Weight / total * 100f));
                    builder.Append("% Rank ");
                    builder.Append(options[i].Quality);
                }
                builder.Append(" (rerolled every time you spawn in it).");
                return builder.ToString();
            }

            if (AircraftRwrQuality.TryGetValue(aircraftCode, out int quality))
            {
                return $"Default: Rank {quality}.";
            }

            return null;
        }

        // Base-game aircraft are keyed by their short `code` (e.g. "KR-67")
        // everywhere in this file. Some modded aircraft (e.g. Blueprinter
        // addons) don't expose a useful code, so those get keyed by
        // `jsonKey` instead (same convention already used for ships/ground
        // units) -- this checks both, code first, so a single lookup works
        // for either kind of entry without duplicating every call site.
        private static bool TryGetByCodeOrJsonKey<T>(Dictionary<string, T> dict, UnitDefinition definition, out T value)
        {
            value = default;
            if (definition == null)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(definition.code) && dict.TryGetValue(definition.code, out value))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(definition.jsonKey) && dict.TryGetValue(definition.jsonKey, out value))
            {
                return true;
            }
            return false;
        }

        private int _currentRwrQuality = DefaultRwrQuality;

        // --- Missile warnings -------------------------------------------------
        // ARH (active radar homing): has its own radar, gets its own "M"
        // contact tracked separately from onRadarWarning since its
        // lifecycle comes from MissileWarning instead. SARH (semi-active):
        // no radar of its own, riding the launcher's beam in -- so instead
        // of a new contact, we flash the launcher's existing one.
        private static Color SarhFlashColorA => WithOpacity(new Color(ThreatFlashColor.r, ThreatFlashColor.g, ThreatFlashColor.b, 0.95f));
        private static Color SarhFlashColorB => WithOpacity(new Color(ThreatColor.r, ThreatColor.g, ThreatColor.b, 0.95f));
        // Fixed, not tied to ThreatFlashColor -- see the note near
        // ThemeColor (this is a fade, not a two-color flash).
        private static Color Rank0SarhPulseColor => WithOpacity(new Color(1f, 0.9f, 0.1f, 0.95f));
        private const float SarhFlashInterval = 0.15f;
        // Rank 0 can't tell color apart well enough to flash yellow/red --
        // it just pulses the existing contact's opacity, and slower.
        private const float SarhOpacityFlashInterval = 0.20f;

        private MissileWarning _missileWarningSystem;
        private bool _missileWarningSubscribed;
        private readonly Dictionary<Unit, int> _sarhThreatCounts = new Dictionary<Unit, int>();
        // Rank 0 and Rank 4's IR warning rings (see UpdateIrWarningRing).
        // Just a presence set, not keyed to any per-missile state -- which
        // division(s) it implies gets recomputed fresh every frame, per
        // ring, from each missile's current position.
        private readonly HashSet<Missile> _irMissileContacts = new HashSet<Missile>();

        private const float MissileResolveDelaySeconds = 1f;
        // Rank 3+: sharper gear resolves an ARH missile's designation faster.
        private const float FastMissileResolveDelaySeconds = 0.5f;

        private float CurrentMissileResolveDelaySeconds => _currentRwrQuality >= 3 ? FastMissileResolveDelaySeconds : MissileResolveDelaySeconds;

        private class ArhMissileContact
        {
            public RectTransform Group;
            public RectTransform SymbolTransform;
            public Image SymbolImage;
            public Text SymbolLetter;
            public Text DesignationLabel;
            public float CreationTime;
            public bool Resolved;

            // Rank 0 only: tracked so a quadrant containing an inbound ARH
            // missile is recognized as contested (closest-wins) even though
            // the missile icon itself lives outside _contacts.
            public int Rank0Quadrant = -1;

            // Icon lifecycle is now driven by radar pings (onRadarWarning),
            // not MissileWarning -- refreshed every time this missile's own
            // active seeker radar is detected, same staleness convention as
            // the general _contacts dictionary. MissileWarning only
            // controls HasMissileWarning below.
            public float LastRadarPingTime;

            // True once this specific missile is confirmed actually
            // locked onto/guiding toward the player (MissileWarning fired
            // for it), not just detected as a nearby radar emitter. Ranks
            // 0-3 hold off drawing the connecting line to center until
            // this is true, so a missile that's merely radar-searching
            // nearby (not threatening the player specifically) shows its
            // icon without implying a confirmed threat bearing. Rank 4
            // ignores this and always draws its line once the icon exists.
            public bool HasMissileWarning;
        }

        private readonly Dictionary<Missile, ArhMissileContact> _arhMissileContacts = new Dictionary<Missile, ArhMissileContact>();

        // Rank 4's triangle icon always points back at the player, so its
        // rotation is recomputed every frame. Every rank's icon also gets a
        // line to center (solid, dotted for Rank 4). PERF: line segments
        // are destroyed/rebuilt every frame rather than pooled -- same
        // tradeoff as the notch/LOB lines, fine at typical missile counts.
        private const float ArhLineThickness = 1.5f;
        private const float ArhLineDashLength = 4f;
        private const float ArhLineDashGap = 3f;
        private readonly List<GameObject> _arhConnectingLines = new List<GameObject>();

        // --- Jamming -----------------------------------------------------------
        // While an enemy is actively jamming the player (Unit.onJam), the
        // scope floods with fake contacts -- random blips with no real Unit
        // behind them, following the same visual lifecycle as genuine
        // contacts so they can't be told apart at a glance. Rank 4 filters
        // this out entirely.
        private const float JamActiveTimeoutSeconds = 2f;
        private const float JamBatchIntervalMin = 1f;
        private const float JamBatchIntervalMax = 1.5f;
        private const int JamBatchMaxContacts = 5;
        private const float JamShipChance = 0.25f;
        private const float JamAircraftChance = 0.5f;

        private float _lastJamTime = float.NegativeInfinity;
        private float _nextJamBatchTime;
        private Unit _lastJammingUnit;

        private bool IsCurrentlyJammed()
        {
            return (Time.unscaledTime - _lastJamTime) <= JamActiveTimeoutSeconds;
        }

        private class JamGhostContact
        {
            public RectTransform Group;
            public Image[] SymbolImages;
            public Text Label;
            public float SpawnTime;
            public Color BaseColor;

            // Rank 0 only: its low-tech gear can't tell a ghost apart from a
            // real contact, so ghosts compete for quadrant priority the
            // same way real contacts do. No real Unit/position though, so
            // SimulatedRangeMeters stands in for range when closest-wins.
            public int Rank0Quadrant = -1;
            public float Rank0QuadrantEnteredTime;
            public float SimulatedRangeMeters;
        }

        private readonly List<JamGhostContact> _jamGhostContacts = new List<JamGhostContact>();

        // Extra decoy aircraft designators layered on top of whatever the
        // current rank's real aircraft codes are, so jammed contacts can
        // read as something other than the handful of real airframes.
        private static readonly string[] JamAircraftDecoyCodes =
        {
            "F16", "F15", "F14", "F5", "111", "104", "B2", "B17", "P51", "T95",
            "M29", "M25", "M21", "M15", "LA7", "F4", "104", "F18", "E2K", "M2K",
            "B29", "B52", "HELP!", "S57", "S35", "F22", "F35", "C22", "H90", "V49",
            "67", "420", "69", "IDK", "UFO", "???", "CIA", "UMM",
        };

        private void OnJamReceived(Unit.JamEventArgs e)
        {
            _lastJamTime = Time.unscaledTime;
            _lastJammingUnit = e.jammingUnit;
        }

        private List<string> GetJamAircraftDesignationPool()
        {
            Dictionary<string, string> rankDict;
            if (_currentRwrQuality == 0)
            {
                rankDict = Rank0AircraftCodeOverrides;
            }
            else if (_currentRwrQuality == 1)
            {
                rankDict = Rank1AircraftCodeOverrides;
            }
            else
            {
                rankDict = RwrCodeOverrides;
            }

            List<string> pool = new List<string>(rankDict.Values);
            pool.AddRange(JamAircraftDecoyCodes);
            return pool;
        }

        private List<string> GetJamShipDesignationPool()
        {
            if (_currentRwrQuality == 0)
            {
                return new List<string> { "SHP" };
            }
            return new List<string>(ActiveShipCodeOverrides.Values);
        }

        private List<string> GetJamGroundDesignationPool()
        {
            if (_currentRwrQuality == 0)
            {
                return new List<string> { "SAM", "SRC" };
            }
            if (_currentRwrQuality == 1)
            {
                return new List<string>(Rank1GroundCodeOverrides.Values);
            }
            return new List<string>(GroundCodeOverrides.Values);
        }

        private static string PickRandom(List<string> pool)
        {
            if (pool == null || pool.Count == 0)
            {
                return "???";
            }
            return pool[UnityEngine.Random.Range(0, pool.Count)];
        }

        private void UpdateJamGhostContacts()
        {
            bool isJammed = _currentRwrQuality != 4 && IsCurrentlyJammed();

            if (isJammed && _scopeRoot != null && Time.unscaledTime >= _nextJamBatchTime)
            {
                int count = UnityEngine.Random.Range(0, JamBatchMaxContacts + 1);
                for (int i = 0; i < count; i++)
                {
                    CreateJamGhostContact();
                }
                _nextJamBatchTime = Time.unscaledTime + UnityEngine.Random.Range(JamBatchIntervalMin, JamBatchIntervalMax);
            }

            float now = Time.unscaledTime;
            float totalLifetime = ContactBrightSeconds + ContactDarkSeconds + ContactFadeOutSeconds;

            for (int i = _jamGhostContacts.Count - 1; i >= 0; i--)
            {
                JamGhostContact ghost = _jamGhostContacts[i];
                float age = now - ghost.SpawnTime;

                if (age > totalLifetime)
                {
                    if (ghost.Group != null)
                    {
                        Destroy(ghost.Group.gameObject);
                    }
                    _jamGhostContacts.RemoveAt(i);
                    continue;
                }

                if (age > ContactBrightSeconds + ContactDarkSeconds)
                {
                    float fadeT = Mathf.Clamp01((age - ContactBrightSeconds - ContactDarkSeconds) / ContactFadeOutSeconds);
                    Color fading = ghost.BaseColor;
                    fading.a *= DarkenedAlphaFactor * (1f - fadeT);
                    SetGhostColor(ghost, fading);
                }
                else if (age > ContactBrightSeconds)
                {
                    Color dimmed = ghost.BaseColor;
                    dimmed.a *= DarkenedAlphaFactor;
                    SetGhostColor(ghost, dimmed);
                }
                else
                {
                    SetGhostColor(ghost, ghost.BaseColor);
                }
            }
        }

        private void CreateJamGhostContact()
        {
            float roll = UnityEngine.Random.value;
            float bearing = UnityEngine.Random.Range(0f, 360f);

            Vector2 gridPosition;
            int rank0Quadrant = -1;
            float simulatedRangeMeters = 0f;
            if (_currentRwrQuality == 0)
            {
                gridPosition = SnapToQuadrant(bearing);
                rank0Quadrant = GetQuadrantIndex(((bearing % 360f) + 360f) % 360f);
                simulatedRangeMeters = UnityEngine.Random.Range(0f, MaxDisplayRangeMeters);
            }
            else
            {
                gridPosition = BearingRangeToGrid(bearing, UnityEngine.Random.value);
            }

            RectTransform group = CreateContactGroup(_contactsOverlayRoot, "JamGhost", gridPosition);

            Image[] symbolImages;
            string designation;

            if (roll < JamShipChance)
            {
                symbolImages = new[] { CreateBar(group, "ShipSymbol", new Vector2(16f, 2f), new Vector2(0f, ShipSymbolVerticalOffset)) };
                designation = PickRandom(GetJamShipDesignationPool());
            }
            else if (roll < JamShipChance + JamAircraftChance)
            {
                symbolImages = BuildChevronSymbol(group, Vector2.zero);
                designation = PickRandom(GetJamAircraftDesignationPool());
            }
            else
            {
                symbolImages = new[] { BuildDomeSymbol(group, new Vector2(0f, DomeSymbolVerticalOffset)) };
                designation = PickRandom(GetJamGroundDesignationPool());
            }

            Text label = CreateLabel(group, designation, new Vector2(0f, SymbolToLabelOffset), 11, ContactColor);

            _jamGhostContacts.Add(new JamGhostContact
            {
                Group = group,
                SymbolImages = symbolImages,
                Label = label,
                SpawnTime = Time.unscaledTime,
                BaseColor = ContactColor,
                Rank0Quadrant = rank0Quadrant,
                Rank0QuadrantEnteredTime = Time.unscaledTime,
                SimulatedRangeMeters = simulatedRangeMeters,
            });
        }

        private static void SetGhostColor(JamGhostContact ghost, Color color)
        {
            foreach (Image image in ghost.SymbolImages)
            {
                image.color = color;
            }
            if (ghost.Label != null)
            {
                ghost.Label.color = color;
            }
        }

        // Rank 3+ only: unlike the Rank 4 notch line, this points straight
        // at the jammer (no 90-degree offset) -- it's a direction-finding
        // line of bearing, not an evasion heading.
        private void UpdateJamLineOfBearing()
        {
            foreach (GameObject line in _jamLobLines)
            {
                if (line != null)
                {
                    Destroy(line);
                }
            }
            _jamLobLines.Clear();

            bool showLob = _currentRwrQuality >= 3 && IsCurrentlyJammed();
            if (!showLob || _lastJammingUnit == null || _jamLobOverlayRoot == null || _playerAircraft == null)
            {
                return;
            }

            float bearing = GetBearingForWorldPosition(_lastJammingUnit.transform.position);
            Vector2 direction = BearingToDirection(bearing);
            Quaternion rotation = Quaternion.Euler(0f, 0f, -bearing);

            float pitch = JamLobDashLength + JamLobDashGap;
            int dashCount = Mathf.Max(1, Mathf.FloorToInt(JamLobLineLength / pitch));

            for (int i = 0; i < dashCount; i++)
            {
                float offset = (i * pitch) + (JamLobDashLength / 2f);

                GameObject dashObject = new GameObject("JamLobDash", typeof(RectTransform), typeof(Image));
                RectTransform rect = dashObject.GetComponent<RectTransform>();
                rect.SetParent(_jamLobOverlayRoot, false);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(JamLobLineThickness, JamLobDashLength);
                rect.anchoredPosition = direction * offset;
                rect.localRotation = rotation;

                Image dashImage = dashObject.GetComponent<Image>();
                dashImage.color = JamLobColor;
                dashImage.raycastTarget = false;

                _jamLobLines.Add(dashObject);
            }

            CreateJamLobXMark(direction * JamLobLineLength);
        }

        private void CreateJamLobXMark(Vector2 position)
        {
            for (int i = 0; i < 2; i++)
            {
                GameObject barObject = new GameObject("JamLobX", typeof(RectTransform), typeof(Image));
                RectTransform rect = barObject.GetComponent<RectTransform>();
                rect.SetParent(_jamLobOverlayRoot, false);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(JamLobXThickness, JamLobXSize);
                rect.anchoredPosition = position;
                rect.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);

                Image image = barObject.GetComponent<Image>();
                image.color = JamLobColor;
                image.raycastTarget = false;

                _jamLobLines.Add(barObject);
            }
        }

        private void EnsureSubscribed()
        {
            CombatHUD hud = SceneSingleton<CombatHUD>.i;
            if (hud == null || hud.aircraft == null)
            {
                return;
            }

            // Already subscribed to this exact aircraft instance -- nothing
            // to do. If the instance changed (respawn/aircraft swap) or our
            // reference went stale, fall through and resubscribe.
            if (_subscribed && _playerAircraft == hud.aircraft)
            {
                return;
            }

            if (_subscribed && _playerAircraft != null)
            {
                try
                {
                    _playerAircraft.onRadarWarning -= OnRadarWarningReceived;
                    _playerAircraft.onJam -= OnJamReceived;
                }
                catch
                {
                    // best-effort unsubscribe from a possibly-stale instance
                }
            }

            _playerAircraft = hud.aircraft;
            _playerAircraft.onRadarWarning += OnRadarWarningReceived;
            _playerAircraft.onJam += OnJamReceived;
            _subscribed = true;

            _currentRwrQuality = FallbackRwrQuality;
            if (OverwriteRwrSettings && TryGetByCodeOrJsonKey(AircraftRwrQualityOverrides, _playerAircraft.definition, out int overrideQuality))
            {
                // A user-set per-aircraft override always wins, even
                // over airframes that would normally roll randomly.
                _currentRwrQuality = overrideQuality;
            }
            else if (TryGetByCodeOrJsonKey(ProbabilisticRwrQuality, _playerAircraft.definition, out (int Quality, float Weight)[] options))
            {
                _currentRwrQuality = RollWeightedQuality(options);
            }
            else if (TryGetByCodeOrJsonKey(AircraftRwrQuality, _playerAircraft.definition, out int quality))
            {
                _currentRwrQuality = quality;
            }
            ApplyOverlayVisibility();
            ShowSplashScreen();
            StartWarningPanelStartupSequence();

            WriteDebug($"Subscribed to onRadarWarning for aircraft '{_playerAircraft.name}', RWR quality={_currentRwrQuality}.");
        }

        private void ResetState()
        {
            if (_subscribed && _playerAircraft != null)
            {
                try
                {
                    _playerAircraft.onRadarWarning -= OnRadarWarningReceived;
                    _playerAircraft.onJam -= OnJamReceived;
                }
                catch
                {
                    // best-effort unsubscribe from a possibly-stale instance
                }
            }

            if (_missileWarningSubscribed && _missileWarningSystem != null)
            {
                try
                {
                    _missileWarningSystem.onMissileWarning -= OnMissileWarningReceived;
                    _missileWarningSystem.offMissileWarning -= OnMissileWarningEnded;
                }
                catch
                {
                    // best-effort unsubscribe from a possibly-stale instance
                }
            }

            foreach (ArhMissileContact arhContact in _arhMissileContacts.Values)
            {
                if (arhContact.Group != null)
                {
                    Destroy(arhContact.Group.gameObject);
                }
            }

            foreach (JamGhostContact ghost in _jamGhostContacts)
            {
                if (ghost.Group != null)
                {
                    Destroy(ghost.Group.gameObject);
                }
            }

            _contacts.Clear();
            _sarhThreatCounts.Clear();
            _arhMissileContacts.Clear();
            _irMissileContacts.Clear();
            _arhConnectingLines.Clear();
            _jamGhostContacts.Clear();
            _lastJamTime = float.NegativeInfinity;
            _nextJamBatchTime = 0f;
            _playerAircraft = null;
            _subscribed = false;
            _currentRwrQuality = DefaultRwrQuality;
            _missileWarningSystem = null;
            _missileWarningSubscribed = false;
            _missileWarningCheckedAircraft = null;
            _built = false;
            _scopeRoot = null;
            _normalOverlayRoot = null;
            _rank0OverlayRoot = null;
            _rank2TicksOverlayRoot = null;
            _normalRingImage = null;
            _normalHalfRingImage = null;
            _normalReticleHorizontalImage = null;
            _normalReticleVerticalImage = null;
            _rank0RingImage = null;
            _rank0HalfRingImage = null;
            _rank0CrossHorizontalImage = null;
            _rank0CrossVerticalImage = null;
            Array.Clear(_rank0IrArcImages, 0, _rank0IrArcImages.Length);
            _rank0IrRingVisible = false;
            _airInterceptBorder = null;
            _airInterceptLabel = null;
            _airInterceptLastPing = float.NegativeInfinity;
            _navalBorder = null;
            _navalLabel = null;
            _navalLastPing = float.NegativeInfinity;
            _radarTruckBorder = null;
            _radarTruckLabel = null;
            _radarTruckLastPing = float.NegativeInfinity;
            _boltstrikeBorder = null;
            _boltstrikeLabel = null;
            _boltstrikeLastPing = float.NegativeInfinity;
            _rank2TickImages.Clear();
            _rank4NotchOverlayRoot = null;
            _rank4NotchLines.Clear();
            _rank4IrOverlayRoot = null;
            Array.Clear(_rank4IrArcImages, 0, _rank4IrArcImages.Length);
            _rank4IrRingVisible = false;
            _jamLobOverlayRoot = null;
            _jamLobLines.Clear();
            _lastJammingUnit = null;
            _normalInnerElements = null;
            _rank0InnerElements = null;
            _contactsOverlayRoot = null;
            _priorityDiamondImage = null;
            _splashOverlayRoot = null;
            _splashTitleText = null;
            _splashSubtitleText = null;
            _splashVersionText = null;
            _splashStatusText = null;
            _splashStatusLines = null;
            _splashActive = false;
            _warningPanelRoot = null;
            _warningPanelBackground = null;
            _tgtLightBorder = null;
            _tgtLightLabel = null;
            _mslLightBorder = null;
            _mslLightLabel = null;
            _seenLightBorder = null;
            _seenLightLabel = null;
            _hiLoDiagonal = null;
            _hiBorder = null;
            _hiLabel = null;
            _loBorder = null;
            _loLabel = null;
            _priorityEmitter = null;
            _tgtLightState = default;
            _seenLightState = default;
            _startupPhase = WarningPanelStartupPhase.Done;
        }

        // Ground SARH launchers can borrow a nearby radar truck's radar
        // instead of carrying their own (e.g. the MSV R9 Stratolance
        // Launcher uses whichever HLT-R/MSV Radar truck is within ~200m).
        // missile.owner is the launcher there, but the launcher never emits
        // radar itself and so never becomes a contact -- flashing by owner
        // alone misses it. SARHSeeker.radarSource.attachedUnit is the unit
        // actually illuminating us (both fields private, hence reflection);
        // falls back to missile.owner for all-in-one systems like the
        // T9K41 Boltstrike, where they're the same unit anyway.
        private static readonly FieldInfo SarhRadarSourceField =
            typeof(SARHSeeker).GetField("radarSource", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RadarAttachedUnitField =
            typeof(Radar).GetField("attachedUnit", BindingFlags.NonPublic | BindingFlags.Instance);

        private static Unit GetSarhSourceUnit(SARHSeeker seeker, Missile missile)
        {
            if (SarhRadarSourceField != null)
            {
                object radar = SarhRadarSourceField.GetValue(seeker);
                if (radar != null && RadarAttachedUnitField != null
                    && RadarAttachedUnitField.GetValue(radar) is Unit attachedUnit)
                {
                    return attachedUnit;
                }
            }
            return missile.owner;
        }

        // PERF: tracked separately from _missileWarningSystem so this can
        // skip the GetComponent call entirely once already subscribed for
        // the current aircraft, instead of paying that cost every frame.
        private Aircraft _missileWarningCheckedAircraft;

        private void EnsureMissileWarningSubscribed()
        {
            if (_playerAircraft == null)
            {
                return;
            }

            if (_missileWarningSubscribed && _missileWarningCheckedAircraft == _playerAircraft)
            {
                return;
            }

            MissileWarning missileWarning = _playerAircraft.GetComponent<MissileWarning>();
            if (missileWarning == null)
            {
                return;
            }

            if (_missileWarningSubscribed && _missileWarningSystem == missileWarning)
            {
                return;
            }

            if (_missileWarningSubscribed && _missileWarningSystem != null)
            {
                try
                {
                    _missileWarningSystem.onMissileWarning -= OnMissileWarningReceived;
                    _missileWarningSystem.offMissileWarning -= OnMissileWarningEnded;
                }
                catch
                {
                    // best-effort unsubscribe from a possibly-stale instance
                }

                // Any "off" event for a missile/launcher tracked against the
                // OLD aircraft will never arrive now that we've unsubscribed
                // from its MissileWarning -- without this, a SARH flash or
                // ARH icon from a previous life (respawn without a full
                // mission restart) could get stuck on-screen forever.
                foreach (ArhMissileContact arhContact in _arhMissileContacts.Values)
                {
                    if (arhContact.Group != null)
                    {
                        Destroy(arhContact.Group.gameObject);
                    }
                }
                _arhMissileContacts.Clear();
                _sarhThreatCounts.Clear();
            }

            _missileWarningSystem = missileWarning;
            _missileWarningSystem.onMissileWarning += OnMissileWarningReceived;
            _missileWarningSystem.offMissileWarning += OnMissileWarningEnded;
            _missileWarningSubscribed = true;
            _missileWarningCheckedAircraft = _playerAircraft;
            WriteDebug($"Subscribed to MissileWarning for aircraft '{_playerAircraft.name}'.");
        }

        private void OnMissileWarningReceived(MissileWarning.OnMissileWarning e)
        {
            try
            {
                Missile missile = e.missile;
                if (missile == null)
                {
                    return;
                }

                // seekerMode (active/passive) only tells us whether the
                // missile's own radar is transmitting -- IR/ARAD/Optical
                // are just as "passive" as SARH is, so that alone can't
                // tell them apart. The seeker component's actual type can.
                MissileSeeker seeker = missile.GetComponent<MissileSeeker>();

                if (seeker is ARHSeeker)
                {
                    // ARH: the icon itself is now created/kept alive by
                    // radar pings (OnRadarWarningReceived), not here -- this
                    // just confirms the missile is actually locked onto/
                    // guiding toward the player, which is what unlocks the
                    // connecting line on ranks 0-3 (see UpdateArhMissileContacts).
                    // Defensive CreateArhMissileContact call in case
                    // MissileWarning somehow fires before any radar ping has
                    // -- shouldn't normally happen, but avoids a null lookup
                    // below if it does.
                    CreateArhMissileContact(missile);
                    if (_arhMissileContacts.TryGetValue(missile, out ArhMissileContact arhContact))
                    {
                        arhContact.HasMissileWarning = true;
                    }
                }
                else if (seeker is SARHSeeker sarhSeeker)
                {
                    // SARH: flash whichever unit is actually illuminating us
                    // with radar, if we have a contact for them. Reference-
                    // counted since multiple SARH missiles could be inbound
                    // from the same source at once. IR/ARAD/Optical/etc.
                    // intentionally do neither.
                    Unit sarhSourceUnit = GetSarhSourceUnit(sarhSeeker, missile);
                    if (sarhSourceUnit != null)
                    {
                        _sarhThreatCounts[sarhSourceUnit] = _sarhThreatCounts.TryGetValue(sarhSourceUnit, out int count) ? count + 1 : 1;
                    }
                }
                else if (seeker is IRSeeker)
                {
                    // IR (heat-seeking): no radar of its own for
                    // onRadarWarning to ever pick up, so MissileWarning is
                    // the only signal available at all, same reasoning as
                    // SARH above. Consumed by both Rank 0 and Rank 4's
                    // warning rings (UpdateIrWarningRing) -- tracked
                    // regardless of rank since a HashSet add is cheap and
                    // there's no reason to miss the ON event just because
                    // the current rank doesn't use it.
                    _irMissileContacts.Add(missile);
                }

                WriteDebug($"Missile warning ON: {missile.name} seekerType={(seeker != null ? seeker.GetType().Name : "null")} owner={(missile.owner != null ? missile.owner.name : "null")}");
            }
            catch (Exception ex)
            {
                WriteDebug($"EXCEPTION in OnMissileWarningReceived: {ex}");
            }
        }

        private void OnMissileWarningEnded(MissileWarning.OffMissileWarning e)
        {
            try
            {
                Missile missile = e.missile;
                if (missile == null)
                {
                    return;
                }

                // Lost lock on the player specifically -- doesn't mean the
                // missile stopped radar-searching nearby, so only clear the
                // "confirmed threat" flag (hides the line again on ranks
                // 0-3) rather than destroying the contact. The icon itself
                // now lives/dies by radar-ping staleness in
                // UpdateArhMissileContacts, same as any other contact.
                if (_arhMissileContacts.TryGetValue(missile, out ArhMissileContact arhContact))
                {
                    arhContact.HasMissileWarning = false;
                }

                _irMissileContacts.Remove(missile);

                // Must resolve the exact same source unit as OnMissileWarningReceived
                // did when it incremented, or the reference count never balances.
                if (missile.GetComponent<MissileSeeker>() is SARHSeeker sarhSeeker)
                {
                    Unit sarhSourceUnit = GetSarhSourceUnit(sarhSeeker, missile);
                    if (sarhSourceUnit != null && _sarhThreatCounts.TryGetValue(sarhSourceUnit, out int count))
                    {
                        if (count <= 1)
                        {
                            _sarhThreatCounts.Remove(sarhSourceUnit);
                        }
                        else
                        {
                            _sarhThreatCounts[sarhSourceUnit] = count - 1;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteDebug($"EXCEPTION in OnMissileWarningEnded: {ex}");
            }
        }

        private void CreateArhMissileContact(Missile missile)
        {
            if (_scopeRoot == null || _arhMissileContacts.ContainsKey(missile))
            {
                return;
            }

            RectTransform group = CreateContactGroup(_contactsOverlayRoot, "ArhMissile_" + missile.name, ComputeGridPosition(missile.transform.position));
            (RectTransform symbolTransform, Image symbolImage, Text symbolLetter) = BuildMissileSymbol(group, Vector2.zero, "M");

            // Rank 0 RWRs can't classify ARH missiles at all -- just the
            // red "M" icon, no designation label ever appears.
            Text label = null;
            if (_currentRwrQuality != 0)
            {
                // Starts unidentified and resolves to its real designation
                // after a short delay, like a real RWR classifying a new track.
                label = CreateLabel(group, "???", new Vector2(0f, SymbolToLabelOffset), 11, WithOpacity(new Color(ThreatColor.r, ThreatColor.g, ThreatColor.b, 0.95f)));
            }

            _arhMissileContacts[missile] = new ArhMissileContact
            {
                Group = group,
                SymbolTransform = symbolTransform,
                SymbolImage = symbolImage,
                SymbolLetter = symbolLetter,
                DesignationLabel = label,
                CreationTime = Time.unscaledTime,
                Resolved = _currentRwrQuality == 0,
                LastRadarPingTime = Time.unscaledTime,
                HasMissileWarning = false,
            };
        }

        // Called from OnRadarWarningReceived every time this missile's own
        // seeker radar is detected -- creates the contact if it doesn't
        // exist yet (first ping) and refreshes its staleness timer either
        // way, so the icon's lifecycle tracks radar visibility rather than
        // MissileWarning.
        private void RegisterArhRadarPing(Missile missile)
        {
            CreateArhMissileContact(missile);
            if (_arhMissileContacts.TryGetValue(missile, out ArhMissileContact contact))
            {
                contact.LastRadarPingTime = Time.unscaledTime;
            }
        }

        // The three ARH missiles in the game, keyed by jsonKey since they
        // all share the generic code "MSL".
        private static readonly Dictionary<string, string> MissileCodeOverrides = new Dictionary<string, string>
        {
            { "ARH1", "98" }, // NL-98
            { "AAM4", "36" }, // AAM-36 Scimitar
            { "AAM2", "29" }, // AAM-29 Scythe
            { "Aryx_Missile_AAM45", "45" }, // AAM-45 Sabre (FS-41 Eclipse)
        };

        private static string GetMissileDesignation(Missile missile)
        {
            if (missile.definition != null && !string.IsNullOrEmpty(missile.definition.jsonKey)
                && MissileCodeOverrides.TryGetValue(missile.definition.jsonKey, out string code))
            {
                return code;
            }

            return "???";
        }

        private void UpdateArhMissileContacts()
        {
            foreach (GameObject line in _arhConnectingLines)
            {
                if (line != null)
                {
                    Destroy(line);
                }
            }
            _arhConnectingLines.Clear();

            if (_arhMissileContacts.Count == 0 || _playerAircraft == null)
            {
                return;
            }

            List<Missile> stale = null;

            foreach (KeyValuePair<Missile, ArhMissileContact> kvp in _arhMissileContacts)
            {
                Missile missile = kvp.Key;
                ArhMissileContact contact = kvp.Value;

                // Icon lifecycle now tracks radar-ping staleness (same
                // ContactBrightSeconds window the general _contacts
                // dictionary uses) rather than MissileWarning, since the
                // missile might still exist and still be radar-searching
                // after it stops threatening the player specifically.
                bool radarSilent = Time.unscaledTime - contact.LastRadarPingTime > ContactBrightSeconds;
                if (missile == null || contact.Group == null || radarSilent)
                {
                    if (contact.Group != null)
                    {
                        Destroy(contact.Group.gameObject);
                    }
                    if (stale == null)
                    {
                        stale = new List<Missile>();
                    }
                    stale.Add(missile);
                    continue;
                }

                Vector2 localPosition = GridToLocalPosition(ComputeGridPosition(missile.transform.position));
                contact.Group.anchoredPosition = localPosition;

                // Recomputed every frame (rather than once at creation) so
                // a live RWR Opacity / Threat Color change in ConfigManager
                // shows up immediately on already-existing missile contacts.
                Color missileColor = WithOpacity(new Color(ThreatColor.r, ThreatColor.g, ThreatColor.b, 1f));
                if (contact.SymbolImage != null)
                {
                    contact.SymbolImage.color = missileColor;
                }
                if (contact.SymbolLetter != null)
                {
                    contact.SymbolLetter.color = missileColor;
                }
                if (contact.DesignationLabel != null)
                {
                    contact.DesignationLabel.color = WithOpacity(new Color(ThreatColor.r, ThreatColor.g, ThreatColor.b, 0.95f));
                }

                if (_currentRwrQuality == 0)
                {
                    contact.Rank0Quadrant = GetQuadrantForWorldPosition(missile.transform.position);
                }

                if (!contact.Resolved && Time.unscaledTime - contact.CreationTime >= CurrentMissileResolveDelaySeconds)
                {
                    contact.DesignationLabel.text = GetMissileDesignation(missile);
                    contact.Resolved = true;
                }

                // Derived from the icon's actual rendered position (not the
                // raw real-world bearing) so the line always points exactly
                // at the icon even where position gets clamped/snapped
                // (e.g. Rank 0's quadrant snap).
                float distance = localPosition.magnitude;
                if (distance > 0.01f)
                {
                    Vector2 direction = localPosition / distance;
                    float bearingDegrees = Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;

                    if (_currentRwrQuality == 4 && contact.SymbolTransform != null)
                    {
                        // The triangle always points back at the player, i.e.
                        // the opposite direction from the icon to center.
                        contact.SymbolTransform.localRotation = Quaternion.Euler(0f, 0f, -(bearingDegrees + 180f));
                    }

                    // Ranks 0-3: only draw the line once this missile is a
                    // confirmed threat to the player (MissileWarning fired),
                    // not just a nearby radar-detected emitter -- the icon
                    // alone communicates "ARH missile radar active nearby,"
                    // the line specifically communicates "this one's locked
                    // onto you." Rank 4's gear is good enough to show the
                    // line unconditionally, matching its existing behavior.
                    if (_currentRwrQuality == 4 || contact.HasMissileWarning)
                    {
                        CreateArhConnectingLine(bearingDegrees, distance);
                    }
                }
            }

            if (stale != null)
            {
                foreach (Missile missile in stale)
                {
                    _arhMissileContacts.Remove(missile);
                }
            }
        }

        // Solid for every rank but 4, dotted for Rank 4. PERF: rebuilt
        // fresh each frame per active missile, same tradeoff as the
        // notch/LOB lines above.
        private void CreateArhConnectingLine(float bearingDegrees, float distance)
        {
            if (_contactsOverlayRoot == null)
            {
                return;
            }

            Color lineColor = WithOpacity(new Color(ThreatColor.r, ThreatColor.g, ThreatColor.b, 0.6f));
            Vector2 direction = BearingToDirection(bearingDegrees);
            Quaternion rotation = Quaternion.Euler(0f, 0f, -bearingDegrees);

            if (_currentRwrQuality == 4)
            {
                float pitch = ArhLineDashLength + ArhLineDashGap;
                int dashCount = Mathf.Max(1, Mathf.FloorToInt(distance / pitch));

                for (int i = 0; i < dashCount; i++)
                {
                    float offset = (i * pitch) + (ArhLineDashLength / 2f);
                    if (offset > distance)
                    {
                        break;
                    }

                    GameObject dashObject = new GameObject("ArhConnectingDash", typeof(RectTransform), typeof(Image));
                    RectTransform rect = dashObject.GetComponent<RectTransform>();
                    rect.SetParent(_contactsOverlayRoot, false);
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.sizeDelta = new Vector2(ArhLineThickness, ArhLineDashLength);
                    rect.anchoredPosition = direction * offset;
                    rect.localRotation = rotation;

                    Image dashImage = dashObject.GetComponent<Image>();
                    dashImage.color = lineColor;
                    dashImage.raycastTarget = false;

                    _arhConnectingLines.Add(dashObject);
                }
            }
            else
            {
                GameObject lineObject = new GameObject("ArhConnectingLine", typeof(RectTransform), typeof(Image));
                RectTransform lineRect = lineObject.GetComponent<RectTransform>();
                lineRect.SetParent(_contactsOverlayRoot, false);
                lineRect.anchorMin = new Vector2(0.5f, 0.5f);
                lineRect.anchorMax = new Vector2(0.5f, 0.5f);
                lineRect.pivot = new Vector2(0.5f, 0.5f);
                lineRect.sizeDelta = new Vector2(ArhLineThickness, distance);
                lineRect.anchoredPosition = direction * (distance / 2f);
                lineRect.localRotation = rotation;

                Image lineImage = lineObject.GetComponent<Image>();
                lineImage.color = lineColor;
                lineImage.raycastTarget = false;

                _arhConnectingLines.Add(lineObject);
            }
        }

        // Normally Rank 4 only; "Enable Notch line display for every Rank"
        // (General tab) extends it down to ranks 1-3 too. Rank 0 is
        // excluded either way -- it doesn't distinguish targeted-vs-not in
        // the first place (no color coding at that quality), so there's no
        // "targeted by an emitter" signal to hang a notch line off of.
        private static bool ShouldShowNotchLine(int rwrQuality)
        {
            return rwrQuality == 4 || (NotchLineOnAllRanks && rwrQuality >= 1 && rwrQuality <= 3);
        }

        // A notch line 90 degrees off the bearing of anything locking the
        // player or any inbound missile (ARH/SARH) -- flying that heading
        // is what actually notches a Doppler-guided threat, unlike a line
        // pointing straight at it. PERF: rebuilt from scratch every frame
        // rather than diffed per-threat -- simpler, and fine since the
        // threat count stays small.
        private void UpdateRank4NotchLines()
        {
            foreach (GameObject line in _rank4NotchLines)
            {
                if (line != null)
                {
                    Destroy(line);
                }
            }
            _rank4NotchLines.Clear();

            if (_rank4NotchOverlayRoot == null)
            {
                return;
            }

            // ApplyOverlayVisibility() only runs at scope build time and on
            // an actual aircraft swap -- it never re-fires when
            // NotchLineOnAllRanks changes live in ConfigManager. Without
            // this, flipping that toggle mid-flight would create the dash/
            // arc line segments below just fine, but parent them under a
            // GameObject that's still inactive from the last time
            // visibility was computed, so they'd silently not render until
            // the next respawn. Keeping this in sync here every frame
            // covers both triggers (rank change and live config change)
            // with one check. Also has to respect the splash screen's own
            // hiding of this same overlay (SetContactsVisible(false)) --
            // this method runs every frame regardless of splash state, so
            // without the !_splashActive check here it would fight that and
            // re-show the notch line through the splash text.
            bool shouldShow = ShouldShowNotchLine(_currentRwrQuality) && !_splashActive;
            _rank4NotchOverlayRoot.gameObject.SetActive(shouldShow);

            if (!shouldShow || _playerAircraft == null)
            {
                return;
            }

            bool useYellow = Mathf.Repeat(Time.unscaledTime, Rank4NotchFlashInterval * 2f) < Rank4NotchFlashInterval;
            Color flashColor = useYellow ? Rank4NotchColorYellow : Rank4NotchColorOrange;

            foreach (KeyValuePair<Unit, TrackedContact> kvp in _contacts)
            {
                Unit emitter = kvp.Key;
                if (emitter == null)
                {
                    continue;
                }

                bool isLockingTarget = kvp.Value.IsTargeted;
                bool isSarhThreat = _sarhThreatCounts.ContainsKey(emitter);
                if (isLockingTarget || isSarhThreat)
                {
                    CreateRank4NotchLine(Rank4NotchBearing(GetBearingForWorldPosition(emitter.transform.position)), flashColor);
                }
            }

            foreach (KeyValuePair<Missile, ArhMissileContact> kvp in _arhMissileContacts)
            {
                if (kvp.Key != null)
                {
                    CreateRank4NotchLine(Rank4NotchBearing(GetBearingForWorldPosition(kvp.Key.transform.position)), flashColor);
                }
            }
        }

        private void CreateRank4NotchLine(float bearingDegrees, Color color)
        {
            Vector2 direction = BearingToDirection(bearingDegrees);
            Quaternion rotation = Quaternion.Euler(0f, 0f, -bearingDegrees);

            float pitch = Rank4NotchDashLength + Rank4NotchDashGap;
            int dashCount = Mathf.Max(1, Mathf.FloorToInt(Rank4NotchLineLength / pitch));

            for (int i = 0; i < dashCount; i++)
            {
                float offset = (i * pitch) + (Rank4NotchDashLength / 2f);

                GameObject dashObject = new GameObject("Rank4NotchDash", typeof(RectTransform), typeof(Image));
                RectTransform rect = dashObject.GetComponent<RectTransform>();
                rect.SetParent(_rank4NotchOverlayRoot, false);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(Rank4NotchLineThickness, Rank4NotchDashLength);
                rect.anchoredPosition = direction * offset;
                rect.localRotation = rotation;

                Image dashImage = dashObject.GetComponent<Image>();
                dashImage.color = color;
                dashImage.raycastTarget = false;

                _rank4NotchLines.Add(dashObject);
            }

            // Small bar at the ring where the dotted line arrives. Long-X/
            // thin-Y, the opposite axes from the dashes above (long-Y/
            // thin-X) -- so the same rotation as the dashes keeps it
            // perpendicular to them regardless of the actual bearing.
            GameObject arcObject = new GameObject("Rank4NotchArc", typeof(RectTransform), typeof(Image));
            RectTransform arcRect = arcObject.GetComponent<RectTransform>();
            arcRect.SetParent(_rank4NotchOverlayRoot, false);
            arcRect.anchorMin = new Vector2(0.5f, 0.5f);
            arcRect.anchorMax = new Vector2(0.5f, 0.5f);
            arcRect.pivot = new Vector2(0.5f, 0.5f);
            arcRect.sizeDelta = new Vector2(Rank4NotchArcLength, Rank4NotchArcThickness);
            arcRect.anchoredPosition = direction * Rank4NotchLineLength;
            arcRect.localRotation = rotation;

            Image arcImage = arcObject.GetComponent<Image>();
            arcImage.color = color;
            arcImage.raycastTarget = false;

            _rank4NotchLines.Add(arcObject);
        }

        // Tracks whether each ring was left showing anything last frame, so
        // the "nothing to do" path below can skip touching its Images
        // entirely once they're already all off, instead of re-issuing
        // SetActive(false) calls every single frame regardless.
        private bool _rank0IrRingVisible;
        private bool _rank4IrRingVisible;

        // Cleans up a missile destroyed without a matching OffMissileWarning
        // ever arriving (shouldn't normally happen, but avoids leaking an
        // entry forever if it does). Split out from UpdateIrWarningRing and
        // run once per frame regardless of rank -- _irMissileContacts is
        // shared between the Rank 0 and Rank 4 rings, and tying this
        // cleanup to one specific ring's own "is it relevant this frame"
        // check would mean it never runs at all while flying anything
        // ranked 1-3.
        private void CleanupStaleIrMissiles()
        {
            if (_irMissileContacts.Count == 0)
            {
                return;
            }

            List<Missile> stale = null;
            foreach (Missile missile in _irMissileContacts)
            {
                if (missile == null)
                {
                    if (stale == null)
                    {
                        stale = new List<Missile>();
                    }
                    stale.Add(missile);
                }
            }
            if (stale != null)
            {
                foreach (Missile missile in stale)
                {
                    _irMissileContacts.Remove(missile);
                }
            }
        }

        // Shared by the Rank 0 (4-division) and Rank 4 (8-division) IR
        // warning rings. Recomputes which division(s) currently have an
        // inbound IR missile every frame (a missile's bearing can drift as
        // it flies, same reasoning as ArhMissileContact.Rank0Quadrant),
        // then flashes just those. Checks _splashActive directly rather
        // than relying on a separate visibility-sync call --
        // UpdateRank4NotchLines() originally didn't, and a live
        // ConfigManager toggle silently not taking effect until next
        // respawn was the bug that came from it.
        private void UpdateIrWarningRing(Image[] arcImages, int targetRank, ref bool wasVisible)
        {
            if (arcImages[0] == null)
            {
                return;
            }

            // No IR missile has an active MissileWarning at all -- by far
            // the common case -- so skip the per-division array allocation/
            // loop/SetActive calls below entirely rather than paying that
            // cost every frame for nothing. Only actually touches the
            // Images once, the frame the last threat clears, rather than
            // repeating no-op SetActive(false) calls forever afterward.
            bool relevant = _currentRwrQuality == targetRank && _playerAircraft != null && !_splashActive && _irMissileContacts.Count > 0;
            if (!relevant)
            {
                if (wasVisible)
                {
                    for (int i = 0; i < arcImages.Length; i++)
                    {
                        arcImages[i].gameObject.SetActive(false);
                    }
                    wasVisible = false;
                }
                return;
            }

            wasVisible = true;

            int divisionCount = arcImages.Length;
            float divisionSpan = 360f / divisionCount;
            bool[] divisionThreatened = new bool[divisionCount];
            foreach (Missile missile in _irMissileContacts)
            {
                if (missile == null)
                {
                    continue;
                }
                float bearing = ((GetBearingForWorldPosition(missile.transform.position) % 360f) + 360f) % 360f;
                divisionThreatened[Mathf.Clamp((int)(bearing / divisionSpan), 0, divisionCount - 1)] = true;
            }

            bool flashOn = Mathf.Repeat(Time.unscaledTime, IrFlashInterval * 2f) < IrFlashInterval;
            for (int i = 0; i < divisionCount; i++)
            {
                arcImages[i].color = IrWarningColor;
                arcImages[i].gameObject.SetActive(divisionThreatened[i] && flashOn);
            }
        }

        // Solid theme color for Rank0IndicatorPingHoldSeconds after the
        // last matching ping, then a linear fade to off over
        // Rank0IndicatorFadeSeconds -- a fresh ping before the fade starts
        // just pushes LastPing forward, refreshing the hold rather than
        // restarting anything (no separate "flash" stage like TGT/SEEN).
        private static Color Rank0IndicatorColor(float lastPingTime, float holdSeconds)
        {
            float sincePing = Time.unscaledTime - lastPingTime;
            if (sincePing <= holdSeconds)
            {
                return Rank0IndicatorActiveColor;
            }

            float fadeT = (sincePing - holdSeconds) / Rank0IndicatorFadeSeconds;
            if (fadeT >= 1f)
            {
                return WarningLightOffColor;
            }

            return Color.Lerp(Rank0IndicatorActiveColor, WarningLightOffColor, fadeT);
        }

        // R9/T9 track a live state (an SARH missile currently guiding on
        // the player, sourced from one of these specific units), not a
        // discrete ping event -- unlike A/I/NVL, there's no fixed hold
        // window here (holdSeconds passed as 0f below): LastPing just gets
        // refreshed every single frame the threat is still active, so the
        // light stays solid for exactly as long as the threat does and
        // starts its 1s fade the instant that stops, instead of lingering
        // "on" for a few extra seconds after the missile's actually gone.
        // Single pass over _sarhThreatCounts checking both jsonKey sets at
        // once, rather than two separate full iterations of the same
        // dictionary every frame.
        private void RefreshGroundSarhSourcePings()
        {
            foreach (Unit unit in _sarhThreatCounts.Keys)
            {
                if (unit == null || unit.definition == null)
                {
                    continue;
                }
                string jsonKey = unit.definition.jsonKey;
                if (jsonKey == "HLT-R" || jsonKey == "Truck2-R" || jsonKey == "MC260_RadarContainer")
                {
                    _radarTruckLastPing = Time.unscaledTime;
                }
                else if (jsonKey == "RadarSAM1")
                {
                    _boltstrikeLastPing = Time.unscaledTime;
                }
            }
        }

        private void UpdateRank0CornerIndicators()
        {
            if (_airInterceptBorder == null)
            {
                return;
            }

            if (_currentRwrQuality != 0 || _splashActive)
            {
                ApplyLightColor(_airInterceptBorder, _airInterceptLabel, WarningLightOffColor);
                ApplyLightColor(_navalBorder, _navalLabel, WarningLightOffColor);
                ApplyLightColor(_radarTruckBorder, _radarTruckLabel, WarningLightOffColor);
                ApplyLightColor(_boltstrikeBorder, _boltstrikeLabel, WarningLightOffColor);
                return;
            }

            RefreshGroundSarhSourcePings();

            ApplyLightColor(_airInterceptBorder, _airInterceptLabel, Rank0IndicatorColor(_airInterceptLastPing, Rank0IndicatorPingHoldSeconds));
            ApplyLightColor(_navalBorder, _navalLabel, Rank0IndicatorColor(_navalLastPing, Rank0IndicatorPingHoldSeconds));
            ApplyLightColor(_radarTruckBorder, _radarTruckLabel, Rank0IndicatorColor(_radarTruckLastPing, 0f));
            ApplyLightColor(_boltstrikeBorder, _boltstrikeLabel, Rank0IndicatorColor(_boltstrikeLastPing, 0f));
        }

        private void OnRadarWarningReceived(Aircraft.OnRadarWarning e)
        {
            try
            {
                if (e.emitter == null)
                {
                    return;
                }

                if (e.emitter is Missile missileEmitter)
                {
                    // ARH missiles have their own active seeker radar, so
                    // they show up here like any other emitter -- this is
                    // now what actually drives the "M" icon's existence,
                    // not MissileWarning (which only sets HasMissileWarning,
                    // see OnMissileWarningReceived/Ended). SARH/IR/other
                    // missile types have no radar of their own to detect,
                    // so they fall straight through to the same "do
                    // nothing here" behavior as before -- SARH is still
                    // handled entirely by the MissileWarning flash logic.
                    if (missileEmitter.GetComponent<MissileSeeker>() is ARHSeeker)
                    {
                        RegisterArhRadarPing(missileEmitter);
                    }
                    return;
                }

                // Rank 0 A/I & NVL corner lamps -- tracked unconditionally
                // (like _sarhThreatCounts etc.) regardless of current rank,
                // so the state is already correct/fresh whenever the player
                // is actually at Rank 0 to see it. IsTreatedAsShip() checked
                // first, same precedence as CreateContact()'s symbol pick,
                // so a Playable Ships unit (Aircraft-typed under the hood)
                // pings NVL instead of A/I. R9/T9 aren't driven from here at
                // all -- they track live SARH-threat state instead (see
                // RefreshGroundSarhSourcePings(), called from
                // UpdateRank0CornerIndicators()), not raw radar pings.
                if (IsTreatedAsShip(e.emitter))
                {
                    _navalLastPing = Time.unscaledTime;
                }
                else if (e.emitter is Aircraft)
                {
                    _airInterceptLastPing = Time.unscaledTime;
                }

                if (_currentRwrQuality == 4 && IsCurrentlyJammed())
                {
                    // Rank 4 loses its radar picture entirely while jammed
                    // -- no new contacts, and existing ones stop refreshing
                    // (so they decay through the normal stale/fade cycle).
                    return;
                }

                if (!_contacts.TryGetValue(e.emitter, out TrackedContact contact))
                {
                    contact = CreateContact(e.emitter);
                    _contacts[e.emitter] = contact;
                    WriteDebug($"New contact: {e.emitter.name} ({e.emitter.GetType().Name})");
                }

                contact.LastSeenTime = Time.unscaledTime;
                // Rank 0 can't tell a targeting radar apart from a searching
                // one -- no red-on-lock color change on the scope itself at
                // that quality. The warning panel's TGT light is a separate
                // system, though, and isn't rank-gated -- it reads e.isTarget
                // directly rather than contact.IsTargeted, so it still fires
                // at Rank 0 even though that contact's own icon stays green.
                contact.IsTargeted = _currentRwrQuality != 0 && e.isTarget;
                contact.BaseColor = contact.IsTargeted ? TargetedColor : ContactColor;
                SetContactColor(contact, contact.BaseColor);

                if (e.isTarget)
                {
                    TriggerSpike(ref _tgtLightState);
                }
                // Mirrors the minimap's own grey/yellow/red ping coloring --
                // grey (e.detected false) is untouched, yellow (detected,
                // not targeted) and red (targeted, always also detected)
                // both count as "seen" here, so SEEN and TGT can flash
                // together on the same targeting ping.
                if (e.detected)
                {
                    TriggerSeenPing(ref _seenLightState);
                }
            }
            catch (Exception ex)
            {
                WriteDebug($"EXCEPTION in OnRadarWarningReceived: {ex}");
            }
        }

        private void UpdateContacts()
        {
            if (_contacts.Count == 0 || _playerAircraft == null)
            {
                // Otherwise the diamond would stay stuck at its last
                // position after every contact fades out, since the rest of
                // this method (and the UpdatePriorityDiamond() call at the
                // end of it) never runs.
                if (_priorityDiamondImage != null)
                {
                    _priorityDiamondImage.gameObject.SetActive(false);
                }
                return;
            }

            float now = Time.unscaledTime;
            List<Unit> expired = null;

            foreach (KeyValuePair<Unit, TrackedContact> kvp in _contacts)
            {
                Unit emitter = kvp.Key;
                TrackedContact contact = kvp.Value;
                float silentFor = now - contact.LastSeenTime;

                float totalLifetime = ContactBrightSeconds + ContactDarkSeconds + ContactFadeOutSeconds;
                if (emitter == null || silentFor > totalLifetime)
                {
                    if (contact.Group != null)
                    {
                        Destroy(contact.Group.gameObject);
                    }

                    if (expired == null)
                    {
                        expired = new List<Unit>();
                    }
                    expired.Add(emitter);
                    continue;
                }

                RepositionContact(contact, emitter);

                if (_currentRwrQuality == 0)
                {
                    int quadrant = GetQuadrantForWorldPosition(emitter.transform.position);
                    if (quadrant != contact.Rank0Quadrant)
                    {
                        contact.Rank0Quadrant = quadrant;
                        contact.Rank0QuadrantEnteredTime = now;
                    }
                }

                bool isSarhThreat = _sarhThreatCounts.ContainsKey(emitter);

                // Threat contacts (actively locking you, or a SARH launcher
                // guiding on you) render above everything else sharing
                // _contactsOverlayRoot -- sibling order is what determines
                // Unity UI draw order, so re-asserting last-sibling every
                // frame keeps them on top even as other contacts spawn in
                // after them. Applies at every rank; the flashing SARH
                // colors alternate red/yellow, but the contact stays
                // elevated for the whole threat, not just the red half.
                if (contact.IsTargeted || isSarhThreat)
                {
                    contact.Group.SetAsLastSibling();
                }

                if (_currentRwrQuality == 4 && IsCurrentlyJammed() && !isSarhThreat)
                {
                    // Blank immediately rather than waiting out the normal
                    // stale/fade cycle -- jamming denies the picture, it
                    // doesn't just interrupt updates. Actively-inbound
                    // missile threats (SARH launchers, handled above; ARH
                    // icons are a separate system entirely) still get through.
                    Color hidden = contact.BaseColor;
                    hidden.a = 0f;
                    SetContactColor(contact, hidden);
                }
                else if (isSarhThreat)
                {
                    if (_currentRwrQuality == 0)
                    {
                        // A smooth fade, not a hard toggle -- PingPong's
                        // triangle wave is the right tool here, used as a
                        // continuous 0..1 alpha rather than compared against
                        // a threshold.
                        float alphaMultiplier = Mathf.PingPong(Time.unscaledTime, SarhOpacityFlashInterval) / SarhOpacityFlashInterval;
                        Color pulsed = Rank0SarhPulseColor;
                        pulsed.a *= alphaMultiplier;
                        SetContactColor(contact, pulsed);
                    }
                    else
                    {
                        bool useColorA = Mathf.Repeat(Time.unscaledTime, SarhFlashInterval * 2f) < SarhFlashInterval;
                        SetContactColor(contact, useColorA ? SarhFlashColorA : SarhFlashColorB);
                    }
                }
                else if (silentFor > ContactBrightSeconds + ContactDarkSeconds)
                {
                    // Final quick fade from the dimmed level to fully gone.
                    float fadeT = Mathf.Clamp01((silentFor - ContactBrightSeconds - ContactDarkSeconds) / ContactFadeOutSeconds);
                    Color fading = contact.BaseColor;
                    fading.a *= DarkenedAlphaFactor * (1f - fadeT);
                    SetContactColor(contact, fading);
                }
                else if (silentFor > ContactBrightSeconds)
                {
                    // Stale hold: dimmed but steady, not yet fading.
                    Color dimmed = contact.BaseColor;
                    dimmed.a *= DarkenedAlphaFactor;
                    SetContactColor(contact, dimmed);
                }
                else
                {
                    SetContactColor(contact, contact.BaseColor);
                }
            }

            if (expired != null)
            {
                foreach (Unit emitter in expired)
                {
                    _contacts.Remove(emitter);
                }
            }

            if (_currentRwrQuality == 0)
            {
                EnforceRank0QuadrantPriority();
            }

            UpdatePriorityDiamond();
        }

        // A hollow diamond tracks whichever contact currently has
        // "priority" -- the closest actively-threatening contact (locked
        // onto the player, or a SARH launcher guiding a missile at them) if
        // any exist; otherwise the closest non-stale contact; a stale
        // (dimmed/fading) contact is only picked if it's the only thing
        // left on the scope at all. Re-run every frame so it keeps
        // following as ranges/threats change. Also sets _priorityEmitter,
        // which the warning panel's HI/LO indicator reads independently of
        // whether the diamond itself is showing.
        // The diamond icon itself is Rank 1+ only -- Rank 0's own quadrant
        // priority system already picks one contact per quadrant, and
        // everything sits at a fixed display radius there so "closest"
        // isn't meaningfully visualized on the scope. _priorityEmitter is
        // still computed at Rank 0 though (just not drawn as a diamond),
        // since HI/LO isn't rank-gated. Both the diamond and _priorityEmitter
        // are hidden/cleared entirely while jammed -- the picture is already
        // unreliable then, so highlighting a "priority" contact out of
        // ghosts/blanked data would be misleading. Reappears on its own
        // once IsCurrentlyJammed() goes false again.
        private void UpdatePriorityDiamond()
        {
            if (_priorityDiamondImage == null)
            {
                return;
            }

            if (_playerAircraft == null || IsCurrentlyJammed())
            {
                _priorityDiamondImage.gameObject.SetActive(false);
                _priorityEmitter = null;
                return;
            }

            float now = Time.unscaledTime;

            TrackedContact closestThreat = null;
            Unit closestThreatEmitter = null;
            float closestThreatDistSq = float.MaxValue;
            TrackedContact closestFresh = null;
            Unit closestFreshEmitter = null;
            float closestFreshDistSq = float.MaxValue;
            TrackedContact closestStale = null;
            Unit closestStaleEmitter = null;
            float closestStaleDistSq = float.MaxValue;

            foreach (KeyValuePair<Unit, TrackedContact> kvp in _contacts)
            {
                Unit emitter = kvp.Key;
                if (emitter == null)
                {
                    continue;
                }

                TrackedContact contact = kvp.Value;
                float distSq = (emitter.transform.position - _playerAircraft.transform.position).sqrMagnitude;

                bool isThreat = contact.IsTargeted || _sarhThreatCounts.ContainsKey(emitter);
                if (isThreat && distSq < closestThreatDistSq)
                {
                    closestThreatDistSq = distSq;
                    closestThreat = contact;
                    closestThreatEmitter = emitter;
                }

                // Mirrors UpdateContacts()'s own coloring split -- an active
                // SARH threat never dims regardless of silence time, so it's
                // never treated as stale here either.
                bool isStale = !isThreat && (now - contact.LastSeenTime) > ContactBrightSeconds;
                if (isStale)
                {
                    if (distSq < closestStaleDistSq)
                    {
                        closestStaleDistSq = distSq;
                        closestStale = contact;
                        closestStaleEmitter = emitter;
                    }
                }
                else if (distSq < closestFreshDistSq)
                {
                    closestFreshDistSq = distSq;
                    closestFresh = contact;
                    closestFreshEmitter = emitter;
                }
            }

            // Stale contacts are only picked as a last resort, when
            // there's nothing fresh or actively threatening left to
            // point at.
            TrackedContact priority = closestThreat ?? closestFresh ?? closestStale;
            Unit priorityEmitter = closestThreat != null ? closestThreatEmitter
                : closestFresh != null ? closestFreshEmitter
                : closestStaleEmitter;
            if (priority == null || priority.Group == null)
            {
                _priorityDiamondImage.gameObject.SetActive(false);
                _priorityEmitter = null;
                return;
            }

            _priorityEmitter = priorityEmitter;

            if (_currentRwrQuality < 1)
            {
                _priorityDiamondImage.gameObject.SetActive(false);
                return;
            }

            _priorityDiamondImage.gameObject.SetActive(true);
            _priorityDiamondImage.rectTransform.anchoredPosition = priority.Group.anchoredPosition;
            _priorityDiamondImage.rectTransform.SetAsLastSibling();

            // Matches whatever the contact itself was just colored this
            // frame (normal/targeted/SARH-flash/stale-dim/fade-out/jammed-
            // hidden) -- this runs after the main per-contact coloring pass
            // above, so SymbolImages[0].color already reflects this frame's
            // final color rather than duplicating that whole state machine.
            if (priority.SymbolImages != null && priority.SymbolImages.Length > 0)
            {
                _priorityDiamondImage.color = priority.SymbolImages[0].color;
            }
        }

        private void RepositionContact(TrackedContact contact, Unit emitter)
        {
            contact.Group.anchoredPosition = GridToLocalPosition(ComputeGridPosition(emitter.transform.position));
        }

        private Vector2 ComputeGridPosition(Vector3 worldPosition)
        {
            Vector3 relative = worldPosition - _playerAircraft.transform.position;
            float right = Vector3.Dot(relative, _playerAircraft.transform.right);
            float forward = Vector3.Dot(relative, _playerAircraft.transform.forward);
            float bearingDegrees = Mathf.Atan2(right, forward) * Mathf.Rad2Deg;

            if (_currentRwrQuality == 0)
            {
                // Rank 0 (old Russian-style RWR): no real range/bearing
                // precision, just which of the four quadrants the threat
                // is roughly in.
                return SnapToQuadrant(bearingDegrees);
            }

            float rangeFraction = Mathf.Clamp01(relative.magnitude / MaxDisplayRangeMeters);

            return BearingRangeToGrid(bearingDegrees, rangeFraction);
        }

        // Quadrant centers (NE/SE/SW/NW), snapped to a fixed radius near
        // the outer edge so all rank-0 contacts sit in one of four spots.
        private static readonly float[] QuadrantBearings = { 45f, 135f, 225f, 315f };

        private static Vector2 SnapToQuadrant(float bearingDegrees)
        {
            float normalized = ((bearingDegrees % 360f) + 360f) % 360f;
            return BearingRangeToGrid(QuadrantBearings[GetQuadrantIndex(normalized)], 1f);
        }

        // 0=NE (0-90), 1=SE (90-180), 2=SW (180-270), 3=NW (270-360).
        private static int GetQuadrantIndex(float normalizedBearingDegrees)
        {
            return Mathf.Clamp((int)(normalizedBearingDegrees / 90f), 0, 3);
        }

        private float GetBearingForWorldPosition(Vector3 worldPosition)
        {
            Vector3 relative = worldPosition - _playerAircraft.transform.position;
            float right = Vector3.Dot(relative, _playerAircraft.transform.right);
            float forward = Vector3.Dot(relative, _playerAircraft.transform.forward);
            return Mathf.Atan2(right, forward) * Mathf.Rad2Deg;
        }

        // GetBearingForWorldPosition() is already relative to the player's
        // own nose (0 = straight ahead), so "closest to the player's
        // current heading" just means picking whichever of the two
        // perpendicular options sits closer to 0 -- i.e. whichever notch
        // heading is the smaller turn away from where the player's already
        // pointed, rather than always adding 90 and sometimes landing the
        // "ideal" notch heading behind the aircraft.
        private static float Rank4NotchBearing(float threatBearingDegrees)
        {
            float optionA = NormalizeBearing(threatBearingDegrees + 90f);
            float optionB = NormalizeBearing(threatBearingDegrees - 90f);
            return Mathf.Abs(optionA) <= Mathf.Abs(optionB) ? optionA : optionB;
        }

        // Wraps to (-180, 180].
        private static float NormalizeBearing(float bearingDegrees)
        {
            return Mathf.Repeat(bearingDegrees + 180f, 360f) - 180f;
        }

        private int GetQuadrantForWorldPosition(Vector3 worldPosition)
        {
            float normalized = ((GetBearingForWorldPosition(worldPosition) % 360f) + 360f) % 360f;
            return GetQuadrantIndex(normalized);
        }

        // Rank 0 only: everything snaps to one of four quadrant spots, so
        // only one contact is ever shown per quadrant -- normally the most
        // recent arrival wins, but if the quadrant has a SARH launcher
        // tracking the player or an inbound ARH missile, the physically
        // closest contender wins instead (recency stops mattering once
        // there's a live threat). Re-run every frame so a contact that
        // wanders into an occupied quadrant, not just a new one, still gets
        // arbitrated. ARH missile icons are never touched here -- separate
        // dictionary, always visible. Jam ghosts compete on the same terms
        // as real contacts (their SimulatedRangeMeters stands in for real
        // range), since Rank 0's gear can't tell them apart anyway.
        private void EnforceRank0QuadrantPriority()
        {
            if (_playerAircraft == null)
            {
                return;
            }

            for (int quadrant = 0; quadrant < 4; quadrant++)
            {
                List<Unit> unitContenders = null;
                List<JamGhostContact> ghostContenders = null;
                bool hasThreat = false;

                foreach (KeyValuePair<Unit, TrackedContact> kvp in _contacts)
                {
                    if (kvp.Value.Rank0Quadrant != quadrant)
                    {
                        continue;
                    }

                    if (unitContenders == null)
                    {
                        unitContenders = new List<Unit>();
                    }
                    unitContenders.Add(kvp.Key);

                    if (_sarhThreatCounts.ContainsKey(kvp.Key))
                    {
                        hasThreat = true;
                    }
                }

                foreach (JamGhostContact ghost in _jamGhostContacts)
                {
                    if (ghost.Rank0Quadrant != quadrant)
                    {
                        continue;
                    }

                    if (ghostContenders == null)
                    {
                        ghostContenders = new List<JamGhostContact>();
                    }
                    ghostContenders.Add(ghost);
                }

                int contenderCount = (unitContenders != null ? unitContenders.Count : 0) + (ghostContenders != null ? ghostContenders.Count : 0);
                if (contenderCount <= 1)
                {
                    continue;
                }

                if (!hasThreat)
                {
                    foreach (ArhMissileContact missileContact in _arhMissileContacts.Values)
                    {
                        if (missileContact.Rank0Quadrant == quadrant)
                        {
                            hasThreat = true;
                            break;
                        }
                    }
                }

                Unit winnerUnit = null;
                JamGhostContact winnerGhost = null;

                if (hasThreat)
                {
                    float bestRange = float.MaxValue;
                    if (unitContenders != null)
                    {
                        foreach (Unit candidate in unitContenders)
                        {
                            float range = Vector3.Distance(candidate.transform.position, _playerAircraft.transform.position);
                            if (range < bestRange)
                            {
                                bestRange = range;
                                winnerUnit = candidate;
                                winnerGhost = null;
                            }
                        }
                    }
                    if (ghostContenders != null)
                    {
                        foreach (JamGhostContact candidate in ghostContenders)
                        {
                            if (candidate.SimulatedRangeMeters < bestRange)
                            {
                                bestRange = candidate.SimulatedRangeMeters;
                                winnerGhost = candidate;
                                winnerUnit = null;
                            }
                        }
                    }
                }
                else
                {
                    float bestTime = float.NegativeInfinity;
                    if (unitContenders != null)
                    {
                        foreach (Unit candidate in unitContenders)
                        {
                            float enteredTime = _contacts[candidate].Rank0QuadrantEnteredTime;
                            if (enteredTime > bestTime)
                            {
                                bestTime = enteredTime;
                                winnerUnit = candidate;
                                winnerGhost = null;
                            }
                        }
                    }
                    if (ghostContenders != null)
                    {
                        foreach (JamGhostContact candidate in ghostContenders)
                        {
                            if (candidate.Rank0QuadrantEnteredTime > bestTime)
                            {
                                bestTime = candidate.Rank0QuadrantEnteredTime;
                                winnerGhost = candidate;
                                winnerUnit = null;
                            }
                        }
                    }
                }

                if (unitContenders != null)
                {
                    foreach (Unit candidate in unitContenders)
                    {
                        if (candidate == winnerUnit)
                        {
                            continue;
                        }

                        TrackedContact loser = _contacts[candidate];
                        if (loser.Group != null)
                        {
                            Destroy(loser.Group.gameObject);
                        }
                        _contacts.Remove(candidate);
                    }
                }

                if (ghostContenders != null)
                {
                    foreach (JamGhostContact candidate in ghostContenders)
                    {
                        if (candidate == winnerGhost)
                        {
                            continue;
                        }

                        if (candidate.Group != null)
                        {
                            Destroy(candidate.Group.gameObject);
                        }
                        _jamGhostContacts.Remove(candidate);
                    }
                }
            }
        }

        private TrackedContact CreateContact(Unit emitter)
        {
            RectTransform group = CreateContactGroup(_contactsOverlayRoot, "Contact_" + emitter.name, Vector2.zero);

            Image[] symbolImages;
            if (IsTreatedAsShip(emitter))
            {
                symbolImages = new[] { CreateBar(group, "ShipSymbol", new Vector2(16f, 2f), new Vector2(0f, ShipSymbolVerticalOffset)) };
            }
            else if (emitter is Aircraft)
            {
                symbolImages = BuildChevronSymbol(group, Vector2.zero);
            }
            else
            {
                symbolImages = new[] { BuildDomeSymbol(group, new Vector2(0f, DomeSymbolVerticalOffset)) };
            }

            string designation = GetDesignation(emitter);
            Text label = CreateLabel(group, designation, new Vector2(0f, SymbolToLabelOffset), 11, ContactColor);

            TrackedContact contact = new TrackedContact
            {
                Group = group,
                SymbolImages = symbolImages,
                Label = label,
                LastSeenTime = Time.unscaledTime,
            };

            RepositionContact(contact, emitter);
            return contact;
        }

        // Hand-picked short RWR codes for the aircraft that actually carry
        // radar (and can therefore ever trigger onRadarWarning), keyed by
        // their real UnitDefinition.code.
        private static readonly Dictionary<string, string> RwrCodeOverrides = new Dictionary<string, string>
        {
            { "FS-20", "F20" },  // Vortex
            { "VT-7", "VT7" },   // Vagrant
            { "AB-4", "AB4" },   // Alkyon
            { "EW-25", "E25" },  // Medusa
            { "SFB-81", "B81" }, // Darkreach
            { "FS-12", "F12" },  // Revoker
            { "KR-67", "K67" },  // Ifrit

            // Blueprinter addon aircraft (jsonKey-keyed). MiG-15/RAH-72
            // Knockout omitted -- no radar, never appear as contacts.
            { "Aryx_LightFighter1", "F99" },   // F-99 Shrike
            { "Aryx_F16M_KingViper", "F16" },  // F-16M King Viper
            { "P_Trisurface1", "FS3" },        // FS-3 Ternion
            { "Aryx_CargoPlane1", "260" },     // MC-260 Chimera
            { "Aryx_Interceptor1", "F41" },    // FS-41 Eclipse
        };

        // Ship classes all share the generic code "SHP" (or "PB"), so those
        // can't be told apart the same way -- keyed by jsonKey instead.
        // Surf Class Patrol Boat has no radar and is omitted.
        private static readonly Dictionary<string, string> ShipCodeOverrides = new Dictionary<string, string>
        {
            { "Frigate1", "FFL" },       // Argus
            { "SmallCarrier1", "CVE" },  // Cursor
            { "Corvette1", "FS" },       // Shard
            { "AssaultCarrier1", "AAS" }, // Annex
            { "Destroyer1", "DDG" },     // Dynamo
            { "FleetCarrier1", "CV" },   // Hyperion

            // Aryx Naval Expansion (NAVEX) + FS-41 Eclipse's pair of carriers.
            { "Aryx_StrikeCarrier1", "CGN" },       // Andromeda Class Cruiser
            { "Aryx_SupplyShip1", "AP" },           // Atlas Class Supply Ship
            { "Aryx_EscortCarrier1", "CVE" },       // Devotion Class Light Carrier
            { "Aryx_LightCATOBAR1", "CV" },         // Helion Class Carrier
            { "Aryx_HeavyFrigate1", "FFG" },        // Ironside Class Frigate
            { "Aryx_Supercarrier1", "CVN" },        // Penumbra Class Supercarrier (FS-41 bundle)
            { "Aryx_MissileFrigate_Styx", "PG" },   // Styx Class Missile Cutter

            // Playable Ships addon -- same code as the vanilla class each
            // one is equivalent to. LandingKraft omitted, same reasoning as
            // Surf Class Patrol Boat above (no radar). PatrolBote isn't
            // equivalent to any existing class, so it just gets a literal
            // "PB" in both tables instead of a realistic/simple pair.
            { "SmallKarrier", "CVE" },        // Cursor-class equivalent
            { "PatrolBote", "PB" },
            { "Korvette1", "FS" },            // Shard-class equivalent
            { "Frickate1", "FFL" },           // Argus-class equivalent
            { "Destroyer1_Player", "DDG" },   // Dynamo-class equivalent
            { "AssaultKarrier", "AAS" },      // Annex-class equivalent
            { "FleetKarrier", "CV" },         // Hyperion-class equivalent
        };

        // "Use Simple Ship Designators" (General tab) alternative to
        // ShipCodeOverrides above -- same jsonKeys, simpler class-name-based
        // codes instead of realistic hull-classification codes. Also
        // incidentally disambiguates two pairs that share a hull code under
        // the real system (CVE: Cursor/Devotion Class, CV: Hyperion/Helion
        // Class).
        private static readonly Dictionary<string, string> SimpleShipCodeOverrides = new Dictionary<string, string>
        {
            { "Frigate1", "ARG" },       // Argus
            { "SmallCarrier1", "CSR" },  // Cursor
            { "Corvette1", "SHD" },      // Shard
            { "AssaultCarrier1", "ANX" }, // Annex
            { "Destroyer1", "DYN" },     // Dynamo
            { "FleetCarrier1", "HYP" },  // Hyperion

            { "Aryx_StrikeCarrier1", "AND" },       // Andromeda Class Cruiser
            { "Aryx_SupplyShip1", "ATL" },          // Atlas Class Supply Ship
            { "Aryx_EscortCarrier1", "DVO" },       // Devotion Class Light Carrier
            { "Aryx_LightCATOBAR1", "HEL" },        // Helion Class Carrier
            { "Aryx_HeavyFrigate1", "IRN" },        // Ironside Class Frigate
            { "Aryx_Supercarrier1", "PNU" },        // Penumbra Class Supercarrier (FS-41 bundle)
            { "Aryx_MissileFrigate_Styx", "STX" },  // Styx Class Missile Cutter

            { "SmallKarrier", "CSR" },        // Cursor-class equivalent
            { "PatrolBote", "PB" },
            { "Korvette1", "SHD" },           // Shard-class equivalent
            { "Frickate1", "ARG" },           // Argus-class equivalent
            { "Destroyer1_Player", "DYN" },   // Dynamo-class equivalent
            { "AssaultKarrier", "ANX" },      // Annex-class equivalent
            { "FleetKarrier", "HYP" },        // Hyperion-class equivalent
        };

        private static Dictionary<string, string> ActiveShipCodeOverrides =>
            UseSimpleShipDesignators ? SimpleShipCodeOverrides : ShipCodeOverrides;

        // Playable Ships addon makes these ship classes flyable, which
        // means the units themselves are Aircraft (so they can be piloted)
        // -- but every other RWR check (symbol shape, designation table)
        // should still treat them as ships. IsTreatedAsShip() is the single
        // choke point for that; every "emitter is Ship" check in this file
        // goes through it instead, checked before any "is Aircraft" branch.
        private static readonly HashSet<string> ShipTypeOverrideJsonKeys = new HashSet<string>
        {
            "SmallKarrier",
            "LandingKraft",
            "PatrolBote",
            "Korvette1",
            "Frickate1",
            "Destroyer1_Player",
            "AssaultKarrier",
            "FleetKarrier",
        };

        private static bool IsTreatedAsShip(Unit emitter)
        {
            if (emitter is Ship)
            {
                return true;
            }
            return emitter.definition != null && !string.IsNullOrEmpty(emitter.definition.jsonKey)
                && ShipTypeOverrideJsonKeys.Contains(emitter.definition.jsonKey);
        }

        // Ground vehicles and buildings, also keyed by jsonKey. Note
        // radarStation1's jsonKey is lowercase-r, unlike the others.
        private static readonly Dictionary<string, string> GroundCodeOverrides = new Dictionary<string, string>
        {
            { "RadarSAM1", "T9" },      // T9K41 Boltstrike (has its own radar, unlike SAMTurret1)
            { "HLT-R", "ROW" },         // HLT Radar Truck
            { "Truck2-R", "ROW" },      // MSV Radar
            { "radarStation1", "EW" }, // Radar Station
            { "MC260_RadarContainer", "CNT" }, // MC-260 Chimera's mobile Radar Container
        };

        // Rank 0's own, cruder set of designators: only a handful of
        // aircraft get identified at all (the rest read "???"), and every
        // ship/ground radar/building is lumped into one generic label
        // rather than getting its own real code.
        private static readonly Dictionary<string, string> Rank0AircraftCodeOverrides = new Dictionary<string, string>
        {
            { "FS-20", "F+" },  // Vortex
            { "VT-7", "F" },    // Vagrant
            { "AB-4", "???" },  // Alkyon
            { "EW-25", "AEW" }, // Medusa
            { "SFB-81", "B" },  // Darkreach
            { "FS-12", "F" },   // Revoker
            { "KR-67", "???" }, // Ifrit

            // Blueprinter addon aircraft. MiG-15/RAH-72 Knockout omitted
            // -- no radar, never appear as contacts.
            { "Aryx_LightFighter1", "F" },     // F-99 Shrike
            { "Aryx_F16M_KingViper", "F" },    // F-16M King Viper
            { "P_Trisurface1", "F+" },         // FS-3 Ternion
            { "Aryx_CargoPlane1", "C" },       // MC-260 Chimera
            { "Aryx_Interceptor1", "???" },    // FS-41 Eclipse
        };

        private string GetRank0Designation(Unit emitter)
        {
            if (IsTreatedAsShip(emitter))
            {
                return "SHP";
            }

            if (emitter is Aircraft)
            {
                if (TryGetByCodeOrJsonKey(Rank0AircraftCodeOverrides, emitter.definition, out string aircraftCode))
                {
                    return aircraftCode;
                }
                return "???";
            }

            // Ground radars and the early-warning radar station.
            if (emitter.definition != null && emitter.definition.jsonKey == "radarStation1")
            {
                return "SRC";
            }
            return "SAM";
        }

        // Rank 1: real per-ship codes and the same ground/radar-station
        // labels as the normal roster, but aircraft get their own trimmed
        // set of codes (mostly matching, except Alkyon reads UNK), and
        // the two radar trucks share a single "9" instead of "ROW".
        private static readonly Dictionary<string, string> Rank1AircraftCodeOverrides = new Dictionary<string, string>
        {
            { "FS-20", "F20" },  // Vortex
            { "VT-7", "T7" },    // Vagrant
            { "AB-4", "UNK" },   // Alkyon
            { "EW-25", "AEW" },  // Medusa
            { "SFB-81", "B81" }, // Darkreach
            { "FS-12", "F12" },  // Revoker
            { "KR-67", "K67" },  // Ifrit

            // Blueprinter addon aircraft. MiG-15/RAH-72 Knockout omitted
            // -- no radar, never appear as contacts.
            { "Aryx_LightFighter1", "F99" },   // F-99 Shrike
            { "Aryx_F16M_KingViper", "F16" },  // F-16M King Viper
            { "P_Trisurface1", "FS3" },        // FS-3 Ternion
            { "Aryx_CargoPlane1", "260" },     // MC-260 Chimera
            { "Aryx_Interceptor1", "UNK" },    // FS-41 Eclipse
        };

        private static readonly Dictionary<string, string> Rank1GroundCodeOverrides = new Dictionary<string, string>
        {
            { "RadarSAM1", "T9" },     // T9K41 Boltstrike
            { "HLT-R", "9" },          // HLT Radar Truck
            { "Truck2-R", "9" },       // MSV Radar
            { "radarStation1", "EW" }, // Radar Station
            { "MC260_RadarContainer", "CNT" }, // MC-260 Chimera's mobile Radar Container
        };

        private string GetRank1Designation(Unit emitter)
        {
            if (IsTreatedAsShip(emitter))
            {
                if (emitter.definition != null && !string.IsNullOrEmpty(emitter.definition.jsonKey)
                    && ActiveShipCodeOverrides.TryGetValue(emitter.definition.jsonKey, out string shipCode))
                {
                    return shipCode;
                }
                return "UNK";
            }

            if (emitter is Aircraft)
            {
                if (TryGetByCodeOrJsonKey(Rank1AircraftCodeOverrides, emitter.definition, out string aircraftCode))
                {
                    return aircraftCode;
                }
                return "UNK";
            }

            if (emitter.definition != null && !string.IsNullOrEmpty(emitter.definition.jsonKey)
                && Rank1GroundCodeOverrides.TryGetValue(emitter.definition.jsonKey, out string groundCode))
            {
                return groundCode;
            }
            return "UNK";
        }

        // Only recognized aircraft/ships/ground units show a real code --
        // anything else is unidentified.
        private string GetDesignation(Unit emitter)
        {
            if (_currentRwrQuality == 0)
            {
                return GetRank0Designation(emitter);
            }

            if (_currentRwrQuality == 1)
            {
                return GetRank1Designation(emitter);
            }

            if (emitter.definition != null)
            {
                if (TryGetByCodeOrJsonKey(RwrCodeOverrides, emitter.definition, out string aircraftCode))
                {
                    return aircraftCode;
                }

                if (!string.IsNullOrEmpty(emitter.definition.jsonKey))
                {
                    if (ActiveShipCodeOverrides.TryGetValue(emitter.definition.jsonKey, out string shipCode))
                    {
                        return shipCode;
                    }

                    if (GroundCodeOverrides.TryGetValue(emitter.definition.jsonKey, out string groundCode))
                    {
                        return groundCode;
                    }
                }
            }

            return "???";
        }

        private static void SetContactColor(TrackedContact contact, Color color)
        {
            foreach (Image image in contact.SymbolImages)
            {
                image.color = color;
            }

            if (contact.Label != null)
            {
                contact.Label.color = color;
            }
        }

        private static Vector2 BearingToDirection(float bearingDegrees)
        {
            float rad = bearingDegrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        }

        // Convenience for plotting by bearing/range instead of raw x,y.
        // rangeFraction is 0 (center) to 1 (edge of the grid).
        private static Vector2 BearingRangeToGrid(float bearingDegrees, float rangeFraction)
        {
            return BearingToDirection(bearingDegrees) * (rangeFraction * GridExtent);
        }

        // Grid coordinates (-50..50 per axis) to scope-local units, clamped
        // so a contact can never render outside the outer ring.
        private static Vector2 GridToLocalPosition(Vector2 gridPosition)
        {
            float scale = (ScopeDiameter / 2f) / GridExtent;
            Vector2 local = gridPosition * scale;

            if (local.magnitude > MaxContactRadius)
            {
                local = local.normalized * MaxContactRadius;
            }

            return local;
        }

        private RectTransform CreateContactGroup(RectTransform parent, string name, Vector2 gridPosition)
        {
            GameObject groupObject = new GameObject(name, typeof(RectTransform));
            RectTransform rect = groupObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = GridToLocalPosition(gridPosition);

            return rect;
        }

        private Image[] BuildChevronSymbol(RectTransform parent, Vector2 position)
        {
            GameObject chevronRoot = new GameObject("AirSymbol", typeof(RectTransform));
            RectTransform root = chevronRoot.GetComponent<RectTransform>();
            root.SetParent(parent, false);
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = Vector2.zero;
            root.anchoredPosition = position;

            Image left = CreateAngledBar(root, "ChevronLeft", new Vector2(9f, 2f), new Vector2(-3f, -1.5f), 35f);
            Image right = CreateAngledBar(root, "ChevronRight", new Vector2(9f, 2f), new Vector2(3f, -1.5f), -35f);
            return new[] { left, right };
        }

        private Image BuildDomeSymbol(RectTransform parent, Vector2 position)
        {
            const int size = 18;

            GameObject domeObject = new GameObject("GroundSymbol", typeof(RectTransform), typeof(Image));
            RectTransform rect = domeObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(size, size);
            rect.anchoredPosition = position;

            Image image = domeObject.GetComponent<Image>();
            // Plain white alpha mask (shape only) -- color comes from
            // image.color below and gets overwritten every frame by the
            // contact's normal color update. Baking a color into the
            // texture instead would multiply against every later tint
            // (targeting red, dimming, SARH flash) instead of replacing it,
            // producing a muddy wrong color.
            if (_domeSymbolSprite == null)
            {
                _domeSymbolSprite = CreateDomeOutlineSprite(size, 2f);
            }
            image.sprite = _domeSymbolSprite;
            image.color = ContactColor;
            image.raycastTarget = false;
            return image;
        }

        // ARH missile marker. Rank 4: thin unlabeled triangle always
        // pointing back at the player (rotated per-frame in
        // UpdateArhMissileContacts, hence returning its RectTransform).
        // Every other rank: hollow red circle with a red "M". Hue is fixed
        // (a threat indicator, not a themed element), applied once here
        // since neither ever gets re-tinted after creation.
        private (RectTransform Transform, Image Symbol, Text Letter) BuildMissileSymbol(RectTransform parent, Vector2 position, string letter)
        {
            Color missileColor = WithOpacity(new Color(ThreatColor.r, ThreatColor.g, ThreatColor.b, 1f));

            if (_currentRwrQuality == 4)
            {
                const int triangleWidth = 8;
                const int triangleHeight = 16;

                GameObject triangleObject = new GameObject("MissileSymbol", typeof(RectTransform), typeof(Image));
                RectTransform rect = triangleObject.GetComponent<RectTransform>();
                rect.SetParent(parent, false);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(triangleWidth, triangleHeight);
                rect.anchoredPosition = position;

                Image image = triangleObject.GetComponent<Image>();
                if (_missileTriangleSprite == null)
                {
                    _missileTriangleSprite = CreateTriangleSprite(triangleWidth, triangleHeight);
                }
                image.sprite = _missileTriangleSprite;
                image.color = missileColor;
                image.raycastTarget = false;

                return (rect, image, null);
            }
            else
            {
                const int size = 18;

                GameObject circleObject = new GameObject("MissileSymbol", typeof(RectTransform), typeof(Image));
                RectTransform rect = circleObject.GetComponent<RectTransform>();
                rect.SetParent(parent, false);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(size, size);
                rect.anchoredPosition = position;

                Image image = circleObject.GetComponent<Image>();
                if (_missileRingSprite == null)
                {
                    _missileRingSprite = CreateRingSprite(size, 2.5f);
                }
                image.sprite = _missileRingSprite;
                image.color = missileColor;
                image.raycastTarget = false;

                Text letterLabel = CreateLabel(parent, letter, position, 12, missileColor);
                return (rect, image, letterLabel);
            }
        }

        private Image CreateBar(RectTransform parent, string name, Vector2 size, Vector2 position)
        {
            GameObject barObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            RectTransform rect = barObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            Image image = barObject.GetComponent<Image>();
            image.color = ContactColor;
            image.raycastTarget = false;
            return image;
        }

        private Image CreateAngledBar(RectTransform parent, string name, Vector2 size, Vector2 offset, float angleDegrees)
        {
            GameObject barObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            RectTransform rect = barObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = offset;
            rect.localRotation = Quaternion.Euler(0f, 0f, angleDegrees);

            Image image = barObject.GetComponent<Image>();
            image.color = ContactColor;
            image.raycastTarget = false;
            return image;
        }

        // The game's own map grid-coordinate label font (GridLabels is
        // private on DynamicMap's public gridLabels field) -- same
        // reflection trick used in KcCruiseMissileWaypoints so the RWR's
        // text visually matches the map's own labels instead of Unity's
        // generic built-in font.
        private static readonly FieldInfo GridFontField =
            typeof(GridLabels).GetField("defaultFont", BindingFlags.NonPublic | BindingFlags.Instance);

        private Font _labelFont;
        // PERF: these three are always generated with identical params
        // (color applied separately via Image.color), so they're cached and
        // reused instead of baking a new Texture2D+Sprite per contact/
        // ghost/missile spawned -- that would otherwise leak, since
        // destroying the Image/GameObject doesn't free the texture asset.
        private Sprite _domeSymbolSprite;
        private Sprite _missileRingSprite;
        private Sprite _missileTriangleSprite;

        // "Best Font" (Secrets, see Plugin.cs) overrides the map-grid font
        // with Arial -- the objectively correct typeface.
        private Font ResolveLabelFont()
        {
            if (BestFontEnabled)
            {
                Font arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (arial != null)
                {
                    return arial;
                }
            }

            DynamicMap map = SceneSingleton<DynamicMap>.i;
            Font gridFont = map != null && map.gridLabels != null && GridFontField != null
                ? GridFontField.GetValue(map.gridLabels) as Font
                : null;

            Font resolved = gridFont != null ? gridFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (resolved == null)
            {
                resolved = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            return resolved;
        }

        // Live toggle: existing labels were created with whatever font was
        // cached at the time, so a mid-flight flip needs to walk every Text
        // already on screen once -- cheap since it only runs the frame the
        // setting actually changes (see the _lastBestFontEnabled check in
        // Update()), not every frame. _warningPanelRoot is a separate root
        // from _scopeRoot (parented directly to the canvas, not under the
        // scope), so both need their own sweep -- missing this was exactly
        // why the warning panel's labels didn't respond to this toggle.
        private void RefreshAllLabelFonts()
        {
            _labelFont = ResolveLabelFont();
            ApplyFontToRoot(_scopeRoot);
            ApplyFontToRoot(_warningPanelRoot);
        }

        private void ApplyFontToRoot(RectTransform root)
        {
            if (root == null)
            {
                return;
            }

            Text[] labels = root.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i].font = _labelFont;
            }
        }

        private Text CreateLabel(RectTransform parent, string text, Vector2 position, int fontSize, Color color,
            FontStyle fontStyle = FontStyle.Normal, float width = 60f, float height = 16f)
        {
            if (_labelFont == null)
            {
                _labelFont = ResolveLabelFont();
            }

            GameObject textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = position;

            Text uiText = textObject.GetComponent<Text>();
            uiText.text = text;
            uiText.font = _labelFont;
            uiText.fontSize = fontSize;
            uiText.fontStyle = fontStyle;
            uiText.alignment = TextAnchor.MiddleCenter;
            uiText.color = color;
            uiText.raycastTarget = false;
            return uiText;
        }

        // Outline only: just the curved top arc, open at the bottom. White
        // alpha mask -- callers tint it live via Image.color.
        private static Sprite CreateDomeOutlineSprite(int size, float thickness)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            float radius = size / 2f;
            Vector2 center = new Vector2(radius, radius);
            float innerRadius = radius - thickness;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float dist = Vector2.Distance(new Vector2(px, py), center);
                    bool upperHalf = py >= center.y;

                    float alpha = 0f;

                    if (upperHalf && dist <= radius && dist >= innerRadius)
                    {
                        alpha = 1f;
                        if (dist > radius - 1f)
                        {
                            alpha = Mathf.Clamp01(radius - dist);
                        }
                    }

                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        }

        // Hollow diamond outline -- same alpha-mask/anti-aliased-edge
        // technique as CreateDomeOutlineSprite, just Manhattan (L1) distance
        // from center instead of Euclidean, which traces a diamond instead
        // of a circle.
        private static Sprite CreateDiamondOutlineSprite(int size, float thickness)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            float radius = size / 2f;
            Vector2 center = new Vector2(radius, radius);
            float innerRadius = radius - thickness;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float dist = Mathf.Abs(px - center.x) + Mathf.Abs(py - center.y);

                    float alpha = 0f;
                    if (dist <= radius && dist >= innerRadius)
                    {
                        alpha = 1f;
                        if (dist > radius - 1f)
                        {
                            alpha = Mathf.Clamp01(radius - dist);
                        }
                    }

                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        }

        // Thin arrow pointing "up" (bearing 0) -- apex at the top, full
        // width at the bottom. White alpha mask; caller tints it live and
        // rotates it per-frame to keep the apex pointed at the player.
        private static Sprite CreateTriangleSprite(int width, int height)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            float halfWidth = width / 2f;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;

                    float t = py / height;
                    float allowedHalfWidth = halfWidth * (1f - t);
                    float distFromCenter = Mathf.Abs(px - halfWidth);

                    float alpha = Mathf.Clamp01(allowedHalfWidth - distFromCenter + 0.5f);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f));
        }
    }
}
