using System;
using System.Diagnostics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.Phone.UI.Input;
using Windows.ApplicationModel.Activation;
using Windows.System.Profile;


namespace kicq4WP
{
    public sealed partial class SettingsPage : Page
    {
        private void PickBackground_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation =
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            Windows.Storage.ApplicationData.Current.LocalSettings
                .Values["PickerTarget"] = "background";
            picker.PickSingleFileAndContinue();
        }

        private void PickChatBackground_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation =
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            Windows.Storage.ApplicationData.Current.LocalSettings
                .Values["PickerTarget"] = "chat_background";
            picker.PickSingleFileAndContinue();
        }
    public SettingsPage()
        {
            this.InitializeComponent();
            HardwareButtons.BackPressed += HardwareButtons_BackPressed;
        }



        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadSettings();

            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            string bgPath = settings.Values["BackgroundPath"] as string;
            if (!string.IsNullOrEmpty(bgPath))
                UpdatePreview(bgPath);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
            base.OnNavigatedFrom(e);
        }

        private void LoadSettings()
        {
            var settings = ApplicationData.Current.LocalSettings;

            // Загружаем показ групп
            object showGroups = settings.Values["ShowGroups"];
            ShowGroupsToggle.IsOn = showGroups != null && (bool)showGroups;

            object hideOffline = settings.Values["HideOffline"];
            HideOfflineToggle.IsOn = hideOffline != null && (bool)hideOffline;

            object typingEnabled = settings.Values["TypingNotifications"];
            // По умолчанию включено
            TypingNotificationsToggle.IsOn = typingEnabled == null || (bool)typingEnabled;

            var isW10 = IsW10M();

            if (!isW10)
                BackgroundModeBorder.Visibility = Visibility.Collapsed;
                BackgroundModeUnavailable.Visibility = Visibility.Collapsed;

            // работа в фоне
            object bgMode = settings.Values["BackgroundMode"];
            BackgroundModeToggle.IsOn = bgMode == null || (bool)bgMode;

            // Загружаем прозрачность
            object opacity = settings.Values["BackgroundOpacity"];
            double opacityVal = opacity != null ? (double)opacity : 100.0;
            BackgroundOpacitySlider.Value = opacityVal;
            OpacityValueText.Text = ((int)opacityVal) + "%";

            // Показываем превью фона
            string bgPath = settings.Values["BackgroundPath"] as string;
            UpdatePreview(bgPath);
            object contactOpacity = settings.Values["ContactOpacity"];

            double contactOpacityVal = contactOpacity != null ? (double)contactOpacity : 100.0;
            ContactOpacitySlider.Value = contactOpacityVal;
            ContactOpacityText.Text = ((int)contactOpacityVal) + "%";
        }

        private void HardwareButtons_BackPressed(object sender, BackPressedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        public static bool IsW10M()
        {
            try
            {
                var type = Type.GetType("Windows.System.Profile.AnalyticsInfo, Windows, ContentType=WindowsRuntime");
                return type != null;
            }
            catch
            {
                return false;
            }
        }
        

        private async void UpdatePreview(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                BackgroundPreview.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 13, 17, 23));
                BackgroundPreviewText.Visibility = Visibility.Visible;
                BackgroundPreviewText.Text = "Фон не выбран";
                return;
            }

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using (var stream = await file.OpenReadAsync())
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    BackgroundPreview.Background = new ImageBrush
                    {
                        ImageSource = bitmap,
                        Stretch = Stretch.UniformToFill
                    };
                    BackgroundPreviewText.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                BackgroundPreview.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 13, 17, 23));
                BackgroundPreviewText.Visibility = Visibility.Visible;
                BackgroundPreviewText.Text = "Фото недоступно";
            }
        }

        private void ContactOpacitySlider_ValueChanged(object sender,
    Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (ContactOpacityText == null) return;
            int val = (int)e.NewValue;
            ContactOpacityText.Text = val + "%";
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["ContactOpacity"] = (double)val;
        }



        private async void ClearChatBackground_Click(object sender, RoutedEventArgs e)
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["ChatBackgroundPath"] = null;
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                                     .GetFileAsync("chat_background.jpg");
                await file.DeleteAsync();
            }
            catch { }
        }

        private void TypingNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["TypingNotifications"] = TypingNotificationsToggle.IsOn;
            Debug.WriteLine("[Settings] TypingNotifications=" + TypingNotificationsToggle.IsOn);
        }


        // ── Показ групп ─────────────────────────────────────────────
        private void ShowGroupsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["ShowGroups"] = ShowGroupsToggle.IsOn;
            Debug.WriteLine("[Settings] ShowGroups=" + ShowGroupsToggle.IsOn);
        }

        private void BackgroundModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            settings.Values["BackgroundMode"] = BackgroundModeToggle.IsOn;

            if (!BackgroundModeToggle.IsOn)
                ControlChannelService.Instance.Cleanup();

            Debug.WriteLine("[Settings] BackgroundMode=" + BackgroundModeToggle.IsOn);
        }

        // ── Очистка фона ────────────────────────────────────────────
        private async void ClearBackground_Click(object sender, RoutedEventArgs e)
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["BackgroundPath"] = null;
            UpdatePreview(null);

            // Удаляем файл
            try
            {
                StorageFolder localFolder = ApplicationData.Current.LocalFolder;
                var file = await localFolder.GetFileAsync("background.jpg");
                await file.DeleteAsync();
            }
            catch { }

            Debug.WriteLine("[Settings] Background cleared");
        }

        // ── Прозрачность ────────────────────────────────────────────
        private void OpacitySlider_ValueChanged(object sender,
            Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (OpacityValueText == null) return;

            int val = (int)e.NewValue;
            OpacityValueText.Text = val + "%";

            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["BackgroundOpacity"] = (double)val;
        }

        private void HideOfflineToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["HideOffline"] = HideOfflineToggle.IsOn;

            Debug.WriteLine("[Settings] HideOffline=" + HideOfflineToggle.IsOn);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.GoBack();
        }
    }
}