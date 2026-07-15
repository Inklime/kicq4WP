using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Core;

namespace kicq4WP
{
    /// <summary>
    /// Следит за соединением и переподключается при обрыве
    /// </summary>
    public class ReconnectService
    {
        private OscarProtocol _oscar;
        private string _uin;
        private string _password;
        private uint _statusCode;
        private CoreDispatcher _dispatcher;
        private CancellationTokenSource _cts;
        private bool _running = false;
        public event Action OnDisconnected;
        public event Action OnReconnecting;
        private volatile bool _kicked = false;
        public event Action<string> KickedOut;
        private Windows.Networking.Connectivity.NetworkStatusChangedEventHandler _networkHandler;

        // Событие — подписываемся в App чтобы обновить UI после реконнекта
        public event Action<OscarProtocol> Reconnected;
        public event Action Disconnected;

        public ReconnectService(string uin, string password, uint statusCode, CoreDispatcher dispatcher)
        {
            _uin = uin;
            _password = password;
            _statusCode = statusCode;
            _dispatcher = dispatcher;
        }

        public void Start(OscarProtocol oscar)
        {
            _oscar = oscar;
            _running = true;
            _cts = new CancellationTokenSource();

            // Подписываемся на изменения сети
            _networkHandler = new Windows.Networking.Connectivity.NetworkStatusChangedEventHandler(
                OnNetworkStatusChanged);
            Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += _networkHandler;

            Task.Run(() => MonitorLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _running = false;
            if (_networkHandler != null)
            {
                Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= _networkHandler;
                _networkHandler = null;
            }
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
            }
        }

        private async void OnNetworkStatusChanged(object sender)
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation
                .GetInternetConnectionProfile();

            if (profile == null)
            {
                Debug.WriteLine("[Reconnect] Network lost");
                return;
            }

            var level = profile.GetNetworkConnectivityLevel();
            if (level == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess)
            {
                Debug.WriteLine("[Reconnect] Network restored — forcing reconnect");

                // Отменяем текущий цикл и запускаем реконнект
                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts = new CancellationTokenSource();
                }

                if (_oscar != null)
                {
                    try { await _oscar.DisconnectAsync(); } catch { }
                }

                // Небольшая пауза чтобы сеть стабилизировалась
                await Task.Delay(2000);

                // Запускаем переподключение
                Task.Run(() => MonitorLoopAsync(_cts.Token));
            }
        }

        private void OnKickedByServer(string reason)
        {
            _kicked = true;
            KickedOut?.Invoke(reason);
        }

        private async Task MonitorLoopAsync(CancellationToken token)
        {
            Debug.WriteLine("[Reconnect] Monitor started");

            while (_running && !token.IsCancellationRequested)
            {
                try
                {
                    await _oscar.ReceiveServerSnacsAsync();
                    break; // нормальный выход = намеренная остановка
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                catch (Exception ex)
                {
                    Debug.WriteLine("[Reconnect] Disconnected: " + ex.Message);
                }

                if (_kicked)
                {
                    Debug.WriteLine("[Reconnect] Не переподключаемся — сервер разорвал сессию (другой вход)");
                    _running = false;
                    break;
                }

                if (!_running || token.IsCancellationRequested) break;

                // Уведомляем UI — все офлайн
                if (_dispatcher != null)
                    await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (OnDisconnected != null) OnDisconnected();
                    });

                // Переподключаемся с экспоненциальным backoff
                int attempt = 0;
                while (_running && !token.IsCancellationRequested)
                {
                    attempt++;
                    int delay = Math.Min(30000, attempt * 5000);
                    Debug.WriteLine("[Reconnect] Retry in " + delay + "ms");
                    await Task.Delay(delay);
                    if (token.IsCancellationRequested) break;

                    bool ok = await TryReconnectAsync(token);
                    if (ok) break;
                }
            }

            Debug.WriteLine("[Reconnect] Monitor stopped");
        }

        public async Task ForceReconnectAsync()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    bool ok = await _oscar.AuthenticateAndInitializeAsync(_uin, _statusCode);
                    if (ok) return;
                }
                catch { }
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)));
            }
            // Только тут — на LoginPage
        }

        private async Task<bool> TryReconnectAsync(CancellationToken token)
        {
            try
            {
                Debug.WriteLine("[Reconnect] Connecting...");
                var newOscar = new OscarProtocol(_uin, _password, _dispatcher);

                bool auth = await newOscar.AuthenticateAsync(_statusCode);
                if (!auth)
                {
                    Debug.WriteLine("[Reconnect] Auth failed");
                    return false;
                }

                await newOscar.InitializeOscarSessionAsync(_statusCode);

                // Отписываем старые события
                if (_oscar != null)
                    _oscar.IncomingMessage -= OnIncomingMessage;

                _oscar = newOscar;
                _oscar.IncomingMessage += OnIncomingMessage;

                if (_dispatcher != null)
                    await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        ((App)Windows.UI.Xaml.Application.Current).Oscar = _oscar;
                        if (Reconnected != null) Reconnected(_oscar);
                    });

                Debug.WriteLine("[Reconnect] Success!");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Reconnect] Failed: " + ex.Message);
                return false;
            }
        }

        private async void OnIncomingMessage(string senderUin, string text)
        {
            await NotificationService.Instance.OnMessageReceived(
                senderUin, senderUin, text, _dispatcher);
        }
    }
}