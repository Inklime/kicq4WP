using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Popups;
using Windows.System;
using Windows.UI.Xaml.Input;

namespace kicq4WP
{
    public sealed partial class ChatPage : Page
    {
        private Contact _contact;
        private OscarProtocol _oscarProtocol;
        public ObservableCollection<string> Messages { get; } = new ObservableCollection<string>();

        public ChatPage()
        {
            this.InitializeComponent();
            MessagesList.ItemsSource = Messages;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is Tuple<Contact, OscarProtocol>)
            {
                var parameters = (Tuple<Contact, OscarProtocol>)e.Parameter;
                _contact = parameters.Item1;
                _oscarProtocol = parameters.Item2;

                if (_contact != null)
                {
                    ContactNameTextBlock.Text = $"Чат с {_contact.Name}";
                    LoadMessageHistory();
                }
            }
        }

        private async void LoadMessageHistory()
        {
            try
            {
                // Здесь можно добавить загрузку истории сообщений
                // var history = await _oscarProtocol.GetMessageHistoryAsync(_contact.Uin);
                // foreach (var msg in history) Messages.Add(msg);
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"Ошибка загрузки истории: {ex.Message}");
            }
        }

        private async void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }

        private async void MessageTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter && !string.IsNullOrWhiteSpace(MessageTextBox.Text))
            {
                await SendMessageAsync();
                e.Handled = true;
            }
        }

        private async Task SendMessageAsync()
        {
            string message = MessageTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(message))
            {
                await ShowErrorDialog("Сообщение не может быть пустым");
                return;
            }

            if (_contact == null || string.IsNullOrEmpty(_contact.Uin))
            {
                await ShowErrorDialog("Контакт не выбран");
                return;
            }

            try
            {
                // Временная реализация без реальной отправки
                Messages.Add($"Я: {message}");
                MessageTextBox.Text = string.Empty;

                if (Messages.Count > 0)
                {
                    MessagesList.ScrollIntoView(Messages[Messages.Count - 1]);
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"Ошибка отправки: {ex.Message}");
            }
        }
        private async void SmileButton_Click(object sender, RoutedEventArgs e)
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

        private async Task ShowErrorDialog(string message)
        {
            var dialog = new MessageDialog(message);
            await dialog.ShowAsync();
        }
    }
}