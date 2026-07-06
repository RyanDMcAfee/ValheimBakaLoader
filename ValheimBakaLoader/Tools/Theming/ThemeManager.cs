using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools.Theming
{
    /// <summary>
    /// Applies the active <see cref="Theme"/> to a form and every control beneath it.
    /// Theming is applied recursively, re-applied for controls added later at runtime,
    /// and extended to the native Win32 title bar via DWM so windows look dark end-to-end.
    /// </summary>
    public static class ThemeManager
    {
        #region DWM (dark title bar)

        // Windows 10 1809+ : DWMWA_USE_IMMERSIVE_DARK_MODE. Older builds used attribute 19.
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private static void ApplyTitleBar(Form form)
        {
            if (form == null || !form.IsHandleCreated) return;

            try
            {
                int useDark = Theme.IsDarkMode ? 1 : 0;
                if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
                {
                    // Fall back to the pre-20H1 attribute number on older Windows 10 builds.
                    DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
                }
            }
            catch
            {
                // DWM not available (e.g. very old Windows) - safe to ignore, just keeps the default bar.
            }
        }

        #endregion

        /// <summary>
        /// Loads the persisted dark-mode preference and sets it as the active theme.
        /// Call this once at startup before any non-splash form is created.
        /// </summary>
        /// <summary>
        /// Diagnostic override (set by the <c>--light</c>/<c>--dark</c> screenshot flags).
        /// When non-null it wins over the saved preference, so forms that re-read prefs at
        /// load (e.g. SplashForm) can't clobber the forced audit theme.
        /// </summary>
        public static bool? ForcedDarkMode { get; set; }

        public static void LoadFromPreferences(IUserPreferencesProvider provider)
        {
            if (ForcedDarkMode.HasValue)
            {
                Theme.IsDarkMode = ForcedDarkMode.Value;
                return;
            }
            try
            {
                Theme.IsDarkMode = provider.LoadPreferences().DarkMode;
            }
            catch
            {
                Theme.IsDarkMode = true; // dark-first default if prefs can't be read
            }
        }

        /// <summary>
        /// Themes a form and all of its child controls, hooks the title bar, and keeps
        /// future-added controls themed automatically.
        /// </summary>
        public static void Apply(Form form)
        {
            if (form == null) return;

            var p = Theme.Current;

            form.BackColor = p.WindowBackground;
            form.ForeColor = p.PrimaryText;

            // The title bar needs a created handle; apply now if we have one, else wait.
            if (form.IsHandleCreated) ApplyTitleBar(form);
            else form.HandleCreated += (s, e) => ApplyTitleBar(form);

            // Re-assert colors after the form is fully shown so anything built during
            // Load (e.g. dynamically added fields) also gets themed.
            form.Shown += (s, e) => ApplyToControl(form);

            ApplyToControl(form);
        }

        /// <summary>
        /// Re-applies the current theme to every open form. Used when the user toggles
        /// the theme at runtime so the change is reflected immediately everywhere.
        /// </summary>
        public static void ApplyToAllOpenForms()
        {
            foreach (Form form in Application.OpenForms)
            {
                Apply(form);
                form.Invalidate(true);
            }
        }

        private static void ApplyToControl(Control control)
        {
            if (control == null) return;

            var p = Theme.Current;

            switch (control)
            {
                case Button button:
                    StyleButton(button, p);
                    break;

                // LinkLabel must come before Label since it derives from Label.
                case LinkLabel link:
                    link.LinkColor = p.LinkText;
                    link.ActiveLinkColor = p.Accent;
                    link.VisitedLinkColor = p.LinkText;
                    link.ForeColor = p.PrimaryText;
                    link.BackColor = Color.Transparent;
                    break;

                case CheckBox:
                case RadioButton:
                case Label:
                    control.ForeColor = p.PrimaryText;
                    control.BackColor = Color.Transparent;
                    break;

                case TextBoxBase textBox:
                    StyleTextBox(textBox, p);
                    break;

                case ComboBox combo:
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = p.InputBackground;
                    combo.ForeColor = p.PrimaryText;
                    break;

                case NumericUpDown numeric:
                    numeric.BackColor = p.InputBackground;
                    numeric.ForeColor = p.PrimaryText;
                    break;

                case ListView listView:
                    StyleListView(listView, p);
                    break;

                case TreeView treeView:
                    treeView.BackColor = p.InputBackground;
                    treeView.ForeColor = p.PrimaryText;
                    treeView.BorderStyle = BorderStyle.FixedSingle;
                    break;

                case TabControl tabControl:
                    StyleTabControl(tabControl, p);
                    break;

                case ProgressBar progressBar:
                    progressBar.ForeColor = p.Accent;
                    progressBar.BackColor = p.InputBackground;
                    break;

                // Panel covers TableLayoutPanel, FlowLayoutPanel, SplitterPanel and TabPage
                // (all derive from Panel), so they don't need their own cases.
                case GroupBox:
                case Panel:
                case UserControl:
                case SplitContainer:
                    control.ForeColor = p.PrimaryText;
                    control.BackColor = p.ControlBackground;
                    break;

                case ToolStrip toolStrip:
                    toolStrip.RenderMode = ToolStripRenderMode.Professional;
                    toolStrip.Renderer = new DarkToolStripRenderer(p);
                    toolStrip.BackColor = p.ControlBackground;
                    toolStrip.ForeColor = p.PrimaryText;
                    break;

                default:
                    control.ForeColor = p.PrimaryText;
                    control.BackColor = p.ControlBackground;
                    break;
            }

            // Recurse into children, and keep any future children themed too.
            foreach (Control child in control.Controls)
            {
                ApplyToControl(child);
            }

            // Avoid stacking duplicate handlers if Apply runs more than once.
            control.ControlAdded -= OnControlAdded;
            control.ControlAdded += OnControlAdded;
        }

        private static void OnControlAdded(object sender, ControlEventArgs e)
        {
            ApplyToControl(e.Control);
        }

        #region Per-type styling

        private static void StyleButton(Button button, ThemePalette p)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = p.ButtonBackground;
            button.ForeColor = p.ButtonText;
            button.UseVisualStyleBackColor = false;
            button.FlatAppearance.BorderColor = p.ButtonBorder;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = p.ButtonHover;
            button.FlatAppearance.MouseDownBackColor = p.ButtonActive;
        }

        private static void StyleTextBox(TextBoxBase textBox, ThemePalette p)
        {
            textBox.ForeColor = p.PrimaryText;
            textBox.BorderStyle = BorderStyle.FixedSingle;

            // Read-only multiline boxes are typically log/console surfaces - give them the
            // darker monospace-friendly background for a console feel.
            if (textBox is TextBox tb && tb.ReadOnly && tb.Multiline)
            {
                textBox.BackColor = p.LogBackground;
                textBox.ForeColor = p.LogText;
            }
            else
            {
                textBox.BackColor = textBox.Enabled ? p.InputBackground : p.InputBackgroundDisabled;
            }
        }

        private static void StyleListView(ListView listView, ThemePalette p)
        {
            listView.BackColor = p.InputBackground;
            listView.ForeColor = p.PrimaryText;
            listView.BorderStyle = BorderStyle.FixedSingle;

            // .NET won't dark-theme the column header band on its own, so owner-draw just
            // the header while letting rows render normally with the colors set above.
            if (listView.View == View.Details)
            {
                listView.OwnerDraw = true;
                listView.DrawColumnHeader -= OnDrawColumnHeader;
                listView.DrawColumnHeader += OnDrawColumnHeader;
                listView.DrawItem -= OnDrawListItem;
                listView.DrawItem += OnDrawListItem;
                listView.DrawSubItem -= OnDrawListSubItem;
                listView.DrawSubItem += OnDrawListSubItem;
            }
        }

        private static void OnDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            var p = Theme.Current;
            using var back = new SolidBrush(p.ControlBackground);
            using var text = new SolidBrush(p.PrimaryText);
            using var pen = new Pen(p.Border);

            e.Graphics.FillRectangle(back, e.Bounds);
            e.Graphics.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width, e.Bounds.Height - 1);

            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
            var bounds = e.Bounds;
            bounds.X += 4;
            bounds.Width -= 8;
            TextRenderer.DrawText(e.Graphics, e.Header.Text, e.Font ?? listFont, bounds, p.PrimaryText, flags);
        }

        private static readonly Font listFont = SystemFonts.DefaultFont;

        private static void OnDrawListItem(object sender, DrawListViewItemEventArgs e)
        {
            // Let sub-items handle their own drawing in Details view.
            e.DrawDefault = true;
        }

        private static void OnDrawListSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            e.DrawDefault = true;
        }

        private static void StyleTabControl(TabControl tabControl, ThemePalette p)
        {
            tabControl.BackColor = p.WindowBackground;
            tabControl.ForeColor = p.PrimaryText;
            tabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabControl.DrawItem -= OnDrawTabItem;
            tabControl.DrawItem += OnDrawTabItem;

            foreach (TabPage page in tabControl.TabPages)
            {
                page.BackColor = p.ControlBackground;
                page.ForeColor = p.PrimaryText;
                page.UseVisualStyleBackColor = false;
            }
        }

        private static void OnDrawTabItem(object sender, DrawItemEventArgs e)
        {
            if (sender is not TabControl tab) return;

            var p = Theme.Current;
            var page = tab.TabPages[e.Index];
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            var backColor = selected ? p.ControlBackground : p.WindowBackground;
            using (var back = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }

            // Accent underline on the selected tab for a modern look.
            if (selected)
            {
                using var accent = new Pen(p.Accent, 2);
                e.Graphics.DrawLine(accent, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }

            var textColor = selected ? p.PrimaryText : p.SecondaryText;
            TextRenderer.DrawText(
                e.Graphics,
                page.Text,
                tab.Font,
                e.Bounds,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        #endregion
    }

    /// <summary>
    /// A flat dark renderer for ToolStrip / MenuStrip / StatusStrip so toolbars match the theme.
    /// </summary>
    internal class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        public DarkToolStripRenderer(ThemePalette palette) : base(new DarkColorTable(palette))
        {
            RoundedEdges = false;
            _palette = palette;
        }

        private readonly ThemePalette _palette;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(_palette.ControlBackground);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = _palette.PrimaryText;
            base.OnRenderItemText(e);
        }
    }

    internal class DarkColorTable : ProfessionalColorTable
    {
        private readonly ThemePalette _p;
        public DarkColorTable(ThemePalette palette) { _p = palette; }

        public override Color ToolStripGradientBegin => _p.ControlBackground;
        public override Color ToolStripGradientMiddle => _p.ControlBackground;
        public override Color ToolStripGradientEnd => _p.ControlBackground;
        public override Color MenuItemSelected => _p.SelectionBackground;
        public override Color MenuItemBorder => _p.Border;
        public override Color ButtonSelectedHighlight => _p.ButtonHover;
        public override Color ButtonSelectedBorder => _p.Border;
        public override Color SeparatorDark => _p.Border;
        public override Color SeparatorLight => _p.Border;
    }
}
