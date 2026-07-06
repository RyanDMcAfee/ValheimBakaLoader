using System;
using System.Windows.Forms;
using ValheimBakaLoader.Properties;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The few WinForms conveniences the shell still needs now that the UI
    /// lives in the Blend WebView.
    /// </summary>
    public static class FormExtensions
    {
        /// <summary>
        /// Wraps a handler so it is always safe to raise from any thread:
        /// disposed controls swallow the call, and off-thread calls are
        /// marshaled onto the UI thread.
        /// </summary>
        public static EventHandler BuildEventHandler(this Control control, Action action)
        {
            return (_, _) =>
            {
                if (control.IsDisposed) return;

                if (control.InvokeRequired)
                {
                    control.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            };
        }

        /// <summary>Typed variant of <see cref="BuildEventHandler(Control, Action)"/>.</summary>
        public static EventHandler<TArgs> BuildEventHandler<TArgs>(this Control control, Action<TArgs> action)
        {
            return (_, args) =>
            {
                if (control.IsDisposed) return;

                if (control.InvokeRequired)
                {
                    control.BeginInvoke(action, args);
                }
                else
                {
                    action(args);
                }
            };
        }

        /// <summary>
        /// Assigns the embedded application icon. Set in code rather than the
        /// designer so single-file publishing doesn't lose the icon resource.
        /// </summary>
        public static void AddApplicationIcon(this Form form)
        {
            form.Icon = Resources.ApplicationIcon;
        }
    }
}
