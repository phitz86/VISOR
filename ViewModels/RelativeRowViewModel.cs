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

        private string _classPos = string.Empty;
        private string _carNum = string.Empty;
        private string _name = string.Empty;
        private Brush _classBackground = Brushes.Transparent;
        private Brush _nameColor = Brushes.White;
        private FontStyle _fontStyle = FontStyles.Normal;
        private bool _isOnPitRoad;
        private string _gapText = string.Empty;
        private Brush _segment1Color = Brushes.Transparent;
        private Brush _segment2Color = Brushes.Transparent;
        private Brush _segment3Color = Brushes.Transparent;
        private Brush _segment4Color = Brushes.Transparent;
        private Brush _segment5Color = Brushes.Transparent;
        private Brush _gapColor = Brushes.LightGray;
        private FontWeight _gapFontWeight = FontWeights.Normal;

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

        public string GapText
        {
            get => _gapText;
            set { _gapText = value; OnPropertyChanged(); }
        }

        public Brush Segment1Color { get => _segment1Color; set { _segment1Color = value; OnPropertyChanged(); } }
        public Brush Segment2Color { get => _segment2Color; set { _segment2Color = value; OnPropertyChanged(); } }
        public Brush Segment3Color { get => _segment3Color; set { _segment3Color = value; OnPropertyChanged(); } }
        public Brush Segment4Color { get => _segment4Color; set { _segment4Color = value; OnPropertyChanged(); } }
        public Brush Segment5Color { get => _segment5Color; set { _segment5Color = value; OnPropertyChanged(); } }
        public Brush GapColor { get => _gapColor; set { _gapColor = value; OnPropertyChanged(); } }
        public FontWeight GapFontWeight { get => _gapFontWeight; set { _gapFontWeight = value; OnPropertyChanged(); } }

        public float LapDistPct { get; set; }
        public int CurrentLap { get; set; }

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
                // If gap jumped by more than 5 seconds, reset smoothing (likely data gap recovery)
                if (System.Math.Abs(newGap - _smoothedGap) > 5.0f)
                {
                    _smoothedGap = newGap; // Hard reset
                }
                else
                {
                    _smoothedGap = (smoothingFactor * newGap) + ((1f - smoothingFactor) * _smoothedGap);
                }
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