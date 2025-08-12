using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VISOR.ViewModels;
using VISOR.Telemetry;

namespace VISOR.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private SVappsLABSDKWrapper _sdk;

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // Window chrome / translucency
            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            Background = new SolidColorBrush(Color.FromArgb(160, 32, 32, 32));
            Topmost = true;

            Loaded += MainWindow_Loaded;

            // Position near center with offset
            double centerX = SystemParameters.PrimaryScreenWidth / 2;
            double centerY = SystemParameters.PrimaryScreenHeight / 2;
            double offsetX = 900;
            double offsetY = 400;
            Left = centerX + offsetX - (Width / 2);
            Top = centerY + offsetY - (Height / 2);

            // Drag anywhere
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // If your XAML has x:Name="StatusText", this will show progress without needing a VM prop
                if (StatusText != null)
                {
                    StatusText.Text = "Connecting...";
                    StatusText.Visibility = Visibility.Visible;
                }

                _sdk = new SVappsLABSDKWrapper();

                // Telemetry snapshots → ViewModel
                _sdk.SnapshotAvailable += snapshot =>
                {
                    // Keep UI updates on the dispatcher
                    Dispatcher.Invoke(() => _viewModel.UpdateFromTelemetry(snapshot));
                };

                // Session YAML dump for baselines
                _sdk.SessionYamlAvailable += yaml =>
                {
                    try
                    {
                        var dir = Path.Combine(AppContext.BaseDirectory, "telemetry_baselines", "SVapps");
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "session.yaml"), yaml);
                    }
                    catch
                    {
                        // Non-fatal during smoke test
                    }
                };

                _sdk.Start();
                await WaitForFirstSnapshotAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"SDK failed to start: {ex.Message}");
                Close();
            }
        }

        private async Task WaitForFirstSnapshotAsync()
        {
            int retries = 0;
            bool gotData = false;

            void Handler(SVappsLABSnapshot s) => gotData = true;
            _sdk.SnapshotAvailable += Handler;

            while (!gotData && retries < 360)
            {
                if (StatusText != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "Waiting for iRacing telemetry...";
                        StatusText.Visibility = Visibility.Visible;
                    });
                }

                await Task.Delay(500);
                retries++;
            }

            _sdk.SnapshotAvailable -= Handler;

            if (!gotData)
            {
                if (StatusText != null)
                {
                    Dispatcher.Invoke(() => { StatusText.Text = "No telemetry connection."; });
                }
                return;
            }

            if (StatusText != null)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Connected.";
                    StatusText.Visibility = Visibility.Collapsed;
                });
            }
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}