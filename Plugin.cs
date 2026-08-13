using System;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace TraditionalRWR
{
    [BepInPlugin("pavehog727.traditionalrwr", "KaceyTronic-RWR-1.0", "1.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            // Bound first (before "General" below), so this section appears
            // above it -- purely informational, nothing here has a real
            // saved value.
            BindQualityGuideHeader("Information");
            BindRankInfoEntries("Information");

            // Renamed key (not just a DispName swap) -- accepts a one-time
            // reset of just this one slider's saved value, in exchange for
            // not relying on the DispName reflection path, which (along
            // with the <b> tags on every section name) is one of the two
            // suspects for a ConfigManager-side crash that made the whole
            // plugin's entry disappear from the settings window.
            ConfigEntry<int> maxRangeKm = Config.Bind(
                "General",
                "RWR Scope Scale",
                50,
                new ConfigDescription(
                    "Changes the Scale of the RWR Scope in Kilometers. Increasing this will not increase your RWR range, simply 'zoom out' to make it easier to see far away contacts. Any contacts detected outside this range will stick to the outer ring.",
                    new AcceptableValueRange<int>(5, 150)));

            RwrScopeController.MaxDisplayRangeMeters = maxRangeKm.Value * 1000f;
            maxRangeKm.SettingChanged += (sender, args) =>
            {
                RwrScopeController.MaxDisplayRangeMeters = maxRangeKm.Value * 1000f;
            };

            ConfigEntry<bool> simpleShipDesignators = Config.Bind(
                "General",
                "Use Simple Ship Designators",
                false,
                "Changes the realistic (but confusing) designators for naval units to more simple ones based on their class name. For example FFL (Argus) FS (Shard) to ARG (Argus) SHD (Shard)");
            RwrScopeController.UseSimpleShipDesignators = simpleShipDesignators.Value;
            simpleShipDesignators.SettingChanged += (sender, args) =>
            {
                RwrScopeController.UseSimpleShipDesignators = simpleShipDesignators.Value;
            };

            ConfigEntry<bool> notchLineOnAllRanks = Config.Bind(
                "General",
                "Enable Notch line display for every Rank",
                false,
                "Turns the notch line on for ranks 1-3 when targeted by a emitter.");
            RwrScopeController.NotchLineOnAllRanks = notchLineOnAllRanks.Value;
            notchLineOnAllRanks.SettingChanged += (sender, args) =>
            {
                RwrScopeController.NotchLineOnAllRanks = notchLineOnAllRanks.Value;
            };

            BindRwrQualityOverrides();
            BindAppearanceSettings();
            BindPositionSettings();
            // Bound last so its section lands at the very bottom of the
            // window (ConfigManager orders sections by first-bind order).
            BindSecretsSettings();

            gameObject.AddComponent<RwrScopeController>();

            Logger.LogInfo($"KaceyTronic-RWR-1.0 loaded — scope overlay created, max range {maxRangeKm.Value}km.");
        }

        // ConfigManager sorts entries alphabetically within a section
        // unless given an explicit Order (higher = higher up the list) --
        // without this, the colors and opacity slider interleave by
        // spelling instead of staying grouped. Values just need to be
        // relatively correct, not sequential.
        private const int OrderRwrColor = 60;
        private const int OrderThreatColor = 50;
        private const int OrderThreatFlashColor = 40;
        private const int OrderJamLobColor = 30;
        private const int OrderNotchPrimaryColor = 20;
        private const int OrderNotchSecondaryColor = 10;
        private const int OrderOpacity = 0;

        private void BindAppearanceSettings()
        {
            const string section = "RWR Appearance";

            BindThemeColorEntry(
                section,
                "Theme Color",
                "Color of Contacts, Scope, and Crosshair.",
                new Color(0.2f, 1f, 0.4f),
                OrderRwrColor,
                value => RwrScopeController.UserThemeColor = value);

            BindThemeColorEntry(
                section,
                "Threat Primary Color",
                "Color of things targeting you, along with incoming missiles.",
                new Color(1f, 0.2f, 0.15f),
                OrderThreatColor,
                value => RwrScopeController.UserThreatColor = value);

            BindThemeColorEntry(
                section,
                "Threat Secondary Color",
                "Color that flashes between the threat primary for things actively trying to kill you.",
                new Color(1f, 0.9f, 0.1f),
                OrderThreatFlashColor,
                value => RwrScopeController.UserThreatFlashColor = value);

            BindThemeColorEntry(
                section,
                "Jamming LOB Color",
                "Color of the line point towards a jammer actively jamming you. (Rank 3 and 4 Only)",
                new Color(1f, 0.45f, 0f),
                OrderJamLobColor,
                value => RwrScopeController.UserJamLobBaseColor = value);

            BindThemeColorEntry(
                section,
                "Notch-Line Primary Color",
                "First flashing color for the notch line that appears when you're targeted. (Rank 4 Only)",
                new Color(1f, 0.9f, 0.1f),
                OrderNotchPrimaryColor,
                value => RwrScopeController.UserNotchPrimaryColor = value);

            BindThemeColorEntry(
                section,
                "Notch-Line Secondary Color",
                "Second flashing color for the notch line that... you get it. (Rank 4 Only)",
                new Color(1f, 0.55f, 0.05f),
                OrderNotchSecondaryColor,
                value => RwrScopeController.UserNotchSecondaryColor = value);

            ConfigEntry<float> opacity = Config.Bind(
                section,
                "RWR Opacity",
                1f,
                new ConfigDescription(
                    "Affects how 'see through' the HUD element is. 0 is invisible, 100 is default opacity.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { Order = OrderOpacity }));
            RwrScopeController.ThemeOpacity = opacity.Value;
            opacity.SettingChanged += (sender, args) => RwrScopeController.ThemeOpacity = opacity.Value;
        }

        // Color pickers all share this shape: bind, apply the picked RGB
        // once immediately (alpha forced to 1 -- opacity is controlled
        // separately, not through the picker's own alpha channel), then
        // keep applying it live as the user adjusts it in ConfigManager.
        private void BindThemeColorEntry(string section, string displayName, string description, Color defaultColor, int order, Action<Color> apply)
        {
            ConfigEntry<Color> entry = Config.Bind(
                section,
                displayName,
                defaultColor,
                new ConfigDescription(description, null, new ConfigurationManagerAttributes { Order = order }));
            apply(new Color(entry.Value.r, entry.Value.g, entry.Value.b, 1f));
            entry.SettingChanged += (sender, args) => apply(new Color(entry.Value.r, entry.Value.g, entry.Value.b, 1f));
        }

        private void BindPositionSettings()
        {
            const string section = "RWR Position";

            // Ranges sized against a 2560x1440 reference (260x260 panel
            // kept fully on-screen at that size). Smaller monitors can
            // still dial in a value that pushes the scope off-screen --
            // that's fine, it's just a number in ConfigManager they can
            // read and correct, not a broken/stuck state.
            ConfigEntry<int> positionX = Config.Bind(
                section,
                "RWR X Position",
                0,
                new ConfigDescription(
                    "How far from the left edge of the screen the RWR sits. Default is flush against the left edge.",
                    new AcceptableValueRange<int>(0, 2300),
                    new ConfigurationManagerAttributes { Order = 10 }));
            RwrScopeController.ScopePositionX = positionX.Value;
            positionX.SettingChanged += (sender, args) => RwrScopeController.ScopePositionX = positionX.Value;

            ConfigEntry<int> positionY = Config.Bind(
                section,
                "RWR Y Position",
                446,
                new ConfigDescription(
                    "How far up from the bottom edge of the screen the RWR sits. Default sits just above the minimap.",
                    new AcceptableValueRange<int>(0, 1180),
                    new ConfigurationManagerAttributes { Order = 0 }));
            RwrScopeController.ScopePositionY = positionY.Value;
            positionY.SettingChanged += (sender, args) => RwrScopeController.ScopePositionY = positionY.Value;
        }

        private void BindSecretsSettings()
        {
            const string section = "Secrets";

            ConfigEntry<bool> devLogging = Config.Bind(
                section,
                "Dev Logging",
                false,
                new ConfigDescription(
                    "Turns on Dev Logging. If you have issues, turn this on and send it to the developer! BepInEx\\plugins\\rwrdebug",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 30 }));
            RwrScopeController.DevToolsEnabled = devLogging.Value;
            devLogging.SettingChanged += (sender, args) => RwrScopeController.DevToolsEnabled = devLogging.Value;

            ConfigEntry<bool> bestFont = Config.Bind(
                section,
                "Best Font",
                false,
                new ConfigDescription(
                    "Changes the typeface to the best typeface",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 40 }));
            RwrScopeController.BestFontEnabled = bestFont.Value;
            bestFont.SettingChanged += (sender, args) => RwrScopeController.BestFontEnabled = bestFont.Value;

            ConfigEntry<bool> funnyMode = Config.Bind(
                section,
                "Funny Mode",
                false,
                new ConfigDescription(
                    "Rainbow. Adds 20% Gayge to airframe when enabled",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 20 }));
            RwrScopeController.FunnyModeEnabled = funnyMode.Value;
            funnyMode.SettingChanged += (sender, args) => RwrScopeController.FunnyModeEnabled = funnyMode.Value;

            ConfigEntry<bool> dealerMode = Config.Bind(
                section,
                "Dealer Mode",
                false,
                new ConfigDescription(
                    "Why's this dealer Taking the piss? I've been standing here... for thirty mins.  Brought to you by Toyota Yaris. BPM below effects how fast it bounces.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 10 }));
            RwrScopeController.DealerModeEnabled = dealerMode.Value;
            dealerMode.SettingChanged += (sender, args) => RwrScopeController.DealerModeEnabled = dealerMode.Value;

            ConfigEntry<int> bpm = Config.Bind(
                section,
                "BPM",
                130,
                new ConfigDescription(
                    "",
                    new AcceptableValueRange<int>(40, 300),
                    new ConfigurationManagerAttributes { IsAdvanced = true, Order = 0 }));
            RwrScopeController.DealerModeBpm = bpm.Value;
            bpm.SettingChanged += (sender, args) => RwrScopeController.DealerModeBpm = bpm.Value;
        }

        private void BindRwrQualityOverrides()
        {
            const string section = "RWR Quality Overrides";
            const string perAircraftSection = "RWR Quality Overrides - Per Aircraft";

            ConfigEntry<bool> overwrite = Config.Bind(
                section,
                "Overwrite Default RWR Settings",
                false,
                "Ensure this is set to 'Enabled' in order for the Aircraft settings below to take effect.  If Disabled, the mod will use the hardcoded values per-aircraft.");
            RwrScopeController.OverwriteRwrSettings = overwrite.Value;
            overwrite.SettingChanged += (sender, args) => RwrScopeController.OverwriteRwrSettings = overwrite.Value;

            ConfigEntry<int> fallback = Config.Bind(
                section,
                "Default RWR Level Fallback",
                2,
                new ConfigDescription(
                    "For modded or otherwise unsupported aircraft. If the airframe you're flying isn't recognized, it will use this value.",
                    new AcceptableValueRange<int>(0, 4)));
            RwrScopeController.FallbackRwrQuality = fallback.Value;
            fallback.SettingChanged += (sender, args) => RwrScopeController.FallbackRwrQuality = fallback.Value;

            // Order: higher = higher up the list. Every vanilla aircraft
            // outranks every modded one so the modded group sits together
            // at the bottom instead of interleaving alphabetically; values
            // are just assigned in alphabetical order within each group to
            // match what ConfigManager would show by default anyway.
            BindAircraftRwrQualityOverride(perAircraftSection, "Alkyon (AB-4)", "AB-4", order: 113);
            BindAircraftRwrQualityOverride(perAircraftSection, "Brawler (A-19)", "A-19", order: 112);
            BindAircraftRwrQualityOverride(perAircraftSection, "Chicane (SAH-46)", "SAH-46", order: 111);
            BindAircraftRwrQualityOverride(perAircraftSection, "Compass (T/A-30)", "T/A-30", order: 110);
            BindAircraftRwrQualityOverride(perAircraftSection, "Cricket (CI-22)", "CI-22", order: 109);
            BindAircraftRwrQualityOverride(perAircraftSection, "Darkreach (SFB-81)", "SFB-81", order: 108);
            BindAircraftRwrQualityOverride(perAircraftSection, "Ibis (UH-90)", "UH-90", order: 107);
            BindAircraftRwrQualityOverride(perAircraftSection, "Ifrit (KR-67)", "KR-67", order: 106);
            BindAircraftRwrQualityOverride(perAircraftSection, "Medusa (EW-25)", "EW-25", order: 105);
            BindAircraftRwrQualityOverride(perAircraftSection, "Revoker (FS-12)", "FS-12", order: 104);
            BindAircraftRwrQualityOverride(perAircraftSection, "Tarantula (VL-49)", "VL-49", order: 103);
            BindAircraftRwrQualityOverride(perAircraftSection, "Vagrant (VT-7)", "VT-7", order: 102);
            BindAircraftRwrQualityOverride(perAircraftSection, "Vortex (FS-20)", "FS-20", order: 101);

            // Modded aircraft (via the Blueprinter addon loader). Marked
            // advanced so they're hidden from ConfigManager by default (via
            // its own "Show advanced settings" toggle) -- most users don't
            // have these mods installed and shouldn't see rows for planes
            // they don't have.
            BindAircraftRwrQualityOverride(perAircraftSection, "F-16M King Viper", "Aryx_F16M_KingViper", "Mod by Aryx.", isAdvanced: true, order: 7);
            BindAircraftRwrQualityOverride(perAircraftSection, "F-99 Shrike", "Aryx_LightFighter1", "Mod by Aryx.", isAdvanced: true, order: 6);
            BindAircraftRwrQualityOverride(perAircraftSection, "FS-3 Ternion", "P_Trisurface1", "Mod by Nikkorap, Raikan, ErrorByte, AAA Battery, javiairplane, and Drunk Driving Compilation #42.", isAdvanced: true, order: 5);
            BindAircraftRwrQualityOverride(perAircraftSection, "FS-41 Eclipse", "Aryx_Interceptor1", "Mod by Aryx.", isAdvanced: true, order: 4);
            BindAircraftRwrQualityOverride(perAircraftSection, "MC-260 Chimera", "Aryx_CargoPlane1", "Mod by Aryx.", isAdvanced: true, order: 3);
            BindAircraftRwrQualityOverride(perAircraftSection, "MiG-15", "Aryx_MiG-15", "Mod by Aryx.", isAdvanced: true, order: 2);
            BindAircraftRwrQualityOverride(perAircraftSection, "RAH-72 Knockout", "Aryx_LightHelicopter1", "Mod by Aryx.", isAdvanced: true, order: 1);
        }

        // A dummy bool entry whose widget is replaced by DrawQualityGuideHeader
        // below -- ConfigManager has no plain-text/label element, so a fake
        // setting with a CustomDrawer is the standard way to show a banner.
        // Bound first, with a high Order, so it's the first thing shown
        // within its section.
        private void BindQualityGuideHeader(string section)
        {
            Config.Bind(
                section,
                "RWR Settings Guide",
                false,
                new ConfigDescription(
                    "",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        Order = 100,
                        HideSettingName = true,
                        HideDefaultButton = true,
                        CustomDrawer = DrawQualityGuideHeader,
                    }));
        }

        private static void DrawQualityGuideHeader(ConfigEntryBase entry)
        {
            GUILayout.BeginVertical();

            DrawCenteredLabel("Kaceytronic INC Electronic Warfare Systems", TitleStyle.Value);
            DrawCenteredLabel("\"Reliable. Refined. Kaceytronic\"", TaglineStyle.Value);
            DrawCenteredLabel("Copyright 2068 - All Rights Reserved", CopyrightStyle.Value);
            GUILayout.Space(6);
            DrawCenteredLabel(
                "RWR Quality is as follows.\n0 = Poor\n1 = Okay\n2 = Average\n3 = Good\n4 = Excellent",
                BodyStyle.Value);
            GUILayout.Space(6);
            DrawCenteredLabel("Hover over each aircraft below to see their default value.", BodyStyle.Value);
            DrawCenteredLabel("Hover over each rank below for their descriptions.", BodyStyle.Value);
            DrawCenteredLabel("Toggle 'Advanced settings' at the top of this window to view settings for modded aircraft.", BodyStyle.Value);

            GUILayout.EndVertical();
        }

        // Five dummy entries, name visible but no widget (via a no-op
        // CustomDrawer) -- ConfigManager shows an entry's description as a
        // hover tooltip regardless of whether it has a real value, so this
        // is just a name to hover for info about each rank's behavior.
        // Order keeps them below the banner (100) and above RWR Scope
        // Scale (unset/0).
        private void BindRankInfoEntries(string section)
        {
            BindRankInfoEntry(section, "Rank 0 - Poor", 95,
                "The oldest RWR model. Due to its age, it cannot recognize newer airframe's radars. The Scope is split into four quadrants representing the four corners of the aircraft. Very light weight, compact, cheap, and rudimentary system. Lacks color coding. Does not display range.");
            BindRankInfoEntry(section, "Rank 1 - Okay", 94,
                "Full Precise positioning, but an older model, so doesn't recognize some newer airframe's radars as firmware is unable to be updated. Has color coding. Is able to identify incoming ARH missiles eventually.");
            BindRankInfoEntry(section, "Rank 2 - Average", 93,
                "Old midrange model. Due to updateable software, has the newest list of airframes and their radars. Good choice for most aircraft.");
            BindRankInfoEntry(section, "Rank 3 - Good", 92,
                "Older Advanced Model. Identifies incoming ARH missiles faster than Rank 2. Is also able to give you a line of bearing toward any jamming sources actively targeting you, however is unable to filter out contacts caused by jamming.");
            BindRankInfoEntry(section, "Rank 4 - Excellent", 91,
                "New Advanced Model. Once it detects a targeting radar contact, it provides a rough line to turn toward as a preemptive notch. Instantly resolves incoming ARH missiles. Also gives a LOB toward jamming sources, and is able to filter out contacts from jamming, but cannot receive any other contacts while jammed, other than from attached systems like the aircraft's optical sensors or Datalink.");
        }

        private void BindRankInfoEntry(string section, string displayName, int order, string description)
        {
            Config.Bind(
                section,
                displayName,
                false,
                new ConfigDescription(
                    description,
                    null,
                    new ConfigurationManagerAttributes
                    {
                        Order = order,
                        HideDefaultButton = true,
                        CustomDrawer = DrawNothing,
                    }));
        }

        private static void DrawNothing(ConfigEntryBase entry)
        {
        }

        private static void DrawCenteredLabel(string text, GUIStyle style)
        {
            GUILayout.Label(text, style, GUILayout.ExpandWidth(true));
        }

        // Lazy -- GUI.skin isn't valid to touch outside an OnGUI call, so
        // these can't be built as static field initializers. Alignment is
        // center-based so the banner reads as a proper header, not left-run.
        private static readonly Lazy<GUIStyle> TitleStyle = new Lazy<GUIStyle>(() =>
            new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter });
        private static readonly Lazy<GUIStyle> TaglineStyle = new Lazy<GUIStyle>(() =>
            new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = 10, fontStyle = FontStyle.Italic, alignment = TextAnchor.UpperCenter });
        private static readonly Lazy<GUIStyle> CopyrightStyle = new Lazy<GUIStyle>(() =>
            new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = 8, alignment = TextAnchor.UpperCenter });
        private static readonly Lazy<GUIStyle> BodyStyle = new Lazy<GUIStyle>(() =>
            new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, alignment = TextAnchor.UpperCenter });

        private void BindAircraftRwrQualityOverride(string section, string displayName, string aircraftCode, string creditNote = null, bool isAdvanced = false, int order = 0)
        {
            string description = "Set's this Airframe's RWR quality. -1 is default. Changing this has no effect unless 'Overwrite Default RWR Settings' above is enabled.";
            string defaultQualityText = RwrScopeController.DescribeDefaultQuality(aircraftCode);
            if (!string.IsNullOrEmpty(defaultQualityText))
            {
                description += " " + defaultQualityText;
            }
            if (!string.IsNullOrEmpty(creditNote))
            {
                description += " " + creditNote;
            }

            ConfigEntry<int> entry = Config.Bind(
                section,
                displayName,
                -1,
                new ConfigDescription(
                    description,
                    new AcceptableValueRange<int>(-1, 4),
                    new ConfigurationManagerAttributes { IsAdvanced = isAdvanced, Order = order }));

            ApplyAircraftRwrQualityOverride(aircraftCode, entry.Value);
            entry.SettingChanged += (sender, args) => ApplyAircraftRwrQualityOverride(aircraftCode, entry.Value);
        }

        private static void ApplyAircraftRwrQualityOverride(string aircraftCode, int value)
        {
            if (value < 0)
            {
                RwrScopeController.AircraftRwrQualityOverrides.Remove(aircraftCode);
            }
            else
            {
                RwrScopeController.AircraftRwrQualityOverrides[aircraftCode] = value;
            }
        }
    }
}
