using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The Herald's look: the name and picture a post is signed with, the title it is headed
    /// with, a line the host writes at the top, and a small BakaLoader line that links to the
    /// project page. The status post and the event posts both read it from here, so the two
    /// cannot sign themselves differently. The name, the picture and the line are app wide; the
    /// title and the status line are each realm's own, like the post they head.
    /// <para>
    /// Every one of these is blank by default, and blank is never "nothing": a blank name is
    /// the webhook's own name, a blank picture is BakaLoader's logo, a blank title is the server
    /// name as it always was, and a blank status line is no line. The link is on by default.
    /// </para>
    /// <para>
    /// A picture is a link Discord fetches itself, so the one check worth doing here is that
    /// it could be a picture at all: https, and either an image file ending or one of the hosts
    /// that only serve images. A link that fails is refused at the save with one sentence, and
    /// one that got into the file some other way is never sent; the logo goes in its place.
    /// Nothing here asks the link whether it answers.
    /// </para>
    /// </summary>
    public static class DiscordHeraldLook
    {
        /// <summary>The project page the BakaLoader line links to.</summary>
        public const string ProjectUrl = "https://github.com/RyanDMcAfee/ValheimBakaLoader";

        /// <summary>
        /// BakaLoader's logo as a 256 pixel square, served from the repository. The logo in
        /// Resources is 1024 pixels and two megabytes, which is more than a picture beside a
        /// name needs and more than Discord likes to fetch, so the post uses this copy.
        /// </summary>
        public const string LogoUrl = "https://raw.githubusercontent.com/RyanDMcAfee/ValheimBakaLoader/main/img/herald-avatar.png";

        /// <summary>The catalog id of the one sentence a refused picture link gets.</summary>
        public const string BadAvatarSentence = "herald.look.avatar.refused";

        /// <summary>The catalog id of the one sentence a refused display name gets.</summary>
        public const string BadNameSentence = "herald.look.name.refused";

        /// <summary>Discord's own limit on the name a webhook post is signed with.</summary>
        public const int NameMax = 80;

        /// <summary>Discord's own limit on an embed title.</summary>
        public const int TitleMax = 256;

        /// <summary>
        /// How long the status line may be. Discord allows far more in a description, but this
        /// is one line at the top of a post, not a letter.
        /// </summary>
        public const int StatusLineMax = 300;

        /// <summary>The hosts that serve only images, so a link there needs no file ending.</summary>
        public static readonly string[] ImageHosts = { "raw.githubusercontent.com", "cdn.discordapp.com", "i.imgur.com" };

        /// <summary>The file endings Discord shows as a picture.</summary>
        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

        /// <summary>
        /// Whether a link could be a picture Discord will show: https, no user name or password
        /// in it, and an image ending on the path or one of the image hosts.
        /// </summary>
        public static bool IsImageUrl(string url)
        {
            var u = url?.Trim();
            if (string.IsNullOrEmpty(u) || u.Length > 2048) return false;
            if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed)) return false;
            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;
            if (string.IsNullOrEmpty(parsed.Host)) return false;

            if (ImageHosts.Any(h => string.Equals(parsed.Host, h, StringComparison.OrdinalIgnoreCase))) return true;
            var path = parsed.AbsolutePath;
            return ImageExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The catalog id of the reason a picture link cannot be saved, or null when it can.
        /// Blank can always be saved: blank is the logo.
        /// </summary>
        public static string AvatarProblem(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            return IsImageUrl(url) ? null : BadAvatarSentence;
        }

        /// <summary>
        /// The catalog id of the reason a display name cannot be saved, or null when it can.
        /// Discord refuses a whole post signed with a name longer than eighty characters or one
        /// that contains "discord" or "clyde", so a name like that would take every post down
        /// with it. Blank can always be saved: blank is the webhook's own name.
        /// </summary>
        public static string NameProblem(string name)
        {
            var n = name?.Trim();
            if (string.IsNullOrEmpty(n)) return null;
            if (n.Length > NameMax) return BadNameSentence;
            if (n.IndexOf("discord", StringComparison.OrdinalIgnoreCase) >= 0) return BadNameSentence;
            if (n.IndexOf("clyde", StringComparison.OrdinalIgnoreCase) >= 0) return BadNameSentence;
            return null;
        }

        /// <summary>The name to sign with, or null for the webhook's own name.</summary>
        public static string NameFor(UserPreferences prefs)
        {
            var n = prefs?.DiscordBotName?.Trim();
            if (string.IsNullOrEmpty(n)) return null;
            return NameProblem(n) == null ? n : null;
        }

        /// <summary>
        /// The picture to sign with. Blank is the logo, and so is a link that is not a picture:
        /// one that slipped past the save (a hand edit of the file) is never sent.
        /// </summary>
        public static string AvatarFor(UserPreferences prefs)
        {
            var a = prefs?.DiscordAvatarUrl?.Trim();
            if (string.IsNullOrEmpty(a)) return LogoUrl;
            return IsImageUrl(a) ? a : LogoUrl;
        }

        /// <summary>
        /// The status post's title: the realm's own when set, otherwise the server name. A title
        /// belongs to the realm, since each realm keeps a post of its own (1.2.16).
        /// </summary>
        public static string TitleFor(ServerPreferences realm, string serverTitle)
        {
            var t = realm?.HeraldTitle?.Trim();
            return Clip(string.IsNullOrEmpty(t) ? serverTitle : t, TitleMax);
        }

        /// <summary>The line at the top of the realm's status post, or null for none.</summary>
        public static string StatusLineFor(ServerPreferences realm)
        {
            var s = realm?.HeraldStatusLine?.Trim();
            return string.IsNullOrEmpty(s) ? null : Clip(s, StatusLineMax);
        }

        /// <summary>
        /// What the status post is signed with, as one string. Discord fixes a webhook post's
        /// name and picture when the post is made, and an edit cannot change them, so the
        /// status service keeps this beside the post's id and makes the post again when it moves.
        /// </summary>
        public static string SignatureFor(UserPreferences prefs) => (NameFor(prefs) ?? "") + "\n" + AvatarFor(prefs);

        /// <summary>
        /// Signs a post: the name when one is set, the picture always, and the BakaLoader line
        /// on the embed when the switch is on. The title and the status line are the status
        /// post's own business and are set by it.
        /// </summary>
        public static void Sign(JObject payload, JObject embed, UserPreferences prefs)
        {
            var name = NameFor(prefs);
            if (name != null) payload["username"] = name;
            payload["avatar_url"] = AvatarFor(prefs);

            if (prefs == null || prefs.DiscordLinkToBakaLoader)
            {
                embed["author"] = new JObject
                {
                    ["name"] = HostCatalog.T("host.discord.author"),
                    ["url"] = ProjectUrl,
                    ["icon_url"] = LogoUrl,
                };
            }
        }

        // Cut at the limit, and one short of it when the cut would split a character that
        // takes two UTF-16 units, which Discord would answer with a refused post.
        private static string Clip(string s, int max)
        {
            if (s == null || s.Length <= max) return s;
            var at = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
            return s.Substring(0, at);
        }
    }
}
