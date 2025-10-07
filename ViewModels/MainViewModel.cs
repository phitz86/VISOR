using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using VISOR.Telemetry;
using VISOR.Views;
using VISOR.Settings;

namespace VISOR.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private record CarData(int CarIdx, int ClassID, bool IsPlayer, int CurrentLap, float LapDistPct);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public FuelViewModel FuelVM { get; private set; }
        public RelativeViewModel RelativeVM { get; private set; }
        public DeltaBarViewModel DeltaBarVM { get; private set; }
        public WarningsViewModel WarningsVM { get; private set; }
        public CountdownViewModel CountdownVM { get; private set; }

        private readonly ClassColorManager _classColorManager;
        private readonly SettingsManager _settingsManager;

        private int _lastSessionNum = -1;
        private int _lastSessionState = -999;
        private float _topSpeedBaseline = 0f;
        private float _currentLapTopSpeed = 0f;
        private float _lastLapTopSpeed = 0f;

        private int _lastLoggedPlayerPosition = -1;
        private bool[] _carWasValid = new bool[64];

        private int _lastValidCarCount = -1;
        private int _lastClassCarCount = -1;

        public string ClassPositionNumber { get; private set; } = "--";
        public string GearDisplay { get; private set; } = "N";
        public string LastLapTime { get; private set; } = "-:--.---";
        public string BestLapTime { get; private set; } = "-:--.---";

        public bool ShowGear => _settingsManager.Settings.ShowRow0;
        public bool ShowPosition => _settingsManager.Settings.ShowRow0;
        public bool ShowTimeRemaining => _settingsManager.Settings.ShowRow1;
        public bool ShowFuelRemaining => _settingsManager.Settings.ShowRow1;
        public bool ShowLapDelta => _settingsManager.Settings.ShowRow2;
        public bool ShowLapTimes => _settingsManager.Settings.ShowRow3;
        public bool ShowRelative => _settingsManager.Settings.ShowRow4;
        public bool ShowWarnings => _settingsManager.Settings.ShowRow5;

        public double ScaleFactor => _settingsManager.Settings.WindowSize switch
        {
            WindowSizePreset.Small => 0.6,
            WindowSizePreset.Medium => 0.8,
            WindowSizePreset.Large => 1.0,
            _ => 1.0
        };

        public MainViewModel()
        {
            _classColorManager = new ClassColorManager();
            _settingsManager = SettingsManager.Instance;
            FuelVM = new FuelViewModel();
            RelativeVM = new RelativeViewModel(_classColorManager);
            DeltaBarVM = new DeltaBarViewModel();
            WarningsVM = new WarningsViewModel();
            CountdownVM = new CountdownViewModel();
        }

        private bool _isTelemetryConnected = false;
        public bool IsTelemetryConnected
        {
            get => _isTelemetryConnected;
            set { _isTelemetryConnected = value; OnPropertyChanged(); }
        }

        public void RefreshElementVisibility()
        {
            OnPropertyChanged(nameof(ShowGear));
            OnPropertyChanged(nameof(ShowPosition));
            OnPropertyChanged(nameof(ShowTimeRemaining));
            OnPropertyChanged(nameof(ShowFuelRemaining));
            OnPropertyChanged(nameof(ShowLapDelta));
            OnPropertyChanged(nameof(ShowLapTimes));
            OnPropertyChanged(nameof(ShowRelative));
            OnPropertyChanged(nameof(ShowWarnings));
            OnPropertyChanged(nameof(ScaleFactor));
        }

        public void UpdateFromTelemetry(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            CheckSessionStateTransitions(snapshot);
            CheckForSessionTransition(snapshot, sessionDataProvider);

            FuelVM.Update(snapshot.GetValue<float>("FuelLevel"), snapshot.GetValue<int>("Lap"));
            RelativeVM.Update(snapshot, sessionDataProvider);
            DeltaBarVM.Update(snapshot);
            CountdownVM.Update(snapshot, sessionDataProvider);

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

                UpdatePlayerPosition(snapshot, sessionDataProvider);
            }
            else
            {
                ClassPositionNumber = "--";
                OnPropertyChanged(nameof(ClassPositionNumber));
            }

            float currentSpeed = snapshot.GetValue<float>("Speed");
            if (currentSpeed > _topSpeedBaseline)
            {
                _topSpeedBaseline = currentSpeed;
            }
            if (currentSpeed > _currentLapTopSpeed)
            {
                _currentLapTopSpeed = currentSpeed;
            }

            float lastLap = snapshot.GetValue<float>("LapLastLapTime");
            if (lastLap > 0)
            {
                LastLapTime = FormatLapTime(lastLap);
                OnPropertyChanged(nameof(LastLapTime));

                _lastLapTopSpeed = _currentLapTopSpeed;
                _currentLapTopSpeed = 0f;

                bool onPitRoad = snapshot.GetValue<bool[]>("CarIdxOnPitRoad")?[snapshot.GetValue<int>("PlayerCarIdx")] ?? false;
                int lapsRemaining = snapshot.GetValue<int>("SessionLapsRemain");

                WarningsVM.CheckPace(lastLap, _lastLapTopSpeed, _topSpeedBaseline, lapsRemaining, onPitRoad);
            }

            float bestLap = snapshot.GetValue<float>("LapBestLapTime");
            if (bestLap > 0)
            {
                BestLapTime = FormatLapTime(bestLap);
                OnPropertyChanged(nameof(BestLapTime));
            }

            UpdateGearDisplay(snapshot);
        }

        #region --- Player Position Calculation ---

        private void UpdatePlayerPosition(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            var carClassIDs = dataProvider.CarClassIDs;
            var userNames = dataProvider.UserNames;
            var carNumbers = dataProvider.CarNumbers;
            // --- MODIFIED: Get CarIdxLapCompleted from the snapshot ---
            var lapCompleted = snapshot.GetValue<int[]>("CarIdxLapCompleted");
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var currentLap = snapshot.GetValue<int[]>("CarIdxLap");
            var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx");

            // Ensure we have the new data before proceeding
            if (lapCompleted == null) return;

            // --- MODIFIED: Pass the new data into the method ---
            var allValidCars = BuildValidCarsList(lapCompleted, carNumbers, userNames,
                carClassIDs, currentLap, lapDistPct, playerCarIdx);

            if (!allValidCars.Any())
            {
                ClassPositionNumber = "--";
                OnPropertyChanged(nameof(ClassPositionNumber));
                return;
            }

            if (allValidCars.Count != _lastValidCarCount)
            {
                var player = allValidCars.FirstOrDefault(c => c.IsPlayer);
                if (player != null)
                {
                    var carsInPlayerClass = allValidCars.Count(c => c.ClassID == player.ClassID);
                    if (carsInPlayerClass != _lastClassCarCount)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainViewModel Position] Total valid cars: {allValidCars.Count}, " +
                            $"In player's class (ID {player.ClassID}): {carsInPlayerClass}");
                        _lastClassCarCount = carsInPlayerClass;
                    }
                }
                _lastValidCarCount = allValidCars.Count;
            }

            bool useFastestLap = dataProvider.ShouldUseFastestLapPositioning();
            var newPosition = CalculatePlayerClassPosition(snapshot, allValidCars, playerCarIdx, useFastestLap, dataProvider);

            if (ClassPositionNumber != newPosition)
            {
                ClassPositionNumber = newPosition;
                OnPropertyChanged(nameof(ClassPositionNumber));
            }
        }

        private List<CarData> BuildValidCarsList(int[] lapCompleted, string[] carNumbers, string[] userNames,
                                                  int[] carClassIDs, int[] currentLap, float[] lapDistPct, int playerCarIdx)
        {
            var allValidCars = new List<CarData>();
            for (int i = 0; i < lapCompleted.Length; i++)
            {
                // --- MODIFIED: The core filtering logic is now based on CarIdxLapCompleted ---
                // A car is considered active if it has a valid lap count (>= -1).
                // A value of -1 indicates the car is on the grid and has not completed a lap yet.
                if (lapCompleted[i] >= -1 &&
                    !string.IsNullOrEmpty(carNumbers[i]) &&
                    !string.IsNullOrEmpty(userNames[i]))
                {
                    allValidCars.Add(new CarData(
                        CarIdx: i,
                        ClassID: carClassIDs[i],
                        IsPlayer: (i == playerCarIdx),
                        CurrentLap: currentLap[i],
                        LapDistPct: lapDistPct[i]
                    ));
                }
            }
            return allValidCars;
        }

        private string CalculatePlayerClassPosition(SVappsLABSnapshot snapshot, List<CarData> allCars, int playerCarIdx, bool isFastestLapMode, ISessionDataProvider dataProvider)
        {
            var player = allCars.FirstOrDefault(c => c.IsPlayer);
            if (player == null) return "--";

            if (isFastestLapMode)
            {
                var fastestLapData = dataProvider.GetFastestLapPositioning();
                var playerData = fastestLapData.FirstOrDefault(d => d.carIdx == playerCarIdx);
                return playerData.position > 0 ? playerData.position.ToString() : "--";
            }
            else
            {
                var carsInClass = allCars.Where(c => c.ClassID == player.ClassID).ToList();
                var sorted = carsInClass.OrderByDescending(c => c.CurrentLap + c.LapDistPct).ToList();
                int pos = sorted.FindIndex(c => c.IsPlayer) + 1;

                if (pos != _lastLoggedPlayerPosition)
                {
                    var tick = snapshot.GetValue<int>("SessionTick", 0);
                    System.Diagnostics.Debug.WriteLine($"--- POS DATA [Tick:{tick}] --- POSITION CHANGED: {_lastLoggedPlayerPosition} -> {pos} ---");
                    foreach (var car in carsInClass)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Unsorted] CarIdx: {car.CarIdx,2}, Lap: {car.CurrentLap,3}, Dist: {car.LapDistPct:F4}");
                    }

                    System.Diagnostics.Debug.WriteLine("--- SORTED RESULTS ---");
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        var car = sorted[i];
                        string isPlayerMarker = car.IsPlayer ? "<- (PLAYER)" : "";
                        System.Diagnostics.Debug.WriteLine($"P{i + 1,2}: CarIdx: {car.CarIdx,2}, Lap: {car.CurrentLap,3}, Dist: {car.LapDistPct:F4} {isPlayerMarker}");
                    }

                    _lastLoggedPlayerPosition = pos;
                }

                return pos > 0 ? pos.ToString() : "--";
            }
        }
        #endregion

        private void UpdateGearDisplay(SVappsLABSnapshot snapshot)
        {
            int gear = snapshot.GetValue<int>("Gear");
            string newGearDisplay = gear switch { -1 => "R", 0 => "N", _ => gear.ToString() };
            if (GearDisplay != newGearDisplay)
            {
                GearDisplay = newGearDisplay;
                OnPropertyChanged(nameof(GearDisplay));
            }
        }

        private void CheckSessionStateTransitions(SVappsLABSnapshot snapshot)
        {
            int currentSessionState = snapshot.GetValue<int>("SessionState", -1);
            if (currentSessionState != _lastSessionState)
            {
                if (_lastSessionState == 6 && currentSessionState == 1)
                {
                    ClearSessionUI();
                }
                _lastSessionState = currentSessionState;
            }
        }

        private void ClearSessionUI()
        {
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";
            _topSpeedBaseline = 0f;
            FuelVM.Reset();
            DeltaBarVM.Reset();
            RelativeVM.Reset();
            CountdownVM.Reset();
            GearDisplay = "N";
            ClassPositionNumber = "--";

            _classColorManager.Reset();

            _lastLoggedPlayerPosition = -1;
            Array.Fill(_carWasValid, false);

            _lastValidCarCount = -1;
            _lastClassCarCount = -1;

            OnPropertyChanged(string.Empty);
        }

        private void CheckForSessionTransition(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            int currentSessionNum = snapshot.GetValue<int>("SessionNum", -1);
            if (currentSessionNum != _lastSessionNum && _lastSessionNum != -1)
            {
                ResetSessionData();

                if (sessionDataProvider != null && sessionDataProvider.IsDataReady)
                {
                    CountdownVM.OnSessionTransition(sessionDataProvider, currentSessionNum);
                }
            }
            _lastSessionNum = currentSessionNum;
        }

        private void ResetSessionData()
        {
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";
            _topSpeedBaseline = 0f;
            DeltaBarVM.Reset();
            FuelVM.Reset();
            WarningsVM.Reset();
            CountdownVM.Reset();
            _lastLoggedPlayerPosition = -1;
            Array.Fill(_carWasValid, false);
            OnPropertyChanged(string.Empty);
        }

        public void Reset()
        {
            _lastSessionNum = -1;
            _lastSessionState = -999;
            ClearSessionUI();
            WarningsVM.Reset();
        }

        public ClassColorManager ClassColorManager => _classColorManager;

        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }
}