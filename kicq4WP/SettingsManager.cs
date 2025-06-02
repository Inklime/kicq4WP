using Windows.Storage;

namespace kicq4WP
{
    public static class SettingsManager
    {
        public static void SaveSetting(string key, string value)
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }

        public static string LoadSetting(string key)
        {
            object value;
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out value))
            {
                return value as string;
            }
            return string.Empty;
        }
    }
}
