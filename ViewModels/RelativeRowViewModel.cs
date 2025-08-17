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
        private int _carIdx;
        private string _classPos = string.Empty;
        private string _carNum = string.Empty;
        private string _name = string.Empty;
        private string _gap = string.Empty;
        private Brush _classBackground = Brushes.Transparent;
        private Brush _nameColor = Brushes.White;
        private FontStyle _fontStyle = FontStyles.Normal;
        private bool _isPlayer;

        // --- Public Properties for UI Binding ---
        public int CarIdx { get => _carIdx; set { _carIdx = value; OnPropertyChanged(); } }
        public string ClassPos { get => _classPos; set { _classPos = value; OnPropertyChanged(); } }
        public string CarNum { get => _carNum; set { _carNum = value; OnPropertyChanged(); } }
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public string Gap { get => _gap; set { _gap = value; OnPropertyChanged(); } }
        public Brush ClassBackground { get => _classBackground; set { _classBackground = value; OnPropertyChanged(); } }
        public Brush NameColor { get => _nameColor; set { _nameColor = value; OnPropertyChanged(); } }
        public FontStyle FontStyle { get => _fontStyle; set { _fontStyle = value; OnPropertyChanged(); } }
        public bool IsPlayer { get => _isPlayer; set { _isPlayer = value; OnPropertyChanged(); } }

        // --- Properties for Internal Logic ---
        public float LapDistPct { get; set; }
        public int CurrentLap { get; set; }
        public string Class { get; set; } = string.Empty; // <-- ADDED THIS MISSING PROPERTY
    }
}
