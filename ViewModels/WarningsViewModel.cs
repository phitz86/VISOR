using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace VISOR.ViewModels
{
    public class WarningsViewModel : INotifyPropertyChanged, IDisposable
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // --- Private Fields ---
        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _gpuCounter;
        private readonly DispatcherTimer _performanceTimer;
        private DateTime _highGpuStartTime = DateTime.MinValue;
        private DateTime _highCpuStartTime = DateTime.MinValue;
        private int _incidentCount = 0;
        private string _incidentDisplay = "0x";
        private Brush _incidentColor = Brushes.White;
        private bool _isCpuWarningVisible = false;
        private Brush _cpuColor = Brushes.White;
        private string _cpuUsageDisplay = "--%";
        private bool _isGpuWarningVisible = false;
        private Brush _gpuColor = Brushes.White;
        private string _gpuUsageDisplay = "--%";

        // --- Public Properties ---
        public string IncidentDisplay { get => _incidentDisplay; private set { _incidentDisplay = value; OnPropertyChanged(); } }
        public Brush IncidentColor { get => _incidentColor; private set { _incidentColor = value; OnPropertyChanged(); } }
        public bool IsCpuWarningVisible { get => _isCpuWarningVisible; private set { _isCpuWarningVisible = value; OnPropertyChanged(); } }
        public Brush CpuColor { get => _cpuColor; private set { _cpuColor = value; OnPropertyChanged(); } }
        public string CpuUsageDisplay { get => _cpuUsageDisplay; private set { _cpuUsageDisplay = value; OnPropertyChanged(); } }
        public bool IsGpuWarningVisible { get => _isGpuWarningVisible; private set { _isGpuWarningVisible = value; OnPropertyChanged(); } }
        public Brush GpuColor { get => _gpuColor; private set { _gpuColor = value; OnPropertyChanged(); } }
        public string GpuUsageDisplay { get => _gpuUsageDisplay; private set { _gpuUsageDisplay = value; OnPropertyChanged(); } }

        public WarningsViewModel()
        {
            _performanceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _performanceTimer.Tick += OnPerformanceTick;
            _performanceTimer.Start();
        }

        public void InitializePerformanceCounters(string? gpuInstanceName, string? cpuInstanceName)
        {
            if (!string.IsNullOrEmpty(gpuInstanceName))
            {
                try
                {
                    _gpuCounter = new PerformanceCounter("GPU Engine", "Utilization Percentage", gpuInstanceName);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to create GPU counter: {ex.Message}");
                    _gpuCounter = null;
                }
            }

            if (!string.IsNullOrEmpty(cpuInstanceName))
            {
                try
                {
                    _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", cpuInstanceName);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to create CPU counter: {ex.Message}");
                    _cpuCounter = null;
                }
            }
        }

        public void UpdateIncidentCount(int newCount, int incidentLimit)
        {
            if (newCount != _incidentCount)
            {
                _incidentCount = newCount;
                IncidentDisplay = $"{newCount}x";
                IncidentColor = GetIncidentColor(newCount);
            }
        }

        private void OnPerformanceTick(object sender, EventArgs e)
        {
            // --- CPU Monitoring ---
            float cpuUsage = _cpuCounter?.NextValue() ?? 0f;
            CpuUsageDisplay = $"{cpuUsage:F0}%";
            UpdateWarningState(cpuUsage, ref _highCpuStartTime, out bool cpuVisible, out Brush cpuBrush);
            IsCpuWarningVisible = cpuVisible;
            CpuColor = cpuBrush;

            // --- GPU Monitoring ---
            float gpuUsage = _gpuCounter?.NextValue() ?? 0f;
            GpuUsageDisplay = $"{gpuUsage:F0}%";
            UpdateWarningState(gpuUsage, ref _highGpuStartTime, out bool gpuVisible, out Brush gpuBrush);
            IsGpuWarningVisible = gpuVisible;
            GpuColor = gpuBrush;
        }

        private void UpdateWarningState(float usage, ref DateTime highUsageStartTime, out bool isVisible, out Brush color)
        {
            const float warningThreshold = 85.0f;
            const float criticalThreshold = 95.0f;

            if (usage < warningThreshold)
            {
                isVisible = false;
                color = Brushes.White;
                highUsageStartTime = DateTime.MinValue;
            }
            else
            {
                isVisible = true;
                color = (usage >= criticalThreshold) ? Brushes.Red : Brushes.Yellow;
            }
        }

        private Brush GetIncidentColor(int count)
        {
            if (count == 0) return Brushes.White;
            if (count <= 4) return Brushes.Yellow;
            if (count <= 8) return Brushes.Orange;
            return Brushes.Red;
        }

        public void Reset()
        {
            _incidentCount = 0;
            IncidentDisplay = "0x";
            IncidentColor = Brushes.White;
            IsCpuWarningVisible = false;
            CpuColor = Brushes.White;
            CpuUsageDisplay = "--%";
            IsGpuWarningVisible = false;
            GpuColor = Brushes.White;
            GpuUsageDisplay = "--%";
        }

        public void Dispose()
        {
            _performanceTimer?.Stop();
            _gpuCounter?.Dispose();
            _cpuCounter?.Dispose();
        }
    }
}