using System;
using System.Drawing;
using System.Windows.Forms;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The WinForms half of the self updater: the progress window Run shows, and the one place
    /// one is made. It is apart from ManagedUpdater because that file compiles into Core, which
    /// has no desktop toolkit; Program.Main hands this to the updater (through
    /// <see cref="ManagedUpdater.MakeWindow"/>) before it calls Run, so a host still sees their
    /// update exactly as before, and a build with no window at all still updates.
    /// </summary>
    internal static class ManagedUpdaterWindow
    {
        /// <summary>
        /// The window Run asks for. Only ever called below the guard in Run, which is what keeps
        /// a run told to show nothing from ever drawing one; the suite proves that by swapping
        /// the hook for one that throws.
        /// </summary>
        internal static ManagedUpdater.IUpdaterWindow Make() => new UpdaterWindow();

        /// <summary>
        /// The updater's window: a title and one line of status, the size of a small dialog, in the
        /// middle of the screen and in the taskbar. It cannot be closed by hand while files are
        /// being copied, because a copy stopped half way is the thing the whole updater exists to
        /// avoid; it closes itself when the work is done.
        /// </summary>
        internal sealed class UpdaterWindow : Form, ManagedUpdater.IUpdaterWindow
        {
            private readonly Label Status;
            private volatile bool Done;

            public UpdaterWindow()
            {
                Text = HostCatalog.T("host.updater.title");
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = true;
                ShowInTaskbar = true;
                StartPosition = FormStartPosition.CenterScreen;
                AutoScaleMode = AutoScaleMode.Dpi;
                ClientSize = new Size(380, 64);

                Status = new Label
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(14, 0, 14, 0),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true,
                    Text = HostCatalog.T("host.updater.waiting"),
                };
                Controls.Add(Status);

                FormClosing += (_, e) =>
                {
                    if (!Done && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
                };
            }

            /// <summary>
            /// The message pump, with the work started from Shown: the same two lines Run used
            /// to hold itself, so nothing about when the window appears or when the copy begins
            /// has moved.
            /// </summary>
            public void Run(Action work)
            {
                Shown += (_, _) => work();
                Application.Run(this);
            }

            public void Say(string line)
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => Status.Text = line));
                }
                catch
                {
                    // A window going away is not a reason to stop copying.
                }
            }

            public void Finish()
            {
                Done = true;
                try
                {
                    if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Close));
                }
                catch
                {
                    // Already closed.
                }
            }
        }
    }
}
