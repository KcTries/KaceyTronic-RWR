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
                        EnsureSubscribed();
                        EnsureMissileWarningSubscribed();
                        UpdateContacts();
                        UpdateArhMissileContacts();
                        UpdateRank4NotchLines();
                        UpdateJamGhostContacts();
                        UpdateJamLineOfBearing();
                        UpdateSplashScreen();
                        UpdateThemedStaticElements();
                        UpdateScopePosition();
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
        private Image _normalRingImage;
        private Image _normalHalfRingImage;
        private Image _normalReticleHorizontalImage;
        private Image _normalReticleVerticalImage;
        private Image _rank0RingImage;
        private Image _rank0HalfRingImage;
        private Image _rank0CrossHorizontalImage;
        private Image _rank0CrossVerticalImage;
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

            _rank2TicksOverlayRoot = BuildOverlayRoot(_scopeRoot, "Rank2TicksOverlay");
            BuildRank2Ticks(_rank2TicksOverlayRoot);

            // Rank 4 lines are built dynamically each frame, not here --
            // this is just the container they get parented under.
            _rank4NotchOverlayRoot = BuildOverlayRoot(_scopeRoot, "Rank4NotchOverlay");

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
                _rank4NotchOverlayRoot.gameObject.SetActive(_currentRwrQuality == 4);
            }
        }

        // Rings/reticle/ticks are built once and never touched again
        // elsewhere, so retint them here each frame -- otherwise a live
        // color/opacity change wouldn't show up until the next respawn.
        private void UpdateThemedStaticElements()
        {
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
                _rank4NotchOverlayRoot.gameObject.SetActive(visible && _currentRwrQuality == 4);
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
            image.sprite = CreateRoundedRectSprite(PanelSize, PanelSize, 18f, new Color(0.02f, 0.05f, 0.03f, 0.5f));
            image.raycastTarget = false;
        }

        // Set from Plugin.Awake() ("RWR Position" section) and live-updated
        // via UpdateScopePosition(). Defaults match the original hardcoded
        // position, so an untouched install looks unchanged.
        public static float ScopePositionX = 0f;
        public static float ScopePositionY = 446f;

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

        private void UpdateScopePosition()
        {
            if (_scopeRoot == null)
            {
                return;
            }

            float scaleX = 1f;
            float scaleY = 1f;
            float compensatedX = ScopePositionX;

            if (DealerModeEnabled)
            {
                // One full squish-and-release cycle per beat. Cosine-based
                // so it starts and ends each cycle at squishT=0 (full
                // height) rather than jumping straight into the squish.
                float bounceHz = DealerModeBpm / 60f;
                float squishT = (1f - Mathf.Cos(Time.unscaledTime * bounceHz * 2f * Mathf.PI)) / 2f;

                scaleY = Mathf.Lerp(1f, DealerModeMinScaleY, squishT);
                scaleX = Mathf.Lerp(1f, DealerModeMaxScaleX, squishT);

                // _scopeRoot's pivot is bottom-left (see BuildScopeRoot), so
                // scaling Y already keeps the bottom edge fixed for free --
                // only the top comes down. X needs a compensating shift,
                // though, or widening would only grow the panel rightward
                // off the left edge instead of bulging out symmetrically
                // around its horizontal center.
                compensatedX = ScopePositionX + (PanelSize / 2f) * (1f - scaleX);
            }

            _scopeRoot.anchoredPosition = new Vector2(compensatedX, ScopePositionY);
            _scopeRoot.localScale = new Vector3(scaleX, scaleY, 1f);
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
            return new List<string>(ShipCodeOverrides.Values);
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
            _rank2TickImages.Clear();
            _rank4NotchOverlayRoot = null;
            _rank4NotchLines.Clear();
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
                    // ARH: the missile has its own active radar seeker --
                    // show it as its own contact.
                    CreateArhMissileContact(missile);
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

                if (_arhMissileContacts.TryGetValue(missile, out ArhMissileContact arhContact))
                {
                    if (arhContact.Group != null)
                    {
                        Destroy(arhContact.Group.gameObject);
                    }
                    _arhMissileContacts.Remove(missile);
                }

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
            };
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

                if (missile == null || contact.Group == null)
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

                    CreateArhConnectingLine(bearingDegrees, distance);
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

        // Rank 4 only: a notch line 90 degrees off the bearing of anything
        // locking the player or any inbound missile (ARH/SARH) -- flying
        // that heading is what actually notches a Doppler-guided threat,
        // unlike a line pointing straight at it. PERF: rebuilt from scratch
        // every frame rather than diffed per-threat -- simpler, and fine
        // since the threat count stays small.
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

            if (_currentRwrQuality != 4 || _rank4NotchOverlayRoot == null || _playerAircraft == null)
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
                    CreateRank4NotchLine(GetBearingForWorldPosition(emitter.transform.position) + 90f, flashColor);
                }
            }

            foreach (KeyValuePair<Missile, ArhMissileContact> kvp in _arhMissileContacts)
            {
                if (kvp.Key != null)
                {
                    CreateRank4NotchLine(GetBearingForWorldPosition(kvp.Key.transform.position) + 90f, flashColor);
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

        private void OnRadarWarningReceived(Aircraft.OnRadarWarning e)
        {
            try
            {
                if (e.emitter == null || e.emitter is Missile)
                {
                    // Missiles are handled entirely by the dedicated
                    // MissileWarning system (ARH icon / SARH flash) --
                    // without this they'd also show up here as a second,
                    // overlapping dome+??? contact.
                    return;
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
                // one -- no red-on-lock color change at that quality.
                contact.IsTargeted = _currentRwrQuality != 0 && e.isTarget;
                contact.BaseColor = contact.IsTargeted ? TargetedColor : ContactColor;
                SetContactColor(contact, contact.BaseColor);
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

        // Rank 1+ only: a hollow diamond tracks whichever contact currently
        // has "priority" -- the closest actively-threatening contact
        // (locked onto the player, or a SARH launcher guiding a missile at
        // them) if any exist; otherwise the closest non-stale contact; a
        // stale (dimmed/fading) contact is only picked if it's the only
        // thing left on the scope at all. Re-run every frame so it keeps
        // following as ranges/threats change.
        // Not used at Rank 0 -- its own quadrant priority system already
        // picks one contact per quadrant, and everything sits at a fixed
        // display radius there so "closest" isn't meaningfully visualized.
        // Also hidden entirely while jammed -- the picture is already
        // unreliable then, so highlighting a "priority" contact out of
        // ghosts/blanked data would be misleading. Reappears on its own
        // once IsCurrentlyJammed() goes false again.
        private void UpdatePriorityDiamond()
        {
            if (_priorityDiamondImage == null)
            {
                return;
            }

            if (_currentRwrQuality < 1 || _playerAircraft == null || IsCurrentlyJammed())
            {
                _priorityDiamondImage.gameObject.SetActive(false);
                return;
            }

            float now = Time.unscaledTime;

            TrackedContact closestThreat = null;
            float closestThreatDistSq = float.MaxValue;
            TrackedContact closestFresh = null;
            float closestFreshDistSq = float.MaxValue;
            TrackedContact closestStale = null;
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
                    }
                }
                else if (distSq < closestFreshDistSq)
                {
                    closestFreshDistSq = distSq;
                    closestFresh = contact;
                }
            }

            // Stale contacts are only picked as a last resort, when
            // there's nothing fresh or actively threatening left to
            // point at.
            TrackedContact priority = closestThreat ?? closestFresh ?? closestStale;
            if (priority == null || priority.Group == null)
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
            if (emitter is Aircraft)
            {
                symbolImages = BuildChevronSymbol(group, Vector2.zero);
            }
            else if (emitter is Ship)
            {
                symbolImages = new[] { CreateBar(group, "ShipSymbol", new Vector2(16f, 2f), new Vector2(0f, ShipSymbolVerticalOffset)) };
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
        };

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
            if (emitter is Ship)
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
            if (emitter is Ship)
            {
                if (emitter.definition != null && !string.IsNullOrEmpty(emitter.definition.jsonKey)
                    && ShipCodeOverrides.TryGetValue(emitter.definition.jsonKey, out string shipCode))
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
                    if (ShipCodeOverrides.TryGetValue(emitter.definition.jsonKey, out string shipCode))
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

        private Font _labelFont;
        // PERF: these three are always generated with identical params
        // (color applied separately via Image.color), so they're cached and
        // reused instead of baking a new Texture2D+Sprite per contact/
        // ghost/missile spawned -- that would otherwise leak, since
        // destroying the Image/GameObject doesn't free the texture asset.
        private Sprite _domeSymbolSprite;
        private Sprite _missileRingSprite;
        private Sprite _missileTriangleSprite;

        private Text CreateLabel(RectTransform parent, string text, Vector2 position, int fontSize, Color color,
            FontStyle fontStyle = FontStyle.Normal, float width = 60f, float height = 16f)
        {
            if (_labelFont == null)
            {
                _labelFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_labelFont == null)
                {
                    _labelFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                }
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
