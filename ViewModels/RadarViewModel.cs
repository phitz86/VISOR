using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VISOR.Diagnostics;
using VISOR.Telemetry;
using VISOR.Settings;

namespace VISOR.ViewModels
{
    public enum RadarZone
    {
        LeftFar = 0,
        LeftNear = 1,
        Center = 2,
        RightNear = 3,
        RightFar = 4
    }

    public class RadarViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private const float AVERAGE_CAR_LENGTH = 4.5f; // meters
        private const float DETECTION_RANGE = 5.0f; // car lengths
        private const float RADAR_HEIGHT = 396f;
        private const float RADAR_CENTER_Y = 198f;
        private const float CANVAS_CAR_POSITIONS = 11.0f; // 5 ahead + 1 player + 5 behind
        private const float CANVAS_HALF_RANGE = 5.5f; // car lengths from center to edge

        private const double BASE_CAR_WIDTH = 24.0;
        private const double BASE_CAR_HEIGHT = 36.0;
        private const double BASE_FONT_SIZE = 12.0;
        private const double BASE_CANVAS_WIDTH = 240.0;

        private float[] GetZoneXPositions(double canvasWidth)
        {
            double zoneWidth = canvasWidth / 5.0;
            return new float[]
            {
                (float)(zoneWidth * 0.5),
                (float)(zoneWidth * 1.5),
                (float)(zoneWidth * 2.5),
                (float)(zoneWidth * 3.5),
                (float)(zoneWidth * 4.5)
            };
        }

        private readonly Dictionary<int, RadarCarElement> _carElements = new();

        private readonly ClassColorManager _classColorManager;
        private readonly SettingsManager _settingsManager;

        private readonly Dictionary<int, RadarZone> _carZoneAssignments = new();
        private string _lastCarLeftRightState = "Off";

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
                WindowSizePreset.Small => 0.8,
                WindowSizePreset.Medium => 0.9,
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

            _trackLength = GetTrackLength(snapshot, sessionDataProvider);
            if (_trackLength <= 0) return;

            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var trackSurface = snapshot.GetValue<int[]>("CarIdxTrackSurface");
            var carNumbers = sessionDataProvider.CarNumbers;
            var userNames = sessionDataProvider.UserNames;
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var carClassColors = sessionDataProvider.CarClassColors;
            var onPitRoad = snapshot.GetValue<bool[]>("CarIdxOnPitRoad");

            if (lapDistPct == null || trackSurface == null) return;

            var playerLapDistPct = lapDistPct[playerCarIdx];
            var playerOnPitRoad = onPitRoad?[playerCarIdx] ?? false;
            var visibleCars = new List<RadarCarData>();

            for (int i = 0; i < trackSurface.Length; i++)
            {
                if (i == playerCarIdx) continue;
                if (trackSurface[i] == (int)iRacingTrackSurface.NotInWorld) continue;
                if (string.IsNullOrEmpty(carNumbers?[i])) continue;

                var carOnPitRoad = onPitRoad?[i] ?? false;

                // Hide cars on pit road unless the player is also on pit road.
                if (carOnPitRoad && !playerOnPitRoad) continue;

                var proximityData = CalculateCarProximity(playerLapDistPct, lapDistPct[i], _trackLength);

                if (proximityData.TrackDistance <= DETECTION_RANGE * AVERAGE_CAR_LENGTH)
                {
                    var carData = new RadarCarData
                    {
                        CarIdx = i,
                        LapDistPct = lapDistPct[i],
                        PlayerLapDistPct = playerLapDistPct,
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

            var carLeftRightEnum = snapshot.GetValue<object>("CarLeftRight", null);
            var carLeftRightState = carLeftRightEnum?.ToString() ?? "Off";
            UpdateZoneAssignments(visibleCars, carLeftRightState);

            UpdateRadarDisplay(carsContainer, visibleCars, carClassColors, carClassIDs);
            VisibleCarCount = visibleCars.Count;
        }

        private (float TrackDistance, float Proximity, bool IsAhead) CalculateCarProximity(float playerDistPct, float carDistPct, float trackLength)
        {
            float directDistance = Math.Abs(carDistPct - playerDistPct) * trackLength;
            float wrapAroundDistance = trackLength - directDistance;
            float trackDistance = Math.Min(directDistance, wrapAroundDistance);

            float distancePct = Math.Abs(carDistPct - playerDistPct);
            float proximity = Math.Min(distancePct, 1.0f - distancePct);
            bool isAhead = (carDistPct - playerDistPct + 1.5f) % 1.0f > 0.5f;

            return (trackDistance, proximity, isAhead);
        }

        private void UpdateZoneAssignments(List<RadarCarData> visibleCars, string carLeftRightState)
        {
            // Reassign on every tick while CarLeftRight is active, so the closest car always wins the side zone.
            bool shouldReassign = carLeftRightState != _lastCarLeftRightState ||
                                  (carLeftRightState != "Clear" && carLeftRightState != "Off");

            if (!shouldReassign) return;

            bool stateChanged = carLeftRightState != _lastCarLeftRightState;
            _lastCarLeftRightState = carLeftRightState;

            if (stateChanged)
            {
                Log.Info($"[Radar] CarLeftRight state changed to {carLeftRightState}, {visibleCars.Count} visible cars");
            }

            foreach (var car in visibleCars)
            {
                _carZoneAssignments[car.CarIdx] = RadarZone.Center;
            }

            // Closest cars get first pick of the side zones.
            visibleCars.Sort((a, b) => a.Proximity.CompareTo(b.Proximity));

            switch (carLeftRightState)
            {
                case "CarLeft":
                    if (visibleCars.Count > 0)
                    {
                        _carZoneAssignments[visibleCars[0].CarIdx] = RadarZone.LeftNear;
                        if (stateChanged)
                            Log.Debug($"[Radar] Assigned car {visibleCars[0].CarNumber} to LeftNear zone");
                    }
                    break;

                case "CarRight":
                    if (visibleCars.Count > 0)
                    {
                        _carZoneAssignments[visibleCars[0].CarIdx] = RadarZone.RightNear;
                        if (stateChanged)
                            Log.Debug($"[Radar] Assigned car {visibleCars[0].CarNumber} to RightNear zone");
                    }
                    break;

                case "CarLeftRight":
                    if (visibleCars.Count > 0)
                    {
                        _carZoneAssignments[visibleCars[0].CarIdx] = RadarZone.LeftNear;
                        if (stateChanged)
                            Log.Debug($"[Radar] Assigned car {visibleCars[0].CarNumber} to LeftNear zone (CarLeftRight)");
                    }
                    if (visibleCars.Count > 1)
                    {
                        _carZoneAssignments[visibleCars[1].CarIdx] = RadarZone.RightNear;
                        if (stateChanged)
                            Log.Debug($"[Radar] Assigned car {visibleCars[1].CarNumber} to RightNear zone (CarLeftRight)");
                    }
                    break;

                case "TwoCarsLeft":
                    if (visibleCars.Count > 0)
                    {
                        _carZoneAssignments[visibleCars[0].CarIdx] = RadarZone.LeftNear;
                        if (stateChanged)
                            Log.Debug($"[Radar] Assigned car {visibleCars[0].CarNumber} to LeftNear zone (TwoCarsLeft)");
                    }
                    if (visibleCars.Count > 1)
                    {
                        _carZoneAssignments[visibleCars[1].CarIdx] = RadarZone.LeftFar;
                        if (stateChanged)
                            Log.Debug($"[Radar] Assigned car {visibleCars[1].CarNumber} to LeftFar zone (TwoCarsLeft)");
                    }
                    break;

                case "TwoCarsRight":
                    if (visibleCars.Count > 0)
                    {
                        _carZoneAssignments[visibleCars[0].CarIdx] = RadarZone.RightNear;
                        if (stateChanged)
                            Log.Debug($"[Radar] Assigned car {visibleCars[0].CarNumber} to RightNear zone (TwoCarsRight)");
                    }
                    if (visibleCars.Count > 1)
                    {
                        _carZoneAssignments[visibleCars[1].CarIdx] = RadarZone.RightFar;
                        if (stateChanged)
                            Log.Debug($"[Radar] Assigned car {visibleCars[1].CarNumber} to RightFar zone (TwoCarsRight)");
                    }
                    break;

                case "Clear":
                case "Off":
                default:
                    if (stateChanged)
                        Log.Debug($"[Radar] All cars assigned to Center zone (state: {carLeftRightState})");
                    break;
            }
        }

        private float GetTrackLength(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            if (sessionDataProvider is SessionDataCoordinator coordinator)
            {
                float trackLength = coordinator.GetTrackLength();
                if (trackLength > 0)
                    return trackLength;
            }

            float trackLengthFromSnapshot = snapshot.GetValue<float>("TrackLength", 0f);
            if (trackLengthFromSnapshot > 0)
                return trackLengthFromSnapshot;

            return 5000f;
        }

        private void UpdateRadarDisplay(Canvas carsContainer, List<RadarCarData> visibleCars, int[] carClassColors, int[] carClassIDs)
        {
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

            foreach (var car in visibleCars)
            {
                var position = CalculateRadarPosition(car);

                if (!_carElements.ContainsKey(car.CarIdx))
                {
                    var element = CreateCarElement(car);
                    _carElements[car.CarIdx] = element;
                    carsContainer.Children.Add(element.Rectangle);
                    carsContainer.Children.Add(element.NumberText);
                }

                var carElement = _carElements[car.CarIdx];
                UpdateCarElement(carElement, car, position, carClassColors, carClassIDs);
            }
        }

        private RadarPosition CalculateRadarPosition(RadarCarData car)
        {
            var scaleFactor = GetScaleFactor();
            double canvasWidth = BASE_CANVAS_WIDTH * scaleFactor;
            var zonePositions = GetZoneXPositions(canvasWidth);

            var zone = _carZoneAssignments.GetValueOrDefault(car.CarIdx, RadarZone.Center);
            float x = zonePositions[(int)zone];

            float relativeDistPct = car.LapDistPct - car.PlayerLapDistPct;
            if (relativeDistPct > 0.5f) relativeDistPct -= 1.0f;
            if (relativeDistPct < -0.5f) relativeDistPct += 1.0f;

            float aheadBehindMeters = relativeDistPct * _trackLength;

            float maxDisplayDistance = CANVAS_HALF_RANGE * AVERAGE_CAR_LENGTH;
            float distanceRatio = aheadBehindMeters / maxDisplayDistance;
            distanceRatio = Math.Clamp(distanceRatio, -1.0f, 1.0f);

            // Negative distanceRatio = ahead = smaller Y.
            float scaledCenterY = RADAR_CENTER_Y * (float)scaleFactor;
            float y = scaledCenterY - (distanceRatio * scaledCenterY);

            return new RadarPosition { X = x, Y = y };
        }

        private RadarCarElement CreateCarElement(RadarCarData car)
        {
            var scaleFactor = GetScaleFactor();

            var rectangle = new Rectangle
            {
                Width = BASE_CAR_WIDTH * scaleFactor,
                Height = BASE_CAR_HEIGHT * scaleFactor,
                Stroke = Brushes.Black,
                StrokeThickness = 1 * scaleFactor
            };

            var numberText = new TextBlock
            {
                Width = BASE_CAR_WIDTH * scaleFactor,
                FontSize = BASE_FONT_SIZE * scaleFactor,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Text = car.CarNumber,
                TextAlignment = TextAlignment.Center
            };

            numberText.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 0,
                ShadowDepth = 2 * scaleFactor,
                BlurRadius = 4 * scaleFactor
            };

            return new RadarCarElement
            {
                Rectangle = rectangle,
                NumberText = numberText,
                CarIdx = car.CarIdx
            };
        }

        private void UpdateCarElement(RadarCarElement element, RadarCarData car, RadarPosition position, int[] carClassColors, int[] carClassIDs)
        {
            var scaleFactor = GetScaleFactor();
            var halfWidth = (BASE_CAR_WIDTH * scaleFactor) / 2;
            var halfHeight = (BASE_CAR_HEIGHT * scaleFactor) / 2;

            Canvas.SetLeft(element.Rectangle, position.X - halfWidth);
            Canvas.SetTop(element.Rectangle, position.Y - halfHeight);
            Canvas.SetLeft(element.NumberText, position.X - halfWidth);
            Canvas.SetTop(element.NumberText, position.Y - halfHeight + (4 * scaleFactor));

            element.Rectangle.Fill = _classColorManager.GetClassColor(car.ClassID, carClassColors, carClassIDs);

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

            element.NumberText.Text = car.CarNumber;
        }

        public void Reset()
        {
            _carElements.Clear();
            _carZoneAssignments.Clear();
            _lastCarLeftRightState = "Off";
            VisibleCarCount = 0;
        }

        private class RadarCarData
        {
            public int CarIdx { get; set; }
            public float LapDistPct { get; set; }
            public float PlayerLapDistPct { get; set; }
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