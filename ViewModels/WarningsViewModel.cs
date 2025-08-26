using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace VISOR.ViewModels
{
    public class WarningsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Incident Points Properties
        private int _incidentCount = 0;
        private string _incidentDisplay = "0x";
        private Brush _incidentColor = Brushes.White;
        private int _incidentLimit = 0;

        public int IncidentCount
        {
            get => _incidentCount;
            private set { _incidentCount = value; OnPropertyChanged(); }
        }

        public string IncidentDisplay
        {
            get => _incidentDisplay;
            private set { _incidentDisplay = value; OnPropertyChanged(); }
        }

        public Brush IncidentColor
        {
            get => _incidentColor;
            private set { _incidentColor = value; OnPropertyChanged(); }
        }

        // Connection Health Properties
        private Brush _connectionColor = Brushes.White;
        public Brush ConnectionColor
        {
            get => _connectionColor;
            private set { _connectionColor = value; OnPropertyChanged(); }
        }

        // FPS Properties (placeholder for now)
        private Brush _fpsColor = Brushes.White;
        public Brush FpsColor
        {
            get => _fpsColor;
            private set { _fpsColor = value; OnPropertyChanged(); }
        }

        // Connection Health Monitoring - Immediate (10 second window)
        private readonly Queue<DateTime> _telemetryTimestamps = new();
        private const int HealthWindowSeconds = 10;
        private const double ExpectedTelemetryInterval = 16.7; // 60Hz = 16.7ms
        private const double WarningThreshold = 0.15; // 15% of updates can be late
        private const double CriticalThreshold = 0.30; // 30% of updates late = red

        // Connection Health Monitoring - Session-level persistence
        private readonly Queue<DateTime> _warningEvents = new();
        private readonly Queue<DateTime> _criticalEvents = new();
        private const int SessionWindowMinutes = 5; // Track warnings over 5 minutes
        private const int WarningEventThreshold = 3; // 3 warnings in 5 minutes = persistent yellow
        private const int CriticalEventThreshold = 2; // 2 critical events in 5 minutes = persistent red
        private const int ClearanceMinutes = 2; // No warnings for 2 minutes = clear persistent state

        private DateTime _lastWarningTime = DateTime.MinValue;
        private DateTime _lastCriticalTime = DateTime.MinValue;

        // Flashing animation timer and states
        private DispatcherTimer _flashTimer;
        private int _flashCount = 0;
        private const int MaxFlashes = 4; // Flash 4 times for increment
        private bool _isFlashing = false;
        private bool _isProximityFlashing = false; // Persistent red flashing near limit

        public WarningsViewModel()
        {
            _flashTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(0.5) // 1Hz = 0.5s on, 0.5s off
            };
            _flashTimer.Tick += OnFlashTick;
        }

        /// <summary>
        /// Update incident count and trigger appropriate flashing
        /// </summary>
        public void UpdateIncidentCount(int newCount, int incidentLimit)
        {
            _incidentLimit = incidentLimit;

            // Check for proximity to limit (within 4x)
            bool nearLimit = _incidentLimit > 0 && (newCount + 4 >= _incidentLimit);

            if (newCount > _incidentCount && !_isFlashing)
            {
                // Incident count increased - start increment flashing (yellow)
                IncidentCount = newCount;
                IncidentDisplay = $"{newCount}x";
                StartIncrementFlashing();
            }
            else if (newCount != _incidentCount)
            {
                // Count changed but not flashing (could be reset or initial load)
                IncidentCount = newCount;
                IncidentDisplay = $"{newCount}x";

                // Set color based on incident count and proximity
                UpdateIncidentColor(nearLimit);
            }
            else
            {
                // Count unchanged, but check if proximity status changed
                UpdateIncidentColor(nearLimit);
            }

            // Handle proximity flashing
            if (nearLimit && !_isProximityFlashing)
            {
                StartProximityFlashing();
            }
            else if (!nearLimit && _isProximityFlashing)
            {
                StopProximityFlashing();
            }
        }

        private void UpdateIncidentColor(bool nearLimit)
        {
            if (_isFlashing)
            {
                // Don't change color during increment flash
                return;
            }

            if (nearLimit)
            {
                // Near limit - use red (will flash if proximity flashing is active)
                IncidentColor = Brushes.Red;
            }
            else
            {
                // Normal color progression
                IncidentColor = GetIncidentColor(_incidentCount);
            }
        }

        private void StartIncrementFlashing()
        {
            if (_isFlashing) return; // Already flashing

            _isFlashing = true;
            _flashCount = 0;

            if (!_flashTimer.IsEnabled)
                _flashTimer.Start();
        }

        private void StartProximityFlashing()
        {
            _isProximityFlashing = true;

            if (!_flashTimer.IsEnabled)
                _flashTimer.Start();
        }

        private void StopProximityFlashing()
        {
            _isProximityFlashing = false;

            // If not increment flashing either, stop the timer
            if (!_isFlashing)
                _flashTimer.Stop();

            // Update color to non-flashing state
            UpdateIncidentColor(false);
        }

        private void OnFlashTick(object sender, EventArgs e)
        {
            // Handle increment flashing (4 flashes then stop)
            if (_isFlashing)
            {
                if (_flashCount >= MaxFlashes * 2) // Each flash = 2 ticks (on/off)
                {
                    // Stop increment flashing
                    _isFlashing = false;
                    _flashCount = 0;

                    // Update to final color
                    bool nearLimit = _incidentLimit > 0 && (_incidentCount + 4 >= _incidentLimit);
                    UpdateIncidentColor(nearLimit);

                    // Stop timer if proximity flashing is also not active
                    if (!_isProximityFlashing)
                        _flashTimer.Stop();

                    return;
                }

                // Increment flash: toggle between yellow and normal color
                bool isFlashOn = (_flashCount % 2) == 0;
                IncidentColor = isFlashOn ? Brushes.Yellow : GetIncidentColor(_incidentCount);

                _flashCount++;
            }
            else if (_isProximityFlashing)
            {
                // Proximity flash: toggle between red and off (black)
                bool isFlashOn = (DateTime.Now.Millisecond / 500) % 2 == 0;
                IncidentColor = isFlashOn ? Brushes.Red : Brushes.Black;
            }
        }

        private Brush GetIncidentColor(int count)
        {
            // Color coding based on incident count
            return count switch
            {
                0 => Brushes.White,
                1 or 2 => Brushes.Yellow,
                3 or 4 => Brushes.Orange,
                _ => Brushes.Red
            };
        }

        /// <summary>
        /// Update connection health based on telemetry timing with layered persistence
        /// </summary>
        public void UpdateConnectionHealth()
        {
            var now = DateTime.Now;
            _telemetryTimestamps.Enqueue(now);

            // Remove timestamps older than our immediate monitoring window
            var immediateCutoff = now.AddSeconds(-HealthWindowSeconds);
            while (_telemetryTimestamps.Count > 0 && _telemetryTimestamps.Peek() < immediateCutoff)
            {
                _telemetryTimestamps.Dequeue();
            }

            // Clean up old session events
            var sessionCutoff = now.AddMinutes(-SessionWindowMinutes);
            while (_warningEvents.Count > 0 && _warningEvents.Peek() < sessionCutoff)
                _warningEvents.Dequeue();
            while (_criticalEvents.Count > 0 && _criticalEvents.Peek() < sessionCutoff)
                _criticalEvents.Dequeue();

            // Need at least 30 samples for meaningful immediate analysis
            if (_telemetryTimestamps.Count < 30)
            {
                // Check for persistent issues even without current data
                SetConnectionColorWithPersistence(ConnectionHealthLevel.Unknown);
                return;
            }

            // Calculate immediate health (10-second window)
            var immediateHealth = CalculateImmediateHealth();

            // Record warning/critical events for session tracking
            if (immediateHealth == ConnectionHealthLevel.Critical)
            {
                _criticalEvents.Enqueue(now);
                _lastCriticalTime = now;
            }
            else if (immediateHealth == ConnectionHealthLevel.Warning)
            {
                _warningEvents.Enqueue(now);
                _lastWarningTime = now;
            }

            // Apply persistence logic and set final color
            SetConnectionColorWithPersistence(immediateHealth);
        }

        private enum ConnectionHealthLevel
        {
            Unknown,
            Healthy,
            Warning,
            Critical
        }

        private ConnectionHealthLevel CalculateImmediateHealth()
        {
            var timestamps = _telemetryTimestamps.ToArray();
            int lateUpdates = 0;

            for (int i = 1; i < timestamps.Length; i++)
            {
                var interval = (timestamps[i] - timestamps[i - 1]).TotalMilliseconds;

                // Consider an update "late" if it's more than 50% longer than expected
                if (interval > ExpectedTelemetryInterval * 1.5)
                {
                    lateUpdates++;
                }
            }

            double latePercentage = (double)lateUpdates / (timestamps.Length - 1);

            if (latePercentage >= CriticalThreshold)
                return ConnectionHealthLevel.Critical;
            else if (latePercentage >= WarningThreshold)
                return ConnectionHealthLevel.Warning;
            else
                return ConnectionHealthLevel.Healthy;
        }

        private void SetConnectionColorWithPersistence(ConnectionHealthLevel immediateLevel)
        {
            var now = DateTime.Now;

            // Check for persistent critical issues
            bool hasPersistentCritical = _criticalEvents.Count >= CriticalEventThreshold;
            bool criticalRecent = (now - _lastCriticalTime).TotalMinutes < ClearanceMinutes;

            // Check for persistent warning issues
            bool hasPersistentWarning = _warningEvents.Count >= WarningEventThreshold;
            bool warningRecent = (now - _lastWarningTime).TotalMinutes < ClearanceMinutes;

            // Determine final color with persistence logic
            if (immediateLevel == ConnectionHealthLevel.Critical || (hasPersistentCritical && criticalRecent))
            {
                ConnectionColor = Brushes.Red;
            }
            else if (immediateLevel == ConnectionHealthLevel.Warning || (hasPersistentWarning && warningRecent))
            {
                ConnectionColor = Brushes.Yellow;
            }
            else if (immediateLevel == ConnectionHealthLevel.Healthy)
            {
                ConnectionColor = Brushes.Green;
            }
            else // Unknown
            {
                // Check for persistent issues even without current data
                if (hasPersistentCritical && criticalRecent)
                    ConnectionColor = Brushes.Red;
                else if (hasPersistentWarning && warningRecent)
                    ConnectionColor = Brushes.Yellow;
                else
                    ConnectionColor = Brushes.Gray; // Not enough data yet
            }
        }

        /// <summary>
        /// Update FPS warning (placeholder implementation)
        /// </summary>
        public void UpdateFPS(float fps)
        {
            // TODO: Implement FPS thresholds and color logic
            if (fps < 30) // Example threshold
                FpsColor = Brushes.Red;
            else if (fps < 60)
                FpsColor = Brushes.Yellow;
            else
                FpsColor = Brushes.White;
        }

        public void Reset()
        {
            _flashTimer?.Stop();
            _isFlashing = false;
            _isProximityFlashing = false;
            _flashCount = 0;

            IncidentCount = 0;
            IncidentDisplay = "0x";
            IncidentColor = Brushes.White;
            ConnectionColor = Brushes.White;
            FpsColor = Brushes.White;

            // Clear connection monitoring data
            _telemetryTimestamps.Clear();
            _warningEvents.Clear();
            _criticalEvents.Clear();
            _lastWarningTime = DateTime.MinValue;
            _lastCriticalTime = DateTime.MinValue;
        }

        public void Dispose()
        {
            _flashTimer?.Stop();
            _flashTimer = null;
        }
    }
}