using System;
using BepInEx.Configuration;

namespace TraditionalRWR
{
    // Copied from BepInEx.ConfigurationManager's own template (per its usage
    // instructions) so we can pass an explicit display Order -- without it,
    // ConfigManager falls back to sorting entries alphabetically by name
    // within each section, which splits up related settings whenever one of
    // their names happens to fall in a different spot in the alphabet.
    // Trimmed to just the field we use; the template allows removing the
    // rest since a missing field just means "don't override this."
    internal sealed class ConfigurationManagerAttributes
    {
        /// <summary>
        /// Order of the setting on the settings list relative to other settings in a category.
        /// 0 by default, higher number is higher on the list.
        /// </summary>
        public int? Order;

        /// <summary>
        /// Should the setting be shown as advanced (hidden unless the user
        /// enables ConfigManager's own "Show advanced settings" toggle)?
        /// </summary>
        public bool? IsAdvanced;

        /// <summary>
        /// If set, replaces the normal editable widget with custom IMGUI
        /// drawing code -- used for plain-text banners/headers that aren't
        /// backed by a real editable value.
        /// </summary>
        public Action<ConfigEntryBase> CustomDrawer;

        /// <summary>
        /// If true, the setting's name label isn't drawn, letting a
        /// CustomDrawer use the full row width.
        /// </summary>
        public bool? HideSettingName;

        /// <summary>
        /// If true, hides the Reset button for this setting.
        /// </summary>
        public bool? HideDefaultButton;
    }
}
