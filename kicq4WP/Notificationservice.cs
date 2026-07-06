using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;
using Windows.Media.Playback;
using Windows.Media.Core;
using Windows.Storage;
using Windows.UI.Core;

namespace kicq4WP
{
    /// <summary>
    /// Управляет счётчиком непрочитанных, звуком и toast-уведомлениями
    /// </summary>
    public class NotificationService
    {
        private static NotificationService _instance;
        public static NotificationService Instance
        {
            get
            {
                if (_instance == null) _instance = new NotificationService();
                return _instance;
            }
        }

        // Счётчик непрочитанных по UIN
        private Dictionary<string, int> _unread = new Dictionary<string, int>();

        // Событие — MainPage подписывается чтобы обновить UI
        public event Action UnreadChanged;

        // UIN открытого чата (чтобы не считать его сообщения непрочитанными)
        public string ActiveChatUin { get; set; }

        private NotificationService() { }

        /// <summary>Вызывается когда пришло новое сообщение</summary>
        public async Task OnMessageReceived(string senderUin, string senderName, string text, CoreDispatcher dispatcher)
        {

            // Если чат с этим контактом открыт — не считаем непрочитанным
            if (ActiveChatUin == senderUin) return;

            // Увеличиваем счётчик
            if (!_unread.ContainsKey(senderUin))
                _unread[senderUin] = 0;
            _unread[senderUin]++;

            // Обновляем UI счётчика
            if (dispatcher != null)
            {
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (UnreadChanged != null) UnreadChanged();
                });
            }

            // Toast уведомление
            ShowToast(senderName, text);
        }

        /// <summary>Получить количество непрочитанных от конкретного UIN</summary>
        public int GetUnread(string uin)
        {
            if (_unread.ContainsKey(uin)) return _unread[uin];
            return 0;
        }

        /// <summary>Сбросить счётчик при открытии чата</summary>
        public void ClearUnread(string uin)
        {
            // Всегда сбрасываем и всегда стреляем событием
            _unread[uin] = 0;
            if (UnreadChanged != null) UnreadChanged();
        }

        /// <summary>Общее количество непрочитанных</summary>
        public int TotalUnread
        {
            get
            {
                int total = 0;
                foreach (var v in _unread.Values) total += v;
                return total;
            }
        }

        private void ShowToast(string senderName, string text)
        {
            try
            {
                string preview = text.Length > 60 ? text.Substring(0, 60) + "..." : text;

                string xml = string.Format(
                    "<toast>" +
                    "<visual><binding template=\"ToastText02\">" +
                    "<text id=\"1\">{0}</text>" +
                    "<text id=\"2\">{1}</text>" +
                    "</binding></visual>" +
                    "</toast>",
                    EscapeXml(senderName),
                    EscapeXml(preview));

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                var notifier = ToastNotificationManager.CreateToastNotifier();
                var notification = new ToastNotification(doc);
                notifier.Show(notification);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Toast ERROR] " + ex.Message);
            }
        }

        private string EscapeXml(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private async void PlaySound(CoreDispatcher dispatcher)
        {
            try
            {
                // Используем встроенный звук уведомления WP
                if (dispatcher != null)
                {
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            var player = BackgroundMediaPlayer.Current;
                            player.SetUriSource(new Uri("ms-appx:///Assets/message.wav"));
                            player.Play();
                        }
                        catch
                        {
                            // Если файла нет — используем системный звук через MediaElement
                            // (создаём временный элемент)
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Sound ERROR] " + ex.Message);
            }
        }
    }
}