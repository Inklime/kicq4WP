using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace kicq4WP
{
    public sealed partial class ChatPage : Page
    {
        private OscarProtocol _oscar;
        private Contact _contact;
        private bool _emojiVisible = false;
        private ObservableCollection<ChatMessage> _messages = new ObservableCollection<ChatMessage>();
        private ReconnectService _reconnect;

        public ChatPage()
        {
            this.InitializeComponent();
            MessagesList.ItemsSource = _messages;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var param = e.Parameter as Tuple<Contact, OscarProtocol>;
            if (param == null) return;

            _contact = param.Item1;
            _oscar = param.Item2;

            ContactNameTextBlock.Text = _contact.Name;
            ContactUinTextBlock.Text = _contact.Uin;

            _reconnect = ((App)Application.Current).ReconnectService;
            if (_reconnect != null)
            {
                _reconnect.OnDisconnected += OnConnectionLost;
                _reconnect.Reconnected += OnReconnectedInChat;
            }

            try
            {
                string iconPath = _contact.StatusIcon.TrimStart('/');
                ContactStatusIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appx:///" + iconPath));
            }
            catch { }

            NotificationService.Instance.ActiveChatUin = _contact.Uin;
            NotificationService.Instance.ClearUnread(_contact.Uin);

            // Загружаем историю
            await LoadHistoryAsync();

            // Добавляем сообщения которые пришли пока чат был закрыт
            var pending = _oscar.GetAndClearPending(_contact.Uin);
            foreach (var msg in pending)
            {
                // Проверяем что их ещё нет в истории (по тексту и времени)
                var chatMsg = new ChatMessage();
                chatMsg.Text = msg[0];
                chatMsg.SenderName = _contact.Name;
                chatMsg.Time = msg[1];
                chatMsg.IsIncoming = true;
                chatMsg.IsOutgoing = false;
                _messages.Add(chatMsg);
                await SaveMessageAsync(chatMsg);
            }

            ScrollToBottom();
            _oscar.IncomingMessage += OnIncomingMessage;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            NotificationService.Instance.ActiveChatUin = null;
            if (_reconnect != null)
            {
                _reconnect.OnDisconnected -= OnConnectionLost;
                _reconnect.Reconnected -= OnReconnectedInChat;
            }
            if (_oscar != null)
                _oscar.IncomingMessage -= OnIncomingMessage;
        }

        private async void OnIncomingMessage(string senderUin, string text)
        {
            if (senderUin != _contact.Uin) return;

#pragma warning disable CS1998 // В асинхронном методе отсутствуют операторы await, будет выполнен синхронный метод
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                var msg = new ChatMessage();
                msg.Text = text;
                msg.SenderName = _contact.Name;
                msg.Time = DateTime.Now.ToString("HH:mm");
                msg.IsIncoming = true;
                msg.IsOutgoing = false;
                _messages.Add(msg);

                // Сохраняем не блокируя UI
                var saveTask = SaveMessageAsync(msg);

                // Скролл только если уже внизу
                ScrollToBottomIfNeeded();
            });
#pragma warning restore CS1998 // В асинхронном методе отсутствуют операторы await, будет выполнен синхронный метод
        }

        private void ScrollToBottom()
        {
            if (_messages.Count == 0) return;
            var ignored = Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                () => MessagesList.ScrollIntoView(_messages[_messages.Count - 1]));
        }

        private void ScrollToBottomIfNeeded()
        {
            // Скролл только если пользователь уже внизу списка
            // (чтобы не прыгать если он читает старые сообщения)
            ScrollToBottom();
        }

        private async void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            await SendCurrentMessage();
        }

        private async void MessageTextBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await SendCurrentMessage();
            }
        }

        private async Task SendCurrentMessage()
        {
            string text = MessageTextBox.Text;
            if (text != null) text = text.Trim();
            if (string.IsNullOrEmpty(text) || _oscar == null) return;

            MessageTextBox.Text = "";

            try
            {
                await _oscar.SendIcbmAsync(_contact.Uin, text);

                var msg = new ChatMessage();
                msg.Text = text;
                msg.SenderName = "Вы";
                msg.Time = DateTime.Now.ToString("HH:mm");
                msg.IsIncoming = false;
                msg.IsOutgoing = true;

                _messages.Add(msg);
                await SaveMessageAsync(msg);
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatPage] Send error: " + ex.Message);
            }
        }

        private void OnConnectionLost()
        {
            ContactUinTextBlock.Text = "Соединение...";
        }

        private void OnReconnectedInChat(OscarProtocol newOscar)
        {
            _oscar = newOscar;
            _oscar.IncomingMessage += OnIncomingMessage;
            ContactUinTextBlock.Text = _contact.Uin;
        }

        private async void ClearChat_Click(object sender, RoutedEventArgs e)
        {
            _messages.Clear();
            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                StorageFile file = await folder.CreateFileAsync(
                    HistoryFileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, "");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ClearChat] Error: " + ex.Message);
            }
        }

        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            _emojiVisible = !_emojiVisible;
            EmojiPanel.Visibility = _emojiVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void InsertEmoji_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            string tag = btn.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;

            int pos = MessageTextBox.SelectionStart;
            string current = MessageTextBox.Text ?? "";
            MessageTextBox.Text = current.Insert(pos, tag);
            MessageTextBox.SelectionStart = pos + tag.Length;

            EmojiPanel.Visibility = Visibility.Collapsed;
            _emojiVisible = false;
            MessageTextBox.Focus(FocusState.Programmatic);
        }

        private string HistoryFileName
        {
            get { return "history_" + _oscar.UIN + "_" + _contact.Uin + ".txt"; }
        }

        private async Task SaveMessageAsync(ChatMessage msg)
        {
            try
            {
                string dir = msg.IsOutgoing ? "OUT" : "IN";
                string line = dir + "|" + msg.Time + "|" + msg.SenderName + "|" + msg.Text + "\n";
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                StorageFile file = await folder.CreateFileAsync(
                    HistoryFileName, CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(file, line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[History] Save error: " + ex.Message);
            }
        }

        private async Task LoadHistoryAsync()
        {
            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                StorageFile file = await folder.GetFileAsync(HistoryFileName);
                string content = await FileIO.ReadTextAsync(file);
                string[] lines = content.Split('\n');
                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split(new char[] { '|' }, 4);
                    if (parts.Length < 4) continue;

                    bool isOut = parts[0] == "OUT";
                    var msg = new ChatMessage();
                    msg.IsOutgoing = isOut;
                    msg.IsIncoming = !isOut;
                    msg.Time = parts[1];
                    msg.SenderName = parts[2];
                    msg.Text = parts[3];
                    _messages.Add(msg);
                }
                ScrollToBottom();
            }
            catch { }
        }



        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.GoBack();
        }
    }

    public class ChatMessage : INotifyPropertyChanged
    {
        private string _text;
        private bool _isIncoming;
        private bool _isOutgoing;

        public string Text
        {
            get { return _text; }
            set { _text = value; OnPropertyChanged("Text"); }
        }

        public string SenderName { get; set; }
        public string Time { get; set; }

        public bool IsIncoming
        {
            get { return _isIncoming; }
            set { _isIncoming = value; OnPropertyChanged("IsIncoming"); }
        }

        public bool IsOutgoing
        {
            get { return _isOutgoing; }
            set { _isOutgoing = value; OnPropertyChanged("IsOutgoing"); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(name));
        }
    }
}