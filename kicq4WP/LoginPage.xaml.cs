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
        public uint _selectedStatusCode = 0x0000;

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
            LoadingOverlay.Visibility = Visibility.Visible;
            Debug.WriteLine($"LoginButton clicked: Login={login}");

            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            {
                await ShowMessageDialog("Заполните все поля: UIN и пароль.");
                LoadingOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            _oscarProtocol = new OscarProtocol(login, password, this.Dispatcher);
            ((App)Application.Current).Oscar = _oscarProtocol;
            _oscarProtocol.StatusUpdater = UpdateStatusText;
            Debug.WriteLine($"OscarProtocol instance created with UIN: {login}");

            try
            {
                // AuthenticateAsync внутри уже делает BOS redirect и подключается к BOS
                bool success = await _oscarProtocol.AuthenticateAsync(statusCode);
                if (!success)
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    await ShowMessageDialog("Ошибка авторизации. Проверьте логин и пароль.");
                    return;
                }
                Debug.WriteLine("Authentication succeeded");

                // Полная инициализация сессии
                await _oscarProtocol.InitializeOscarSessionAsync(statusCode);

                ((App)Application.Current).Oscar = _oscarProtocol;

                // Запускаем ReconnectService
                var reconnect = new ReconnectService(_oscarProtocol.UIN, password, statusCode, this.Dispatcher);
                reconnect.OnDisconnected += () =>
                reconnect.Reconnected += (newOscar) =>
                {
                    ((App)Application.Current).Oscar = newOscar;
                };
                ((App)Application.Current).ReconnectService = reconnect;
                reconnect.Start(_oscarProtocol);

                SettingsManager.SaveSetting("Login", login);
                SettingsManager.SaveSetting("Password", password);

                LoadingOverlay.Visibility = Visibility.Collapsed;
                Frame.Navigate(typeof(MainPage), _oscarProtocol);
            }
            catch (TimeoutException)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                await ShowMessageDialog("Сервер не ответил вовремя. Повторите попытку позже.");
            }
            catch (Exception ex)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                await ShowMessageDialog("Ошибка: " + ex.Message);
            }
        }

        public async void UpdateStatusText(string text)
        {
            // Выполнение кода на UI-потоке
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
             {
                 StatusTextBlock.Text = text;
             });
        }


        private uint GetSelectedStatusCode()
        {
            var selectedItem = StatusComboBox.SelectedItem as ComboBoxItem;
            string statusText = selectedItem?.Content?.ToString() ?? "Онлайн";

            switch (statusText)
            {
                case "Готов поболтать": return 0x000020;
                case "Отошел": return 0x000001;
                case "Недоступен": return 0x000004;
                case "Занят": return 0x000010;
                case "Не беспокоить": return 0x000002;
                case "Дома": return 0x000060;
                case "Работа": return 0x000070;
                case "Кушаю": return 0x000080;
                case "Депрессия": return 0x000090;
                case "Злой": return 0x0000A0;
                case "Невидимый": return 0x000100;
                case "Невидимый для всех": return 0x000100;
                default: return 0x000000;
            }
        }


        private async Task ShowErrorDialog(string message)
        {
            var dialog = new MessageDialog(message);
            await dialog.ShowAsync();
        }
      

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowErrorDialog("В разработке");
        }

        private async void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(InfoPage));
        }

        private async void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Exit();
        }

        private async void RegButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowErrorDialog("К сожалению зарегистрироваться в kicq через приложение пока что невозможно, инструкция регистрации есть на сайте: abrbus.ru/kicq.htm");
        }

        private void CommandBar_Opened(object sender, object e)
        {
            // Например, логирование
            System.Diagnostics.Debug.WriteLine("AppBar открыт.");
        }

        private async Task ShowMessageDialog(string message)
        {
            Debug.WriteLine($"Showing message dialog: {message}");
            var dialog = new MessageDialog(message);
            await dialog.ShowAsync();
        }

       
    }
}