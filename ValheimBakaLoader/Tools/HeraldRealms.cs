using System;
using System.Collections.Generic;
using System.Linq;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools
{
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

        /// <summary>The webhook a realm posts through: its own, or the app-wide one when it has none.</summary>
        public static string WebhookUrlFor(UserPreferences app, ServerPreferences realm)
            => ServerPreferences.BlankAsNull(realm?.HeraldWebhookUrl)
               ?? ServerPreferences.BlankAsNull(app?.DiscordWebhookUrl);

        /// <summary>
        /// The thread a realm posts into. Its own when it has one. Otherwise the app-wide thread,
        /// but only while it also uses the app-wide webhook: a thread lives in one channel, and a
        /// webhook of the realm's own posts somewhere else.
        /// </summary>
        public static string ThreadIdFor(UserPreferences app, ServerPreferences realm)
        {
            var own = ServerPreferences.BlankAsNull(realm?.HeraldThreadId);
            if (own != null) return own;
            return ServerPreferences.BlankAsNull(realm?.HeraldWebhookUrl) == null
                ? ServerPreferences.BlankAsNull(app?.DiscordWebhookThreadId)
                : null;
        }

        /// <summary>Whether a realm's post is kept up to date: the master on, the realm on, and somewhere to post.</summary>
        public static bool Shares(UserPreferences app, ServerPreferences realm)
            => app != null && app.DiscordSharingEnabled
               && realm != null && realm.HeraldOn
               && WebhookUrlFor(app, realm) != null;

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

            prefs.HeraldPerRealmMigrated = true;
            return true;
        }
    }
}
