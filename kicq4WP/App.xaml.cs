using System;
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