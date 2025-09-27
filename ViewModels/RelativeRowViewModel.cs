using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace VISOR.ViewModels
{
    public class RelativeRowViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // --- Backing Fields ---
        private string _classPos = string.Empty;
        private string _carNum = string.Empty;
        private string _name = string.Empty;
        private Brush _classBackground = Brushes.Transparent;
        private Brush _nameColor = Brushes.White;
        private FontStyle _fontStyle = FontStyles.Normal;
        private bool _isOnPitRoad;

        // --- Proximity Bar Properties ---
        private double _barWidthRatio;
        private Brush _barStartColor = Brushes.Transparent;
        private Brush _barEndColor = Brushes.Transparent;


        // --- Public Properties for UI Binding ---
        public int CarIdx { get; set; }
        public string ClassPos { get => _classPos; set { _classPos = value; OnPropertyChanged(); } }
        public string CarNum { get => _carNum; set { _carNum = value; OnPropertyChanged(); } }
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public Brush ClassBackground { get => _classBackground; set { _classBackground = value; OnPropertyChanged(); } }
        public Brush NameColor { get => _nameColor; set { _nameColor = value; OnPropertyChanged(); } }
        public FontStyle FontStyle { get => _fontStyle; set { _fontStyle = value; OnPropertyChanged(); } }
        public bool IsPlayer { get; set; }
        public int ClassID { get; set; }
        public int IncidentCount { get; set; }

        public bool IsOnPitRoad
        {
            get => _isOnPitRoad;
            set { _isOnPitRoad = value; OnPropertyChanged(); }
        }

        // --- NEW Properties for Proximity Bar ---
        public double BarWidthRatio { get => _barWidthRatio; set { _barWidthRatio = value; OnPropertyChanged(); } }
        public Brush BarStartColor { get => _barStartColor; set { _barStartColor = value; OnPropertyChanged(); } }
        public Brush BarEndColor { get => _barEndColor; set { _barEndColor = value; OnPropertyChanged(); } }


        // --- Properties for Internal Logic ---
        public float LapDistPct { get; set; }
        public int CurrentLap { get; set; }

        // NOTE: The 'Gap' property and smoothing logic are no longer used by the UI
        // but can be kept for other potential uses or removed if desired.
        internal float _smoothedGap = 0f;
        internal bool _hasInitializedGap = false;

        public void UpdateSmoothedGap(float newGap, float smoothingFactor = 0.3f)
        {
            if (!_hasInitializedGap)
            {
                _smoothedGap = newGap;
                _hasInitializedGap = true;
            }
            else
            {
                _smoothedGap = (smoothingFactor * newGap) + ((1f - smoothingFactor) * _smoothedGap);
            }
        }

        public float SmoothedGap => _smoothedGap;

        public void ResetSmoothing()
        {
            _smoothedGap = 0f;
            _hasInitializedGap = false;
        }
    }
}