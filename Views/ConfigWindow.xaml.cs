using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using VISOR.Telemetry;
using VISOR.ViewModels;

namespace VISOR.Views
{
    public partial class ConfigWindow : Window
    {
        private readonly SVappsLABSDKWrapper _telemetry;

        public ConfigWindow(SVappsLABSDKWrapper telemetry)
        {
            InitializeComponent();
            _telemetry = telemetry;
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = new MainWindow(_telemetry);

            var mainViewModel = mainWindow.DataContext as MainViewModel;
            if (mainViewModel != null)
            {
                // Auto-detect the active GPU and set the CPU to total usage
                string activeGpuInstance = FindActiveGpuInstance();
                string cpuInstance = "_Total"; // Use the overall CPU usage counter
                mainViewModel.WarningsVM.InitializePerformanceCounters(activeGpuInstance, cpuInstance);
            }

            mainWindow.Show();
            this.Close();
        }

        private void DumpYamlButton_Click(object sender, RoutedEventArgs e) { }

        private string FindActiveGpuInstance()
        {
            try
            {
                var gpuCategory = new PerformanceCounterCategory("GPU Engine");
                var instanceNames = gpuCategory.GetInstanceNames()
                    .Where(inst => inst.EndsWith("engtype_3D"))
                    .ToList();

                if (!instanceNames.Any()) return null;

                var counters = instanceNames.Select(name => new PerformanceCounter("GPU Engine", "Utilization Percentage", name)).ToList();

                foreach (var counter in counters)
                {
                    counter.NextValue();
                }

                Thread.Sleep(500);

                float maxUsage = 0;
                string bestInstance = null;
                for (int i = 0; i < counters.Count; i++)
                {
                    float usage = counters[i].NextValue();
                    if (usage > maxUsage)
                    {
                        maxUsage = usage;
                        bestInstance = instanceNames[i];
                    }
                    counters[i].Dispose();
                }

                return bestInstance;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}