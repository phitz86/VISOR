using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VISOR.Telemetry;
using VISOR.Settings;

namespace VISOR.ViewModels
{
    public enum RadarZone
    {
        LeftFar = 0,    // Zone 0 (0-48px)
        LeftNear = 1,   // Zone 1 (48-96px) 
        Center = 2,     // Zone 2 (96-144px)
        RightNear = 3,  // Zone 3 (144-192px)
        RightFar = 4    // Zone 4 (192-240px)
    }

    public class RadarViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Constants for radar calculations
        private const float AVERAGE_CAR_LENGTH = 4.5f; // meters - average racing car length
        private const float DETECTION_RANGE = 5.0f; // car lengths for detection boundary
        private const float RADAR_HEIGHT = 396f; // Total height of radar display
        private const float RADAR_CENTER_Y = 198f; // Y position of player (center)
        private const float CANVAS_CAR_POSITIONS = 11.0f; // Total positions: 5 ahead + 1 player + 5 behind
        private const float CANVAS_HALF_RANGE = 5.5f; // Half the canvas in car lengths (from center to edge)

        // Base dimensions for scaling
        private const double BASE_CAR_WIDTH = 24.0;
        private const double BASE_CAR_HEIGHT = 36.0;
        private const double BASE_FONT_SIZE = 12.0;
        private const double BASE_CANVAS_WIDTH = 240.0;

        // Zone positioning - calculated dynamically based on canvas size
        private float[] GetZoneXPositions(double canvasWidth)
        {
            double zoneWidth = canvasWidth / 5.0; // 5 equal zones
            return new float[]
            {
                (float)(zoneWidth * 0.5), // Center of zone 0
                (float)(zoneWidth * 1.5), // Center of zone 1  
                (float)(zoneWidth * 2.5), // Center of zone 2
                (float)(zoneWidth * 3.5), // Center of zone 3
                (float)(zoneWidth * 4.5)  // Center of zone 4
            };
        }

        // Car display elements cache
        private readonly Dictionary<int, RadarCarElement> _carElements = new();

        // Shared services
        private readonly ClassColorManager _classColorManager;
        private readonly SettingsManager _settingsManager;

        // Zone assignment tracking
        private readonly Dictionary<int, RadarZone> _carZoneAssignments = new();
        private string _lastCarLeftRightState = "Off";

        // State tracking
        private float _trackLength = 0f;
        private int _visibleCarCount = 0;

        public int VisibleCarCount
        {
            get => _visibleCarCount;
            private set { _visibleCarCount = value; OnPropertyChanged(); }
        }

        public RadarViewModel(ClassColorManager classColorManager)
        {
            _classColorManager = classColorManager;
            _settingsManager = SettingsManager.Instance;
        }

        /// <summary>
        /// Calculate current scale factor based on window size preset
        /// </summary>
        private double GetScaleFactor()
        {
            return _settingsManager.Settings.WindowSize switch
            {
                WindowSizePreset.Small => 0.8,   // 80% of base size
                WindowSizePreset.Medium => 0.9,  // 90% of base size  
                WindowSizePreset.Large => 1.0,   // 100% of base size
                _ => 1.0
            };
        }

        public void UpdateFromTelemetry(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider, Canvas carsContainer)
        {
            if (snapshot == null || sessionDataProvider == null || !sessionDataProvider.IsDataReady)
            {
                VisibleCarCount = 0;
                return;
            }

            var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx", -1);
            if (playerCarIdx == -1) return;

            // Get track length
            _trackLength = GetTrackLength(snapshot, sessionDataProvider);
            if (_trackLength <= 0) return;

            // Get all necessary arrays
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var trackSurface = snapshot.GetValue<int[]>("CarIdxTrackSurface");
            var carNumbers = sessionDataProvider.CarNumbers;
            var userNames = sessionDataProvider.UserNames;
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var onPitRoad = snapshot.GetValue<bool[]>("CarIdxOnPitRoad");

            if (lapDistPct == null || trackSurface == null) return;

            var playerLapDistPct = lapDistPct[playerCarIdx];
            var playerOnPitRoad = onPitRoad?[playerCarIdx] ?? false;
            var visibleCars = new List<RadarCarData>();

            // Process all cars and determine which are in radar range
            for (int i = 0; i < trackSurface.Length; i++)
            {
                if (i == playerCarIdx) continue; // Skip player car
                if (trackSurface[i] == (int)iRacingTrackSurface.NotInWorld) continue;
                if (string.IsNullOrEmpty(carNumbers?[i])) continue;

                var carOnPitRoad = onPitRoad?[i] ?? false;

                // Pit lane filtering: exclude pit road cars unless player is also on pit road
                if (carOnPitRoad && !playerOnPitRoad) continue;

                // Calculate proximity using RelativeDisplayCalculator logic
                var proximityData = CalculateCarProximity(playerLapDistPct, lapDistPct[i], _trackLength);

                // Check if car is within radar range
                if (proximityData.TrackDistance <= DETECTION_RANGE * AVERAGE_CAR_LENGTH)
                {
                    var carData = new RadarCarData
                    {
                        CarIdx = i,
                        LapDistPct = lapDistPct[i],
                        PlayerLapDistPct = playerLapDistPct, // Add player position for Y calculation
                        TrackDistance = proximityData.TrackDistance,
                        Proximity = proximityData.Proximity,
                        IsAhead = proximityData.IsAhead,
                        CarNumber = carNumbers[i],
                        ClassID = carClassIDs[i],
                        IsOnPitRoad = onPitRoad?[i] ?? false
                    };

                    visibleCars.Add(carData);
                }
            }

            // Update zone assignments based on CarLeftRight state
            var carLeftRightEnum = snapshot.GetValue<object>("CarLeftRight", null);
            var carLeftRightState = carLeftRightEnum?.ToString() ?? "Off";
            UpdateZoneAssignments(visibleCars, carLeftRightState);

            // Update radar display
            UpdateRadarDisplay(carsContainer, visibleCars);
            VisibleCarCount = visibleCars.Count;
        }

        private (float TrackDistance, float Proximity, bool IsAhead) CalculateCarProximity(float playerDistPct, float carDistPct, float trackLength)
        {
            // Use original radar distance calculation for range checking
            float directDistance = Math.Abs(carDistPct - playerDistPct) * trackLength;
            float wrapAroundDistance = trackLength - directDistance;
            float trackDistance = Math.Min(directDistance, wrapAroundDistance);

            // Calculate proximity for positioning (borrowed from RelativeDisplayCalculator)
            float distancePct = Math.Abs(carDistPct - playerDistPct);
            float proximity = Math.Min(distancePct, 1.0f - distancePct);
            bool isAhead = (carDistPct - playerDistPct + 1.5f) % 1.0f > 0.5f;

            return (trackDistance, proximity, isAhead);
        }

        private void UpdateZoneAssignments(List<RadarCarData> visibleCars, string carLeftRightState)
        {
            // Always reassign when CarLeftRight is active (not just when state changes)
            bool shouldReassign = carLeftRightState != _lastCarLeftRightState ||
                                  (carLeftRightState != "Clear" && carLeftRightState != "Off");

            if (!shouldReassign) return;

            // Update state tracking
            bool stateChanged = carLeftRightState != _lastCarLeftRightState;
            _lastCarLeftRightState = carLeftRightState;

            // Debug output only when state changes
            if (stateChanged)
            {
                System.Diagnostics.Debug.WriteLine($"[Radar] CarLeftRight state changed to {carLeftRightState}, {visibleCars.Count} visible cars");
            }

            // Reset all cars to center zone first
            foreach (var car in visibleCars)
            {
                _carZoneAssignments[car.CarIdx] = RadarZone.Center;
            }

            // Sort cars by proximity for assignment priority (closest first)
            visibleCars.Sort((a, b) => a.Proximity.CompareTo(b.Proximity));

            // Assign zones based on CarLeftRight state
            switch (carLeftRightState)
            {
                case "CarLeft": // CarLeft - assign closest car to left near zone
                    if (visibleCars.Count > 0)
                    {
                        _carZoneAssignments[visibleCars[0].CarIdx] = RadarZone.LeftNear;
                        if (stateChanged)
                            System.Diagnostics.Debug.WriteLine($"[Radar] Assigned car {visibleCars[0].CarNumber} to LeftNear zone");
                    }
                    break;

                case "CarRight": // CarRight - assign closest car to right near zone
                    if (visibleCars.Count > 0)
                    {
                        _carZoneAssignments[visibleCars[0].CarIdx] = RadarZone.RightNear;
                        if (stateChanged)
                            System.Diagnostics.Debug.WriteLine($"[Radar] Assigned car {visibleCars[0].CarNumber} to RightNear zone");
                    }
                    break;

                case "CarLeftRight": // CarLeftRight - assign two closest cars to both sides
                    if (visibleCars.Count > 0)
                    {
                        _carZoneAssignments[visibleCars[0].CarIdx] = RadarZone.LeftNear;
                        if (stateChanged)
                            System.Diagnostics.Debug.WriteLine($"[Radar] Assigned car {visibleCars[0].CarNumber} to LeftNear zone (CarLeftRight)");
                    }
                    if (visibleCars.Count > 1)
                    {
                        _carZoneAssignments[visibleCars[1].CarIdx] = RadarZone.RightNear;
                        if (stateChanged)
                            System.Diagnostics.Debug.WriteLine($"[Radar] Assigned car {visibleCars[1].CarNumber} to RightNear zone (CarLeftRight)");
                    }
                    break;

                case "TwoCarsLeft": // TwoCarsLeft - assign two closest to left zones
                    if (visibleCars.Count > 0)
                    {
                        _carZoneAssignments[visibleCars[0].CarIdx] = RadarZone.LeftNear;
                        if (stateChanged)
                            System.Diagnostics.Debug.WriteLine($"[Radar] Assigned car {visibleCars[0].CarNumber} to LeftNear zone (TwoCarsLeft)");
                    }
                    if (visibleCars.Count > 1)
                    {
                        _carZoneAssignments[visibleCars[1].CarIdx] = RadarZone.LeftFar;
                        if (stateChanged)
                            System.Diagnostics.Debug.WriteLine($"[Radar] Assigned car {visibleCars[1].CarNumber} to LeftFar zone (TwoCarsLeft)");
                    }
                    break;

                case "TwoCarsRight": // TwoCarsRight - assign two closest to right zones
                    if (visibleCars.Count > 0)
                    {
                        _carZoneAssignments[visibleCars[0].CarIdx] = RadarZone.RightNear;
                        if (stateChanged)
                            System.Diagnostics.Debug.WriteLine($"[Radar] Assigned car {visibleCars[0].CarNumber} to RightNear zone (TwoCarsRight)");
                    }
                    if (visibleCars.Count > 1)
                    {
                        _carZoneAssignments[visibleCars[1].CarIdx] = RadarZone.RightFar;
                        if (stateChanged)
                            System.Diagnostics.Debug.WriteLine($"[Radar] Assigned car {visibleCars[1].CarNumber} to RightFar zone (TwoCarsRight)");
                    }
                    break;

                case "Clear": // Clear
                case "Off": // Off
                default:
                    // All cars stay in center zone (already set above)
                    if (stateChanged)
                        System.Diagnostics.Debug.WriteLine($"[Radar] All cars assigned to Center zone (state: {carLeftRightState})");
                    break;
            }
        }

        private float GetTrackLength(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            // Get track length from session data coordinator (parsed from YAML)
            if (sessionDataProvider is SessionDataCoordinator coordinator)
            {
                float trackLength = coordinator.GetTrackLength();
                if (trackLength > 0)
                    return trackLength;
            }

            // Fallback: try to get from snapshot
            float trackLengthFromSnapshot = snapshot.GetValue<float>("TrackLength", 0f);
            if (trackLengthFromSnapshot > 0)
                return trackLengthFromSnapshot;

            // Final fallback: estimate
            return 5000f; // 5km default for most road courses
        }

        private void UpdateRadarDisplay(Canvas carsContainer, List<RadarCarData> visibleCars)
        {
            // Clear existing car elements that are no longer visible
            var carsToRemove = new List<int>();
            foreach (var kvp in _carElements)
            {
                bool found = visibleCars.Exists(car => car.CarIdx == kvp.Key);
                if (!found)
                {
                    carsToRemove.Add(kvp.Key);
                }
            }

            foreach (var carIdx in carsToRemove)
            {
                if (_carElements.TryGetValue(carIdx, out var element))
                {
                    carsContainer.Children.Remove(element.Rectangle);
                    carsContainer.Children.Remove(element.NumberText);
                    _carElements.Remove(carIdx);
                }
            }

            // Add or update visible cars
            foreach (var car in visibleCars)
            {
                var position = CalculateRadarPosition(car);

                if (!_carElements.ContainsKey(car.CarIdx))
                {
                    // Create new car element
                    var element = CreateCarElement(car);
                    _carElements[car.CarIdx] = element;
                    carsContainer.Children.Add(element.Rectangle);
                    carsContainer.Children.Add(element.NumberText);
                }

                // Update position and appearance
                var carElement = _carElements[car.CarIdx];
                UpdateCarElement(carElement, car, position);
            }
        }

        private RadarPosition CalculateRadarPosition(RadarCarData car)
        {
            // Get current canvas dimensions for zone calculation
            var scaleFactor = GetScaleFactor();
            double canvasWidth = BASE_CANVAS_WIDTH * scaleFactor;

            // Calculate zone positions based on current canvas size
            var zonePositions = GetZoneXPositions(canvasWidth);

            // Get zone assignment for this car
            var zone = _carZoneAssignments.GetValueOrDefault(car.CarIdx, RadarZone.Center);

            // X position based on dynamically calculated zone
            float x = zonePositions[(int)zone];

            // Y position using proper canvas scaling
            float relativeDistPct = car.LapDistPct - car.PlayerLapDistPct;
            if (relativeDistPct > 0.5f) relativeDistPct -= 1.0f;
            if (relativeDistPct < -0.5f) relativeDistPct += 1.0f;

            float aheadBehindMeters = relativeDistPct * _trackLength;

            // Scale based on actual canvas dimensions: 396px represents 11 car lengths total
            float maxDisplayDistance = CANVAS_HALF_RANGE * AVERAGE_CAR_LENGTH; // 5.5 * 4.5m = 24.75m
            float distanceRatio = aheadBehindMeters / maxDisplayDistance;
            distanceRatio = Math.Clamp(distanceRatio, -1.0f, 1.0f);

            // Convert to Y coordinate (negative distanceRatio = ahead = smaller Y)
            float scaledCenterY = RADAR_CENTER_Y * (float)scaleFactor;
            float y = scaledCenterY - (distanceRatio * scaledCenterY);

            return new RadarPosition { X = x, Y = y };
        }

        private RadarCarElement CreateCarElement(RadarCarData car)
        {
            var scaleFactor = GetScaleFactor();

            // Create rectangle for car with scaled dimensions
            var rectangle = new Rectangle
            {
                Width = BASE_CAR_WIDTH * scaleFactor,
                Height = BASE_CAR_HEIGHT * scaleFactor,
                Stroke = Brushes.Black,
                StrokeThickness = 1 * scaleFactor
            };

            // Create text for car number with scaled font
            var numberText = new TextBlock
            {
                Width = BASE_CAR_WIDTH * scaleFactor,
                FontSize = BASE_FONT_SIZE * scaleFactor,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Text = car.CarNumber,
                TextAlignment = TextAlignment.Center
            };

            // Apply enhanced outline effect to text for better visibility
            numberText.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 0,
                ShadowDepth = 2 * scaleFactor, // Scale shadow depth
                BlurRadius = 4 * scaleFactor   // Scale blur radius
            };

            return new RadarCarElement
            {
                Rectangle = rectangle,
                NumberText = numberText,
                CarIdx = car.CarIdx
            };
        }

        private void UpdateCarElement(RadarCarElement element, RadarCarData car, RadarPosition position)
        {
            var scaleFactor = GetScaleFactor();
            var halfWidth = (BASE_CAR_WIDTH * scaleFactor) / 2;
            var halfHeight = (BASE_CAR_HEIGHT * scaleFactor) / 2;

            // Update position - center the scaled rectangle at the calculated position
            Canvas.SetLeft(element.Rectangle, position.X - halfWidth);
            Canvas.SetTop(element.Rectangle, position.Y - halfHeight);
            Canvas.SetLeft(element.NumberText, position.X - halfWidth);
            Canvas.SetTop(element.NumberText, position.Y - halfHeight + (4 * scaleFactor)); // Slight text offset

            // Update color using shared ClassColorManager
            element.Rectangle.Fill = _classColorManager.GetClassColor(car.ClassID);

            // Update appearance based on pit road status
            if (car.IsOnPitRoad)
            {
                element.Rectangle.Opacity = 0.5;
                element.NumberText.FontStyle = FontStyles.Italic;
            }
            else
            {
                element.Rectangle.Opacity = 1.0;
                element.NumberText.FontStyle = FontStyles.Normal;
            }

            // Update car number text
            element.NumberText.Text = car.CarNumber;
        }

        public void Reset()
        {
            _carElements.Clear();
            _carZoneAssignments.Clear();
            _lastCarLeftRightState = "Off";
            VisibleCarCount = 0;
            // Note: ClassColorManager reset is handled by MainViewModel
        }

        // Helper classes
        private class RadarCarData
        {
            public int CarIdx { get; set; }
            public float LapDistPct { get; set; }
            public float PlayerLapDistPct { get; set; } // Add player position for Y calculation
            public float TrackDistance { get; set; }
            public float Proximity { get; set; }
            public bool IsAhead { get; set; }
            public string CarNumber { get; set; } = string.Empty;
            public int ClassID { get; set; }
            public bool IsOnPitRoad { get; set; }
        }

        private class RadarPosition
        {
            public float X { get; set; }
            public float Y { get; set; }
        }

        private class RadarCarElement
        {
            public Rectangle Rectangle { get; set; }
            public TextBlock NumberText { get; set; }
            public int CarIdx { get; set; }
        }
    }
}