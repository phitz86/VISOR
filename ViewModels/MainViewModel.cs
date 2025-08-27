using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using VISOR.Telemetry;
using VISOR.Views;

namespace VISOR.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // --- Child ViewModels ---
        public FuelViewModel FuelVM { get; private set; }
        public RelativeViewModel RelativeVM { get; private set; }
        public DeltaBarViewModel DeltaBarVM { get; private set; }
        public WarningsViewModel WarningsVM { get; private set; }

        // --- Session Transition Tracking ---
        private int _lastSessionNum = -1;
        private string _lastSessionType = string.Empty;

        // --- SessionState Debug Tracking ---
        private int _lastSessionState = -999; // Use invalid initial value to catch first state
        private DateTime _lastSessionStateChange = DateTime.MinValue;
        private readonly Dictionary<int, string> _sessionStateNames = new Dictionary<int, string>
        {
            { 0, "Invalid" },
            { 1, "GetInCar" },
            { 2, "Warmup" },
            { 3, "ParadeLaps" },
            { 4, "Racing" },
            { 5, "Checkered" },
            { 6, "CoolDown" }
            // Add more as we discover them
        };

        // --- State Properties ---
        private bool _isTelemetryConnected = false;
        public bool IsTelemetryConnected
        {
            get => _isTelemetryConnected;
            set { _isTelemetryConnected = value; OnPropertyChanged(); }
        }

        // --- Visibility Toggle Properties (for UI sections) ---
        private bool _showPosition = true;
        private bool _showGear = true;
        private bool _showFuelRemaining = true;
        private bool _showTimeRemaining = true;
        private bool _showLapDelta = true;
        private bool _showLapTimes = true;
        private bool _showRelative = true;
        private bool _showWarnings = true;

        public bool ShowPosition { get => _showPosition; set { _showPosition = value; OnPropertyChanged(); } }
        public bool ShowGear { get => _showGear; set { _showGear = value; OnPropertyChanged(); } }
        public bool ShowFuelRemaining { get => _showFuelRemaining; set { _showFuelRemaining = value; OnPropertyChanged(); } }
        public bool ShowTimeRemaining { get => _showTimeRemaining; set { _showTimeRemaining = value; OnPropertyChanged(); } }
        public bool ShowLapDelta { get => _showLapDelta; set { _showLapDelta = value; OnPropertyChanged(); } }
        public bool ShowLapTimes { get => _showLapTimes; set { _showLapTimes = value; OnPropertyChanged(); } }
        public bool ShowRelative { get => _showRelative; set { _showRelative = value; OnPropertyChanged(); } }
        public bool ShowWarnings { get => _showWarnings; set { _showWarnings = value; OnPropertyChanged(); } }

        // --- Position Properties ---
        public string ClassPosition => RelativeVM.LivePlayerClassPosition;
        public string ClassPositionNumber => RelativeVM.LivePlayerClassPositionNumber;

        // --- Other Data Properties ---
        private string _gearDisplay = "N";
        private string _lastLapTime = "-:--.---";
        private string _bestLapTime = "-:--.---";
        private string _timeRemainingDisplay = "--:--";
        private string _timeRemainingSymbol = "⏳";

        public string GearDisplay { get => _gearDisplay; set { _gearDisplay = value; OnPropertyChanged(); } }
        public string LastLapTime { get => _lastLapTime; set { _lastLapTime = value; OnPropertyChanged(); } }
        public string BestLapTime { get => _bestLapTime; set { _bestLapTime = value; OnPropertyChanged(); } }
        public string TimeRemainingDisplay { get => _timeRemainingDisplay; set { _timeRemainingDisplay = value; OnPropertyChanged(); } }
        public string TimeRemainingSymbol { get => _timeRemainingSymbol; set { _timeRemainingSymbol = value; OnPropertyChanged(); } }

        public MainViewModel()
        {
            FuelVM = new FuelViewModel();
            RelativeVM = new RelativeViewModel();
            DeltaBarVM = new DeltaBarViewModel();
            WarningsVM = new WarningsViewModel();
            RelativeVM.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(RelativeViewModel.LivePlayerClassPosition))
                {
                    OnPropertyChanged(nameof(ClassPosition));
                }
                if (args.PropertyName == nameof(RelativeViewModel.LivePlayerClassPositionNumber))
                {
                    OnPropertyChanged(nameof(ClassPositionNumber));
                }
            };
        }

        // Updated to accept either SessionDataParser or SessionDataWrapper
        public void UpdateFromTelemetry(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            // NEW: Debug session state transitions
            DebugSessionState(snapshot);

            // Check for session transitions and reset session-specific data
            CheckForSessionTransition(snapshot);

            // Update fuel calculation (doesn't need session data)
            FuelVM.Update(
                snapshot.GetValue<float>("FuelLevel"),
                snapshot.GetValue<int>("Lap")
            );

            // Update relative positioning (needs session data)
            RelativeVM.Update(snapshot, sessionDataProvider);

            // Update delta bar (doesn't need session data)
            DeltaBarVM.Update(snapshot);

            // Update warnings (incident count from session data, connection health from telemetry timing)
            WarningsVM.UpdateConnectionHealth(); // Track telemetry timing for connection health

            if (sessionDataProvider != null && sessionDataProvider.IsDataReady)
            {
                var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx", -1);
                if (playerCarIdx >= 0)
                {
                    var incidentCounts = sessionDataProvider.CurDriverIncidentCount;
                    if (incidentCounts != null && playerCarIdx < incidentCounts.Length)
                    {
                        WarningsVM.UpdateIncidentCount(incidentCounts[playerCarIdx], sessionDataProvider.IncidentLimit);
                    }
                }
            }

            // TODO: Update FPS when those telemetry fields are available
            // WarningsVM.UpdateFPS(snapshot.GetValue<float>("SomeFPSField", 0f));

            // Update gear display
            int gear = snapshot.GetValue<int>("Gear");
            GearDisplay = gear switch
            {
                -1 => "R",
                0 => "N",
                _ => gear.ToString()
            };

            // Update lap times
            float lastLap = snapshot.GetValue<float>("LapLastLapTime");
            if (lastLap > 0) LastLapTime = FormatLapTime(lastLap);

            float bestLap = snapshot.GetValue<float>("LapBestLapTime");
            if (bestLap > 0) BestLapTime = FormatLapTime(bestLap);

            // Update session timer - smart switching between time and laps with symbols
            int sessionLapsTotal = snapshot.GetValue<int>("SessionLapsTotal", 0);
            int sessionLapsRemain = snapshot.GetValue<int>("SessionLapsRemain", 0);
            double timeRemain = snapshot.GetValue<double>("SessionTimeRemain", 0.0);

            // Check if this is a legitimate lap-limited session
            bool isLapLimitedSession = sessionLapsTotal > 0 &&
                                       sessionLapsTotal < 1000 && // Reasonable max lap count
                                       sessionLapsRemain > 0 &&
                                       sessionLapsRemain <= sessionLapsTotal; // Remaining can't exceed total

            if (isLapLimitedSession)
            {
                // Lap-limited session - show laps remaining with checkered flag
                TimeRemainingSymbol = "🏁";

                int currentLap = snapshot.GetValue<int>("Lap", 0);

                // More accurate lap counting logic
                if (currentLap >= sessionLapsTotal)
                {
                    // On or past final lap
                    TimeRemainingDisplay = "Final Lap";
                }
                else
                {
                    // Calculate laps remaining (including current lap)
                    int lapsRemaining = sessionLapsTotal - currentLap;

                    if (lapsRemaining == 1)
                    {
                        TimeRemainingDisplay = "Final Lap";
                    }
                    else
                    {
                        // Show total laps remaining including current incomplete lap
                        TimeRemainingDisplay = $"{lapsRemaining} Laps";
                    }
                }
            }
            else if (timeRemain > 0)
            {
                // Time-limited session - show time remaining with hourglass
                TimeRemainingSymbol = "⏳";

                TimeSpan remaining = TimeSpan.FromSeconds(timeRemain);

                // Handle white flag scenario (< 1 minute remaining in time-limited session)
                if (remaining.TotalMinutes < 1.0)
                {
                    TimeRemainingDisplay = $"{remaining.Seconds:D2}s";
                }
                else if (remaining.TotalHours >= 1.0)
                {
                    // Sessions over 1 hour - show h:mm:ss format
                    TimeRemainingDisplay = $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                }
                else
                {
                    // Sessions under 1 hour - show mm:ss format
                    TimeRemainingDisplay = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
                }
            }
            else
            {
                // Session transition or unknown state - keep last known symbol but show waiting
                // TimeRemainingSymbol stays as previous (don't change during transitions)
                TimeRemainingDisplay = "--:--";
            }
        }

        /// <summary>
        /// Debug SessionState transitions to understand when UI should be cleared
        /// </summary>
        private void DebugSessionState(SVappsLABSnapshot snapshot)
        {
            int currentSessionState = snapshot.GetValue<int>("SessionState", -1);
            int currentSessionNum = snapshot.GetValue<int>("SessionNum", -1);
            int playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx", -1);
            float speed = snapshot.GetValue<float>("Speed", 0f);
            double sessionTime = snapshot.GetValue<double>("SessionTime", 0.0);

            // Log state changes with context
            if (currentSessionState != _lastSessionState)
            {
                DateTime now = DateTime.Now;
                double timeSinceLastChange = _lastSessionStateChange != DateTime.MinValue
                    ? (now - _lastSessionStateChange).TotalSeconds
                    : 0.0;

                string currentStateName = _sessionStateNames.TryGetValue(currentSessionState, out string name)
                    ? name
                    : $"Unknown({currentSessionState})";

                string lastStateName = _sessionStateNames.TryGetValue(_lastSessionState, out string lastState)
                    ? lastState
                    : $"Unknown({_lastSessionState})";

                Console.WriteLine($"[SessionState DEBUG] ===== STATE CHANGE =====");
                Console.WriteLine($"[SessionState DEBUG] Time: {now:HH:mm:ss.fff}");
                Console.WriteLine($"[SessionState DEBUG] Transition: {lastStateName} -> {currentStateName}");
                Console.WriteLine($"[SessionState DEBUG] Raw values: {_lastSessionState} -> {currentSessionState}");
                Console.WriteLine($"[SessionState DEBUG] Time since last change: {timeSinceLastChange:F1}s");
                Console.WriteLine($"[SessionState DEBUG] Context:");
                Console.WriteLine($"[SessionState DEBUG]   SessionNum: {currentSessionNum}");
                Console.WriteLine($"[SessionState DEBUG]   PlayerCarIdx: {playerCarIdx}");
                Console.WriteLine($"[SessionState DEBUG]   Speed: {speed:F1}");
                Console.WriteLine($"[SessionState DEBUG]   SessionTime: {sessionTime:F1}");
                Console.WriteLine($"[SessionState DEBUG] ============================");

                // Update tracking
                _lastSessionState = currentSessionState;
                _lastSessionStateChange = now;

                // This is where we'd add the UI clearing logic once we understand the patterns
                // For now, just log what we would do
                if (ShouldClearUIOnStateChange(_lastSessionState, currentSessionState))
                {
                    Console.WriteLine($"[SessionState DEBUG] WOULD CLEAR UI: Transition from {lastStateName} to {currentStateName}");
                }
            }
        }

        /// <summary>
        /// Placeholder logic for when to clear UI - will be refined based on logged data
        /// </summary>
        private bool ShouldClearUIOnStateChange(int fromState, int toState)
        {
            // Initial guesses - will refine based on actual data
            // Clear when going from active driving states to inactive states

            // From Racing/Warmup to GetInCar/Invalid might indicate session exit
            if ((fromState == 4 || fromState == 2) && (toState == 1 || toState == 0))
                return true;

            // From any active state to Checkered/CoolDown might indicate session end
            if ((fromState == 2 || fromState == 3 || fromState == 4) && (toState == 5 || toState == 6))
                return true;

            return false;
        }

        /// <summary>
        /// Clear session-specific UI elements without affecting connection state
        /// </summary>
        private void ClearSessionUI()
        {
            // Clear lap times
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";

            // Clear fuel display
            FuelVM.Reset();

            // Clear delta bar
            DeltaBarVM.Reset();

            // Clear relative display
            RelativeVM.Reset();

            // Reset gear to neutral
            GearDisplay = "N";

            // Keep time remaining display as it's session info, not driving-specific
            // Keep incident warnings as they persist across in-car/out-of-car states

            Console.WriteLine("[MainViewModel] Session UI cleared");
        }

        /// <summary>
        /// Check for session transitions and reset session-specific data when needed
        /// </summary>
        private void CheckForSessionTransition(SVappsLABSnapshot snapshot)
        {
            int currentSessionNum = snapshot.GetValue<int>("SessionNum", -1);

            // Detect session change
            if (currentSessionNum != _lastSessionNum && _lastSessionNum != -1)
            {
                Console.WriteLine($"[MainViewModel] Session transition detected: {_lastSessionNum} -> {currentSessionNum}");

                // Reset session-specific data
                ResetSessionData();
            }

            _lastSessionNum = currentSessionNum;
        }

        /// <summary>
        /// Reset session-specific data that shouldn't carry over between sessions
        /// </summary>
        private void ResetSessionData()
        {
            Console.WriteLine("[MainViewModel] Resetting session-specific data");

            // Reset lap times (these are session-specific)
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";

            // Reset delta bar (session best lap reference)
            DeltaBarVM.Reset();

            // Reset fuel tracking (new session = fresh fuel calculations)
            FuelVM.Reset();

            // Reset incident points (incidents are per-session)
            WarningsVM.Reset();

            // Note: We don't reset RelativeVM here because it handles its own data
            // and we want to see other cars immediately when the new session loads
        }

        public void Reset()
        {
            FuelVM.Reset();
            RelativeVM.Reset();
            DeltaBarVM.Reset();
            WarningsVM.Reset();
            GearDisplay = "N";
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";
            TimeRemainingDisplay = "--:--";
            TimeRemainingSymbol = "⏳";
            IsTelemetryConnected = false;

            // Reset debug tracking
            _lastSessionState = -999;
            _lastSessionStateChange = DateTime.MinValue;
        }

        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }

    // Interface to allow both SessionDataParser and SessionDataWrapper to be used
    public interface ISessionDataProvider
    {
        bool IsDataReady { get; }
        string[] UserNames { get; }
        string[] CarNumbers { get; }
        int[] CarNumberRaw { get; }
        int[] CarClassIDs { get; }
        bool[] CarIsAI { get; }
        int[] CurDriverIncidentCount { get; }
        int IncidentLimit { get; }
    }
}