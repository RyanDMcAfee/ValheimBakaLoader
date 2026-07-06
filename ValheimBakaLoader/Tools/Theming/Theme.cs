using System.Drawing;

namespace ValheimBakaLoader.Tools.Theming
{
    /// <summary>
    /// Defines a complete color palette for a single UI theme. All foreground/background
    /// pairings in here are chosen to meet or exceed WCAG AA contrast (4.5:1), and most of
    /// the body text pairings meet AAA (7:1+), so every piece of text stays clearly legible.
    /// </summary>
    public class ThemePalette
    {
        // Surfaces
        public Color WindowBackground { get; init; }   // top-level form background
        public Color ControlBackground { get; init; }  // panels, group boxes, tab pages
        public Color InputBackground { get; init; }     // text boxes, combos, lists
        public Color InputBackgroundDisabled { get; init; }

        // Borders / separators
        public Color Border { get; init; }
        public Color BorderStrong { get; init; }

        // Text
        public Color PrimaryText { get; init; }
        public Color SecondaryText { get; init; }
        public Color DisabledText { get; init; }
        public Color LinkText { get; init; }
        public Color ErrorText { get; init; }
        public Color SuccessText { get; init; }

        // Buttons
        public Color ButtonBackground { get; init; }
        public Color ButtonHover { get; init; }
        public Color ButtonActive { get; init; }
        public Color ButtonText { get; init; }
        public Color ButtonBorder { get; init; }

        // Accent / selection
        public Color Accent { get; init; }
        public Color SelectionBackground { get; init; }
        public Color SelectionText { get; init; }

        // Logs / monospace surfaces
        public Color LogBackground { get; init; }
        public Color LogText { get; init; }
    }

    /// <summary>
    /// Central theme registry. <see cref="Current"/> is the active palette and
    /// <see cref="IsDarkMode"/> toggles between the dark and light palettes.
    /// Dark is the default because the app ships dark-first.
    /// </summary>
    public static class Theme
    {
        private static bool _isDarkMode = true;

        public static bool IsDarkMode
        {
            get => _isDarkMode;
            set
            {
                _isDarkMode = value;
                Current = value ? Dark : Light;
            }
        }

        public static ThemePalette Current { get; private set; } = Dark;

        /// <summary>
        /// A modern, VS Code-inspired dark palette. Every text color below is paired
        /// against the surface it sits on to keep contrast high:
        ///   PrimaryText  #F0F0F0 on #1E1E1E  ~= 14:1  (AAA)
        ///   SecondaryText #C8C8C8 on #252526 ~=  9:1  (AAA)
        ///   LogText      #D4D4D4 on #1A1A1A  ~= 11:1  (AAA)
        ///   ButtonText   #F0F0F0 on #3A3D41  ~=  8:1  (AAA)
        ///   LinkText     #4FC1FF on #1E1E1E  ~=  7:1  (AAA)
        /// </summary>
        public static readonly ThemePalette Dark = new()
        {
            WindowBackground = Color.FromArgb(30, 30, 30),       // #1E1E1E
            ControlBackground = Color.FromArgb(37, 37, 38),      // #252526
            InputBackground = Color.FromArgb(60, 60, 60),        // #3C3C3C
            InputBackgroundDisabled = Color.FromArgb(42, 42, 42),// #2A2A2A

            Border = Color.FromArgb(63, 63, 70),                 // #3F3F46
            BorderStrong = Color.FromArgb(85, 85, 85),           // #555555

            PrimaryText = Color.FromArgb(240, 240, 240),         // #F0F0F0
            SecondaryText = Color.FromArgb(200, 200, 200),       // #C8C8C8
            DisabledText = Color.FromArgb(140, 140, 140),        // #8C8C8C
            LinkText = Color.FromArgb(79, 193, 255),             // #4FC1FF
            ErrorText = Color.FromArgb(244, 135, 113),           // #F48771
            SuccessText = Color.FromArgb(137, 209, 133),         // #89D185

            ButtonBackground = Color.FromArgb(58, 61, 65),       // #3A3D41
            ButtonHover = Color.FromArgb(69, 73, 78),            // #45494E
            ButtonActive = Color.FromArgb(45, 47, 51),           // #2D2F33
            ButtonText = Color.FromArgb(240, 240, 240),          // #F0F0F0
            ButtonBorder = Color.FromArgb(90, 90, 90),           // #5A5A5A

            Accent = Color.FromArgb(79, 183, 217),               // #4FB7D9
            SelectionBackground = Color.FromArgb(9, 71, 113),    // #094771
            SelectionText = Color.FromArgb(255, 255, 255),       // #FFFFFF

            LogBackground = Color.FromArgb(26, 26, 26),          // #1A1A1A
            LogText = Color.FromArgb(212, 212, 212),             // #D4D4D4
        };

        /// <summary>
        /// Light palette mirrors the classic Windows system look so switching back to
        /// light restores the familiar appearance with no surprises.
        /// </summary>
        public static readonly ThemePalette Light = new()
        {
            WindowBackground = SystemColors.Control,
            ControlBackground = SystemColors.Control,
            InputBackground = SystemColors.Window,
            InputBackgroundDisabled = SystemColors.ControlLight,

            Border = SystemColors.ControlDark,
            BorderStrong = SystemColors.ControlDarkDark,

            PrimaryText = SystemColors.ControlText,
            SecondaryText = SystemColors.GrayText,
            DisabledText = SystemColors.GrayText,
            LinkText = Color.FromArgb(0, 102, 204),
            ErrorText = Color.FromArgb(176, 0, 32),
            SuccessText = Color.FromArgb(30, 120, 30),

            ButtonBackground = SystemColors.ButtonFace,
            ButtonHover = SystemColors.ControlLight,
            ButtonActive = SystemColors.ControlDark,
            ButtonText = SystemColors.ControlText,
            ButtonBorder = SystemColors.ControlDark,

            Accent = Color.FromArgb(0, 120, 215),
            SelectionBackground = SystemColors.Highlight,
            SelectionText = SystemColors.HighlightText,

            LogBackground = SystemColors.Window,
            LogText = SystemColors.WindowText,
        };
    }
}
