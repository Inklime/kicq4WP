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

namespace kicq4WP
{
    public sealed partial class MainPage : Page
    {
        private OscarProtocol _oscarProtocol;
        private Task _;


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

            if (!_initialized)
            {
                var oscarProtocol = e.Parameter as OscarProtocol;
                if (oscarProtocol == null) return;

                _oscarProtocol = oscarProtocol;
                _initialized = true;

                SaveLastUin(_oscarProtocol.UIN);
                UinTextBlock.Text = _oscarProtocol.UIN;

                // Подписываемся на реконнект
                var reconnect = ((App)Application.Current).ReconnectService;
                if (reconnect != null)
                {
                    reconnect.OnDisconnected += OnConnectionLost;
                    reconnect.Reconnected += OnReconnected;
                }

                _oscarProtocol.ContactStatusChanged += OnContactStatusChanged;

                NotificationService.Instance.UnreadChanged += OnUnreadChanged;
                LoadContacts(0x00000000);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            NotificationService.Instance.UnreadChanged -= OnUnreadChanged;

            var reconnect = ((App)Application.Current).ReconnectService;
            if (reconnect != null)
            {
                reconnect.OnDisconnected -= OnConnectionLost;
                reconnect.Reconnected -= OnReconnected;
            }
            _oscarProtocol.ContactStatusChanged -= OnContactStatusChanged;
        }

        private async void OnContactStatusChanged()
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                SortContacts();
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

        private void OnReconnected(OscarProtocol newOscar)
        {
            if (_oscarProtocol != null)
                _oscarProtocol.ContactStatusChanged -= OnContactStatusChanged;

            _oscarProtocol = newOscar;
            _oscarProtocol.ContactStatusChanged += OnContactStatusChanged;
            UinTextBlock.Text = _oscarProtocol.UIN;
            foreach (var contact in Contacts)
                contact.StatusIcon = "/Assets/statuses/offline.png";
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
            var proto = ((App)Application.Current).CurrentOscarProtocol;

            if (proto != null)
            {
                await proto.DisconnectAsync();
                ((App)Application.Current).CurrentOscarProtocol = null;
            }

            Frame.Navigate(typeof(LoginPage));
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

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
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