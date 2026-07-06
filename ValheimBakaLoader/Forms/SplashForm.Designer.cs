using System.Drawing;
using System.Windows.Forms;

namespace ValheimBakaLoader.Forms
{
    partial class SplashForm
    {
        private System.ComponentModel.IContainer components = null;

        private Label AppNameLabel;
        private ProgressBar ProgressBar;

        protected override void Dispose(bool disposing)
        {
            if (disposing) components?.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>
        /// Hand-written layout: a borderless, centered mini-window with the app
        /// name over a continuous progress bar. Not designer-generated.
        /// </summary>
        private void InitializeComponent()
        {
            SuspendLayout();

            AppNameLabel = new Label
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(12, 9),
                Size = new Size(174, 23),
                TabIndex = 0,
                Text = "ValheimBakaLoader",
                TextAlign = ContentAlignment.MiddleCenter,
                Name = "AppNameLabel",
            };

            ProgressBar = new ProgressBar
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(12, 35),
                Size = new Size(174, 16),
                Style = ProgressBarStyle.Continuous,
                TabIndex = 1,
                Name = "ProgressBar",
            };

            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(198, 63);
            ControlBox = false;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            Name = "SplashForm";
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(AppNameLabel);
            Controls.Add(ProgressBar);

            ResumeLayout(false);
        }
    }
}
