using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Networking.Sockets;

namespace kicq4WP
{
    /// <summary>
    /// Управляет ControlChannelTrigger для фоновой работы
    /// </summary>
    public class ControlChannelService
    {
        private ControlChannelTrigger _trigger;
        private BackgroundTaskRegistration _taskReg;
        private const string TaskName = "kicqPushTask";
        private const string TriggerId = "kicqChannel";
        private const string TaskEntry = "kicq4WP.BackgroundTask";

        // Singleton
        private static ControlChannelService _instance;
        public static ControlChannelService Instance
        {
            get
            {
                if (_instance == null) _instance = new ControlChannelService();
                return _instance;
            }
        }

        private ControlChannelService() { }

        /// <summary>
        /// Инициализация CCT — вызывать ДО ConnectAsync сокета
        /// </summary>
        public async Task<ControlChannelTrigger> InitializeAsync()
        {
            try
            {
                // Запрашиваем доступ к фоновым задачам
                var status = await BackgroundExecutionManager.RequestAccessAsync();
                if (status == BackgroundAccessStatus.Denied)
                {
                    Debug.WriteLine("[CCT] Background access denied");
                    return null;
                }

                // Удаляем старую регистрацию
                UnregisterTask();

                // Создаём триггер
                // ServerKeepAliveInterval = 30 минут
                _trigger = new ControlChannelTrigger(
                    TriggerId,
                    30,
                    ControlChannelTriggerResourceType.RequestSoftwareSlot);

                Debug.WriteLine("[CCT] Trigger created: " + TriggerId);

                // Регистрируем фоновую задачу
                var builder = new BackgroundTaskBuilder();
                builder.Name = TaskName;
                builder.TaskEntryPoint = TaskEntry;
                builder.SetTrigger(_trigger.PushNotificationTrigger);
                _taskReg = builder.Register();

                Debug.WriteLine("[CCT] Background task registered: " + TaskName);
                return _trigger;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CCT] InitializeAsync error: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Привязываем StreamSocket к триггеру после создания но ДО подключения
        /// </summary>
        public bool AssignSocket(StreamSocket socket)
        {
            if (_trigger == null || socket == null) return false;
            try
            {
                _trigger.UsingTransport(socket);
                Debug.WriteLine("[CCT] Socket assigned to trigger");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CCT] AssignSocket error: " + ex.Message);
                return false;
            }
        }




        /// <summary>
        /// Уведомляем систему что получили данные (вызывать после чтения пакета)
        /// </summary>
        public void NotifyDataReceived()
        {
            try
            {
                _trigger?.FlushTransport();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CCT] NotifyDataReceived error: " + ex.Message);
            }
        }

        public void Cleanup()
        {
            UnregisterTask();
            try { _trigger?.Dispose(); } catch { }
            _trigger = null;
        }

        private void UnregisterTask()
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                if (task.Value.Name == TaskName)
                {
                    task.Value.Unregister(true);
                    Debug.WriteLine("[CCT] Unregistered old task");
                }
            }
        }
    }
}