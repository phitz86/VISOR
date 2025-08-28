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
        private int _debugLogCounter = 0; // Throttle debug output to reduce lag
        private int _lapDebugCounter = 0; // For lap counting debug throttling
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
            // Check for session state transitions (including UI clearing logic)
            CheckSessionStateTransitions(snapshot);

            // Always monitor SessionNum changes
            int currentSessionNum = snapshot.GetValue<int>("SessionNum", -1);
            if (currentSessionNum != _lastSessionNum)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionNum] Changed: {_lastSessionNum} -> {currentSessionNum}");
                _lastSessionNum = currentSessionNum;
            }

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

            // Update session timer with context-aware logic
            UpdateSessionTimer(snapshot);
        }

        /// <summary>
        /// Context-aware session timer that uses SessionNum to determine display logic
        /// </summary>
        private void UpdateSessionTimer(SVappsLABSnapshot snapshot)
        {
            int currentSessionNum = snapshot.GetValue<int>("SessionNum", -1);
            int sessionLapsTotal = snapshot.GetValue<int>("SessionLapsTotal", 0);
            int sessionLapsRemain = snapshot.GetValue<int>("SessionLapsRemain", 0);
            double timeRemain = snapshot.GetValue<double>("SessionTimeRemain", 0.0);
            int currentLap = snapshot.GetValue<int>("Lap", 0);

            // Context detection
            bool isQualifying = (currentSessionNum == 1);
            bool isPracticeOrQualifying = (currentSessionNum == 0 || currentSessionNum == 1);

            Console.WriteLine($"[SessionTimer DEBUG] SessionNum: {currentSessionNum}, " +
                            $"LapsRemain: {sessionLapsRemain}, TimeRemain: {timeRemain:F1}s, " +
                            $"IsQualifying: {isQualifying}");

            // ISSUE 1 FIX: Ignore SessionLapsRemain during qualifying (SessionNum 1)
            // Use lap-limited logic only for race sessions with valid lap data
            if (!isQualifying && sessionLapsRemain > 0 && sessionLapsRemain < 10000)
            {
                // Lap-limited session (race only) - show laps remaining with checkered flag
                TimeRemainingSymbol = "🏁";

                if (sessionLapsRemain == 1)
                {
                    TimeRemainingDisplay = "Final Lap";
                }
                else
                {
                    TimeRemainingDisplay = $"{sessionLapsRemain} Laps";
                }

                Console.WriteLine($"[SessionTimer DEBUG] Using lap-limited display: {TimeRemainingDisplay}");
            }
            else if (timeRemain > 0)
            {
                // Time-limited session - show time remaining with hourglass
                // This handles practice, qualifying, and time-limited races
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

                Console.WriteLine($"[SessionTimer DEBUG] Using time-limited display: {TimeRemainingDisplay}");
            }
            else
            {
                // No valid time or lap data - show waiting
                TimeRemainingDisplay = "--:--";
                Console.WriteLine("[SessionTimer DEBUG] No valid session data available");
            }
        }

        /// <summary>
        /// ISSUE 2 FIX: Check for SessionState transitions and handle UI clearing
        /// </summary>
        private void CheckSessionStateTransitions(SVappsLABSnapshot snapshot)
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

                // ISSUE 2 FIX: Clear UI on CoolDown -> GetInCar transition (session exit)
                if (_lastSessionState == 6 && currentSessionState == 1)
                {
                    Console.WriteLine($"[SessionState] Session exit detected (CoolDown -> GetInCar) - clearing UI");
                    ClearSessionUI();
                }

                // Update tracking
                _lastSessionState = currentSessionState;
                _lastSessionStateChange = now;
            }
        }

        /// <summary>
        /// Clear session-specific UI elements without affecting connection state
        /// </summary>
        private void ClearSessionUI()
        {
            Console.WriteLine("[MainViewModel] Clearing session UI due to session exit");

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

            // Reset session timer display
            TimeRemainingDisplay = "--:--";
            TimeRemainingSymbol = "⏳";

            // Keep incident warnings as they persist across in-car/out-of-car states
            // Connection health should also persist

            Console.WriteLine("[MainViewModel] Session UI cleared successfully");
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
                System.Diagnostics.Debug.WriteLine($"[SessionTransition] SessionNum: {_lastSessionNum} -> {currentSessionNum}");
                Console.WriteLine($"[MainViewModel] Session transition detected: {_lastSessionNum} -> {currentSessionNum}");

                // Reset session-specific data
                ResetSessionData();
            }
            else if (_lastSessionNum == -1)
            {
                // First session detected
                System.Diagnostics.Debug.WriteLine($"[SessionTransition] Initial SessionNum: {currentSessionNum}");
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