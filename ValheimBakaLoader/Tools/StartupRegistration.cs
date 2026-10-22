using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// One sentence the Upkeep card draws under the Start with Windows switch: the catalog id,
    /// and the path it names when it names one. The same shape as the StartupNotice the
    /// Windows helper works out, carried here so the bridge can hand it to the page without
    /// knowing how the host worked it out.
    /// </summary>
    public sealed class HostStartupNotice
    {
        public string Id { get; init; }

        public string Path { get; init; }
    }

    /// <summary>
    /// Everything the card has to say under the switch right now: one sentence or two, the
    /// machine wide one first and this account's own under it. StartupHelper.cs says why
    /// there are two.
    /// </summary>
    public sealed class HostStartupNotes
    {
        public HostStartupNotice Notice { get; init; }

        public HostStartupNotice Also { get; init; }

        /// <summary>Nothing to say, which is what a healthy machine and a host with no login entry answer.</summary>
        public static readonly HostStartupNotes None = new();
    }

    /// <summary>
    /// The host's "start me at login" entry, as the bridge sees it. On Windows this is the Run
    /// key behind StartupHelper; a service started by its own supervisor has no such entry
    /// and answers that there is nothing to say. The bridge cannot name StartupHelper itself,
    /// because that helper knows it is running inside a Windows Forms application and this
    /// side does not.
    /// </summary>
    public interface IStartupRegistration
    {
        /// <summary>
        /// Puts the login entry where the preference says, and answers the notes the card
        /// should draw afterwards. Called only by a save that MOVED the switch.
        /// </summary>
        HostStartupNotes Apply(bool userPreference);

        /// <summary>The notes the card should draw right now, without writing anything.</summary>
        HostStartupNotes NotesFor(bool userPreference);
    }

    /// <summary>A host with no login entry to write.</summary>
    public sealed class NoStartupRegistration : IStartupRegistration
    {
        public HostStartupNotes Apply(bool userPreference) => HostStartupNotes.None;

        public HostStartupNotes NotesFor(bool userPreference) => HostStartupNotes.None;
    }

    /// <summary>The half of the Start with Windows save that is about the preference and not the host.</summary>
    public static class StartupPreference
    {
        /// <summary>
        /// Puts the value a save carried onto the preferences, and answers whether that save
        /// actually MOVED the switch.
        /// <para>
        /// The same rule, and the same reason, as
        /// <see cref="Logging.LogLevelControl.ApplySavedValue"/>: every switch on the Upkeep
        /// card posts the whole card together, so this key arrives on a save that came from a
        /// different switch entirely. Applying on the key's PRESENCE is what made an ordinary
        /// save open the Run key, every time, for a preference nobody had touched.
        /// </para>
        /// </summary>
        public static bool ApplySavedValue(UserPreferences prefs, JToken carried)
        {
            if (prefs == null || carried == null) return false;

            var wanted = carried.Value<bool>();
            var moved = wanted != prefs.StartWithWindows;
            prefs.StartWithWindows = wanted;
            return moved;
        }
    }
}
