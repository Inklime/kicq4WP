using System;
using System.Diagnostics;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace kicq4WP
{
    public static class SoundService
    {
        private static CoreDispatcher _dispatcher;
        private static MediaElement _player;

        public static void Init(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public static void SetPlayer(MediaElement player, CoreDispatcher dispatcher)
        {
            _player = player;
            _dispatcher = dispatcher;
            Debug.WriteLine("[Sound] Player registered: " + (player != null));
        }

        public static async void PlayMessage()
            => await Play("ms-appx:///Assets/Sounds/message.wav");

        public static async void PlayOnline()
            => await Play("ms-appx:///Assets/Sounds/online.wav");

        public static async void PlayOffline()
            => await Play("ms-appx:///Assets/Sounds/offline.wav");

        public static async void PlayError()
            => await Play("ms-appx:///Assets/Sounds/error.wav");

        public static async void PlayNotification()
            => await Play("ms-appx:///Assets/Sounds/notification.wav");

        private static async System.Threading.Tasks.Task Play(string uri)
        {
            try
            {
                if (_dispatcher == null || _player == null)
                {
                    Debug.WriteLine("[Sound] Not ready: dispatcher=" +
                        (_dispatcher != null) + " player=" + (_player != null));
                    return;
                }

                var file = await Windows.Storage.StorageFile
                    .GetFileFromApplicationUriAsync(new Uri(uri));
                var stream = await file.OpenAsync(
                    Windows.Storage.FileAccessMode.Read);
                string contentType = file.ContentType;

                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        _player.Stop();
                        _player.SetSource(stream, contentType);
                        _player.Play();
                        Debug.WriteLine("[Sound] Playing: " + uri);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[Sound] Play error: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Sound] Error: " + ex.Message);
            }
        }
    }

}
