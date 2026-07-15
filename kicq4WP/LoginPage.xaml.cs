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




        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            uint statusCode = GetSelectedStatusCode();
            string login = LoginTextBox.Text?.Trim();
            string password = PasswordBox.Password?.Trim();

            // Блокируем кнопку через sender
            var btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;
            LoadingOverlay.Visibility = Visibility.Visible;

            Debug.WriteLine("LoginButton clicked: Login=" + login);

            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            {
                await ShowMessageDialog("Заполните все поля: UIN и пароль.");
                LoadingOverlay.Visibility = Visibility.Collapsed;
                if (btn != null) btn.IsEnabled = true;
                return;
            }

            _oscarProtocol = new OscarProtocol(login, password, this.Dispatcher);
            ((App)Application.Current).Oscar = _oscarProtocol;
            _oscarProtocol.StatusUpdater = UpdateStatusText;

            try
            {
                bool success = await _oscarProtocol.AuthenticateAsync(statusCode);
                if (!success)
                {
                    string lastError = _oscarProtocol.LastAuthError ??
                                       "Не удалось подключиться к серверу. Проверьте интернет.";
                    await ShowMessageDialog(lastError);
                    return;
                }

                Debug.WriteLine("Authentication succeeded");

                await _oscarProtocol.InitializeOscarSessionAsync(statusCode);
                ((App)Application.Current).Oscar = _oscarProtocol;

                var reconnect = new ReconnectService(
                    _oscarProtocol.UIN, password, statusCode, this.Dispatcher);

                reconnect.OnDisconnected += () => { };
                reconnect.Reconnected += (newOscar) =>
                {
                    ((App)Application.Current).Oscar = newOscar;
                };

                ((App)Application.Current).ReconnectService = reconnect;
                reconnect.Start(_oscarProtocol);

                SettingsManager.SaveSetting("Login", login);
                SettingsManager.SaveSetting("Password", password);

                Windows.Storage.ApplicationData.Current.LocalSettings
    .Values["LastStatus"] = (long)statusCode;

                Frame.Navigate(typeof(MainPage), _oscarProtocol);
            }
            catch (TimeoutException)
            {
                await ShowMessageDialog("Сервер не ответил вовремя. Повторите попытку позже.");
            }
            catch (Exception ex)
            {
                await ShowMessageDialog("Ошибка: " + ex.Message);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                if (btn != null) btn.IsEnabled = true;
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
            var item = StatusComboBox.SelectedItem as ComboBoxItem;
            if (item == null) return 0x10010000;

            switch (item.Content.ToString())
            {
                case "Онлайн": return 0x10010000;
                case "Готов поболтать": return 0x10010020;
                case "Отошел": return 0x10010001;
                case "Недоступен": return 0x10010004;
                case "Занят": return 0x10010010;
                case "Не беспокоить": return 0x10010002;
                case "Дома": return 0x10015000;
                case "Работа": return 0x10016000;
                case "Кушаю": return 0x10012001;
                case "Депрессия": return 0x10014000;
                case "Злой": return 0x10013000;
                case "Невидимый": return 0x10010100;
                case "Невидимый для всех": return 0x10010100;
                default: return 0x10010000;
            }
        }


        private async Task ShowErrorDialog(string message)
        {
            var dialog = new MessageDialog(message);
            await dialog.ShowAsync();
        }


        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage));
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