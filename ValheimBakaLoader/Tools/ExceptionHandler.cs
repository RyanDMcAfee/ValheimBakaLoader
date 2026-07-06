using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Last line of defense: tells the user what went wrong and offers to
    /// send an anonymous crash report.
    /// </summary>
    public interface IExceptionHandler
    {
        event EventHandler ExceptionHandled;

        void HandleException(Exception e, string contextMessage = null);
    }

    public class ExceptionHandler : IExceptionHandler
    {
        private readonly IRemoteApiClient Api;
        private readonly IApplicationLogger Logger;

        public ExceptionHandler(IRemoteApiClient remoteApiClient, IApplicationLogger logger)
        {
            Api = remoteApiClient;
            Logger = logger;
        }

        public event EventHandler ExceptionHandled;

        public void HandleException(Exception e, string contextMessage = null)
        {
            if (e == null) return;

            e = Unwrap(e);
            contextMessage ??= "Unknown Exception";

            var prompt =
                $"A fatal error has occurred: {e.Message}{Environment.NewLine}{Environment.NewLine}" +
                "Would you like to send an automated crash report to the developer?";

            var choice = MessageBox.Show(prompt, contextMessage, MessageBoxButtons.YesNo, MessageBoxIcon.Error);

            if (choice == DialogResult.Yes)
            {
                var report = DescribeCrash(e, contextMessage);

                // Fire-and-forget; the outcome is only logged so a failed
                // upload can never cascade into a second error dialog.
                Task.Run(async () =>
                {
                    try
                    {
                        await Api.SendCrashReportAsync(report);
                        Logger.Information("Crash report sent");
                    }
                    catch (Exception sendEx)
                    {
                        Logger.Warning("Failed to send crash report: {message}", sendEx.Message);
                    }
                });
            }

            ExceptionHandled?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Digs the root failure out of any AggregateException nesting.</summary>
        private static Exception Unwrap(Exception e)
        {
            while (e is AggregateException agg && agg.InnerException != null)
            {
                e = agg.InnerException;
            }

            return e;
        }

        private CrashReport DescribeCrash(Exception e, string contextMessage)
        {
            var report = AssemblyHelper.BuildCrashReport();

            report.Source = "CrashReport";
            report.AdditionalInfo = new Dictionary<string, string>
            {
                ["ExceptionType"] = e.GetType().Name,
                ["Message"] = e.Message,
                ["Context"] = contextMessage,
                ["Source"] = e.Source,
                ["TargetSite"] = e.TargetSite?.ToString(),
                ["StackTrace"] = e.StackTrace,
            };
            report.Logs = Logger.LogBuffer.Reverse().Take(100).ToList();

            return report;
        }
    }
}
