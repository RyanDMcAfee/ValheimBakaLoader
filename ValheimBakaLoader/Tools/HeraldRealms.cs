using System;
using System.Collections.Generic;
using System.Linq;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The three kinds of event post a realm sends, each with its own switch and its own place.
    /// </summary>
    public enum HeraldEventClass
    {
        /// <summary>Started, stopped, updated, update failed, held, old world, cheat marks cleared, mods updated.</summary>
        Routine,
        /// <summary>The server crashed: the call to action, with an optional role ping.</summary>
        Crash,
        /// <summary>A player joined or left.</summary>
        Players,
    }

    /// <summary>
    /// The Herald per realm (1.2.16, issue 24): each realm keeps a status post of its own, with
    /// its own switch, webhook and thread, and its own say in what the post carries. The rules
    /// that decide where a realm posts and whether it posts at all live here, so the status
    /// service, the bridge and the tests all read the same ones.
    /// <para>
    /// The app-wide Discord switch stays the master: off, no realm sends anything. The name, the
    /// picture and the BakaLoader line stay app wide too, because they are how the host signs.
    /// </para>
    /// </summary>
    public static class HeraldRealms
    {
        /// <summary>
        /// The realm by its profile name: the exact spelling first, and the same name in any
        /// case after that, since two realms can never share a name that way.
        /// </summary>
        public static ServerPreferences Find(UserPreferences prefs, string realm)
        {
            if (prefs?.Servers == null || string.IsNullOrWhiteSpace(realm)) return null;
            return prefs.Servers.Where(s => s != null && string.Equals(s.ProfileName, realm, StringComparison.Ordinal))
                       .OrderByDescending(s => s.LastSaved).FirstOrDefault()
                ?? prefs.Servers.Where(s => s != null && string.Equals(s.ProfileName, realm, StringComparison.OrdinalIgnoreCase))
                       .OrderByDescending(s => s.LastSaved).FirstOrDefault();
        }

        /// <summary>
        /// Whether every realm stands on its own (1.2.17): its own webhook, name and picture, and
        /// no master switch over it. Until the move has copied the app-wide values into the
        /// realms, the old reading holds, so nothing stops posting in the moment between loading
        /// an old file and the move.
        /// </summary>
        public static bool StandsAlone(UserPreferences app) => app != null && app.HeraldSimplifiedMigrated;

        /// <summary>The master switch as it is read now: retired, so on, once the realms stand alone.</summary>
        public static bool MasterOn(UserPreferences app) => app != null && (app.HeraldSimplifiedMigrated || app.DiscordSharingEnabled);

        /// <summary>
        /// The webhook a realm posts through: its own. Before the 1.2.17 move a blank one meant
        /// the app-wide webhook; the move copied that link into the realm, so blank is now none.
        /// </summary>
        public static string WebhookUrlFor(UserPreferences app, ServerPreferences realm)
            => ServerPreferences.BlankAsNull(realm?.HeraldWebhookUrl)
               ?? (StandsAlone(app) ? null : ServerPreferences.BlankAsNull(app?.DiscordWebhookUrl));

        /// <summary>
        /// The thread a realm posts into. Its own when it has one. Otherwise the app-wide thread,
        /// but only while it also uses the app-wide webhook: a thread lives in one channel, and a
        /// webhook of the realm's own posts somewhere else.
        /// </summary>
        public static string ThreadIdFor(UserPreferences app, ServerPreferences realm)
        {
            var own = ServerPreferences.BlankAsNull(realm?.HeraldThreadId);
            if (own != null) return own;
            if (StandsAlone(app)) return null;
            return ServerPreferences.BlankAsNull(realm?.HeraldWebhookUrl) == null
                ? ServerPreferences.BlankAsNull(app?.DiscordWebhookThreadId)
                : null;
        }

        /// <summary>Whether a realm's post is kept up to date: the master on, the realm on, and somewhere to post.</summary>
        public static bool Shares(UserPreferences app, ServerPreferences realm)
            => MasterOn(app)
               && realm != null && realm.HeraldOn
               && WebhookUrlFor(app, realm) != null;

        /// <summary>The switch of one kind of event post on a realm.</summary>
        public static bool ClassOn(ServerPreferences realm, HeraldEventClass kind) => realm != null && kind switch
        {
            HeraldEventClass.Routine => realm.HeraldRoutineOn,
            HeraldEventClass.Crash => realm.HeraldCrashOn,
            HeraldEventClass.Players => realm.HeraldPlayersOn,
            _ => false,
        };

        /// <summary>Whether Discord refused the last post of a kind for naming no thread in a forum.</summary>
        public static bool NoThread(ServerPreferences realm, HeraldEventClass kind) => realm != null && kind switch
        {
            HeraldEventClass.Routine => realm.HeraldRoutineNoThread,
            HeraldEventClass.Crash => realm.HeraldCrashNoThread,
            HeraldEventClass.Players => realm.HeraldPlayersNoThread,
            _ => false,
        };

        public static void SetNoThread(ServerPreferences realm, HeraldEventClass kind, bool refused)
        {
            if (realm == null) return;
            switch (kind)
            {
                case HeraldEventClass.Routine: realm.HeraldRoutineNoThread = refused; break;
                case HeraldEventClass.Crash: realm.HeraldCrashNoThread = refused; break;
                case HeraldEventClass.Players: realm.HeraldPlayersNoThread = refused; break;
            }
        }

        private static (string Url, string Thread) ClassBoxes(ServerPreferences realm, HeraldEventClass kind) => kind switch
        {
            HeraldEventClass.Routine => (realm?.HeraldRoutineWebhookUrl, realm?.HeraldRoutineThreadId),
            HeraldEventClass.Crash => (realm?.HeraldCrashWebhookUrl, realm?.HeraldCrashThreadId),
            HeraldEventClass.Players => (realm?.HeraldPlayersWebhookUrl, realm?.HeraldPlayersThreadId),
            _ => (null, null),
        };

        /// <summary>
        /// Where one kind of event post from a realm goes: the kind's own webhook, or the realm's.
        /// The thread follows the same rule a realm's thread does: the kind's own when it has one,
        /// the realm's while the kind posts through the realm's webhook, and the channel itself
        /// when the kind has a webhook of its own, because a thread lives in one channel.
        /// <para>
        /// Routine news with no thread anywhere goes into the forum post the realm's status post
        /// opened, when it opened one: the forum post is the realm, its first message the live
        /// status and its replies the history. A forum channel refuses a post with no thread, so
        /// without this the news would go nowhere.
        /// </para>
        /// </summary>
        public static (string Url, string Thread) EventRouteFor(UserPreferences app, ServerPreferences realm, HeraldEventClass kind)
        {
            var (url, thread) = ClassBoxes(realm, kind);
            var ownUrl = ServerPreferences.BlankAsNull(url);
            var ownThread = ServerPreferences.BlankAsNull(thread);
            if (ownUrl != null) return (ownUrl, ownThread);
            var realmThread = ownThread ?? ThreadIdFor(app, realm);
            if (realmThread == null && kind == HeraldEventClass.Routine)
                realmThread = ServerPreferences.BlankAsNull(realm?.HeraldStatusThreadId);
            return (WebhookUrlFor(app, realm), realmThread);
        }

        /// <summary>
        /// The name of the forum post a realm's status post opens: the realm's display name, and
        /// its profile name when it has none, cut to the hundred characters Discord takes.
        /// </summary>
        public static string ForumPostNameFor(ServerPreferences realm)
        {
            var name = ServerPreferences.BlankAsNull(realm?.Name)?.Trim()
                ?? ServerPreferences.BlankAsNull(realm?.ProfileName)?.Trim()
                ?? HostCatalog.T("host.discord.forum.fallback_name");
            return name.Length > 100 ? name.Substring(0, 100) : name;
        }

        /// <summary>A Discord id: digits only, as long as Discord makes them.</summary>
        public static bool IsSnowflake(string id)
            => id != null && id.Length >= 5 && id.Length <= 25 && id.All(c => c >= '0' && c <= '9');

        /// <summary>
        /// Whether a realm sends one kind of event post: the master on, the realm on, the kind on,
        /// and somewhere to send it. One rule for every event, read when the post is sent.
        /// </summary>
        public static bool SendsEvent(UserPreferences app, ServerPreferences realm, HeraldEventClass kind)
            => MasterOn(app)
               && realm != null && realm.HeraldOn
               && ClassOn(realm, kind)
               && EventRouteFor(app, realm, kind).Url != null;

        /// <summary>
        /// A role id as Discord prints it, or nothing: digits only, and the mention form a host
        /// may paste (&lt;@&amp;123&gt;) read as its digits. Anything else is not a role id.
        /// </summary>
        public static bool TryReadRoleId(string text, out string roleId)
        {
            roleId = null;
            var t = text?.Trim() ?? "";
            if (t.StartsWith("<@&", StringComparison.Ordinal) && t.EndsWith(">", StringComparison.Ordinal))
                t = t.Substring(3, t.Length - 4).Trim();
            if (t.Length == 0) return true;
            if (t.Length < 5 || t.Length > 25 || !t.All(c => c >= '0' && c <= '9')) return false;
            roleId = t;
            return true;
        }

        /// <summary>
        /// The realm the one post of the builds before this one described, by the rule they used:
        /// a realm whose server is up, the realm on screen winning a tie, and the realm on screen
        /// when none is up.
        /// </summary>
        public static string PostRealm(IEnumerable<string> liveRealms, string active)
        {
            var live = (liveRealms ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            if (live.Count == 0) return active;
            return live.Any(r => string.Equals(r, active, StringComparison.OrdinalIgnoreCase)) ? active : live[0];
        }

        /// <summary>
        /// Hands the one status post to the realm it described, once. That realm takes the post's
        /// id and signature, the webhook and thread it was made through, the share switches, the
        /// title and the status line, and is switched on. Every other realm starts switched off,
        /// with the app-wide webhook, so the same single post keeps editing after the update and
        /// no realm posts anything new by surprise. Answers whether anything changed.
        /// <para>
        /// With no realm at all there is nothing to hand a post to: when there is a post it waits
        /// for a realm, and when there is none the move is simply done.
        /// </para>
        /// </summary>
        public static bool Migrate(UserPreferences prefs, string describedRealm)
        {
            if (prefs == null || prefs.HeraldPerRealmMigrated) return false;

            var realms = (prefs.Servers ?? new List<ServerPreferences>()).Where(s => s != null).ToList();
            if (realms.Count == 0)
            {
                if (ServerPreferences.BlankAsNull(prefs.DiscordStatusMessageId) != null) return false;
                prefs.HeraldPerRealmMigrated = true;
                return true;
            }

            var owner = Find(prefs, describedRealm)
                ?? realms.FirstOrDefault(r => !r.Archived)
                ?? realms[0];

            foreach (var realm in realms)
            {
                if (ReferenceEquals(realm, owner)) continue;
                realm.HeraldOn = false;
                realm.HeraldWebhookUrl = null;
                realm.ForgetHeraldPost();
            }

            owner.HeraldOn = true;
            owner.HeraldWebhookUrl = ServerPreferences.BlankAsNull(prefs.DiscordWebhookUrl);
            owner.HeraldThreadId = ServerPreferences.BlankAsNull(prefs.DiscordWebhookThreadId);
            owner.HeraldShareAddress = prefs.DiscordShareAddress;
            owner.HeraldSharePassword = prefs.DiscordSharePassword;
            owner.HeraldTitle = ServerPreferences.BlankAsNull(prefs.DiscordTitle);
            owner.HeraldStatusLine = ServerPreferences.BlankAsNull(prefs.DiscordStatusLine);
            owner.HeraldStatusMessageId = ServerPreferences.BlankAsNull(prefs.DiscordStatusMessageId);
            owner.HeraldStatusMessageSignature = owner.HeraldStatusMessageId == null ? null : prefs.DiscordStatusMessageSignature;

            // The one "Event posts" switch becomes all three kinds on that realm, through the
            // realm's own webhook and thread, which are the ones the events went through before.
            owner.HeraldRoutineOn = prefs.DiscordEventPosts;
            owner.HeraldCrashOn = prefs.DiscordEventPosts;
            owner.HeraldPlayersOn = prefs.DiscordEventPosts;

            prefs.HeraldPerRealmMigrated = true;
            return true;
        }

        /// <summary>
        /// Lets every realm stand on its own, once (1.2.17). Runs after <see cref="Migrate"/>,
        /// never before it, because that move decides which realm the old post belongs to.
        /// <list type="bullet">
        /// <item>The master switch goes from the screen. A host who had it off gets every realm
        /// switched off instead, so nothing starts posting by surprise; then it is set on and is
        /// read as on from here on (the file keeps it for the builds before this one).</item>
        /// <item>A realm that was sharing with no webhook of its own posted through the app-wide one, so that link
        /// is copied into it, with the app-wide thread when it has none of its own: the same
        /// channel and thread as before, now its own.</item>
        /// <item>The name and the picture become each realm's: the app-wide values are copied
        /// into every realm that has none, so every post stays signed as it was.</item>
        /// </list>
        /// The app-wide fields are left as they were written, unused. Answers whether anything changed.
        /// </summary>
        public static bool Simplify(UserPreferences prefs)
        {
            if (prefs == null || prefs.HeraldSimplifiedMigrated || !prefs.HeraldPerRealmMigrated) return false;

            var masterOn = prefs.DiscordSharingEnabled;
            var appUrl = ServerPreferences.BlankAsNull(prefs.DiscordWebhookUrl);
            var appThread = ServerPreferences.BlankAsNull(prefs.DiscordWebhookThreadId);
            var appName = ServerPreferences.BlankAsNull(prefs.DiscordBotName);
            var appAvatar = ServerPreferences.BlankAsNull(prefs.DiscordAvatarUrl);

            foreach (var realm in (prefs.Servers ?? new List<ServerPreferences>()).Where(s => s != null))
            {
                // Only a realm that was sharing ever posted through the app-wide link. A realm that
                // was off keeps no webhook, so the screen offers Set up rather than naming a webhook
                // it never used.
                var wasSharing = realm.HeraldOn;
                if (!masterOn) realm.HeraldOn = false;
                if (wasSharing && ServerPreferences.BlankAsNull(realm.HeraldWebhookUrl) == null && appUrl != null)
                {
                    realm.HeraldWebhookUrl = appUrl;
                    realm.HeraldThreadId = ServerPreferences.BlankAsNull(realm.HeraldThreadId) ?? appThread;
                }
                realm.HeraldBotName = ServerPreferences.BlankAsNull(realm.HeraldBotName) ?? appName;
                realm.HeraldAvatarUrl = ServerPreferences.BlankAsNull(realm.HeraldAvatarUrl) ?? appAvatar;
            }

            prefs.DiscordSharingEnabled = true;
            prefs.HeraldSimplifiedMigrated = true;
            return true;
        }
    }
}
