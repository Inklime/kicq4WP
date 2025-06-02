using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Popups;

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

            if (e.Parameter is OscarProtocol)
            {
                var protocol = (OscarProtocol)e.Parameter;
                _oscarProtocol = protocol;
                LoadContacts();
            }
        }

        private async void LoadContacts()
        {
            try
            {
                Contacts.Clear();

                // Используем метод GetContactsAsync из OscarProtocol
                var contactUins = await _oscarProtocol.GetContactsAsync();

                foreach (var uin in contactUins)
                {
                    Contacts.Add(new Contact { Uin = uin, Name = uin }); // Можно добавить логику для получения имени контакта
                }
            }
            catch (Exception ex)
            {
                var dialog = new MessageDialog($"Ошибка загрузки контактов: {ex.Message}");
                await dialog.ShowAsync();
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
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