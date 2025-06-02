using kicq4WP;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace kicq4WP
{
    public sealed partial class LoginPage : Page
    {
        private OscarProtocol _oscarProtocol;
        public uint _selectedStatusCode = 0x00000000;

        public LoginPage()
        {
            this.InitializeComponent();
            Debug.WriteLine("LoginPage initialized");

            // Загружаем сохраненные логин, пароль и никнейм
            string savedLogin = SettingsManager.LoadSetting("Login");
            string savedPassword = SettingsManager.LoadSetting("Password");
            string savedNickname = SettingsManager.LoadSetting("Nickname");

            Debug.WriteLine($"Loaded saved data - Login: {savedLogin}, Nickname: {savedNickname}");

            LoginTextBox.Text = savedLogin;
            PasswordBox.Password = savedPassword;
            NicknameTextBox.Text = savedNickname;
        }

        private async void OnlOpenButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(MainPage));
        }
        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            uint statusCode = GetSelectedStatusCode();
            string login = LoginTextBox.Text?.Trim();
            string password = PasswordBox.Password?.Trim();
            string nickname = NicknameTextBox.Text?.Trim();

            Debug.WriteLine($"LoginButton clicked: Login={login}, Nickname={nickname}");

            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(nickname))
            {
                await ShowMessageDialog("Заполните все поля: Логин, Пароль и Никнейм.");
                return;
            }

            _oscarProtocol = new OscarProtocol(login, password); // <-- ВАЖНО!
            Debug.WriteLine($"OscarProtocol instance created with UIN: {login}");

            bool success = await _oscarProtocol.AuthenticateAsync(nickname, statusCode);
            if (success)
            {
                Debug.WriteLine("Authentication succeeded");

                SettingsManager.SaveSetting("Login", login);
                SettingsManager.SaveSetting("Password", password);
                SettingsManager.SaveSetting("Nickname", nickname);

                Frame.Navigate(typeof(MainPage));
            }

        }

        private uint GetSelectedStatusCode()
        {
            var selectedItem = StatusComboBox.SelectedItem as ComboBoxItem;
            string statusText = selectedItem?.Content?.ToString() ?? "Онлайн";

            switch (statusText)
            {
                case "Готов поболтать": return 0x00000010;
                case "Отошел": return 0x00000020;
                case "Недоступен": return 0x00000030;
                case "Занят": return 0x00000040;
                case "Не беспокоить": return 0x00000050;
                case "Дома": return 0x00000060;
                case "Работа": return 0x00000070;
                case "Кушаю": return 0x00000080;
                case "Депрессия": return 0x00000090;
                case "Злой": return 0x000000A0;
                case "Невидимый": return 0x00000100;
                case "Невидимый для всех": return 0x00010100;
                default: return 0x00000000;
            }
        }




        private async Task ShowMessageDialog(string message)
        {
            Debug.WriteLine($"Showing message dialog: {message}");
            var dialog = new MessageDialog(message);
            await dialog.ShowAsync();
        }
    }
}