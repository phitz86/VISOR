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
        private Brush _gapColor = Brushes.White;
        private FontWeight _gapFontWeight = FontWeights.SemiBold;

        public int CarIdx { get; set; }
        public string ClassPos { get => _classPos; set { if (_classPos == value) return; _classPos = value; OnPropertyChanged(); } }
        public string CarNum { get => _carNum; set { if (_carNum == value) return; _carNum = value; OnPropertyChanged(); } }
        public string Name { get => _name; set { if (_name == value) return; _name = value; OnPropertyChanged(); } }
        public Brush ClassBackground { get => _classBackground; set { if (BrushEquals(_classBackground, value)) return; _classBackground = value; OnPropertyChanged(); } }
        public Brush NameColor { get => _nameColor; set { if (BrushEquals(_nameColor, value)) return; _nameColor = value; OnPropertyChanged(); } }
        public FontStyle FontStyle { get => _fontStyle; set { if (_fontStyle == value) return; _fontStyle = value; OnPropertyChanged(); } }
        public bool IsPlayer { get; set; }
        public int ClassID { get; set; }
        public int IncidentCount { get; set; }
        public bool IsOnPitRoad
        {
            get => _isOnPitRoad;
            set { if (_isOnPitRoad == value) return; _isOnPitRoad = value; OnPropertyChanged(); }
        }

        public string GapText
        {
            get => _gapText;
            set { if (_gapText == value) return; _gapText = value; OnPropertyChanged(); }
        }

        public Brush Segment1Color { get => _segment1Color; set { if (BrushEquals(_segment1Color, value)) return; _segment1Color = value; OnPropertyChanged(); } }
        public Brush Segment2Color { get => _segment2Color; set { if (BrushEquals(_segment2Color, value)) return; _segment2Color = value; OnPropertyChanged(); } }
        public Brush Segment3Color { get => _segment3Color; set { if (BrushEquals(_segment3Color, value)) return; _segment3Color = value; OnPropertyChanged(); } }
        public Brush Segment4Color { get => _segment4Color; set { if (BrushEquals(_segment4Color, value)) return; _segment4Color = value; OnPropertyChanged(); } }
        public Brush Segment5Color { get => _segment5Color; set { if (BrushEquals(_segment5Color, value)) return; _segment5Color = value; OnPropertyChanged(); } }
        public Brush GapColor { get => _gapColor; set { if (BrushEquals(_gapColor, value)) return; _gapColor = value; OnPropertyChanged(); } }
        public FontWeight GapFontWeight { get => _gapFontWeight; set { if (_gapFontWeight == value) return; _gapFontWeight = value; OnPropertyChanged(); } }

        public float LapDistPct { get; set; }
        public int CurrentLap { get; set; }

        internal float _smoothedGap = 0f;
        internal bool _hasInitializedGap = false;
        internal int _lastActiveSegmentCount = 0;

        // Hysteretic ahead/behind latch relative to the player: +1 ahead, -1 behind, 0 unknown.
        // Held inside a small dead-band so a car running dead-even with the player doesn't swap the
        // row slot (and gap sign) every frame. Resolved once per frame in BuildProximityBasedRows.
        internal sbyte _proximitySide = 0;

        // Reject-and-hold latch state: consecutive frames a big gap jump has been rejected.
        internal int _gapRejectCount = 0;

        // A gap jump larger than this in a single frame is physically impossible for two nearby cars
        // (real gaps drift well under this per frame) — it's the history buffer's stale-lap crossing,
        // which reads ~a full lap when two cars sit inside its 0.1s sample spacing. Reject and hold.
        private const float GAP_JUMP_REJECT_SECONDS = 5.0f;
        // ...unless the large value persists this many frames: then it's a real change (teleport /
        // return from a dropout), so resync to it. Set above the longest reject run the flicker can
        // produce — the good value reappears every buffer tick (~10Hz / ~6 frames), so runs are short.
        private const int GAP_JUMP_RESYNC_FRAMES = 20;

        public void UpdateSmoothedGap(float newGap, float smoothingFactor = 0.3f)
        {
            if (!_hasInitializedGap)
            {
                _smoothedGap = newGap;
                _hasInitializedGap = true;
                _gapRejectCount = 0;
                return;
            }

            float delta = newGap - _smoothedGap;

            // Large UPWARD jump: the stale-lap glitch only ever reads too big (~a full lap), never too
            // small, so a big jump up is the artifact, not real motion. Reject and hold the last good
            // value; accept only if it persists (a genuine teleport / return from a dropout).
            if (delta > GAP_JUMP_REJECT_SECONDS)
            {
                _gapRejectCount++;
                if (_gapRejectCount < GAP_JUMP_RESYNC_FRAMES)
                    return; // hold _smoothedGap

                _smoothedGap = newGap;
                _gapRejectCount = 0;
                return;
            }

            // Large DOWNWARD jump: the true (small) gap reappearing after a glitch — snap straight to
            // it rather than crawling down through the EMA. Also self-corrects a latch that happened to
            // initialize on a glitch frame. (A big drop is never the glitch, so it's always real.)
            if (delta < -GAP_JUMP_REJECT_SECONDS)
            {
                _smoothedGap = newGap;
                _gapRejectCount = 0;
                return;
            }

            // Plausible change: smooth exactly as before (EMA), unchanged.
            _gapRejectCount = 0;
            _smoothedGap = (smoothingFactor * newGap) + ((1f - smoothingFactor) * _smoothedGap);
        }

        public float SmoothedGap => _smoothedGap;

        public void ResetSmoothing()
        {
            _smoothedGap = 0f;
            _hasInitializedGap = false;
            _lastActiveSegmentCount = 0;
            _gapRejectCount = 0;
            _proximitySide = 0; // re-seed direction from raw geometry when a car returns from a gap
        }

        private static bool BrushEquals(Brush a, Brush b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is SolidColorBrush sa && b is SolidColorBrush sb)
                return sa.Color == sb.Color;
            return false;
        }
    }
}
