using ElementGui.Models;

namespace ElementGui.Services;

/// <summary>
/// Rewrites a pre-Element <c>SelectedMode</c> value into the current <see cref="UnlockerMode"/> set.
///
/// The old app had four modes: SteamTools, OpenSteamTools, OpenSteamToolsNightly and CloudRedirect.
/// All OpenSteamTool-derived modes become <see cref="UnlockerMode.Element"/>. SteamTools and
/// CloudRedirect are gone with no successor, so those users are reset to "no mode" and re-onboarded.
/// </summary>
public static class ModeMigration
{
    /// <summary>
    /// Apply the migration to persisted settings. Returns true if onboarding must be re-shown.
    /// </summary>
    public static bool Apply(SettingsService settings)
    {
        var (newMode, resetOnboarding) = Migrate(settings.SelectedMode);
        if (newMode != settings.SelectedMode) settings.SelectedMode = newMode;
        return resetOnboarding;
    }

    /// <summary>
    /// The migration itself, as a pure function of the stored string so it can be tested without
    /// touching the real settings file.
    /// </summary>
    /// <param name="stored">Raw <c>SelectedMode</c> as found on disk.</param>
    /// <returns>
    /// The value to store (null = no mode selected), and whether onboarding must be re-shown. True
    /// only when the user WAS on a mode that has since been retired, so they now have none.
    /// </returns>
    public static (string? Mode, bool ResetOnboarding) Migrate(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return (null, false);   // fresh install; onboarding handles it
        if (Enum.TryParse<UnlockerMode>(stored, out var current))
            return (current.ToString(), false);                        // already current. Leave it be

        return stored switch
        {
            // All old OpenSteamTool-derived modes map to Element
            "OpenSteamTools" or "OpenSteamToolsNightly" or "Ost" or "Bst"
                => (UnlockerMode.Element.ToString(), false),

            // SteamTools and the CloudRedirect fix are retired with nothing to map onto. Clear the mode
            // and send them back through onboarding to choose deliberately. Anything else unrecognised
            // (hand-edited, or from a build we don't know) lands here too. Safer than guessing.
            _ => (null, true),
        };
    }
}
