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
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
            {
                // TODO: Load state from previously suspended application
            }

            if (rootFrame.Content == null)
            {
                // Изменено с MainPage на LoginPage
                rootFrame.Navigate(typeof(LoginPage), e.Arguments);
            }

            Window.Current.Activate();
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            // TODO: Save application state
            deferral.Complete();
        }
    }
}