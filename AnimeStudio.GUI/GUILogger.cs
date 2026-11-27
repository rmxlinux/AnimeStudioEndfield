using System;
using System.Windows.Forms;

namespace AnimeStudio.GUI
{
    class GUILogger : ILogger
    {
        public bool ShowErrorMessage = true;
        private Action<string> action;

        public bool Silent { get; set; }
        public LoggerEvent Flags { get; set; }

        public GUILogger(Action<string> action)
        {
            this.action = action;
        }

        public void Log(LoggerEvent loggerEvent, string message)
        {
            if (!Flags.HasFlag(loggerEvent) || Silent)
                return;

            switch (loggerEvent)
            {
                case LoggerEvent.Error:
                    if (ShowErrorMessage)
                    {
                        MessageBox.Show(message);
                    }
                    break;
                default:
                    action(message);
                    break;
            }
        }

        public void LogSilently(LoggerEvent loggerEvent, string message)
        {
            // Logger.File receives these messages separately. Do not surface
            // automatic key refresh or block errors in the status bar/modal UI.
        }
    }
}
