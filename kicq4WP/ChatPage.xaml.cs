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
using System.Collections.Generic;

namespace kicq4WP
{
    public sealed partial class ChatPage : Page
    {
        private OscarProtocol _oscar;
        private Contact _contact;
        private bool _emojiVisible = false;
        private ObservableCollection<ChatMessage> _messages = new ObservableCollection<ChatMessage>();
        private ReconnectService _reconnect;
        private DispatcherTimer _typingTimer;
        private bool _isTyping = false;
        private bool _typingNotificationsEnabled = true;


        // Сообщение, на которое сейчас отвечаем (или null)
        private ChatMessage _replyTo;
        private HashSet<string> _loadedMessageKeys = new HashSet<string>();

        private string MakeMessageKey(ChatMessage msg)
        {
            string textPart = msg.Text != null && msg.Text.Length > 50
                ? msg.Text.Substring(0, 50) : msg.Text ?? "";
            return (msg.IsOutgoing ? "O" : "I") + "|" + msg.Time + "|" + textPart;
        }

        public ChatPage()
        {
            this.InitializeComponent();
            MessagesList.ItemsSource = _messages;
            HardwareButtons.BackPressed += HardwareButtons_BackPressed;
            EmojiItemsControl.ItemsSource = _availableEmojis;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            string forwardText = null;
            SoundPlayer.AudioCategory = Windows.UI.Xaml.Media.AudioCategory.GameEffects;
            SoundPlayer.AudioDeviceType = Windows.UI.Xaml.Media.AudioDeviceType.Multimedia;
            SoundService.SetPlayer(SoundPlayer, Dispatcher);

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

            _oscar.TypingNotificationReceived += OnTypingNotification;
            _typingTimer = new DispatcherTimer();
            _typingTimer.Interval = TimeSpan.FromSeconds(5);
            _typingTimer.Tick += OnTypingTimerTick;
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            object typingEnabled = settings.Values["TypingNotifications"];
            _typingNotificationsEnabled = typingEnabled == null || (bool)typingEnabled;
            ContactNameTextBlock.Text = _contact.Name;
            ContactUinTextBlock.Text = _contact.Uin;

            // Обновляем AppBar в зависимости от типа контакта
            UpdateAppBar();

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

            _messages.Clear();

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

        private void UpdateAppBar()
        {
            var bar = BottomAppBar as CommandBar;
            if (bar == null) return;

            // Очищаем все вторичные команды
            bar.SecondaryCommands.Clear();

            if (_contact.IsTemporary)
            {
                // Только добавление и очистка чата
                var addBtn = new AppBarButton { Label = "Добавить в контакты", Icon = new SymbolIcon(Symbol.Add) };
                addBtn.Click += AddToContacts_Click;
                bar.SecondaryCommands.Add(addBtn);

                var copyBtn = new AppBarButton { Label = "Скопировать UIN", Icon = new SymbolIcon(Symbol.Copy) };
                copyBtn.Click += CopyUin_Click;
                bar.SecondaryCommands.Add(copyBtn);

                var clearBtn = new AppBarButton { Label = "Очистить чат", Icon = new SymbolIcon(Symbol.Clear) };
                clearBtn.Click += ClearChat_Click;
                bar.SecondaryCommands.Add(clearBtn);
            }
            else
            {
                // Полный набор для обычного контакта
                var copyBtn = new AppBarButton { Label = "Скопировать UIN", Icon = new SymbolIcon(Symbol.Copy) };
                copyBtn.Click += CopyUin_Click;
                bar.SecondaryCommands.Add(copyBtn);

                var infoBtn = new AppBarButton { Label = "Информация", Icon = new SymbolIcon(Symbol.People) };
                infoBtn.Click += ContactInfo_Click;
                bar.SecondaryCommands.Add(infoBtn);

                var renameBtn = new AppBarButton { Label = "Переименовать", Icon = new SymbolIcon(Symbol.Edit) };
                renameBtn.Click += RenameContact_Click;
                bar.SecondaryCommands.Add(renameBtn);

                var deleteBtn = new AppBarButton { Label = "Удалить контакт", Icon = new SymbolIcon(Symbol.Delete) };
                deleteBtn.Click += DeleteContact_Click;
                bar.SecondaryCommands.Add(deleteBtn);

                var clearBtn = new AppBarButton { Label = "Очистить чат", Icon = new SymbolIcon(Symbol.Clear) };
                clearBtn.Click += ClearChat_Click;
                bar.SecondaryCommands.Add(clearBtn);
            }
        }

        private async void AddToContacts_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _oscar.AddContactAsync(_contact.Uin, _contact.Name);
                _contact.IsTemporary = false;
                UpdateAppBar();
                await new Windows.UI.Popups.MessageDialog(
                    _contact.Name + " добавлен в контакты").ShowAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AddContact ERROR] " + ex.Message);
                await new Windows.UI.Popups.MessageDialog(
                    "Ошибка: " + ex.Message).ShowAsync();
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

        private void OnEmojiPicked_GridView(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as EmojiItem;
            if (item == null) return;

            int cursorPosition = MessageTextBox.SelectionStart;
            MessageTextBox.Text = MessageTextBox.Text.Insert(cursorPosition, item.Code);
            MessageTextBox.SelectionStart = cursorPosition + item.Code.Length;
            MessageTextBox.Focus(FocusState.Programmatic);
        }

        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            if (EmojiPanel.Visibility == Visibility.Visible)
            {
                EmojiPanel.Visibility = Visibility.Collapsed;
                return;
            }

            EmojiPanel.Visibility = Visibility.Visible;

            // Загружаем анимации лениво после отображения панели
            if (!_emojiLoaded)
                LoadEmojiAnimationsAsync();
        }

        private bool _emojiLoaded = false;

        private async void LoadEmojiAnimationsAsync()
        {
            // Ждём пока GridView отрисуется
            await Task.Delay(100);

            int idx = 0;
            foreach (var item in _availableEmojis)
            {
                item.ImagePath = item.ImagePath; // триггерим PropertyChanged
                idx++;
                if (idx % 5 == 0)
                    await Task.Delay(16); // пауза каждые 5 смайликов
            }
            _emojiLoaded = true;
        }


        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
            NotificationService.Instance.ActiveChatUin = null;
            _oscar.TypingNotificationReceived -= OnTypingNotification;
            _oscar.SendTypingNotificationAsync(_contact.Uin, 0x0000);
            SoundService.SetPlayer(null, null);
            if (_typingTimer != null) _typingTimer.Stop();
            if (_reconnect != null)
            {
                _reconnect.OnDisconnected -= OnConnectionLost;
                _reconnect.Reconnected -= OnReconnectedInChat;
            }
            if (_oscar != null)
                _oscar.IncomingMessage -= OnIncomingMessage;
        }

        private async void OnTypingNotification(string senderUin, ushort type)
        {
            if (senderUin != _contact.Uin) return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                switch (type)
                {
                    case 0x0002: // начал печатать
                    case 0x0001: // набирает текст
                        ContactUinTextBlock.Text = "печатает...";
                        break;
                    case 0x0000: // перестал
                        ContactUinTextBlock.Text = _contact.Uin;
                        break;
                }
            });
        }

        // Отправка при вводе
        private async void MessageTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_typingNotificationsEnabled) return;
            string text = MessageTextBox.Text;

            if (!string.IsNullOrEmpty(text))
            {
                if (!_isTyping)
                {
                    _isTyping = true;
                    await _oscar.SendTypingNotificationAsync(_contact.Uin, 0x0002); // начал
                }

                // Сбрасываем таймер
                _typingTimer.Stop();
                _typingTimer.Start();
            }
            else
            {
                // Текст удалён
                if (_isTyping)
                {
                    _isTyping = false;
                    _typingTimer.Stop();
                    await _oscar.SendTypingNotificationAsync(_contact.Uin, 0x0000); // остановился
                }
            }
        }

        // Таймер — если 5 секунд не печатал:
        private async void OnTypingTimerTick(object sender, object e)
        {
            _typingTimer.Stop();
            if (_isTyping)
            {
                _isTyping = false;
                await _oscar.SendTypingNotificationAsync(_contact.Uin, 0x0001); // текст набран
            }
        }


        private async void OnIncomingMessage(string senderUin, string text)
        {
            if (senderUin != _contact.Uin) return;
            _oscar.GetAndClearPending(_contact.Uin);

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                var msg = new ChatMessage();
                msg.Text = text;
                msg.SenderName = _contact.Name;
                msg.Time = DateTime.Now.ToString("HH:mm");
                msg.IsIncoming = true;
                msg.IsOutgoing = false;


                _messages.Add(msg);
                await SaveMessageAsync(msg);
                ScrollToBottomIfNeeded();
            });
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
            _isTyping = false;
            _typingTimer.Stop();
            await _oscar.SendTypingNotificationAsync(_contact.Uin, 0x0000);
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

            // Проверяем finalText тоже
            if (string.IsNullOrEmpty(finalText)) return;

            MessageTextBox.Text = "";
            _replyTo = null;
            if (ReplyPreviewPanel != null)
                ReplyPreviewPanel.Visibility = Visibility.Collapsed;

            try
            {
                // Отправляем в Task.Run чтобы не блокировать UI
                await Task.Run(async () => await _oscar.SendIcbmAsync(_contact.Uin, finalText));

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    var msg = new ChatMessage();
                    msg.Text = finalText;
                    msg.SenderName = "Вы";
                    msg.Time = DateTime.Now.ToString("HH:mm");
                    msg.IsIncoming = false;
                    msg.IsOutgoing = true;

                    string key = MakeMessageKey(msg);
                    if (!_loadedMessageKeys.Contains(key))
                    {
                        _loadedMessageKeys.Add(key);
                        _messages.Add(msg);
                        await SaveMessageAsync(msg);
                        ScrollToBottom();
                    }
                });
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
                if (!string.IsNullOrEmpty(info.StatusMessage))
                    sb.AppendLine("Доп. статус: " + info.StatusMessage);
                if (!string.IsNullOrEmpty(info.Mood))
                    sb.AppendLine("Настроение: " + info.MoodText);
                if (info.OnlineTime > 0)
                    sb.AppendLine("Онлайн: " + info.OnlineTimeText);
                if (info.SignonTime > 0)
                    sb.AppendLine("Зашел: " + info.SignonTimeText);
                if (info.MemberSince > 0)
                    sb.AppendLine("Регистрация: " + info.MemberSinceText);
                if (info.ExternalIp > 0)
                    sb.AppendLine("IP: " + info.ExternalIpText);

            }

            // Запрашиваем полную анкету — SNAC(15,02)/07D0/04D0, ответ
            // SNAC(15,03)/07DA/00C8 (имя, фамилия, ник, email, город и т.д.)
            ushort seq = (ushort)new Random().Next(1, 60000);
            OscarProtocol.UserFullInfo fullInfo = null;
            try
            {
                fullInfo = await _oscar.RequestFullUserInfoDetailedAsync(_contact.Uin, seq);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactInfo] RequestFullUserInfo error: " + ex.Message);
            }

            if (fullInfo != null)
            {
                sb.AppendLine();
                sb.AppendLine("— Анкета —");
                if (!string.IsNullOrEmpty(fullInfo.FirstName)) sb.AppendLine("Имя: " + fullInfo.FirstName);
                if (!string.IsNullOrEmpty(fullInfo.LastName)) sb.AppendLine("Фамилия: " + fullInfo.LastName);
                if (!string.IsNullOrEmpty(fullInfo.Nickname)) sb.AppendLine("Ник: " + fullInfo.Nickname);
                if (!string.IsNullOrEmpty(fullInfo.Email)) sb.AppendLine("Email: " + fullInfo.Email);
                if (!string.IsNullOrEmpty(fullInfo.HomeCity)) sb.AppendLine("Город: " + fullInfo.HomeCity);
                if (!string.IsNullOrEmpty(fullInfo.HomeState)) sb.AppendLine("Регион: " + fullInfo.HomeState);
                if (!string.IsNullOrEmpty(fullInfo.HomePhone)) sb.AppendLine("Телефон: " + fullInfo.HomePhone);
                if (!string.IsNullOrEmpty(fullInfo.CellPhone)) sb.AppendLine("Мобильный: " + fullInfo.CellPhone);
                if (!string.IsNullOrEmpty(fullInfo.HomeAddress)) sb.AppendLine("Адрес: " + fullInfo.HomeAddress);
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("(Анкета недоступна или сервер не ответил)");
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

       /*rivate void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            _emojiVisible = !_emojiVisible;
            EmojiPanel.Visibility = _emojiVisible ? Visibility.Visible : Visibility.Collapsed;
        }*/

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
                // Используем \x01 как разделитель вместо | 
                // и \x02 как разделитель строк вместо \n
                string text = msg.Text
                    .Replace("\x01", " ")  // убираем спецсимволы из текста
                    .Replace("\x02", " ");
                string line = (msg.IsOutgoing ? "OUT" : "IN") + "\x01" +
                              msg.Time + "\x01" +
                              msg.SenderName + "\x01" +
                              text + "\x02";

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

                // Пробуем новый формат (разделитель \x02)
                bool isNewFormat = content.Contains("\x02");
                string[] lines = isNewFormat
                    ? content.Split('\x02')
                    : content.Split('\n');

                char sep = isNewFormat ? '\x01' : '|';

                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split(new char[] { sep }, 4);
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
                // Пересохраняем в новом формате если читали старый
                if (!isNewFormat && _messages.Count > 0)
                {
                    try
                    {
                        StorageFile newFile = await folder.CreateFileAsync(
                            HistoryFileName, CreationCollisionOption.ReplaceExisting);
                        var sb = new System.Text.StringBuilder();
                        foreach (var m in _messages)
                        {
                            string t = (m.Text ?? "").Replace("\x01", " ").Replace("\x02", " ");
                            sb.Append((m.IsOutgoing ? "OUT" : "IN") + "\x01" +
                                      m.Time + "\x01" + m.SenderName + "\x01" + t + "\x02");
                        }
                        await FileIO.WriteTextAsync(newFile, sb.ToString());
                        Debug.WriteLine("[History] Migrated to new format");
                    }
                    catch { }
                }
            }
            catch { }
        }

        private List<EmojiItem> _availableEmojis = new List<EmojiItem>
{
    new EmojiItem { Code = "O:-)", ImagePath = "ms-appx:///Assets/emoji/aa.gif" },
new EmojiItem { Code = ":-)", ImagePath = "ms-appx:///Assets/emoji/ab.gif" },
new EmojiItem { Code = ":-(", ImagePath = "ms-appx:///Assets/emoji/ac.gif" },
new EmojiItem { Code = ";-)", ImagePath = "ms-appx:///Assets/emoji/ad.gif" },
new EmojiItem { Code = ":-P", ImagePath = "ms-appx:///Assets/emoji/ae.gif" },
new EmojiItem { Code = "8)", ImagePath = "ms-appx:///Assets/emoji/af.gif" },
new EmojiItem { Code = ":-D", ImagePath = "ms-appx:///Assets/emoji/ag.gif" },
new EmojiItem { Code = ":-[", ImagePath = "ms-appx:///Assets/emoji/ah.gif" },
new EmojiItem { Code = "=-O", ImagePath = "ms-appx:///Assets/emoji/ai.gif" },
new EmojiItem { Code = ":-*", ImagePath = "ms-appx:///Assets/emoji/aj.gif" },
new EmojiItem { Code = ":'(", ImagePath = "ms-appx:///Assets/emoji/ak.gif" },
new EmojiItem { Code = ":-X", ImagePath = "ms-appx:///Assets/emoji/al.gif" },
new EmojiItem { Code = ">:o", ImagePath = "ms-appx:///Assets/emoji/am.gif" },
new EmojiItem { Code = ":-|", ImagePath = "ms-appx:///Assets/emoji/an.gif" },
new EmojiItem { Code = ":-\\", ImagePath = "ms-appx:///Assets/emoji/ao.gif" },
new EmojiItem { Code = "*JOKINGLY*", ImagePath = "ms-appx:///Assets/emoji/ap.gif" },
new EmojiItem { Code = "]:->", ImagePath = "ms-appx:///Assets/emoji/aq.gif" },
new EmojiItem { Code = "[:-}", ImagePath = "ms-appx:///Assets/emoji/ar.gif" },
new EmojiItem { Code = "*KISSED*", ImagePath = "ms-appx:///Assets/emoji/as.gif" },
new EmojiItem { Code = ":-!", ImagePath = "ms-appx:///Assets/emoji/at.gif" },
new EmojiItem { Code = "*TIRED*", ImagePath = "ms-appx:///Assets/emoji/au.gif" },
new EmojiItem { Code = "*STOP*", ImagePath = "ms-appx:///Assets/emoji/av.gif" },
new EmojiItem { Code = "*KISSING*", ImagePath = "ms-appx:///Assets/emoji/aw.gif" },
new EmojiItem { Code = "@}->--", ImagePath = "ms-appx:///Assets/emoji/ax.gif" },
new EmojiItem { Code = "*THUMBS UP*", ImagePath = "ms-appx:///Assets/emoji/ay.gif" },
new EmojiItem { Code = "*DRINK*", ImagePath = "ms-appx:///Assets/emoji/az.gif" },
new EmojiItem { Code = "*IN LOVE*", ImagePath = "ms-appx:///Assets/emoji/ba.gif" },
new EmojiItem { Code = "@=", ImagePath = "ms-appx:///Assets/emoji/bb.gif" },
new EmojiItem { Code = "*HELP*", ImagePath = "ms-appx:///Assets/emoji/bc.gif" },
new EmojiItem { Code = "\\m/", ImagePath = "ms-appx:///Assets/emoji/bd.gif" },
new EmojiItem { Code = "%)", ImagePath = "ms-appx:///Assets/emoji/be.gif" },
new EmojiItem { Code = "*OK*", ImagePath = "ms-appx:///Assets/emoji/bf.gif" },
new EmojiItem { Code = "*WASSUP*", ImagePath = "ms-appx:///Assets/emoji/bg.gif" },
new EmojiItem { Code = "*SORRY*", ImagePath = "ms-appx:///Assets/emoji/bh.gif" },
new EmojiItem { Code = "*BRAVO*", ImagePath = "ms-appx:///Assets/emoji/bi.gif" },
new EmojiItem { Code = "*ROFL*", ImagePath = "ms-appx:///Assets/emoji/bj.gif" },
new EmojiItem { Code = "*PARDON*", ImagePath = "ms-appx:///Assets/emoji/bk.gif" },
new EmojiItem { Code = "*NO*", ImagePath = "ms-appx:///Assets/emoji/bl.gif" },
new EmojiItem { Code = "*CRAZY*", ImagePath = "ms-appx:///Assets/emoji/bm.gif" },
new EmojiItem { Code = "*DONT_KNOW*", ImagePath = "ms-appx:///Assets/emoji/bn.gif" },
new EmojiItem { Code = "*DANCE*", ImagePath = "ms-appx:///Assets/emoji/bo.gif" },
new EmojiItem { Code = "*YAHOO*", ImagePath = "ms-appx:///Assets/emoji/bp.gif" },

};

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
        public class EmojiItem
        {
            public string Code { get; set; }      // То, что печатается в чате: ":)", ":D"
            public string ImagePath { get; set; } // Путь к гифке в ресурсах
        }

    }
}