using System.Drawing;
using System.Resources;

namespace ValheimBakaLoader.Properties
{
    /// <summary>
    /// The window icon, the one thing Resources.resx still holds. It stayed with the window
    /// when the rest of Properties\Resources.cs went into Core: an Icon is a GDI handle, the
    /// resx is embedded in this assembly rather than in Core's, and nothing without a window
    /// has a use for either.
    /// </summary>
    internal static class AppIcon
    {
        private static readonly ResourceManager Embedded =
            new("ValheimBakaLoader.Properties.Resources", typeof(AppIcon).Assembly);

        internal static Icon ApplicationIcon => (Icon)Embedded.GetObject("ApplicationIcon");
    }
}
