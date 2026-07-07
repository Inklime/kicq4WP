using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Popups;
using System.Diagnostics;
using Windows.Storage;
using kicq4WP;
using System.Linq;
using Windows.UI.Core;

namespace kicq4WP
{


    public sealed partial class MainPage : Page
    {
        private OscarProtocol _oscarProtocol;
        private Task _;
        private bool _showGroups = false;
        private bool _hideOffline = false;


        public ObservableCollection<Contact> Contacts { get; set; }

        public MainPage()
        {
            this.InitializeComponent();
            this.DataContext = this;
            string uin = "";
            var saved = ContactStorage.LoadContactsFromFileAsync(uin);
            Contacts = new ObservableCollection<Contact>();

            ContactsListView.ItemsSource = Contacts;
            InitAsync();
        }

        private async void InitAsync()
        {
            string uin = await LoadLastUsedUinAsync();
            var saved = await ContactStorage.LoadContactsFromFileAsync(uin);

            foreach (var contact in saved)
                Contacts.Add(contact);
        }
        private void SaveLastUin(string uin)
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            localSettings.Values["LastUin"] = uin;
        }
        private string LoadLastUin()
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            return localSettings.Values["LastUin"]?.ToString();
        }

        public static async Task<string> LoadLastUsedUinAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync("last_uin.txt");
                string uin = await FileIO.ReadTextAsync(file);
                return uin;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static async Task SaveLastUsedUinAsync(string uin)
        {
            StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                "last_uin.txt", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, uin);
        }

        private bool _initialized = false;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // 1. ПЕРВИЧНАЯ ИНИЦИАЛИЗАЦИЯ (выполняется только один раз)
            if (!_initialized)
            {
                var oscarProtocol = e.Parameter as OscarProtocol;
                if (oscarProtocol == null) return;

                _oscarProtocol = oscarProtocol;
                _initialized = true;

                SaveLastUin(_oscarProtocol.UIN);
                UinTextBlock.Text = _oscarProtocol.UIN;
                LoadContacts(0x00000000);
            }

            // 2. ПОДПИСКИ НА СОБЫТИЯ (выполняются каждый раз при входе на страницу)
            var reconnect = ((App)Application.Current).ReconnectService;
            if (reconnect != null)
            {
                // Сначала отписываемся для защиты от двойных подписок
                reconnect.OnDisconnected -= OnConnectionLost;
                reconnect.OnDisconnected += OnConnectionLost;

                reconnect.Reconnected -= OnReconnected;
                reconnect.Reconnected += OnReconnected;

                reconnect.KickedOut -= OnKickedOut;
                reconnect.KickedOut += OnKickedOut;
            }

            if (_oscarProtocol != null)
            {
                _oscarProtocol.ContactStatusChanged -= OnContactStatusChanged;
                _oscarProtocol.ContactStatusChanged += OnContactStatusChanged;
            }

            NotificationService.Instance.UnreadChanged -= OnUnreadChanged;
            NotificationService.Instance.UnreadChanged += OnUnreadChanged;

            // 3. ДЕЙСТВИЯ ПРИ ВОЗВРАТЕ (например, из чата)
            if (_initialized)
            {
                ApplySettings();

                // Принудительно обновляем маркеры у всех контактов!
                // Чат обнулил счетчик пока мы были отписаны от событий, поэтому запрашиваем актуальные данные.
                OnUnreadChanged();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // ОТПИСКИ ОТ СОБЫТИЙ (чтобы страница не "жрала" ресурсы в фоне)
            NotificationService.Instance.UnreadChanged -= OnUnreadChanged;

            var reconnect = ((App)Application.Current).ReconnectService;
            if (reconnect != null)
            {
                reconnect.OnDisconnected -= OnConnectionLost;
                reconnect.Reconnected -= OnReconnected;
                reconnect.KickedOut -= OnKickedOut;
            }

            if (_oscarProtocol != null)
            {
                try { _oscarProtocol.ContactStatusChanged -= OnContactStatusChanged; } catch { }
            }
        }

        private async void OnKickedOut(string reason)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                await new Windows.UI.Popups.MessageDialog(
                    reason, "Отключен").ShowAsync();

                ((App)Application.Current).ReconnectService = null;
                ((App)Application.Current).Oscar = null;
                Frame.Navigate(typeof(LoginPage));
            });
        }


        private async void OnContactStatusChanged()
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                SortContacts();
                RefreshView();
            });
        }

        private void OnConnectionLost()
        {
            UinTextBlock.Text = "Соединение...";
            foreach (var contact in Contacts)
            {
                contact.StatusIcon = "/Assets/statuses/offline.png";
                contact.IsNewOnline = false;
            }
        }

        private async void OnReconnected(OscarProtocol newOscar)
        {
            if (_oscarProtocol != null)
                _oscarProtocol.ContactStatusChanged -= OnContactStatusChanged;

            _oscarProtocol = newOscar;
            _oscarProtocol.ContactStatusChanged += OnContactStatusChanged;

            var fresh = await _oscarProtocol.GetContactsAsync(0);

            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                UinTextBlock.Text = _oscarProtocol.UIN;

                Contacts.Clear();
                foreach (var c in fresh)
                    Contacts.Add(c);

                SortContacts();
                RefreshView();
            });
        }

        private void OnUnreadChanged()
        {
            foreach (var contact in Contacts)
            {
                contact.UnreadCount = NotificationService.Instance.GetUnread(contact.Uin);
            }
        }

        private bool _statusPanelVisible = false;

        private void StatusButton_Click(object sender, RoutedEventArgs e)
        {
            _statusPanelVisible = !_statusPanelVisible;
            StatusPanel.Visibility = _statusPanelVisible
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StatusPanelClose_Click(object sender, RoutedEventArgs e)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            _statusPanelVisible = false;
        }

        private async void SetStatus_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null || _oscarProtocol == null) return;

            string tagStr = btn.Tag as string;
            if (string.IsNullOrEmpty(tagStr)) return;

            try
            {
                uint statusCode = Convert.ToUInt32(tagStr, 16);
                await _oscarProtocol.SendSetStatusAsync(statusCode);

                // Обновляем иконку в шапке
                string icon;
                switch (statusCode & 0xFFFF)
                {
                    case 0x0001: icon = "away"; break;
                    case 0x0002: icon = "dnd"; break;
                    case 0x0004: icon = "na"; break;
                    case 0x0010: icon = "busy"; break;
                    case 0x0020: icon = "f4c"; break;
                    case 0x0100: icon = "inv"; break;
                    default: icon = "online"; break;
                }
                OwnStatusIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appx:///Assets/statuses/" + icon + ".png"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Status ERROR] " + ex.Message);
            }

            StatusPanel.Visibility = Visibility.Collapsed;
            _statusPanelVisible = false;
        }

        private void SortContacts()
        {
            var sorted = Contacts
                .OrderBy(c =>
                {
                    if (c.StatusIcon.Contains("online")) return 0;
                    if (c.StatusIcon.Contains("f4c")) return 1;
                    if (c.StatusIcon.Contains("away")) return 2;
                    if (c.StatusIcon.Contains("busy")) return 3;
                    if (c.StatusIcon.Contains("dnd")) return 4;
                    if (c.StatusIcon.Contains("na")) return 5;
                    if (c.StatusIcon.Contains("inv")) return 6;
                    return 7; // offline
        })
                .ThenBy(c => c.Name)
                .ToList();

            Contacts.Clear();
            foreach (var c in sorted) Contacts.Add(c);
        }

        private async void LoadContacts(uint statusCode)
        {
            try
            {
                Contacts.Clear();
                var parsedContacts = await _oscarProtocol.GetContactsAsync(statusCode);
                foreach (var contact in parsedContacts)
                    Contacts.Add(contact);
                SortContacts();
                ApplySettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainPage] Error loading contacts: {ex.Message}");
            }
        }



        private string GetStatusIcon(Contact contact)
        {
            // Получаем статус из OscarProtocol (предполагаем наличие метода GetContactStatus)
            string status = "offline";
            if (_oscarProtocol != null)
            {
                status = _oscarProtocol.GetContactStatus(contact.Uin) ?? "offline";
            }

            // Маппинг статусов на иконки с использованием классического switch
            switch (status)
            {
                case "online":
                    return "/Assets/statuses/online.png";
                case "depressed":
                    return "/Assets/statuses/depressed.png";
                case "eating":
                    return "/Assets/statuses/eating.png";
                case "evil":
                    return "/Assets/statuses/evil.png";
                case "home":
                    return "/Assets/statuses/home.png";
                case "work":
                    return "/Assets/statuses/work.png";
                case "away":
                    return "/Assets/statuses/away.png";
                case "dnd":
                    return "/Assets/statuses/dnd.png";
                case "na":
                    return "/Assets/statuses/na.png";
                case "busy":
                    return "/Assets/statuses/busy.png";
                case "free4chat":
                    return "/Assets/statuses/f4c.png";
                case "invisible":
                    return "/Assets/statuses/inv.png";
                default:
                    return "/Assets/statuses/offline.png";
            }
        }

        private async Task<bool> CheckIfNewOnline(string uin)
        {
            try
            {
                // Проверяем, был ли контакт недавно онлайн
                DateTime lastOnline = await _oscarProtocol.GetLastOnlineTimeAsync(uin);
                return (DateTime.Now - lastOnline).TotalMinutes < 5; // 5 минут - порог "нового онлайна"
            }
            catch
            {
                return false;
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Останавливаем ReconnectService
                var reconnect = ((App)Application.Current).ReconnectService;
                if (reconnect != null)
                {
                    reconnect.Stop();
                    ((App)Application.Current).ReconnectService = null;
                }

                // 2. Отписываемся от событий
                if (_oscarProtocol != null)
                {
                    try { _oscarProtocol.ContactStatusChanged -= OnContactStatusChanged; } catch { }
                    try { NotificationService.Instance.UnreadChanged -= OnUnreadChanged; } catch { }
                }

                // 3. Статус офлайн
                if (_oscarProtocol != null)
                {
                    try
                    {
                        await _oscarProtocol.SendSetStatusAsync(0xFFFFFFFF);
                        await Task.Delay(200);
                    }
                    catch { }
                }

                // 4. Отключаемся
                if (_oscarProtocol != null)
                {
                    await _oscarProtocol.DisconnectAsync();
                    ((App)Application.Current).Oscar = null;
                    _oscarProtocol = null;
                }

                Contacts.Clear();
                _initialized = false;
                Frame.Navigate(typeof(LoginPage));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Logout ERROR] " + ex.Message);
                Frame.Navigate(typeof(LoginPage));
            }
        }

        private async void StartFlashingArrows(Contact contact)
        {
            string[] arrowStages = { "<<<<", "<<<", "<<", "<" };
            int iterations = 5;

            for (int i = 0; i < iterations * arrowStages.Length; i++)
            {
                var stage = arrowStages[i % arrowStages.Length];
                contact.IsNewOnline = true;
                // Принудительно обновим UI
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    var index = Contacts.IndexOf(contact);
                    if (index >= 0)
                    {
                        Contacts[index] = contact;
                    }
                });

                await Task.Delay(300);
            }

            contact.IsNewOnline = false;
        }

        public async void ApplySettings()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;

                object showGroups = settings.Values["ShowGroups"];
                _showGroups = showGroups != null && (bool)showGroups;

                object hideOffline = settings.Values["HideOffline"];
                _hideOffline = hideOffline != null && (bool)hideOffline;

                string bgPath = settings.Values["BackgroundPath"] as string;
                object opacityObj = settings.Values["BackgroundOpacity"];
                double opacity = opacityObj != null ? (double)opacityObj : 100.0;

                await ApplyBackground(bgPath, opacity);

                object contactOpacityObj = settings.Values["ContactOpacity"];
                double contactOpacity = contactOpacityObj != null ? (double)contactOpacityObj : 100.0;
                byte alpha = (byte)(contactOpacity / 100.0 * 255);
                ((App)Application.Current).ContactAlpha = alpha;
                foreach (var c in Contacts) c.NotifyBackgroundChanged();

                RefreshView();
            });
            
        }



        private async System.Threading.Tasks.Task ApplyBackground(
            string path, double opacityPercent)
        {
            if (string.IsNullOrEmpty(path))
            {
                // Стандартный фон
                ContactsListView.Background =
                    new Windows.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Colors.Transparent);
                return;
            }

            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using (var stream = await file.OpenReadAsync())
                {
                    var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                    await bitmap.SetSourceAsync(stream);

                    byte alpha = (byte)(opacityPercent / 100.0 * 255);
                    ContactsListView.Background = new Windows.UI.Xaml.Media.ImageBrush
                    {
                        ImageSource = bitmap,
                        Stretch = Windows.UI.Xaml.Media.Stretch.UniformToFill,
                        Opacity = opacityPercent / 100.0
                    };
                }
                Debug.WriteLine("[MainPage] Background applied");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MainPage] ApplyBackground error: " + ex.Message);
            }
        }

        // ── Плоский список (без групп) ───────────────────────────────────────
        // ── Единая точка перерисовки списка: применяет и группировку, и фильтр "скрыть офлайн" ──
        private void RefreshView()
        {
            IEnumerable<Contact> filtered = _hideOffline
                ? Contacts.Where(c => c.StatusIcon != null && !c.StatusIcon.Contains("offline"))
                : Contacts;

            if (_showGroups)
            {
                var grouped = new Dictionary<string, ObservableCollection<Contact>>();

                foreach (var contact in filtered)
                {
                    string groupName = !string.IsNullOrEmpty(contact.Group) ? contact.Group : "Без группы";
                    if (!grouped.ContainsKey(groupName))
                        grouped[groupName] = new ObservableCollection<Contact>();
                    grouped[groupName].Add(contact);
                }

                var cvs = new Windows.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true };
                var groupList = new ObservableCollection<ContactGroup>();
                foreach (var kvp in grouped)
                    groupList.Add(new ContactGroup(kvp.Key, kvp.Value));

                cvs.Source = groupList;
                ContactsListView.ItemsSource = cvs.View;
            }
            else
            {
                ContactsListView.ItemsSource = new ObservableCollection<Contact>(filtered);
            }
        }

        // ── Настройки кнопка ────────────────────────────────────────────────
        // Замени существующий SettingsButton_Click:
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage));
        }

        // ── OnNavigatedTo — добавь вызов ApplySettings после LoadContacts ───
        // В конце блока if (!_initialized):
        // ApplySettings();

        // ── При возврате со SettingsPage ─────────────────────────────────────


        private async void StatusComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var comboBox = sender as ComboBox;
            if (comboBox != null && comboBox.SelectedItem != null)
            {
                string status = comboBox.SelectedItem.ToString();
                if (!string.IsNullOrEmpty(status))
                {
                    try
                    {
                        // Временная реализация без реального изменения статуса
                        // StatusTextBlock.Text = $"Статус: {status}";
                    }
                    catch (Exception ex)
                    {
                        var dialog = new MessageDialog($"Ошибка изменения статуса: {ex.Message}");
                        await dialog.ShowAsync();
                    }
                }
            }
        }
        private async Task ShowErrorDialog(string message)
        {
            var dialog = new MessageDialog(message);
            await dialog.ShowAsync();
        }

        private async void AcInfButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowErrorDialog("В разработке");
        }


        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SearchPage), _oscarProtocol);
        }

        private async void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(InfoPage));
        }

        private async void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Exit();
        }

        private void ContactButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null)
            {
                var contact = button.DataContext as Contact;
                if (contact != null && _oscarProtocol != null)
                {
                    Frame.Navigate(typeof(ChatPage), new Tuple<Contact, OscarProtocol>(contact, _oscarProtocol));
                }
            }
        }




        private void CommandBar_Opened(object sender, object e)
        {
            // Например, логирование
            System.Diagnostics.Debug.WriteLine("AppBar открыт.");
        }

        private void ContactsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var clickedContact = e.ClickedItem as Contact;
            if (clickedContact != null && _oscarProtocol != null)
            {
                Frame.Navigate(typeof(ChatPage), new Tuple<Contact, OscarProtocol>(clickedContact, _oscarProtocol));
            }
        }
    }



}