using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using VISOR.Diagnostics;
using VISOR.Settings;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Drives the Row 5 track-temperature element: the current track surface temperature
    /// (°C or °F per user setting) plus a trend arrow — red up when the track is heating,
    /// blue down when cooling.
    ///
    /// TrackTempCrew is direct telemetry, so unlike the removed vehicle-health inference there
    /// is nothing to guess here. The only care needed is in the trend: the value moves in small
    /// steps, so the arrow compares against a reference sample a few minutes old and uses a
    /// Schmitt trigger (enter/exit thresholds) so it can't flicker at a threshold boundary.
    /// </summary>
    public class TrackTempViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Trend is judged against a sample ~TrendLookbackSec old. Samples are kept sparse
        // (one per SampleIntervalSec) so the buffer stays a few dozen entries regardless of
        // the 60Hz update rate. No arrow shows until MinTrendAgeSec of history exists.
        private const double SampleIntervalSec = 5.0;
        private const double TrendLookbackSec = 180.0;
        private const double MinTrendAgeSec = 60.0;

        // Schmitt trigger, in °C: the delta vs. the reference must exceed EnterDeadband to
        // start showing a trend, and the trend holds until the delta falls below ExitDeadband
        // (or crosses to the other side). Prevents up/down flapping on step-wise updates.
        private const float EnterDeadbandC = 0.4f;
        private const float ExitDeadbandC = 0.15f;

        private enum Trend { Flat, Heating, Cooling }

        private readonly Queue<(double time, float tempC)> _samples = new();
        private double _lastSampleTime = double.MinValue;
        private Trend _trend = Trend.Flat;

        // Last rendered state, to avoid redundant PropertyChanged storms at 60Hz.
        private int _lastShownDegrees = int.MinValue;
        private TemperatureUnit _lastShownUnit = (TemperatureUnit)(-1);

        private static readonly Brush HeatingBrush = Brushes.Red;
        private static readonly Brush CoolingBrush = Brushes.DodgerBlue;

        private string _tempDisplay = "--°";
        private string _trendArrow = " ";
        private Brush _trendColor = Brushes.Transparent;

        public string TempDisplay { get => _tempDisplay; private set { _tempDisplay = value; OnPropertyChanged(); } }
        public string TrendArrow { get => _trendArrow; private set { _trendArrow = value; OnPropertyChanged(); } }
        public Brush TrendColor { get => _trendColor; private set { _trendColor = value; OnPropertyChanged(); } }

        /// <summary>
        /// Called per telemetry frame with the live track temp (°C) and SessionTime as the
        /// clock. SessionTime rather than wall time so paused sim time doesn't age samples,
        /// and a backwards jump (session change) resets the history.
        /// </summary>
        public void Update(float trackTempC, double sessionTime)
        {
            if (sessionTime < _lastSampleTime)
            {
                Log.Info("[TrackTemp] Session time went backwards; resetting trend history");
                ResetTrendState();
            }

            if (sessionTime - _lastSampleTime >= SampleIntervalSec)
            {
                _samples.Enqueue((sessionTime, trackTempC));
                _lastSampleTime = sessionTime;

                // Keep the head as the newest sample that is at least the lookback old:
                // evict only while the *next* sample would still satisfy the lookback.
                while (_samples.Count >= 2)
                {
                    var head = _samples.Peek();
                    var second = GetSecond();
                    if (sessionTime - second.time >= TrendLookbackSec && sessionTime - head.time >= TrendLookbackSec)
                        _samples.Dequeue();
                    else
                        break;
                }

                UpdateTrend(trackTempC, sessionTime);
            }

            UpdateDisplay(trackTempC);
        }

        private (double time, float tempC) GetSecond()
        {
            // Queue<T> exposes no index; a bounded copy is fine at one call per 5s on a
            // buffer of ~36 entries.
            var e = _samples.GetEnumerator();
            e.MoveNext();
            e.MoveNext();
            return e.Current;
        }

        private void UpdateTrend(float currentTempC, double sessionTime)
        {
            var reference = _samples.Peek();
            if (sessionTime - reference.time < MinTrendAgeSec)
                return;

            float delta = currentTempC - reference.tempC;

            Trend newTrend = _trend;
            if (delta >= EnterDeadbandC) newTrend = Trend.Heating;
            else if (delta <= -EnterDeadbandC) newTrend = Trend.Cooling;
            else if (_trend == Trend.Heating && delta < ExitDeadbandC) newTrend = Trend.Flat;
            else if (_trend == Trend.Cooling && delta > -ExitDeadbandC) newTrend = Trend.Flat;

            if (newTrend != _trend)
            {
                Log.Info($"[TrackTemp] Trend {_trend} -> {newTrend} (Δ{delta:+0.00;-0.00}°C over {sessionTime - reference.time:F0}s)");
                _trend = newTrend;
                ApplyTrendVisual();
            }
        }

        private void ApplyTrendVisual()
        {
            switch (_trend)
            {
                case Trend.Heating:
                    TrendArrow = "▲";
                    TrendColor = HeatingBrush;
                    break;
                case Trend.Cooling:
                    TrendArrow = "▼";
                    TrendColor = CoolingBrush;
                    break;
                default:
                    // Non-empty placeholder keeps the TextBlock's width stable.
                    TrendArrow = " ";
                    TrendColor = Brushes.Transparent;
                    break;
            }
        }

        private void UpdateDisplay(float trackTempC)
        {
            var unit = UserSettings.Instance.TemperatureUnit;
            int degrees = unit == TemperatureUnit.Fahrenheit
                ? (int)System.Math.Round(trackTempC * 9f / 5f + 32f)
                : (int)System.Math.Round(trackTempC);

            if (degrees == _lastShownDegrees && unit == _lastShownUnit)
                return;

            _lastShownDegrees = degrees;
            _lastShownUnit = unit;
            TempDisplay = $"{degrees}°{(unit == TemperatureUnit.Fahrenheit ? "F" : "C")}";
        }

        private void ResetTrendState()
        {
            _samples.Clear();
            _lastSampleTime = double.MinValue;
            _trend = Trend.Flat;
            ApplyTrendVisual();
        }

        public void Reset()
        {
            ResetTrendState();
            _lastShownDegrees = int.MinValue;
            _lastShownUnit = (TemperatureUnit)(-1);
            TempDisplay = "--°";
        }
    }
}
