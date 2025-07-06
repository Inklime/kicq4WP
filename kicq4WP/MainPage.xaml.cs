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

namespace kicq4WP
{
    public sealed partial class MainPage : Page
    {
        private OscarProtocol _oscarProtocol;
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

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            uint statusCode = 0x0000;
            // 1. Приведение типа с проверкой
            var oscarProtocol = e.Parameter as OscarProtocol;

            // 2. Проверка на null перед использованием
            if (oscarProtocol == null)
            {
                Debug.WriteLine("Error: Navigation parameter is not OscarProtocol");
                return;
            }

            // 3. Присваивание полю класса только после проверки
            _oscarProtocol = oscarProtocol;

            Debug.WriteLine($"Setting login: {_oscarProtocol.UIN}");
            SaveLastUin(_oscarProtocol.UIN);
            // 5. Основная логика
            LoadContacts(statusCode);
            UinTextBlock.Text = _oscarProtocol.UIN;
        }


        private async void LoadContacts(uint statusCode)
        {
            try
            {
                Contacts.Clear();
                var parsedContacts = await _oscarProtocol.GetContactsAsync(statusCode);
                foreach (var contact in parsedContacts)
                {
                    Contacts.Add(contact);
                }
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
            await ShowErrorDialog("Эта кнопка пока что ничего не делает...");
        }

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowErrorDialog("Эта кнопка пока что ничего не делает...");
        }

        private async void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(InfoPage));
        }

        private async void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowErrorDialog("Эта кнопка пока что ничего не делает...");
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

    public class Contact
    {
        public string Uin { get; set; }
        public string Name { get; set; }
        public string StatusIcon { get; set; }
        public string XtrazIcon { get; set; }
        public bool IsNewOnline { get; set; }
    }

}