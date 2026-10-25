using System;
using System.Collections.Generic;
using System.Linq;
using ValheimBakaLoader.Properties;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// One mod recorded in a profile's manifest, so a server's mod set is tracked
    /// and reconstructable on restore/export. Owner+Name+Version identify a
    /// Thunderstore package the same way the mod scanner resolves installed mods.
    /// </summary>
    public class ModManifestEntry
    {
        [Newtonsoft.Json.JsonProperty("owner")] public string Owner { get; set; }
        [Newtonsoft.Json.JsonProperty("name")] public string Name { get; set; }
        [Newtonsoft.Json.JsonProperty("version")] public string Version { get; set; }
    }

    /// <summary>
    /// One saved server profile with all defaults applied. Round-trips to
    /// disk through <see cref="ServerPreferencesFile"/>.
    /// </summary>
    public class ServerPreferences : INamedEntry
    {
        string INamedEntry.EntryName => ProfileName;

        public string ProfileName { get; set; } = Resources.DefaultServerProfileName;

        public DateTime LastSaved { get; set; } = DateTime.UnixEpoch;

        public string Name { get; set; }

        public string Password { get; set; }

        public string WorldName { get; set; }

        public bool Public { get; set; }

        public int Port { get; set; } = int.Parse(Resources.DefaultServerPort);

        public bool Crossplay { get; set; }

        public int SaveInterval { get; set; } = int.Parse(Resources.DefaultSaveInterval);

        public int BackupCount { get; set; } = int.Parse(Resources.DefaultBackupCount);

        public int BackupIntervalShort { get; set; } = int.Parse(Resources.DefaultBackupIntervalShort);

        public int BackupIntervalLong { get; set; } = int.Parse(Resources.DefaultBackupIntervalLong);

        public bool AutoStart { get; set; }

        public string AdditionalArgs { get; set; }

        public string ServerExePath { get; set; }

        public string SaveDataFolderPath { get; set; }

        public bool WriteServerLogsToFile { get; set; } = true;

        /// <summary>
        /// The Steam build id (or, on an install Steam does not track, the binary
        /// fingerprint) of the server this profile last actually started. Null until the
        /// profile has been launched once, which is what makes the first start after a
        /// game update noticeable.
        /// </summary>
        public string LastLaunchedServerBuild { get; set; }

        /// <summary>
        /// The binary fingerprint of the server this profile last actually started, taken at
        /// the same moment as <see cref="LastLaunchedServerBuild"/>. The build id above is the
        /// Steam manifest's answer and the fingerprint is the binaries' own, so keeping both
        /// leaves the launch guard something to compare when the manifest cannot be read at
        /// the next start. Null until the profile has been launched once.
        /// </summary>
        public string LastLaunchedServerFingerprint { get; set; }

        /// <summary>
        /// The game version that server reported once it was up ("1.0.7"), read from its
        /// own banner. Null until a launched server has printed one.
        /// </summary>
        public string LastLaunchedGameVersion { get; set; }

        public bool AutoRestart { get; set; }

        public int AutoRestartDelay { get; set; } = 10;

        public bool EmptyServerRestart { get; set; }

        public int EmptyServerRestartDelayMinutes { get; set; } = 5;

        public bool ScheduledRestart { get; set; }

        public int ScheduledRestartHours { get; set; } = 6;

        /// <summary>
        /// Clear the cheat marks off this realm's world the next time it has been empty for a
        /// minute, then turn this back off.
        /// <para>
        /// The Players hall's button needs a host sitting at the window at a moment when
        /// nobody is playing, because the sweep rewrites containers and a container somebody
        /// has open refuses to reload itself. On a server people actually use that moment is
        /// four in the morning. This is the same sweep, armed and left: one shot, and the app
        /// puts it down again the moment it lands. Off by default and never turned on by
        /// anything but a host's own press.
        /// </para>
        /// </summary>
        public bool CleanseWhenEmpty { get; set; }

        /// <summary>
        /// Which mod manager this realm's mod profile code is for: <c>none</c>,
        /// <c>thunderstore</c> or <c>gale</c>. See <see cref="ModProfileCodes"/>.
        /// </summary>
        public string ModProfileCodeKind { get; set; } = ModProfileCodes.None;

        /// <summary>
        /// The share code players paste into their mod manager to get this realm's mods. It
        /// rides on the Discord status post as one field; blank means no field.
        /// </summary>
        public string ModProfileCode { get; set; }

        // -- The realm's own Herald post --

        /// <summary>
        /// Whether this realm keeps a status post on Discord. Off by default, so a realm made
        /// after the update never posts by surprise; the app-wide Discord switch is still the
        /// master over every realm.
        /// </summary>
        public bool HeraldOn { get; set; }

        /// <summary>
        /// The webhook this realm posts through. Blank was the app-wide webhook until 1.2.17,
        /// which copied that link into every realm that used it; blank is now no webhook.
        /// </summary>
        public string HeraldWebhookUrl { get; set; }

        /// <summary>
        /// The thread inside the webhook's channel. Blank with a webhook of the realm's own is
        /// the channel itself; blank with the app-wide webhook is the app-wide thread, because a
        /// thread belongs to one channel and the app-wide one is where that webhook posts.
        /// </summary>
        public string HeraldThreadId { get; set; }

        /// <summary>Include this realm's public address in its post.</summary>
        public bool HeraldShareAddress { get; set; } = true;

        /// <summary>Include this realm's password in its post. Anyone in the channel can read it.</summary>
        public bool HeraldSharePassword { get; set; }

        /// <summary>The post's title. Blank is the server name.</summary>
        public string HeraldTitle { get; set; }

        /// <summary>One line of the host's own at the top of the post. Blank is no line.</summary>
        public string HeraldStatusLine { get; set; }

        /// <summary>
        /// The Discord message id of this realm's post. State, not a setting: only the status
        /// service writes it, through the gated whole-document write, and every other save of
        /// the profile keeps what is on disk (see <see cref="KeepHeraldPostFrom"/>).
        /// </summary>
        public string HeraldStatusMessageId { get; set; }

        /// <summary>The name and picture the post was made with; see DiscordHeraldLook.SignatureFor.</summary>
        public string HeraldStatusMessageSignature { get; set; }

        /// <summary>The thread the post opened in a forum channel, kept beside the id it belongs with.</summary>
        public string HeraldStatusThreadId { get; set; }

        // -- Where this realm's event posts go (1.2.16). Three kinds, because they are read by
        //    different people: the routine news of the server, a crash somebody has to act on,
        //    and players coming and going, which a busy server fills a channel with. Each kind
        //    has its own switch, and a webhook and thread that are blank for the realm's own.
        //    All off for a realm made after the update, like the realm's own switch. --

        /// <summary>Started, stopped, updated, update failed, held, old world, cheat marks cleared, mods updated.</summary>
        public bool HeraldRoutineOn { get; set; }
        public string HeraldRoutineWebhookUrl { get; set; }
        public string HeraldRoutineThreadId { get; set; }

        /// <summary>The server crashed.</summary>
        public bool HeraldCrashOn { get; set; }
        public string HeraldCrashWebhookUrl { get; set; }
        public string HeraldCrashThreadId { get; set; }

        /// <summary>
        /// A Discord role to ping when the server crashes, by its id. Only that role is ever
        /// pinged: the post says so to Discord, so a name in the post can never reach everyone.
        /// </summary>
        public string HeraldCrashRoleId { get; set; }

        /// <summary>A player joined or left.</summary>
        public bool HeraldPlayersOn { get; set; }
        public string HeraldPlayersWebhookUrl { get; set; }
        public string HeraldPlayersThreadId { get; set; }

        // -- The Discord screen of 1.2.17. The webhook's own name, kept so the screen can say
        //    which webhook the realm posts through without showing the link; the name and the
        //    picture the realm's posts are signed with, which were app wide before; and a note
        //    per kind of event post that Discord refused it for naming no thread in a forum,
        //    so the screen can say so under that kind instead of only in the Saga. --

        /// <summary>The name Discord gave the realm's webhook when it was checked, or nothing.</summary>
        public string HeraldWebhookName { get; set; }

        /// <summary>The name the realm's posts are signed with. Blank is the webhook's own name.</summary>
        public string HeraldBotName { get; set; }

        /// <summary>The picture the realm's posts are signed with. Blank is the BakaLoader logo.</summary>
        public string HeraldAvatarUrl { get; set; }

        /// <summary>Discord refused the last post of that kind: a forum channel and no thread named.</summary>
        public bool HeraldRoutineNoThread { get; set; }
        public bool HeraldCrashNoThread { get; set; }
        public bool HeraldPlayersNoThread { get; set; }

        public bool RconEnabled { get; set; }

        public int RconPort { get; set; } = 25575;

        public string RconPassword { get; set; }

        // -- Multi-server isolation --

        /// <summary>Soft-deleted: hidden from the chip strip, kept for restore. Touches nothing on disk.</summary>
        public bool Archived { get; set; }

        /// <summary>
        /// This profile owns a BakaLoader-provisioned install directory (junctioned game
        /// files + its own BepInEx/plugins). Marks the install as safe to reclaim on delete.
        /// </summary>
        public bool IsolatedInstall { get; set; }

        /// <summary>The mods this server was last seen running, for restore/export.</summary>
        public List<ModManifestEntry> ModManifest { get; set; }

        /// <summary>
        /// The profile name of the realm this one is a test copy of, or null for a realm that is
        /// not one. Set once, by Make a test copy, and moved along when the source is renamed. A
        /// test copy is never listed in the game's server browser: the host side holds Public
        /// off for it on every save and on every start, whatever the page sends. A source that
        /// has been deleted leaves this standing, and the chip says the source is gone.
        /// </summary>
        public string TestCopyOf { get; set; }

        /// <summary>
        /// True once the realm <see cref="TestCopyOf"/> names has been deleted. The mark is a
        /// name, and a name can be given again: without this, a new realm that happened to take
        /// the deleted source's name would quietly become the source of a copy it never made.
        /// Set when the source is removed, and when a realm is made under a name a copy still
        /// points at with nothing there; never cleared, because nothing can bring the old
        /// source back.
        /// </summary>
        public bool TestSourceGone { get; set; }

        /// <summary>
        /// The in-game name the source had when this copy was made, beside
        /// <see cref="TestCopyOf"/>, which is its profile name. The profile name follows the
        /// source through a rename and this does not, so the page can still say both: the name
        /// the host knew it by and the profile it is now. Null for a realm that is not a test
        /// copy, and for a copy of a realm that had no in-game name (the page says the profile
        /// name then). Set once, by servers.create.
        /// </summary>
        public string TestCopyOfName { get; set; }

        // -- How the realm's chip looks in the sidebar --

        /// <summary>
        /// The Elder Futhark rune the realm's chip shows in place of the first letter of its
        /// name, by the rune's name ("uruz", "ansuz"). Null means the letter. The page holds the
        /// list and draws the letter for any name it does not know, so a hand edit can only ever
        /// fall back to the letter.
        /// </summary>
        public string LookRune { get; set; }

        /// <summary>
        /// The colour the chip's letter or rune is drawn in, by name from the page's fixed list
        /// ("ember", "moss"). Null means the chip's own colour.
        /// </summary>
        public string LookColor { get; set; }

        // -- Following a Gale profile --

        /// <summary>
        /// The Gale sync profile this realm follows, as the host pasted it: a base64 UUID or a
        /// six character code. Blank means off, and off means Gale is never contacted for this
        /// realm. A stored value that fails the shape check reads as blank, so a hand edit
        /// cannot send something odd to the service.
        /// </summary>
        public string GaleProfileId { get; set; }

        /// <summary>
        /// The profile's updatedAt the host last approved or dismissed, exactly as the service
        /// wrote it. The condition bar's row comes back only when the profile's updatedAt is
        /// different from this. Null until the first approval or dismissal.
        /// </summary>
        public string GaleLastUpdatedAt { get; set; }

        /// <summary>
        /// The "Namespace-Name" of every mod the host unticked in the approval dialog. They
        /// stay unticked through later changes to the profile until the host ticks them again.
        /// Never null on a loaded profile.
        /// </summary>
        public List<string> GaleUnticked { get; set; } = new();

        /// <summary>
        /// Each pinned build the approval dialog tried and Thunderstore no longer has, as
        /// "Namespace-Name@version". The dialog marks those rows "not available" on the next
        /// review instead of offering them as plain adds; a new version in the profile is a new
        /// entry, so it is offered again. Never null on a loaded profile.
        /// </summary>
        public List<string> GaleNotAvailable { get; set; } = new();

        /// <summary>
        /// The config files (paths under BepInEx/config, forward slashes) the host unticked in
        /// the configs part of the Gale dialog. Shown unticked on every later review until the
        /// host ticks them again, and never written while unticked. Never null on a loaded profile.
        /// </summary>
        public List<string> GaleConfigUnticked { get; set; } = new();

        // -- Following a test copy of this realm (merge-back) --

        /// <summary>
        /// The profile name of a test copy of this realm that this realm takes its mods and
        /// configs from, in place of a Gale profile, or null for none. The Gale box, its check,
        /// its dialog and its condition bar row then read the test copy's folders on this PC and
        /// nothing is asked of Gale's service. Moved along when the copy is renamed, and let go
        /// when the copy is deleted or its name is given to a new realm. The world never travels.
        /// </summary>
        public string FollowTestCopy { get; set; }

        /// <summary>
        /// What the test copy's mods were (a fingerprint of their names, versions and files) when
        /// the host last approved or dismissed them, the test copy's own stand in for
        /// <see cref="GaleLastUpdatedAt"/>. Null until the first approval or dismissal.
        /// </summary>
        public string FollowTestCopyLastApproved { get; set; }

        /// <summary>The mods the host unticked while following the test copy. Kept apart from the Gale profile's. Never null on a loaded profile.</summary>
        public List<string> FollowTestCopyUnticked { get; set; } = new();

        /// <summary>The config files the host unticked while following the test copy. Kept apart from the Gale profile's. Never null on a loaded profile.</summary>
        public List<string> FollowTestCopyConfigUnticked { get; set; } = new();

        /// <summary>
        /// The mods the host ticked while following the test copy (1.3.0), kept apart from
        /// <see cref="GaleTicked"/> so a tick given on one feed never starts a row ticked on the
        /// other. Null until a choice is stored under the follow.
        /// </summary>
        public List<string> FollowTestCopyTicked { get; set; }

        /// <summary>The config files the host ticked while following the test copy, kept apart from <see cref="GaleConfigTicked"/>. Null until a choice is stored.</summary>
        public List<string> FollowTestCopyConfigTicked { get; set; }

        /// <summary>What a test copy follow's key starts with, so it can never equal a Gale profile id.</summary>
        public const string TestCopyFollowPrefix = "testcopy:";

        /// <summary>True when this realm takes its mods and configs from a test copy rather than from a Gale profile.</summary>
        public bool FollowsTestCopy() => !string.IsNullOrWhiteSpace(FollowTestCopy);

        /// <summary>
        /// What this realm follows, as one key: "testcopy:" and the copy's name while it follows
        /// a test copy, else the Gale profile id, else null. A check, a configs read and an
        /// approval are each held to the key they were made for, so a switch of feed drops them.
        /// </summary>
        public string GaleFollowKey() =>
            FollowsTestCopy() ? TestCopyFollowPrefix + FollowTestCopy.Trim() : GaleProfileId;

        /// <summary>The last approval of whatever this realm follows now.</summary>
        public string GaleFeedLastApproved() => FollowsTestCopy() ? FollowTestCopyLastApproved : GaleLastUpdatedAt;

        /// <summary>Stores the last approval for whatever this realm follows now, and leaves the other feed's alone.</summary>
        public void SetGaleFeedLastApproved(string stamp)
        {
            if (FollowsTestCopy()) FollowTestCopyLastApproved = stamp;
            else GaleLastUpdatedAt = stamp;
        }

        /// <summary>The unticked mods of whatever this realm follows now.</summary>
        public List<string> GaleFeedUnticked() =>
            (FollowsTestCopy() ? FollowTestCopyUnticked : GaleUnticked) ?? new List<string>();

        /// <summary>Stores the unticked mods for whatever this realm follows now.</summary>
        public void SetGaleFeedUnticked(List<string> names)
        {
            if (FollowsTestCopy()) FollowTestCopyUnticked = NormalizeUnticked(names);
            else GaleUnticked = NormalizeUnticked(names);
        }

        /// <summary>The unticked config files of whatever this realm follows now.</summary>
        public List<string> GaleFeedConfigUnticked() =>
            (FollowsTestCopy() ? FollowTestCopyConfigUnticked : GaleConfigUnticked) ?? new List<string>();

        /// <summary>Stores the unticked config files for whatever this realm follows now.</summary>
        public void SetGaleFeedConfigUnticked(IEnumerable<string> paths)
        {
            if (FollowsTestCopy()) FollowTestCopyConfigUnticked = NormalizeUnticked(paths);
            else GaleConfigUnticked = NormalizeUnticked(paths);
        }

        /// <summary>The ticked mods of whatever this realm follows now, or null when none has been stored for it yet.</summary>
        public List<string> GaleFeedTicked() => FollowsTestCopy() ? FollowTestCopyTicked : GaleTicked;

        /// <summary>Stores the ticked mods for whatever this realm follows now, and leaves the other feed's alone.</summary>
        public void SetGaleFeedTicked(List<string> names)
        {
            if (FollowsTestCopy()) FollowTestCopyTicked = NormalizeTicked(names);
            else GaleTicked = NormalizeTicked(names);
        }

        /// <summary>The ticked config files of whatever this realm follows now, or null when none has been stored for it yet.</summary>
        public List<string> GaleFeedConfigTicked() => FollowsTestCopy() ? FollowTestCopyConfigTicked : GaleConfigTicked;

        /// <summary>Stores the ticked config files for whatever this realm follows now, and leaves the other feed's alone.</summary>
        public void SetGaleFeedConfigTicked(List<string> paths)
        {
            if (FollowsTestCopy()) FollowTestCopyConfigTicked = NormalizeTicked(paths);
            else GaleConfigTicked = NormalizeTicked(paths);
        }

        /// <summary>Stops following a test copy: the copy's name, its approval, its unticks and its ticks go. A Gale profile id, if any, is left as it was.</summary>
        public void ForgetTestCopyFollow()
        {
            FollowTestCopy = null;
            FollowTestCopyLastApproved = null;
            FollowTestCopyUnticked = new List<string>();
            FollowTestCopyConfigUnticked = new List<string>();
            FollowTestCopyTicked = null;
            FollowTestCopyConfigTicked = null;
        }

        /// <summary>
        /// The "Namespace-Name" of every mod the host ticked in the approval dialog (1.3.0). From
        /// 1.3.0 a mod starts ticked only when it is on this list, so a first review ticks nothing.
        /// Null until 1.3.0 first stores a choice for this realm; an empty list is a stored choice
        /// and is written as one. See <see cref="GaleStartsTicked"/> for a realm that still has
        /// only the older untick list.
        /// </summary>
        public List<string> GaleTicked { get; set; }

        /// <summary>
        /// The config files the host ticked in the configs part of the Gale dialog (1.3.0), the
        /// same way <see cref="GaleTicked"/> keeps the mods. Null until 1.3.0 first stores a choice.
        /// </summary>
        public List<string> GaleConfigTicked { get; set; }

        /// <summary>
        /// Whether a row the Gale dialog offers starts ticked. From 1.3.0 a row is ticked only when
        /// the host ticked it before, so a first review starts with nothing ticked. A realm whose
        /// file has the older untick list and no ticked list yet keeps the older rule until the
        /// next choice is stored: everything not unticked starts ticked, so no choice made before
        /// 1.3.0 is lost.
        /// </summary>
        public static bool GaleStartsTicked(string key, List<string> ticked, List<string> unticked)
        {
            var k = key?.Trim();
            if (string.IsNullOrEmpty(k)) return false;
            if (ticked == null)
                return unticked != null && unticked.Count > 0 && !unticked.Contains(k, StringComparer.OrdinalIgnoreCase);
            return ticked.Contains(k, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A ticked list after a choice: what was kept, less the ones unticked now, plus the ones
        /// ticked now. Never null, so the realm reads by the 1.3.0 rule from then on.
        /// </summary>
        public static List<string> GaleTicksAfter(IEnumerable<string> kept, IEnumerable<string> on, IEnumerable<string> off)
        {
            var dropped = new HashSet<string>(NormalizeUnticked(off), StringComparer.OrdinalIgnoreCase);
            var ticked = new HashSet<string>(NormalizeUnticked(on), StringComparer.OrdinalIgnoreCase);
            return NormalizeUnticked(NormalizeUnticked(kept).Where(n => !dropped.Contains(n) || ticked.Contains(n)).Concat(ticked));
        }

        /// <summary>A ticked list as it is kept: null stays null (no choice stored yet), anything else normalised.</summary>
        public static List<string> NormalizeTicked(IEnumerable<string> names) => names == null ? null : NormalizeUnticked(names);

        /// <summary>
        /// Puts the Gale fields of <paramref name="stored"/> onto this profile. The
        /// settings form has no Gale fields: it fetches the profile, changes what it has boxes
        /// for, and sends the whole object back, so whatever Gale state it carries is a copy of
        /// an older read. An approval that landed between that read and the save would be
        /// undone by it, and the updatedAt the page hands back has been through a JSON date
        /// parse on the way, which does not give the service's text back. What is on disk wins.
        /// </summary>
        public ServerPreferences KeepGaleFollowFrom(ServerPreferences stored)
        {
            if (stored == null) return this;
            GaleProfileId = stored.GaleProfileId;
            GaleLastUpdatedAt = stored.GaleLastUpdatedAt;
            GaleUnticked = NormalizeUnticked(stored.GaleUnticked);
            GaleNotAvailable = NormalizeUnticked(stored.GaleNotAvailable);
            GaleConfigUnticked = NormalizeUnticked(stored.GaleConfigUnticked);
            GaleTicked = NormalizeTicked(stored.GaleTicked);
            GaleConfigTicked = NormalizeTicked(stored.GaleConfigTicked);
            // The test copy follow is written by the same box, and only by it.
            FollowTestCopy = BlankAsNull(stored.FollowTestCopy);
            FollowTestCopyLastApproved = stored.FollowTestCopyLastApproved;
            FollowTestCopyUnticked = NormalizeUnticked(stored.FollowTestCopyUnticked);
            FollowTestCopyConfigUnticked = NormalizeUnticked(stored.FollowTestCopyConfigUnticked);
            FollowTestCopyTicked = NormalizeTicked(stored.FollowTestCopyTicked);
            FollowTestCopyConfigTicked = NormalizeTicked(stored.FollowTestCopyConfigTicked);
            return this;
        }

        /// <summary>
        /// The same guard for a profile with nothing on disk to take the Gale fields from (a
        /// first save under a new name, or a stored file that would not read). The id and the
        /// unticks are kept as sent, but the stamp is dropped: it has been through the page's
        /// date parse and is no longer the service's text, so keeping it would match nothing
        /// and could bring back a row the host had dismissed. Dropped, the worst it does is let
        /// the row show once.
        /// </summary>
        public ServerPreferences KeepGaleFollowWithoutStore()
        {
            GaleLastUpdatedAt = null;
            GaleUnticked = NormalizeUnticked(GaleUnticked);
            GaleNotAvailable = NormalizeUnticked(GaleNotAvailable);
            GaleConfigUnticked = NormalizeUnticked(GaleConfigUnticked);
            GaleTicked = NormalizeTicked(GaleTicked);
            GaleConfigTicked = NormalizeTicked(GaleConfigTicked);
            FollowTestCopy = BlankAsNull(FollowTestCopy);
            FollowTestCopyLastApproved = null;
            FollowTestCopyUnticked = NormalizeUnticked(FollowTestCopyUnticked);
            FollowTestCopyConfigUnticked = NormalizeUnticked(FollowTestCopyConfigUnticked);
            FollowTestCopyTicked = NormalizeTicked(FollowTestCopyTicked);
            FollowTestCopyConfigTicked = NormalizeTicked(FollowTestCopyConfigTicked);
            return this;
        }

        /// <summary>
        /// Puts the test copy mark stored on disk back onto a profile the page sent, and holds
        /// Public off when the realm is a test copy. The settings form has no box for the mark,
        /// so what it sends back is a copy of an older read at best; what is on disk wins, the
        /// same as the Gale fields. With nothing on disk (a first save under a new name) the
        /// mark is kept as sent. Either way a test copy leaves here unlisted.
        /// </summary>
        public ServerPreferences KeepTestCopyFrom(ServerPreferences stored)
        {
            if (stored != null)
            {
                TestCopyOf = stored.TestCopyOf;
                TestSourceGone = stored.TestSourceGone;
                TestCopyOfName = stored.TestCopyOfName;
            }
            TestCopyOf = string.IsNullOrWhiteSpace(TestCopyOf) ? null : TestCopyOf.Trim();
            if (TestCopyOf == null) TestSourceGone = false;
            TestCopyOfName = TestCopyOf == null ? null : BlankAsNull(TestCopyOfName);
            if (TestCopyOf != null) Public = false;
            return this;
        }

        /// <summary>
        /// Puts the post state stored on disk back onto a profile about to replace it. The post's
        /// id is written by the status service from a timer while the server's own thread can be
        /// recording a launched build into this same profile, and that writer loaded the profile
        /// before the post was made: without this its save would put the id back to nothing and
        /// the next edit would make a second post in the channel.
        /// </summary>
        public ServerPreferences KeepHeraldPostFrom(ServerPreferences stored)
        {
            if (stored == null) return this;
            HeraldStatusMessageId = stored.HeraldStatusMessageId;
            HeraldStatusMessageSignature = stored.HeraldStatusMessageSignature;
            HeraldStatusThreadId = stored.HeraldStatusThreadId;
            return this;
        }

        /// <summary>
        /// Puts the Herald settings stored on disk back onto a profile the settings form sent. The
        /// form has no Herald boxes (the Discord hall saves those through userprefs.save), so what
        /// it carries is a copy of an older read at best. With nothing on disk, a first save under
        /// a new name, the realm starts switched off and with no post: a copy never adopts the
        /// post of the realm it was copied from.
        /// </summary>
        public ServerPreferences KeepHeraldFrom(ServerPreferences stored)
        {
            if (stored == null) return ForgetHeraldPost();
            HeraldOn = stored.HeraldOn;
            HeraldWebhookUrl = stored.HeraldWebhookUrl;
            HeraldThreadId = stored.HeraldThreadId;
            HeraldShareAddress = stored.HeraldShareAddress;
            HeraldSharePassword = stored.HeraldSharePassword;
            HeraldTitle = stored.HeraldTitle;
            HeraldStatusLine = stored.HeraldStatusLine;
            HeraldRoutineOn = stored.HeraldRoutineOn;
            HeraldRoutineWebhookUrl = stored.HeraldRoutineWebhookUrl;
            HeraldRoutineThreadId = stored.HeraldRoutineThreadId;
            HeraldCrashOn = stored.HeraldCrashOn;
            HeraldCrashWebhookUrl = stored.HeraldCrashWebhookUrl;
            HeraldCrashThreadId = stored.HeraldCrashThreadId;
            HeraldCrashRoleId = stored.HeraldCrashRoleId;
            HeraldPlayersOn = stored.HeraldPlayersOn;
            HeraldPlayersWebhookUrl = stored.HeraldPlayersWebhookUrl;
            HeraldPlayersThreadId = stored.HeraldPlayersThreadId;
            HeraldWebhookName = stored.HeraldWebhookName;
            HeraldBotName = stored.HeraldBotName;
            HeraldAvatarUrl = stored.HeraldAvatarUrl;
            HeraldRoutineNoThread = stored.HeraldRoutineNoThread;
            HeraldCrashNoThread = stored.HeraldCrashNoThread;
            HeraldPlayersNoThread = stored.HeraldPlayersNoThread;
            return KeepHeraldPostFrom(stored);
        }

        /// <summary>
        /// A realm made from another one shares nothing yet: switched off, and no post, signature
        /// or forum thread of its own. The webhook, the thread, the switches and the look are kept,
        /// so turning the copy on posts where the host would expect.
        /// </summary>
        public ServerPreferences ForgetHeraldPost()
        {
            HeraldOn = false;
            HeraldStatusMessageId = null;
            HeraldStatusMessageSignature = null;
            HeraldStatusThreadId = null;
            return this;
        }

        /// <summary>The unticked list as it is kept: trimmed, no blanks, each name once whatever its case.</summary>
        public static List<string> NormalizeUnticked(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<string>();
            foreach (var name in names ?? Array.Empty<string>())
            {
                var n = name?.Trim();
                if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
                kept.Add(n);
            }
            return kept;
        }

        private static string StoredGaleProfileId(string stored)
        {
            var check = Tools.GaleProfileId.Check(stored);
            return check.Ok ? check.Id : null;
        }

        /// <summary>A text setting as it is stored: trimmed, and null when blank.</summary>
        public static string BlankAsNull(string value)
        {
            var v = value?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }

        /// <summary>
        /// A look name as it is stored: trimmed, at most 32 letters, and null when blank, so a
        /// realm that never chose a look writes nothing at all.
        /// </summary>
        public static string NormalizeLook(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            return trimmed.Length > 32 ? trimmed.Substring(0, 32) : trimmed;
        }

        public static ServerPreferences FromFile(ServerPreferencesFile file)
        {
            var defaults = new ServerPreferences();
            if (file is null) return defaults;

            return new ServerPreferences
            {
                ProfileName = string.IsNullOrWhiteSpace(file.ProfileName) ? defaults.ProfileName : file.ProfileName,
                LastSaved = file.LastSaved ?? defaults.LastSaved,
                Name = file.Name ?? defaults.Name,
                Password = file.Password ?? defaults.Password,
                WorldName = file.WorldName ?? defaults.WorldName,
                Public = file.Community ?? defaults.Public,
                Port = file.Port ?? defaults.Port,
                Crossplay = file.Crossplay ?? defaults.Crossplay,
                SaveInterval = file.SaveInterval ?? defaults.SaveInterval,
                BackupCount = file.BackupCount ?? defaults.BackupCount,
                BackupIntervalShort = file.BackupIntervalShort ?? defaults.BackupIntervalShort,
                BackupIntervalLong = file.BackupIntervalLong ?? defaults.BackupIntervalLong,
                AutoStart = file.AutoStart ?? defaults.AutoStart,
                AdditionalArgs = file.AdditionalArgs ?? defaults.AdditionalArgs,
                ServerExePath = file.ServerExePath ?? defaults.ServerExePath,
                SaveDataFolderPath = file.SaveDataFolderPath ?? defaults.SaveDataFolderPath,
                WriteServerLogsToFile = file.WriteServerLogsToFile ?? defaults.WriteServerLogsToFile,
                LastLaunchedServerBuild = file.LastLaunchedServerBuild ?? defaults.LastLaunchedServerBuild,
                LastLaunchedServerFingerprint = file.LastLaunchedServerFingerprint ?? defaults.LastLaunchedServerFingerprint,
                LastLaunchedGameVersion = file.LastLaunchedGameVersion ?? defaults.LastLaunchedGameVersion,
                AutoRestart = file.AutoRestart ?? defaults.AutoRestart,
                AutoRestartDelay = file.AutoRestartDelay ?? defaults.AutoRestartDelay,
                EmptyServerRestart = file.EmptyServerRestart ?? defaults.EmptyServerRestart,
                EmptyServerRestartDelayMinutes = file.EmptyServerRestartDelayMinutes ?? defaults.EmptyServerRestartDelayMinutes,
                ScheduledRestart = file.ScheduledRestart ?? defaults.ScheduledRestart,
                ScheduledRestartHours = file.ScheduledRestartHours ?? defaults.ScheduledRestartHours,
                CleanseWhenEmpty = file.CleanseWhenEmpty ?? defaults.CleanseWhenEmpty,
                ModProfileCodeKind = ModProfileCodes.NormalizeKind(file.ModProfileCodeKind ?? defaults.ModProfileCodeKind),
                ModProfileCode = string.IsNullOrWhiteSpace(file.ModProfileCode) ? defaults.ModProfileCode : file.ModProfileCode.Trim(),
                HeraldOn = file.HeraldOn ?? defaults.HeraldOn,
                HeraldWebhookUrl = BlankAsNull(file.HeraldWebhookUrl),
                HeraldThreadId = BlankAsNull(file.HeraldThreadId),
                HeraldShareAddress = file.HeraldShareAddress ?? defaults.HeraldShareAddress,
                HeraldSharePassword = file.HeraldSharePassword ?? defaults.HeraldSharePassword,
                HeraldTitle = BlankAsNull(file.HeraldTitle),
                HeraldStatusLine = BlankAsNull(file.HeraldStatusLine),
                HeraldStatusMessageId = BlankAsNull(file.HeraldStatusMessageId),
                HeraldStatusMessageSignature = file.HeraldStatusMessageSignature,
                HeraldStatusThreadId = BlankAsNull(file.HeraldStatusThreadId),
                HeraldRoutineOn = file.HeraldRoutineOn ?? defaults.HeraldRoutineOn,
                HeraldRoutineWebhookUrl = BlankAsNull(file.HeraldRoutineWebhookUrl),
                HeraldRoutineThreadId = BlankAsNull(file.HeraldRoutineThreadId),
                HeraldCrashOn = file.HeraldCrashOn ?? defaults.HeraldCrashOn,
                HeraldCrashWebhookUrl = BlankAsNull(file.HeraldCrashWebhookUrl),
                HeraldCrashThreadId = BlankAsNull(file.HeraldCrashThreadId),
                HeraldCrashRoleId = BlankAsNull(file.HeraldCrashRoleId),
                HeraldPlayersOn = file.HeraldPlayersOn ?? defaults.HeraldPlayersOn,
                HeraldPlayersWebhookUrl = BlankAsNull(file.HeraldPlayersWebhookUrl),
                HeraldPlayersThreadId = BlankAsNull(file.HeraldPlayersThreadId),
                HeraldWebhookName = BlankAsNull(file.HeraldWebhookName),
                HeraldBotName = BlankAsNull(file.HeraldBotName),
                HeraldAvatarUrl = BlankAsNull(file.HeraldAvatarUrl),
                HeraldRoutineNoThread = file.HeraldRoutineNoThread ?? false,
                HeraldCrashNoThread = file.HeraldCrashNoThread ?? false,
                HeraldPlayersNoThread = file.HeraldPlayersNoThread ?? false,
                RconEnabled = file.RconEnabled ?? defaults.RconEnabled,
                RconPort = file.RconPort ?? defaults.RconPort,
                RconPassword = file.RconPassword ?? defaults.RconPassword,
                Archived = file.Archived ?? defaults.Archived,
                IsolatedInstall = file.IsolatedInstall ?? defaults.IsolatedInstall,
                ModManifest = file.ModManifest ?? defaults.ModManifest,
                TestCopyOf = string.IsNullOrWhiteSpace(file.TestCopyOf) ? null : file.TestCopyOf.Trim(),
                TestSourceGone = (file.TestSourceGone ?? false) && !string.IsNullOrWhiteSpace(file.TestCopyOf),
                TestCopyOfName = string.IsNullOrWhiteSpace(file.TestCopyOf) ? null : BlankAsNull(file.TestCopyOfName),
                LookRune = NormalizeLook(file.LookRune),
                LookColor = NormalizeLook(file.LookColor),
                GaleProfileId = StoredGaleProfileId(file.GaleProfileId),
                GaleLastUpdatedAt = string.IsNullOrWhiteSpace(file.GaleLastUpdatedAt) ? defaults.GaleLastUpdatedAt : file.GaleLastUpdatedAt,
                GaleUnticked = NormalizeUnticked(file.GaleUnticked),
                GaleNotAvailable = NormalizeUnticked(file.GaleNotAvailable),
                GaleConfigUnticked = NormalizeUnticked(file.GaleConfigUnticked),
                GaleTicked = NormalizeTicked(file.GaleTicked),
                GaleConfigTicked = NormalizeTicked(file.GaleConfigTicked),
                FollowTestCopy = BlankAsNull(file.FollowTestCopy),
                FollowTestCopyLastApproved = BlankAsNull(file.FollowTestCopyLastApproved),
                FollowTestCopyUnticked = NormalizeUnticked(file.FollowTestCopyUnticked),
                FollowTestCopyConfigUnticked = NormalizeUnticked(file.FollowTestCopyConfigUnticked),
                FollowTestCopyTicked = NormalizeTicked(file.FollowTestCopyTicked),
                FollowTestCopyConfigTicked = NormalizeTicked(file.FollowTestCopyConfigTicked),
            };
        }

        public ServerPreferencesFile ToFile() => new()
        {
            ProfileName = ProfileName,
            LastSaved = LastSaved,
            Name = Name,
            Password = Password,
            WorldName = WorldName,
            Community = Public,
            Port = Port,
            Crossplay = Crossplay,
            SaveInterval = SaveInterval,
            BackupCount = BackupCount,
            BackupIntervalShort = BackupIntervalShort,
            BackupIntervalLong = BackupIntervalLong,
            AutoStart = AutoStart,
            AdditionalArgs = AdditionalArgs,
            ServerExePath = ServerExePath,
            SaveDataFolderPath = SaveDataFolderPath,
            WriteServerLogsToFile = WriteServerLogsToFile,
            LastLaunchedServerBuild = LastLaunchedServerBuild,
            LastLaunchedServerFingerprint = LastLaunchedServerFingerprint,
            LastLaunchedGameVersion = LastLaunchedGameVersion,
            AutoRestart = AutoRestart,
            AutoRestartDelay = AutoRestartDelay,
            EmptyServerRestart = EmptyServerRestart,
            EmptyServerRestartDelayMinutes = EmptyServerRestartDelayMinutes,
            ScheduledRestart = ScheduledRestart,
            ScheduledRestartHours = ScheduledRestartHours,
            CleanseWhenEmpty = CleanseWhenEmpty,
            ModProfileCodeKind = ModProfileCodeKind,
            ModProfileCode = ModProfileCode,
            HeraldOn = HeraldOn,
            HeraldWebhookUrl = BlankAsNull(HeraldWebhookUrl),
            HeraldThreadId = BlankAsNull(HeraldThreadId),
            HeraldShareAddress = HeraldShareAddress,
            HeraldSharePassword = HeraldSharePassword,
            HeraldTitle = BlankAsNull(HeraldTitle),
            HeraldStatusLine = BlankAsNull(HeraldStatusLine),
            HeraldStatusMessageId = BlankAsNull(HeraldStatusMessageId),
            // A signature means nothing without the post it belongs to.
            HeraldStatusMessageSignature = BlankAsNull(HeraldStatusMessageId) == null ? null : HeraldStatusMessageSignature,
            HeraldStatusThreadId = BlankAsNull(HeraldStatusThreadId),
            HeraldRoutineOn = HeraldRoutineOn,
            HeraldRoutineWebhookUrl = BlankAsNull(HeraldRoutineWebhookUrl),
            HeraldRoutineThreadId = BlankAsNull(HeraldRoutineThreadId),
            HeraldCrashOn = HeraldCrashOn,
            HeraldCrashWebhookUrl = BlankAsNull(HeraldCrashWebhookUrl),
            HeraldCrashThreadId = BlankAsNull(HeraldCrashThreadId),
            HeraldCrashRoleId = BlankAsNull(HeraldCrashRoleId),
            HeraldPlayersOn = HeraldPlayersOn,
            HeraldPlayersWebhookUrl = BlankAsNull(HeraldPlayersWebhookUrl),
            HeraldPlayersThreadId = BlankAsNull(HeraldPlayersThreadId),
            HeraldWebhookName = BlankAsNull(HeraldWebhookName),
            HeraldBotName = BlankAsNull(HeraldBotName),
            HeraldAvatarUrl = BlankAsNull(HeraldAvatarUrl),
            // Written only when set, so a realm that was never refused keeps its entry as short as before.
            HeraldRoutineNoThread = HeraldRoutineNoThread ? true : null,
            HeraldCrashNoThread = HeraldCrashNoThread ? true : null,
            HeraldPlayersNoThread = HeraldPlayersNoThread ? true : null,
            RconEnabled = RconEnabled,
            RconPort = RconPort,
            RconPassword = RconPassword,
            Archived = Archived,
            IsolatedInstall = IsolatedInstall,
            ModManifest = ModManifest,
            TestCopyOf = string.IsNullOrWhiteSpace(TestCopyOf) ? null : TestCopyOf.Trim(),
            // Written only when it is true, so a realm that is not a test copy, or one whose
            // source is still here, carries no key for it at all.
            TestSourceGone = TestSourceGone && !string.IsNullOrWhiteSpace(TestCopyOf) ? true : (bool?)null,
            TestCopyOfName = string.IsNullOrWhiteSpace(TestCopyOf) ? null : BlankAsNull(TestCopyOfName),
            LookRune = NormalizeLook(LookRune),
            LookColor = NormalizeLook(LookColor),
            GaleProfileId = StoredGaleProfileId(GaleProfileId),
            GaleLastUpdatedAt = string.IsNullOrWhiteSpace(GaleLastUpdatedAt) ? null : GaleLastUpdatedAt,
            // An empty list is written as null, the same as the other two fields of a realm
            // that has never followed a profile, and null reads back as an empty list.
            GaleUnticked = NormalizeUnticked(GaleUnticked) is { Count: > 0 } kept ? kept : null,
            GaleNotAvailable = NormalizeUnticked(GaleNotAvailable) is { Count: > 0 } gone ? gone : null,
            GaleConfigUnticked = NormalizeUnticked(GaleConfigUnticked) is { Count: > 0 } left ? left : null,
            // Unlike the untick lists, an empty ticked list is written as one: it says a choice
            // was stored under the 1.3.0 rule, where null says none has been yet.
            GaleTicked = NormalizeTicked(GaleTicked),
            GaleConfigTicked = NormalizeTicked(GaleConfigTicked),
            FollowTestCopy = BlankAsNull(FollowTestCopy),
            FollowTestCopyLastApproved = BlankAsNull(FollowTestCopyLastApproved),
            FollowTestCopyUnticked = NormalizeUnticked(FollowTestCopyUnticked) is { Count: > 0 } copyOff ? copyOff : null,
            FollowTestCopyConfigUnticked = NormalizeUnticked(FollowTestCopyConfigUnticked) is { Count: > 0 } copyCfgOff ? copyCfgOff : null,
            // Written as the Gale ticked lists are: an empty one is a stored choice.
            FollowTestCopyTicked = NormalizeTicked(FollowTestCopyTicked),
            FollowTestCopyConfigTicked = NormalizeTicked(FollowTestCopyConfigTicked),
        };
    }
}
