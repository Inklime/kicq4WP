using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace kicq4WP
{
    public sealed partial class AccountInfoPage : Page
    {
        private OscarProtocol _oscar;

        public AccountInfoPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _oscar = e.Parameter as OscarProtocol ?? ((App)Application.Current).Oscar;

            UinText.Text = _oscar.UIN;
            await LoadUserInfo();
        }

        // Каждый META-запрос требует свой sequence number — используем
        // простой случайный ushort, как это уже делается в ChatPage при
        // запросе анкеты контакта.
        private static ushort NextSeq()
        {
            return (ushort)new Random().Next(1, 60000);
        }

        // ── Загрузка информации ─────────────────────────────────────
        private async Task LoadUserInfo()
        {
            SetStatus(true, "Загружаем профиль...");
            try
            {
                var info = await _oscar.RequestFullUserInfoAsync(_oscar.UIN, NextSeq());
                if (info != null)
                {
                    NickBox.Text = info.Nick ?? "";
                    FirstNameBox.Text = info.FirstName ?? "";
                    LastNameBox.Text = info.LastName ?? "";
                    EmailBox.Text = info.Email ?? "";
                    PhoneBox.Text = info.HomePhone ?? "";
                    CellBox.Text = info.CellPhone ?? "";
                    CityBox.Text = info.City ?? "";
                    AddressBox.Text = info.Address ?? "";
                    SetStatus(false, "");
                }
                else
                {
                    SetStatus(false, "Не удалось загрузить профиль");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AccountInfo] Load error: " + ex.Message);
                SetStatus(false, "Ошибка загрузки");
            }
        }

        // ── Сохранение информации ───────────────────────────────────
        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SetStatus(true, "Сохраняем...");
            SaveButton.IsEnabled = false;

            try
            {
                var info = new UserBasicInfo
                {
                    Nick = NickBox.Text.Trim(),
                    FirstName = FirstNameBox.Text.Trim(),
                    LastName = LastNameBox.Text.Trim(),
                    Email = EmailBox.Text.Trim(),
                    HomePhone = PhoneBox.Text.Trim(),
                    CellPhone = CellBox.Text.Trim(),
                    City = CityBox.Text.Trim(),
                    Address = AddressBox.Text.Trim()
                };

                bool ok = await _oscar.SetBasicUserInfoAsync(info, NextSeq());
                SetStatus(false, ok ? "Сохранено!" : "Ошибка сохранения");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AccountInfo] Save error: " + ex.Message);
                SetStatus(false, "Ошибка: " + ex.Message);
            }
            finally
            {
                SaveButton.IsEnabled = true;
            }
        }

        private void SetStatus(bool loading, string text)
        {
            LoadingRing.IsActive = loading;
            StatusText.Text = text;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.GoBack();
        }
    }

    // Модель данных профиля
    public class UserBasicInfo
    {
        public string Nick { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string HomePhone { get; set; }
        public string HomeFax { get; set; }
        public string CellPhone { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string Address { get; set; }
        public string ZipCode { get; set; }
        public ushort Country { get; set; }
        public byte GmtOffset { get; set; }
        public byte AuthFlag { get; set; }
        public byte WebAware { get; set; }
    }
}