using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    public class RadarViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Constants for radar calculations
        private const float AVERAGE_CAR_LENGTH = 4.5f; // meters - average racing car length
        private const float RADAR_RADIUS = 140f; // pixels from center to edge
        private const float DETECTION_RANGE = 5.0f; // car lengths for outer boundary
        private const float INNER_RING_RANGE = 2.0f; // car lengths for inner ring
        private const float OUTER_RING_RANGE = 4.0f; // car lengths for outer ring

        // Car display elements cache
        private readonly Dictionary<int, RadarCarElement> _carElements = new();
        private readonly Dictionary<int, Brush> _classColorMap = new();
        private readonly Brush[] _classColors = {
            Brushes.Gold, Brushes.Silver, Brushes.White, Brushes.HotPink, Brushes.LightBlue
        };
        private int _nextColorIndex = 0;

        // State tracking
        private float _trackLength = 0f;
        private int _visibleCarCount = 0;

        public int VisibleCarCount
        {
            get => _visibleCarCount;
            private set { _visibleCarCount = value; OnPropertyChanged(); }
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

            // Get track length (from YAML or estimate if not available)
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
            var visibleCars = new List<RadarCarData>();

            // Process all cars and determine which are in radar range
            for (int i = 0; i < trackSurface.Length; i++)
            {
                if (i == playerCarIdx) continue; // Skip player car
                if (trackSurface[i] == (int)iRacingTrackSurface.NotInWorld) continue;
                if (string.IsNullOrEmpty(carNumbers?[i])) continue;

                // Calculate distance from player (for range checking only)
                float carDistance = CalculateTrackDistance(playerLapDistPct, lapDistPct[i], _trackLength);

                // Check if car is within radar range
                if (carDistance <= DETECTION_RANGE * AVERAGE_CAR_LENGTH)
                {
                    var carData = new RadarCarData
                    {
                        CarIdx = i,
                        LapDistPct = lapDistPct[i],
                        TrackDistance = carDistance,
                        CarNumber = carNumbers[i],
                        ClassID = carClassIDs[i],
                        IsOnPitRoad = onPitRoad?[i] ?? false,
                        LeftRight = 0f // CarLeftRight is non-functional, always zero
                    };

                    visibleCars.Add(carData);
                }
            }

            // Update radar display
            UpdateRadarDisplay(carsContainer, visibleCars, playerLapDistPct);
            VisibleCarCount = visibleCars.Count;
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

            // Fallback: try to get from snapshot (though this is now less likely to work)
            float trackLengthFromSnapshot = snapshot.GetValue<float>("TrackLength", 0f);
            if (trackLengthFromSnapshot > 0)
                return trackLengthFromSnapshot;

            // Final fallback: estimate based on known track lengths
            // This could be expanded with a lookup table of known tracks
            return 5000f; // 5km default for most road courses
        }

        private float CalculateTrackDistance(float playerDistPct, float carDistPct, float trackLength)
        {
            // Calculate the shortest distance around the track (for range checking only)
            float directDistance = Math.Abs(carDistPct - playerDistPct) * trackLength;
            float wrapAroundDistance = trackLength - directDistance;

            return Math.Min(directDistance, wrapAroundDistance);
        }

        private void UpdateRadarDisplay(Canvas carsContainer, List<RadarCarData> visibleCars, float playerLapDistPct)
        {
            // Clear existing car elements that are no longer visible
            var carsToRemove = new List<int>();
            foreach (var kvp in _carElements)
            {
                bool found = false;
                foreach (var car in visibleCars)
                {
                    if (car.CarIdx == kvp.Key)
                    {
                        found = true;
                        break;
                    }
                }
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
                var position = CalculateRadarPosition(car, playerLapDistPct);

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

        private RadarPosition CalculateRadarPosition(RadarCarData car, float playerLapDistPct)
        {
            float relativeDistPct = car.LapDistPct - playerLapDistPct;

            if (relativeDistPct > 0.5f) relativeDistPct -= 1.0f;
            if (relativeDistPct < -0.5f) relativeDistPct += 1.0f;

            float aheadBehindMeters = relativeDistPct * _trackLength;

            float distanceRatio = aheadBehindMeters / (DETECTION_RANGE * AVERAGE_CAR_LENGTH);
            distanceRatio = Math.Clamp(distanceRatio, -1.0f, 1.0f);

            float y = RADAR_RADIUS - (distanceRatio * RADAR_RADIUS);
            float x = RADAR_RADIUS;

            return new RadarPosition { X = x, Y = y, Angle = 0 };
        }

        private RadarCarElement CreateCarElement(RadarCarData car)
        {
            // Create rectangle for car - larger size for 12pt font
            var rectangle = new Rectangle
            {
                Width = 18,
                Height = 32,
                Stroke = Brushes.Black,
                StrokeThickness = 1
            };

            // Create text for car number - 12pt font as requested
            var numberText = new TextBlock
            {
                Width = 18, // <-- ADD THIS LINE
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Text = car.CarNumber,
                TextAlignment = TextAlignment.Center
            };

            // Apply outline effect to text
            numberText.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 0,
                ShadowDepth = 1,
                BlurRadius = 2
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
            // Update position - adjust for rectangle size (18x32)
            Canvas.SetLeft(element.Rectangle, position.X - element.Rectangle.Width / 2);
            Canvas.SetTop(element.Rectangle, position.Y - element.Rectangle.Height / 2);

            // Center text within the rectangle - properly centered
            Canvas.SetLeft(element.NumberText, position.X - 9);  // Half of 18px width (centered horizontally)
            Canvas.SetTop(element.NumberText, position.Y - 14); // Adjusted for 32px height and 12pt font (centered vertically)

            // Update color based on class - use same logic as Relative display
            element.Rectangle.Fill = GetClassColor(car.ClassID);

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

        private Brush GetClassColor(int classID)
        {
            if (classID == 0) return Brushes.White; // Default for unknown class

            if (!_classColorMap.ContainsKey(classID))
            {
                _classColorMap[classID] = _classColors[_nextColorIndex % _classColors.Length];
                _nextColorIndex++;
            }

            return _classColorMap[classID];
        }

        public void Reset()
        {
            _carElements.Clear();
            _classColorMap.Clear();
            _nextColorIndex = 0;
            VisibleCarCount = 0;
        }

        // Helper classes
        private class RadarCarData
        {
            public int CarIdx { get; set; }
            public float LapDistPct { get; set; }
            public float TrackDistance { get; set; }
            public string CarNumber { get; set; } = string.Empty;
            public int ClassID { get; set; }
            public bool IsOnPitRoad { get; set; }
            public float LeftRight { get; set; }
        }

        private class RadarPosition
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Angle { get; set; }
        }

        private class RadarCarElement
        {
            public Rectangle Rectangle { get; set; }
            public TextBlock NumberText { get; set; }
            public int CarIdx { get; set; }
        }
    }
}