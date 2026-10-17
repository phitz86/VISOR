using System.Collections.ObjectModel;
using System.Windows.Media;
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

    public class RadarViewModel : ObservableObject
    {
        private const float AVERAGE_CAR_LENGTH = 4.5f; // meters
        private const float DETECTION_RANGE = 5.0f; // car lengths
        private const float RADAR_CENTER_Y = 198f;
        private const float CANVAS_HALF_RANGE = 5.5f; // car lengths from center to edge

        private const double BASE_CAR_WIDTH = 24.0;
        private const double BASE_CAR_HEIGHT = 36.0;
        private const double BASE_FONT_SIZE = 12.0;
        private const double BASE_CANVAS_WIDTH = 240.0;

        // The cars RadarWindow draws, one item per car in range, kept and updated in place while
        // the car stays in range. The window draws exactly this list, so a car that leaves it
        // leaves the radar: nothing can be left behind on screen (B1).
        public ObservableCollection<RadarCarItem> Cars { get; } = new();

        private readonly Dictionary<int, RadarCarItem> _carItems = new();
        private readonly List<int> _carsToRemove = new();

        private readonly ClassColorManager _classColorManager;
        private readonly SettingsManager _settingsManager;

        private readonly Dictionary<int, RadarZone> _carZoneAssignments = new();
        private string _lastCarLeftRightState = "Off";

        private float _trackLength = 0f;
        private int _visibleCarCount = 0;

        public int VisibleCarCount
        {
            get => _visibleCarCount;
            private set => Set(ref _visibleCarCount, value);
        }

        public RadarViewModel(ClassColorManager classColorManager)
        {
            _classColorManager = classColorManager;
            _settingsManager = SettingsManager.Instance;
        }

        private double GetScaleFactor() => WindowScale.ForRadar(_settingsManager.Settings.WindowSize);

        public void UpdateFromTelemetry(SVappsLABSnapshot snapshot, SessionView sessionDataProvider)
        {
            if (snapshot == null || sessionDataProvider == null || !sessionDataProvider.IsDataReady)
            {
                VisibleCarCount = 0;
                return;
            }

            var playerCarIdx = snapshot.PlayerCarIdx;
            if (playerCarIdx == -1) return;

            _trackLength = GetTrackLength(sessionDataProvider);
            if (_trackLength <= 0) return;

            var lapDistPct = snapshot.CarIdxLapDistPct;
            var trackSurface = snapshot.CarIdxTrackSurface;
            var carNumbers = sessionDataProvider.CarNumbers;
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var carClassColors = sessionDataProvider.CarClassColors;
            var onPitRoad = snapshot.CarIdxOnPitRoad;

            if (lapDistPct == null || trackSurface == null) return;

            // The live SDK telemetry CarIdx arrays (trackSurface, lapDistPct, onPitRoad) are sized
            // by the SDK, while the session-derived arrays (carNumbers, carClassIDs) are fixed at 64.
            // These are not guaranteed to match — as a new session loads the SDK arrays can come back
            // larger than the still-64-sized session caches. Indexing every parallel array by the same
            // i past the shortest one throws IndexOutOfRangeException, so bound the loop by the minimum.
            int carCount = Math.Min(trackSurface.Length, lapDistPct.Length);
            carCount = Math.Min(carCount, carNumbers.Length);
            carCount = Math.Min(carCount, carClassIDs.Length);
            carCount = Math.Min(carCount, onPitRoad.Length);

            if (playerCarIdx < 0 || playerCarIdx >= carCount)
            {
                VisibleCarCount = 0;
                return;
            }

            var playerLapDistPct = lapDistPct[playerCarIdx];
            var playerOnPitRoad = onPitRoad?[playerCarIdx] ?? false;
            var visibleCars = new List<RadarCarData>();

            for (int i = 0; i < carCount; i++)
            {
                if (i == playerCarIdx) continue;
                if (trackSurface[i] == IRacingIds.TrackSurfaceNotInWorld) continue;
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
                        Proximity = proximityData.Proximity,
                        CarNumber = carNumbers[i],
                        ClassID = carClassIDs[i],
                        IsOnPitRoad = onPitRoad?[i] ?? false
                    };

                    visibleCars.Add(carData);
                }
            }

            var carLeftRightState = snapshot.CarLeftRightState;
            UpdateZoneAssignments(visibleCars, carLeftRightState);

            UpdateRadarDisplay(visibleCars, carClassColors, carClassIDs);
            VisibleCarCount = visibleCars.Count;
        }

        private (float TrackDistance, float Proximity) CalculateCarProximity(float playerDistPct, float carDistPct, float trackLength)
        {
            float directDistance = Math.Abs(carDistPct - playerDistPct) * trackLength;
            float wrapAroundDistance = trackLength - directDistance;
            float trackDistance = Math.Min(directDistance, wrapAroundDistance);

            float distancePct = Math.Abs(carDistPct - playerDistPct);
            float proximity = Math.Min(distancePct, 1.0f - distancePct);

            return (trackDistance, proximity);
        }

        // The side zones each CarLeftRight state fills, closest car first. "Clear", "Off" and
        // anything else leave every car in the centre.
        private static readonly Dictionary<string, RadarZone[]> SideZonesByState = new()
        {
            ["CarLeft"] = new[] { RadarZone.LeftNear },
            ["CarRight"] = new[] { RadarZone.RightNear },
            ["CarLeftRight"] = new[] { RadarZone.LeftNear, RadarZone.RightNear },
            ["TwoCarsLeft"] = new[] { RadarZone.LeftNear, RadarZone.LeftFar },
            ["TwoCarsRight"] = new[] { RadarZone.RightNear, RadarZone.RightFar },
        };

        /// <summary>
        /// The side zones a CarLeftRight state fills, closest car first. Drives both the zone
        /// assignment here and the zone highlights in RadarWindow.
        /// </summary>
        public static IReadOnlyList<RadarZone> SideZonesFor(string carLeftRightState) =>
            carLeftRightState != null && SideZonesByState.TryGetValue(carLeftRightState, out var zones)
                ? zones
                : Array.Empty<RadarZone>();

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
                Log.Debug($"[Radar] CarLeftRight state changed to {carLeftRightState}, {visibleCars.Count} visible cars");
            }

            foreach (var car in visibleCars)
            {
                _carZoneAssignments[car.CarIdx] = RadarZone.Center;
            }

            // Closest cars get first pick of the side zones.
            visibleCars.Sort((a, b) => a.Proximity.CompareTo(b.Proximity));

            var sideZones = SideZonesFor(carLeftRightState);
            for (int i = 0; i < sideZones.Count && i < visibleCars.Count; i++)
            {
                _carZoneAssignments[visibleCars[i].CarIdx] = sideZones[i];
                if (stateChanged)
                    Log.Debug($"[Radar] Assigned car {visibleCars[i].CarNumber} to {sideZones[i]} zone ({carLeftRightState})");
            }
            if (sideZones.Count == 0 && stateChanged)
                Log.Debug($"[Radar] All cars assigned to Center zone (state: {carLeftRightState})");
        }

        // From the session info, not telemetry; 5 km when iRacing gave no length.
        private static float GetTrackLength(SessionView sessionDataProvider)
        {
            float trackLength = sessionDataProvider.Track.LengthMeters;
            return trackLength > 0 ? trackLength : 5000f;
        }

        private void UpdateRadarDisplay(List<RadarCarData> visibleCars, int[] carClassColors, int[] carClassIDs)
        {
            _carsToRemove.Clear();
            foreach (var carIdx in _carItems.Keys)
            {
                if (!visibleCars.Exists(car => car.CarIdx == carIdx))
                    _carsToRemove.Add(carIdx);
            }

            foreach (var carIdx in _carsToRemove)
            {
                Cars.Remove(_carItems[carIdx]);
                _carItems.Remove(carIdx);
            }

            // Every size is set every frame, so cars already on the radar follow a size-preset change.
            double scaleFactor = GetScaleFactor();
            foreach (var car in visibleCars)
            {
                if (!_carItems.TryGetValue(car.CarIdx, out var item))
                {
                    item = new RadarCarItem();
                    _carItems[car.CarIdx] = item;
                    Cars.Add(item);
                }

                UpdateCarItem(item, car, CalculateRadarPosition(car, scaleFactor), scaleFactor, carClassColors, carClassIDs);
            }
        }

        private RadarPosition CalculateRadarPosition(RadarCarData car, double scaleFactor)
        {
            double zoneWidth = BASE_CANVAS_WIDTH * scaleFactor / 5.0;

            // The middle of the car's zone: 0.5, 1.5 ... 4.5 zone widths across.
            var zone = _carZoneAssignments.GetValueOrDefault(car.CarIdx, RadarZone.Center);
            float x = (float)(zoneWidth * ((int)zone + 0.5));

            float relativeDistPct = LapDistance.Delta(car.PlayerLapDistPct, car.LapDistPct);

            float aheadBehindMeters = relativeDistPct * _trackLength;

            float maxDisplayDistance = CANVAS_HALF_RANGE * AVERAGE_CAR_LENGTH;
            float distanceRatio = aheadBehindMeters / maxDisplayDistance;
            distanceRatio = Math.Clamp(distanceRatio, -1.0f, 1.0f);

            // Negative distanceRatio = ahead = smaller Y.
            float scaledCenterY = RADAR_CENTER_Y * (float)scaleFactor;
            float y = scaledCenterY - (distanceRatio * scaledCenterY);

            return new RadarPosition { X = x, Y = y };
        }

        private void UpdateCarItem(RadarCarItem item, RadarCarData car, RadarPosition position, double scaleFactor,
            int[] carClassColors, int[] carClassIDs)
        {
            double width = BASE_CAR_WIDTH * scaleFactor;
            double height = BASE_CAR_HEIGHT * scaleFactor;

            item.Left = position.X - width / 2;
            item.Top = position.Y - height / 2;
            item.Width = width;
            item.Height = height;
            item.StrokeThickness = 1 * scaleFactor;

            item.Number = car.CarNumber;
            item.FontSize = BASE_FONT_SIZE * scaleFactor;
            item.NumberTop = 4 * scaleFactor;

            item.Fill = _classColorManager.GetClassColor(car.ClassID, carClassColors, carClassIDs);

            // Black or white number, whichever reads on this class's fill. The shadow only helps
            // white text on a darker fill, so it goes with it; on black text it would just smudge.
            var textBrush = _classColorManager.GetClassTextBrush(car.ClassID, carClassColors, carClassIDs);
            item.NumberBrush = textBrush;
            item.HasNumberShadow = textBrush is SolidColorBrush solid && solid.Color == Colors.White;
            item.ShadowDepth = 2 * scaleFactor;
            item.ShadowBlur = 4 * scaleFactor;

            item.IsOnPitRoad = car.IsOnPitRoad;
        }

        public void Clear()
        {
            Cars.Clear();
            _carItems.Clear();
            _carZoneAssignments.Clear();
            _lastCarLeftRightState = "Off";
            VisibleCarCount = 0;
        }

        private class RadarCarData
        {
            public int CarIdx { get; set; }
            public float LapDistPct { get; set; }
            public float PlayerLapDistPct { get; set; }
            public float Proximity { get; set; }
            public string CarNumber { get; set; } = string.Empty;
            public int ClassID { get; set; }
            public bool IsOnPitRoad { get; set; }
        }

        private class RadarPosition
        {
            public float X { get; set; }
            public float Y { get; set; }
        }
    }
}