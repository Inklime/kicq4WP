using System;
using System.Diagnostics;
using Windows.ApplicationModel.Background;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;

namespace kicq4WP
{
    public sealed class BackgroundTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            var deferral = taskInstance.GetDeferral();
            Debug.WriteLine("[BGTask] Woke up");

            try
            {
                // Показываем уведомление — приложение разберётся само при открытии
                ShowToast("kicq", "Новое сообщение");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BGTask] Error: " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void ShowToast(string title, string message)
        {
            try
            {
                string xml = string.Format(
                    "<toast><visual><binding template=\"ToastText02\">" +
                    "<text id=\"1\">{0}</text><text id=\"2\">{1}</text>" +
                    "</binding></visual></toast>",
                    EscapeXml(title), EscapeXml(message));

                var doc = new XmlDocument();
                doc.LoadXml(xml);
                ToastNotificationManager.CreateToastNotifier()
                    .Show(new ToastNotification(doc));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BGTask] Toast error: " + ex.Message);
            }
        }

        private string EscapeXml(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;");
        }
    }
}