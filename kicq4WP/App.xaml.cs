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
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed; // кстати, раньше это вообще нигде не подключалось

                var rootGrid = new Grid();
                rootGrid.Children.Add(rootFrame);

                // Единственный MediaElement на всё приложение — живёт здесь,
                // а не на конкретной странице, поэтому Frame.Navigate между
                // MainPage/ChatPage/SettingsPage никогда его не выгружает.
                var soundPlayer = new MediaElement
                {
                    AutoPlay = false,
                    Volume = 1.0,
                    Width = 0,
                    Height = 0
                };
                rootGrid.Children.Add(soundPlayer);

                Window.Current.Content = rootGrid;

                SoundService.SetPlayer(soundPlayer, Window.Current.Dispatcher);
            }

            if (rootFrame.Content == null)
                rootFrame.Navigate(typeof(LoginPage), e.Arguments);

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