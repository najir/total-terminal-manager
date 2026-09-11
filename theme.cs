// -----------------------------------------------------------------------------
//  Theme registration, application and persistence.
// -----------------------------------------------------------------------------

internal static class Theme
{
    private const string Custom =
        """
        {
          "Driver": { "Force16Colors": false },
          "Themes": {
            "TTM Midnight": {
              "Schemes": {
                "Base":   { "Normal": { "Foreground": "White",      "Background": "#0B1021" },
                            "Focus":  { "Foreground": "#0B1021",    "Background": "BrightCyan" } },
                "Menu":   { "Normal": { "Foreground": "BrightCyan", "Background": "#0B1021" },
                            "Focus":  { "Foreground": "#0B1021",    "Background": "BrightCyan" } },
                "Dialog": { "Normal": { "Foreground": "White",      "Background": "#161B33" } }
              }
            },
            "TTM Paper": {
              "Schemes": {
                "Base":   { "Normal": { "Foreground": "#2B2B2B",    "Background": "#F5F2E8" },
                            "Focus":  { "Foreground": "#F5F2E8",    "Background": "#8C5A2B" } },
                "Menu":   { "Normal": { "Foreground": "#2B2B2B",    "Background": "#E8E2D0" },
                            "Focus":  { "Foreground": "#F5F2E8",    "Background": "#8C5A2B" } }
              }
            },
            "Catppuccin Frappé": {
              "Schemes": {
                "Base":     { "Normal":    { "Foreground": "#c6d0f5", "Background": "#303446" },
                              "Focus":    { "Foreground": "#303446", "Background": "#8caaee" },
                              "HotNormal":{ "Foreground": "#ca9ee6", "Background": "#303446" },
                              "HotFocus": { "Foreground": "#303446", "Background": "#babbf1" },
                              "Disabled": { "Foreground": "#737994", "Background": "#303446" } },
                "TopLevel": { "Normal":    { "Foreground": "#c6d0f5", "Background": "#292c3c" },
                              "Focus":    { "Foreground": "#292c3c", "Background": "#8caaee" },
                              "HotNormal":{ "Foreground": "#ca9ee6", "Background": "#292c3c" },
                              "HotFocus": { "Foreground": "#292c3c", "Background": "#babbf1" },
                              "Disabled": { "Foreground": "#737994", "Background": "#292c3c" } },
                "Dialog":   { "Normal":    { "Foreground": "#c6d0f5", "Background": "#414559" },
                              "Focus":    { "Foreground": "#414559", "Background": "#8caaee" },
                              "HotNormal":{ "Foreground": "#ca9ee6", "Background": "#414559" },
                              "HotFocus": { "Foreground": "#414559", "Background": "#babbf1" },
                              "Disabled": { "Foreground": "#737994", "Background": "#414559" } },
                "Menu":     { "Normal":    { "Foreground": "#c6d0f5", "Background": "#292c3c" },
                              "Focus":    { "Foreground": "#292c3c", "Background": "#8caaee" },
                              "HotNormal":{ "Foreground": "#ca9ee6", "Background": "#292c3c" },
                              "HotFocus": { "Foreground": "#292c3c", "Background": "#babbf1" },
                              "Disabled": { "Foreground": "#737994", "Background": "#292c3c" } },
                "Error":    { "Normal":    { "Foreground": "#303446", "Background": "#e78284" },
                              "Focus":    { "Foreground": "#e78284", "Background": "#303446" },
                              "HotNormal":{ "Foreground": "#303446", "Background": "#ef9f76" },
                              "HotFocus": { "Foreground": "#ef9f76", "Background": "#303446" },
                              "Disabled": { "Foreground": "#626880", "Background": "#e78284" } }
              }
            },
            "Catppuccin Macchiato": {
              "Schemes": {
                "Base":     { "Normal":    { "Foreground": "#cad3f5", "Background": "#24273a" },
                              "Focus":    { "Foreground": "#24273a", "Background": "#8aadf4" },
                              "HotNormal":{ "Foreground": "#c6a0f6", "Background": "#24273a" },
                              "HotFocus": { "Foreground": "#24273a", "Background": "#b7bdf8" },
                              "Disabled": { "Foreground": "#6e738d", "Background": "#24273a" } },
                "TopLevel": { "Normal":    { "Foreground": "#cad3f5", "Background": "#1e2030" },
                              "Focus":    { "Foreground": "#1e2030", "Background": "#8aadf4" },
                              "HotNormal":{ "Foreground": "#c6a0f6", "Background": "#1e2030" },
                              "HotFocus": { "Foreground": "#1e2030", "Background": "#b7bdf8" },
                              "Disabled": { "Foreground": "#6e738d", "Background": "#1e2030" } },
                "Dialog":   { "Normal":    { "Foreground": "#cad3f5", "Background": "#363a4f" },
                              "Focus":    { "Foreground": "#363a4f", "Background": "#8aadf4" },
                              "HotNormal":{ "Foreground": "#c6a0f6", "Background": "#363a4f" },
                              "HotFocus": { "Foreground": "#363a4f", "Background": "#b7bdf8" },
                              "Disabled": { "Foreground": "#6e738d", "Background": "#363a4f" } },
                "Menu":     { "Normal":    { "Foreground": "#cad3f5", "Background": "#1e2030" },
                              "Focus":    { "Foreground": "#1e2030", "Background": "#8aadf4" },
                              "HotNormal":{ "Foreground": "#c6a0f6", "Background": "#1e2030" },
                              "HotFocus": { "Foreground": "#1e2030", "Background": "#b7bdf8" },
                              "Disabled": { "Foreground": "#6e738d", "Background": "#1e2030" } },
                "Error":    { "Normal":    { "Foreground": "#24273a", "Background": "#ed8796" },
                              "Focus":    { "Foreground": "#ed8796", "Background": "#24273a" },
                              "HotNormal":{ "Foreground": "#24273a", "Background": "#f5a97f" },
                              "HotFocus": { "Foreground": "#f5a97f", "Background": "#24273a" },
                              "Disabled": { "Foreground": "#5b6078", "Background": "#ed8796" } }
              }
            },
            "Catppuccin Mocha": {
              "Schemes": {
                "Base":     { "Normal":    { "Foreground": "#cdd6f4", "Background": "#1e1e2e" },
                              "Focus":    { "Foreground": "#1e1e2e", "Background": "#89b4fa" },
                              "HotNormal":{ "Foreground": "#cba6f7", "Background": "#1e1e2e" },
                              "HotFocus": { "Foreground": "#1e1e2e", "Background": "#b4befe" },
                              "Disabled": { "Foreground": "#6c7086", "Background": "#1e1e2e" } },
                "TopLevel": { "Normal":    { "Foreground": "#cdd6f4", "Background": "#181825" },
                              "Focus":    { "Foreground": "#181825", "Background": "#89b4fa" },
                              "HotNormal":{ "Foreground": "#cba6f7", "Background": "#181825" },
                              "HotFocus": { "Foreground": "#181825", "Background": "#b4befe" },
                              "Disabled": { "Foreground": "#6c7086", "Background": "#181825" } },
                "Dialog":   { "Normal":    { "Foreground": "#cdd6f4", "Background": "#313244" },
                              "Focus":    { "Foreground": "#313244", "Background": "#89b4fa" },
                              "HotNormal":{ "Foreground": "#cba6f7", "Background": "#313244" },
                              "HotFocus": { "Foreground": "#313244", "Background": "#b4befe" },
                              "Disabled": { "Foreground": "#6c7086", "Background": "#313244" } },
                "Menu":     { "Normal":    { "Foreground": "#cdd6f4", "Background": "#181825" },
                              "Focus":    { "Foreground": "#181825", "Background": "#89b4fa" },
                              "HotNormal":{ "Foreground": "#cba6f7", "Background": "#181825" },
                              "HotFocus": { "Foreground": "#181825", "Background": "#b4befe" },
                              "Disabled": { "Foreground": "#6c7086", "Background": "#181825" } },
                "Error":    { "Normal":    { "Foreground": "#1e1e2e", "Background": "#f38ba8" },
                              "Focus":    { "Foreground": "#f38ba8", "Background": "#1e1e2e" },
                              "HotNormal":{ "Foreground": "#1e1e2e", "Background": "#fab387" },
                              "HotFocus": { "Foreground": "#fab387", "Background": "#1e1e2e" },
                              "Disabled": { "Foreground": "#585b70", "Background": "#f38ba8" } }
              }
            },
            "Ayu Dark": {
              "Schemes": {
                "Base":     { "Normal":    { "Foreground": "#bfbdb6", "Background": "#0b0e14" },
                              "Focus":    { "Foreground": "#0b0e14", "Background": "#e6b450" },
                              "HotNormal":{ "Foreground": "#ff8f40", "Background": "#0b0e14" },
                              "HotFocus": { "Foreground": "#0b0e14", "Background": "#59c2ff" },
                              "Disabled": { "Foreground": "#565b66", "Background": "#0b0e14" } },
                "TopLevel": { "Normal":    { "Foreground": "#bfbdb6", "Background": "#0d1017" },
                              "Focus":    { "Foreground": "#0d1017", "Background": "#e6b450" },
                              "HotNormal":{ "Foreground": "#ff8f40", "Background": "#0d1017" },
                              "HotFocus": { "Foreground": "#0d1017", "Background": "#59c2ff" },
                              "Disabled": { "Foreground": "#565b66", "Background": "#0d1017" } },
                "Dialog":   { "Normal":    { "Foreground": "#bfbdb6", "Background": "#0f131a" },
                              "Focus":    { "Foreground": "#0f131a", "Background": "#e6b450" },
                              "HotNormal":{ "Foreground": "#ff8f40", "Background": "#0f131a" },
                              "HotFocus": { "Foreground": "#0f131a", "Background": "#59c2ff" },
                              "Disabled": { "Foreground": "#565b66", "Background": "#0f131a" } },
                "Menu":     { "Normal":    { "Foreground": "#bfbdb6", "Background": "#0d1017" },
                              "Focus":    { "Foreground": "#0d1017", "Background": "#e6b450" },
                              "HotNormal":{ "Foreground": "#ff8f40", "Background": "#0d1017" },
                              "HotFocus": { "Foreground": "#0d1017", "Background": "#59c2ff" },
                              "Disabled": { "Foreground": "#565b66", "Background": "#0d1017" } },
                "Error":    { "Normal":    { "Foreground": "#0b0e14", "Background": "#d95757" },
                              "Focus":    { "Foreground": "#d95757", "Background": "#0b0e14" },
                              "HotNormal":{ "Foreground": "#0b0e14", "Background": "#f29668" },
                              "HotFocus": { "Foreground": "#f29668", "Background": "#0b0e14" },
                              "Disabled": { "Foreground": "#565b66", "Background": "#d95757" } }
              }
            }
          }
        }
        """;

    public static IReadOnlyList<string> Names => ThemeManager.GetThemeNames ();

    public static string Current
    {
        get => ThemeManager.Theme;
        set
        {
            ThemeManager.Theme = value;
            UserSettings.Set (UserSettings.ThemeName, value);
        }
    }

    public static void Restore ()
    {
        string saved = UserSettings.Get (UserSettings.ThemeName);

        if (Names.Contains (saved))
        {
            ThemeManager.Theme = saved;
        }
    }

    public static void Register () { TuiConfigurationBuilder.Shared.RuntimeConfig = Custom; }

    public static void Apply () { TuiConfigurationBuilder.Shared.ApplyToStaticFacades (); }
}

