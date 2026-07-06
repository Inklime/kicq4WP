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
        public static MediaElement SoundPlayer { get; private set; }

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
                // Оборачиваем Frame в Grid чтобы добавить MediaElement
                var rootGrid = new Grid();
                rootFrame = new Frame();
                rootGrid.Children.Add(rootFrame);

                // Добавляем MediaElement в Grid


                Window.Current.Content = rootGrid;
            }
            else
            {
                // Если уже инициализировано — ищем существующий SoundPlayer
                var rootGrid = Window.Current.Content as Grid;
                if (rootGrid != null && SoundPlayer == null)
                {
                    
                }
            }

            if (rootFrame.Content == null)
                rootFrame.Navigate(typeof(LoginPage), e.Arguments);

            Window.Current.Activate();
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            if (ReconnectService != null)
                ReconnectService.Stop();
        }
    }
}