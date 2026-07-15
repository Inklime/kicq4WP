using System;
using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace kicq4WP
{
    sealed partial class App : Application
    {
        public OscarProtocol Oscar { get; set; }
        public ReconnectService ReconnectService { get; set; }
        public byte ContactAlpha { get; set; } = 255;

        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;


        }
        public OscarProtocol CurrentOscarProtocol { get; set; }

        protected override async void OnActivated(IActivatedEventArgs e)
        {
            base.OnActivated(e);

            if (e.Kind == ActivationKind.PickFileContinuation)
            {
                var args = e as FileOpenPickerContinuationEventArgs;
                if (args == null || args.Files == null || args.Files.Count == 0)
                {
                    Window.Current.Activate();
                    return;
                }

                var file = args.Files[0];
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                string target = settings.Values["PickerTarget"] as string ?? "background";
                string fileName = target == "chat_background"
                    ? "chat_background.jpg" : "background.jpg";
                string settingKey = target == "chat_background"
                    ? "ChatBackgroundPath" : "BackgroundPath";

                try
                {
                    var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                    var copy = await file.CopyAsync(folder, fileName,
                        Windows.Storage.NameCollisionOption.ReplaceExisting);
                    settings.Values[settingKey] = copy.Path;
                    Debug.WriteLine("[App] File picked: " + copy.Path);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[App] Pick error: " + ex.Message);
                }

                // Инициализируем Frame если нужно
                var rootGrid = Window.Current.Content as Grid;
                if (rootGrid == null)
                {
                    rootGrid = new Grid();
                    var frame = new Frame();
                    rootGrid.Children.Add(frame);
                    
                    Window.Current.Content = rootGrid;
                    frame.Navigate(typeof(SettingsPage));
                }

                Window.Current.Activate();
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootGrid = Window.Current.Content as Grid;

            if (rootGrid == null)
            {
                rootGrid = new Grid();
                var rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                rootGrid.Children.Add(rootFrame);

                // MediaElement живёт в корневом Grid — не выгружается при навигации
                var soundPlayer = new MediaElement
                {
                    AutoPlay = false,
                    Volume = 1.0,
                    Width = 0,
                    Height = 0
                };
                rootGrid.Children.Add(soundPlayer);
                Window.Current.Content = rootGrid;

                // Инициализируем SoundService ОДИН РАЗ
                SoundService.SetPlayer(soundPlayer, Window.Current.Dispatcher);
            }

            var frame = null as Frame;
            foreach (var child in ((Grid)Window.Current.Content).Children)
            {
                frame = child as Frame;
                if (frame != null) break;
            }

            if (frame != null && frame.Content == null)
                frame.Navigate(typeof(LoginPage), e.Arguments);

            Window.Current.Activate();
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                if (ReconnectService != null)
                    ReconnectService.Stop();

                if (Oscar != null)
                {
                    try { await Oscar.DisconnectAsync(); } catch { }
                }
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}