using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace VISOR.ViewModels
{
    /// <summary>
    /// One car on the radar, as RadarWindow's car template draws it. Kept for as long as the car
    /// stays in range and updated in place every frame, so its template (and the number's drop
    /// shadow) is built once rather than every frame. Each setter only notifies on a real change.
    /// </summary>
    public sealed class RadarCarItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // Placement and size on the radar canvas, already scaled for the window size preset.
        public double Left { get => _left; set => Set(ref _left, value); }
        public double Top { get => _top; set => Set(ref _top, value); }
        public double Width { get => _width; set => Set(ref _width, value); }
        public double Height { get => _height; set => Set(ref _height, value); }
        public double StrokeThickness { get => _strokeThickness; set => Set(ref _strokeThickness, value); }

        // The car number, and where it sits inside the car.
        public string Number { get => _number; set => Set(ref _number, value); }
        public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }
        public double NumberTop { get => _numberTop; set => Set(ref _numberTop, value); }

        // Class colour, and the black or white number that reads on it.
        public Brush? Fill { get => _fill; set => Set(ref _fill, value); }
        public Brush? NumberBrush { get => _numberBrush; set => Set(ref _numberBrush, value); }

        // White numbers get a drop shadow; on black ones it would only smudge.
        public bool HasNumberShadow { get => _hasNumberShadow; set => Set(ref _hasNumberShadow, value); }
        public double ShadowDepth { get => _shadowDepth; set => Set(ref _shadowDepth, value); }
        public double ShadowBlur { get => _shadowBlur; set => Set(ref _shadowBlur, value); }

        // Drawn faded, with an italic number.
        public bool IsOnPitRoad { get => _isOnPitRoad; set => Set(ref _isOnPitRoad, value); }

        private double _left, _top, _width, _height, _strokeThickness, _fontSize, _numberTop, _shadowDepth, _shadowBlur;
        private string _number = string.Empty;
        private Brush? _fill, _numberBrush;
        private bool _hasNumberShadow, _isOnPitRoad;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
