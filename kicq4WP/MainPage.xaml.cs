using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Popups;
using System.Diagnostics;

namespace kicq4WP
{
    public sealed partial class MainPage : Page
    {
        private OscarProtocol _oscarProtocol;
        public ObservableCollection<Contact> Contacts { get; set; }

        public MainPage()
        {
            this.InitializeComponent();
            Contacts = new ObservableCollection<Contact>();
            ContactsListView.ItemsSource = Contacts;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

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

            // 5. Основная логика
            LoadContacts();
            UinTextBlock.Text = _oscarProtocol.UIN;
        }


        private async void LoadContacts()
        {
            try
            {
                Contacts.Clear();
                Debug.WriteLine("Contacts list cleared (stub implementation)");

                // Здесь позже будет реальная загрузка контактов
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in LoadContacts: {ex.Message}");
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
    }
}