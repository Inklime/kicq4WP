using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace kicq4WP
{
    public sealed partial class SearchPage : Page
    {
        private OscarProtocol _oscar;
        private string _searchMode = "details";
        private ObservableCollection<SearchResult> _results =
            new ObservableCollection<SearchResult>();
        private ushort _searchSeq = 1;

        public SearchPage()
        {
            this.InitializeComponent();
            ResultsList.ItemsSource = _results;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _oscar = e.Parameter as OscarProtocol;
            if (_oscar == null)
                _oscar = ((App)Application.Current).Oscar;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.GoBack();
        }

        // ── Переключение режима поиска ───────────────────────────────
        private void SearchMode_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            _searchMode = btn.Tag as string;

            PanelDetails.Visibility = Visibility.Collapsed;
            PanelUin.Visibility = Visibility.Collapsed;
            PanelEmail.Visibility = Visibility.Collapsed;

            var inactive = Windows.UI.Color.FromArgb(255, 26, 26, 46);
            var active = Windows.UI.Color.FromArgb(255, 0, 120, 215);
            var inactiveFg = Windows.UI.Color.FromArgb(170, 255, 255, 255);
            var activeFg = Windows.UI.Colors.White;

            BtnByDetails.Background = new Windows.UI.Xaml.Media.SolidColorBrush(inactive);
            BtnByUin.Background = new Windows.UI.Xaml.Media.SolidColorBrush(inactive);
            BtnByEmail.Background = new Windows.UI.Xaml.Media.SolidColorBrush(inactive);
            BtnByDetails.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(inactiveFg);
            BtnByUin.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(inactiveFg);
            BtnByEmail.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(inactiveFg);

            switch (_searchMode)
            {
                case "details":
                    PanelDetails.Visibility = Visibility.Visible;
                    BtnByDetails.Background = new Windows.UI.Xaml.Media.SolidColorBrush(active);
                    BtnByDetails.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(activeFg);
                    break;
                case "uin":
                    PanelUin.Visibility = Visibility.Visible;
                    BtnByUin.Background = new Windows.UI.Xaml.Media.SolidColorBrush(active);
                    BtnByUin.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(activeFg);
                    break;
                case "email":
                    PanelEmail.Visibility = Visibility.Visible;
                    BtnByEmail.Background = new Windows.UI.Xaml.Media.SolidColorBrush(active);
                    BtnByEmail.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(activeFg);
                    break;
            }
        }

        // ── Поиск ───────────────────────────────────────────────────
        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_oscar == null)
            {
                StatusText.Text = "Нет подключения";
                return;
            }

            _results.Clear();
            SearchProgress.Visibility = Visibility.Visible;
            StatusText.Text = "";
            BtnSearch.IsEnabled = false;

            try
            {
                List<SearchResult> found;
                switch (_searchMode)
                {
                    case "uin":
                        string uin = (TxtUin.Text ?? "").Trim();
                        if (string.IsNullOrEmpty(uin))
                        {
                            StatusText.Text = "Введите UIN";
                            return;
                        }
                        found = await _oscar.SearchByUinAsync(uin, _searchSeq++);
                        break;

                    case "email":
                        string email = (TxtEmail.Text ?? "").Trim();
                        if (string.IsNullOrEmpty(email))
                        {
                            StatusText.Text = "Введите email";
                            return;
                        }
                        found = await _oscar.SearchByEmailAsync(email, _searchSeq++);
                        break;

                    default: // details
                        string fn = (TxtFirstName.Text ?? "").Trim();
                        string ln = (TxtLastName.Text ?? "").Trim();
                        string nn = (TxtNick.Text ?? "").Trim();
                        if (string.IsNullOrEmpty(fn) && string.IsNullOrEmpty(ln) &&
                            string.IsNullOrEmpty(nn))
                        {
                            StatusText.Text = "Заполните хотя бы одно поле";
                            return;
                        }
                        found = await _oscar.SearchByDetailsAsync(fn, ln, nn, _searchSeq++);
                        break;
                }

                foreach (var r in found)
                    _results.Add(r);

                StatusText.Text = found.Count == 0
                    ? "Ничего не найдено"
                    : "Найдено: " + found.Count;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Search ERROR] " + ex.Message);
                StatusText.Text = "Ошибка поиска: " + ex.Message;
            }
            finally
            {
                SearchProgress.Visibility = Visibility.Collapsed;
                BtnSearch.IsEnabled = true;
            }
        }

        // ── Добавление контакта ─────────────────────────────────────
        private async void AddContact_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null || _oscar == null) return;

            string uin = btn.Tag as string;
            if (string.IsNullOrEmpty(uin)) return;

            btn.IsEnabled = false;
            btn.Content = "✓";

            try
            {
                // Находим результат по UIN
                SearchResult result = null;
                foreach (var r in _results)
                    if (r.Uin == uin) { result = r; break; }

                string displayName = result != null ? result.DisplayName : uin;
                await _oscar.AddContactAsync(uin, displayName);

                StatusText.Text = displayName + " добавлен в контакты";
                Debug.WriteLine("[Search] Added contact: " + uin);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AddContact ERROR] " + ex.Message);
                StatusText.Text = "Ошибка добавления: " + ex.Message;
                btn.IsEnabled = true;
                btn.Content = "+";
            }
        }
    }

    public class SearchResult : INotifyPropertyChanged
    {
        public string Uin { get; set; }
        public string Nick { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public bool IsOnline { get; set; }
        public byte Gender { get; set; }
        public ushort Age { get; set; }

        public string DisplayName
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(FirstName)) parts.Add(FirstName);
                if (!string.IsNullOrEmpty(LastName)) parts.Add(LastName);
                if (parts.Count > 0) return string.Join(" ", parts);
                if (!string.IsNullOrEmpty(Nick)) return Nick;
                return Uin;
            }
        }

        public string UinLine
        {
            get
            {
                string s = "UIN: " + Uin;
                if (!string.IsNullOrEmpty(Nick)) s += "  •  " + Nick;
                if (IsOnline) s += "  🟢";
                return s;
            }
        }

        public string EmailLine
        {
            get { return !string.IsNullOrEmpty(Email) ? Email : ""; }
        }

        public string Initials
        {
            get
            {
                if (!string.IsNullOrEmpty(FirstName))
                    return FirstName.Substring(0, 1).ToUpper();
                if (!string.IsNullOrEmpty(Nick))
                    return Nick.Substring(0, 1).ToUpper();
                return Uin.Length > 0 ? Uin.Substring(0, 1) : "?";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}