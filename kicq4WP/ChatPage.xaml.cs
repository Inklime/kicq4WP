using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Input;
using Windows.Phone.UI.Input;

namespace kicq4WP
{
    public sealed partial class ChatPage : Page
    {
        private OscarProtocol _oscar;
        private Contact _contact;
        private bool _emojiVisible = false;
        private ObservableCollection<ChatMessage> _messages = new ObservableCollection<ChatMessage>();
        private ReconnectService _reconnect;

        // Сообщение, на которое сейчас отвечаем (или null)
        private ChatMessage _replyTo;

        public ChatPage()
        {
            this.InitializeComponent();
            MessagesList.ItemsSource = _messages;
            HardwareButtons.BackPressed += HardwareButtons_BackPressed;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            string forwardText = null;

            // Вариант навигации: (Contact, OscarProtocol, textToForward) — пришли из пересылки
            var paramWithForward = e.Parameter as Tuple<Contact, OscarProtocol, string>;
            if (paramWithForward != null)
            {
                _contact = paramWithForward.Item1;
                _oscar = paramWithForward.Item2;
                forwardText = paramWithForward.Item3;
            }
            else
            {
                // Обычный вариант: (Contact, OscarProtocol)
                var param = e.Parameter as Tuple<Contact, OscarProtocol>;
                if (param == null) return;
                _contact = param.Item1;
                _oscar = param.Item2;
            }

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
            await ApplyChatBackground();
            // Добавляем сообщения которые пришли пока чат был закрыт
            var pending = _oscar.GetAndClearPending(_contact.Uin);
            foreach (var msg in pending)
            {
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

            // Если пришли из "Переслать" — подставляем текст в поле ввода
            if (!string.IsNullOrEmpty(forwardText))
            {
                MessageTextBox.Text = forwardText;
                MessageTextBox.SelectionStart = forwardText.Length;
                MessageTextBox.Focus(FocusState.Programmatic);
            }
        }

        private void HardwareButtons_BackPressed(object sender, BackPressedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
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

#pragma warning disable CS1998
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                var msg = new ChatMessage();
                msg.Text = text;
                msg.SenderName = _contact.Name;
                msg.Time = DateTime.Now.ToString("HH:mm");
                msg.IsIncoming = true;
                msg.IsOutgoing = false;
                _messages.Add(msg);

                var saveTask = SaveMessageAsync(msg);

                ScrollToBottomIfNeeded();
            });
#pragma warning restore CS1998
        }

        private async Task ApplyChatBackground()
        {
            var settings = ApplicationData.Current.LocalSettings;
            string path = settings.Values["ChatBackgroundPath"] as string;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using (var stream = await file.OpenReadAsync())
                {
                    var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    MessagesList.Background = new Windows.UI.Xaml.Media.ImageBrush
                    {
                        ImageSource = bitmap,
                        Stretch = Windows.UI.Xaml.Media.Stretch.UniformToFill,
                        Opacity = 0.3
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatPage] ApplyChatBackground error: " + ex.Message);
            }
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

            string finalText = text;
            if (_replyTo != null)
            {
                string quotedLines = string.Join("\n",
                    _replyTo.Text.Split('\n').Select(l => "> " + l));
                finalText = quotedLines + "\n\n" + text;
            }

            MessageTextBox.Text = "";
            _replyTo = null;
            ReplyPreviewPanel.Visibility = Visibility.Collapsed;

            try
            {
                await _oscar.SendIcbmAsync(_contact.Uin, finalText);

                var msg = new ChatMessage();
                msg.Text = finalText;
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

        // ================== Долгое нажатие на сообщение ==================

        private void MessageBorder_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started) return;

            var element = sender as FrameworkElement;
            var msg = element?.DataContext as ChatMessage;
            if (msg == null) return;

            var flyout = new MenuFlyout();

            var reply = new MenuFlyoutItem { Text = "Ответить" };
            reply.Click += (s, a) => StartReply(msg);
            flyout.Items.Add(reply);

            var forward = new MenuFlyoutItem { Text = "Переслать" };
            forward.Click += (s, a) => ForwardMessage(msg);
            flyout.Items.Add(forward);

            var copy = new MenuFlyoutItem { Text = "Копировать" };
            copy.Click += (s, a) => CopyMessageText(msg);
            flyout.Items.Add(copy);

            flyout.ShowAt(element);
        }

        // ---- Ответить ----

        private void StartReply(ChatMessage msg)
        {
            _replyTo = msg;
            string preview = msg.Text.Length > 60 ? msg.Text.Substring(0, 60) + "…" : msg.Text;
            ReplyPreviewText.Text = "Ответ на: " + preview;
            ReplyPreviewPanel.Visibility = Visibility.Visible;
            MessageTextBox.Focus(FocusState.Programmatic);
        }

        private void CancelReply_Click(object sender, RoutedEventArgs e)
        {
            _replyTo = null;
            ReplyPreviewPanel.Visibility = Visibility.Collapsed;
        }

        // ---- Переслать ----

        private async void ForwardMessage(ChatMessage msg)
        {
            await ShowForwardContactPicker(msg.Text);
        }

        // Popup со списком контактов для пересылки. Не требует перехода на
        // MainPage/ContactsPage и её собственной инициализации — контакты
        // берутся напрямую из уже подключённого _oscar.
        //
        // ВАЖНО: строка "_oscar.ContactList" — предположение об имени свойства
        // со списком контактов. Если у вас он называется иначе — замените
        // только эту строку.
        private async Task ShowForwardContactPicker(string textToForward)
        {
            var contacts = await _oscar.GetContactsAsync(0);
            if (contacts == null || contacts.Count == 0)
            {
                await new Windows.UI.Popups.MessageDialog(
                    "Список контактов пуст или недоступен.", "Переслать").ShowAsync();
                return;
            }

            var popup = new Windows.UI.Xaml.Controls.Primitives.Popup();

            double screenWidth = Window.Current.Bounds.Width;
            double screenHeight = Window.Current.Bounds.Height;
            var margin = new Thickness(20, 40, 20, 40);

            var root = new Grid
            {
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 13, 17, 23)),
                Width = screenWidth - margin.Left - margin.Right,
                Height = screenHeight - margin.Top - margin.Bottom,
                Margin = margin
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Переслать кому?",
                FontSize = 18,
                FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe WP"),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                Margin = new Thickness(16, 12, 16, 8)
            };
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var listBox = new ListBox
            {
                ItemsSource = contacts,
                DisplayMemberPath = "Name",
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                Margin = new Thickness(8, 0, 8, 0)
            };
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(ListBoxItem.ForegroundProperty,
                new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White)));
            itemStyle.Setters.Add(new Setter(ListBoxItem.FontSizeProperty, 18.0));
            itemStyle.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 10, 8, 10)));
            listBox.ItemContainerStyle = itemStyle;
            Grid.SetRow(listBox, 1);
            root.Children.Add(listBox);

            var cancelBtn = new Button
            {
                Content = "Отмена",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 40, 40, 40)),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                FontSize = 17,
                FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe WP"),
                Margin = new Thickness(8, 8, 8, 8)
            };
            Grid.SetRow(cancelBtn, 2);
            root.Children.Add(cancelBtn);

            popup.Child = root;
            popup.HorizontalOffset = 0;
            popup.VerticalOffset = margin.Top;

            var tcs = new TaskCompletionSource<Contact>();

            SelectionChangedEventHandler onSelected = null;
            onSelected = (s, a) =>
            {
                var selected = listBox.SelectedItem as Contact;
                if (selected == null) return;
                listBox.SelectionChanged -= onSelected;
                popup.IsOpen = false;
                tcs.TrySetResult(selected);
            };
            listBox.SelectionChanged += onSelected;

            cancelBtn.Click += (s, a) =>
            {
                listBox.SelectionChanged -= onSelected;
                popup.IsOpen = false;
                tcs.TrySetResult(null);
            };

            popup.IsOpen = true;

            var chosen = await tcs.Task;
            if (chosen != null)
            {
                Frame.Navigate(typeof(ChatPage), Tuple.Create(chosen, _oscar, textToForward));
            }
        }

        // ---- Копировать ----

        private void CopyMessageText(ChatMessage msg)
        {

        }

        private void CopyUin_Click(object sender, RoutedEventArgs e)
        {
        }

        private async void ContactInfo_Click(object sender, RoutedEventArgs e)
        {
            var info = _contact.Info;

            string statusText;
            if (_contact.StatusIcon.Contains("offline"))
                statusText = "Офлайн";
            else if (info != null)
                statusText = info.StatusText;
            else
                statusText = "Неизвестно";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("UIN: " + _contact.Uin);
            sb.AppendLine("Имя: " + _contact.Name);
            sb.AppendLine("Группа: " + (_contact.Group ?? "—"));
            sb.AppendLine("Статус: " + statusText);

            if (info != null && !_contact.StatusIcon.Contains("offline"))
            {
                if (info.OnlineTime > 0)
                    sb.AppendLine("Онлайн: " + info.OnlineTimeText);
                if (info.SignonTime > 0)
                    sb.AppendLine("Зашел: " + info.SignonTimeText);
                if (info.MemberSince > 0)
                    sb.AppendLine("Регистрация: " + info.MemberSinceText);
                if (info.ExternalIp > 0)
                    sb.AppendLine("IP: " + info.ExternalIpText);
            }

            await new Windows.UI.Popups.MessageDialog(
                sb.ToString(), _contact.Name).ShowAsync();
        }

        private async void RenameContact_Click(object sender, RoutedEventArgs e)
        {
            await ShowRenamePopup(_contact, async (newName) =>
            {
                await _oscar.RenameContactAsync(_contact, newName);
                ContactNameTextBlock.Text = _contact.Name;
            });
        }

        private async void DeleteContact_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Windows.UI.Popups.MessageDialog(
                "Удалить " + _contact.Name + " из списка контактов?", "Удаление");

            dialog.Commands.Add(new Windows.UI.Popups.UICommand("Удалить", async cmd =>
            {
                try
                {
                    await _oscar.RemoveContactAsync(_contact);
                    Frame.GoBack();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Delete ERROR] " + ex.Message);
                    await new Windows.UI.Popups.MessageDialog(
                        "Ошибка удаления: " + ex.Message).ShowAsync();
                }
            }));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("Отмена"));
            await dialog.ShowAsync();
        }

        // Универсальный popup переименования
        // ИСПРАВЛЕНО: раньше Width = полная ширина экрана + Margin по бокам => попап
        // "уезжал" за правый край. Теперь ширина панели уменьшена на величину
        // горизонтальных margin'ов, чтобы итоговая занимаемая область была ровно
        // по ширине экрана и центрировалась симметрично.
        private async Task ShowRenamePopup(Contact contact, Func<string, Task> onSave)
        {
            var popup = new Windows.UI.Xaml.Controls.Primitives.Popup();

            double screenWidth = Window.Current.Bounds.Width;
            var margin = new Thickness(20, 16, 20, 16);

            var panel = new StackPanel
            {
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 13, 17, 23)),
                Width = screenWidth - margin.Left - margin.Right,
                Margin = margin,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Новое имя для " + contact.Name,
                FontSize = 16,
                FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe WP"),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                Margin = new Thickness(0, 0, 0, 10)
            });

            var input = new TextBox
            {
                Text = contact.Name,
                FontSize = 18,
                FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe WP"),
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Black),
                Margin = new Thickness(0, 0, 0, 14)
            };
            panel.Children.Add(input);

            var btnOk = new Button
            {
                Content = "Сохранить",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 0, 120, 215)),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                FontSize = 17,
                FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe WP"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(btnOk);

            var btnCancel = new Button
            {
                Content = "Отмена",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 40, 40, 40)),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                FontSize = 17,
                FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe WP")
            };
            panel.Children.Add(btnCancel);

            popup.Child = panel;
            // HorizontalOffset явно оставляем 0 — вся коррекция сделана через ширину панели.
            popup.HorizontalOffset = 0;
            popup.VerticalOffset = Window.Current.Bounds.Height - 300;

            var tcs = new TaskCompletionSource<bool>();

            btnOk.Click += async (s, args) =>
            {
                popup.IsOpen = false;
                string newName = (input.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(newName) && newName != contact.Name)
                {
                    try { await onSave(newName); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[Rename ERROR] " + ex.Message);
                    }
                }
                tcs.TrySetResult(true);
            };

            btnCancel.Click += (s, args) =>
            {
                popup.IsOpen = false;
                tcs.TrySetResult(false);
            };

            popup.IsOpen = true;
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
            await tcs.Task;
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