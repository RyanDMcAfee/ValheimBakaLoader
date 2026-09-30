using System;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// The three sizes the interface can be read at, and the one number each of them means.
    /// <para>
    /// The whole window is one web page, so the honest way to make its text bigger is to zoom
    /// the page rather than to keep a second set of font sizes beside the first. A zoom moves
    /// everything together: the text, the boxes it sits in, the gaps between them, the rune
    /// captions and the tooltips. Two sets of sizes would drift the first time a rule was
    /// added to one and not the other.
    /// </para>
    /// <para>
    /// AND IT REACHES THE MAP TOO, which is worth saying because the first cut of this feature
    /// was built on the opposite. A WebView2 ZoomFactor is a Chromium page zoom: it shrinks the
    /// CSS viewport and raises <c>devicePixelRatio</c> by the same factor. The Atlas sizes its
    /// backing store as its wrap's CSS size times that ratio and draws through
    /// <c>setTransform(dpr, ...)</c>, so its drawing space is CSS pixels exactly like the
    /// stylesheet's: an unchanged 11px canvas font grows on screen by the zoom the same way an
    /// 11px rule does. Nothing on the page multiplies a canvas font, and a version that did
    /// drew the place names about half again taller than the words beside them at Extra large.
    /// Measured in Chromium under its own page zoom: one literal 11px monospace drew ink 43x10
    /// device pixels at 1.00, 51x12 at 1.20 and 61x14 at 1.45, with no scaler anywhere.
    /// </para>
    /// <para>
    /// AND THE WINDOW. Zooming the page shrinks the viewport the halls are laid out in: at 1.45
    /// a 1024 pixel wide window hands the page 706 CSS pixels, which is narrower than anything
    /// the layout is written for. So the window's own minimum grows by the same factor, and the
    /// existing work-area clamp is what keeps that honest on a display too small to hold it.
    /// </para>
    /// </summary>
    public static class TextSizes
    {
        /// <summary>The size the window has always been read at, and the default.</summary>
        public const string Normal = "normal";

        /// <summary>A fifth bigger.</summary>
        public const string Large = "large";

        /// <summary>Getting on for half again.</summary>
        public const string ExtraLarge = "xlarge";

        /// <summary>Every spelling this app answers to, in the order the picker offers them.</summary>
        public static readonly string[] All = { Normal, Large, ExtraLarge };

        /// <summary>
        /// The zoom one spelling means. A spelling this app does not know reads as
        /// <see cref="Normal"/>, because a window nobody can read is a worse answer than a
        /// window at the size it has always been.
        /// </summary>
        public static double Factor(string size) => Normalize(size) switch
        {
            Large => 1.2,
            ExtraLarge => 1.45,
            _ => 1.0,
        };

        /// <summary>
        /// The stored spelling for whatever came in, or <see cref="Normal"/> when it is not one
        /// of the three. Case and surrounding space are forgiven; anything else is not.
        /// </summary>
        public static string Normalize(string size)
        {
            var asked = (size ?? "").Trim();
            foreach (var known in All)
                if (string.Equals(asked, known, StringComparison.OrdinalIgnoreCase)) return known;

            return Normal;
        }
    }
}
