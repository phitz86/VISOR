using System.ComponentModel;
using System.Runtime.CompilerServices;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private int _position;
        public int Position { get => _position; set { _position = value; OnPropertyChanged(); } }

        private double _sessionTime;
        public double SessionTime { get => _sessionTime; set { _sessionTime = value; OnPropertyChanged(); } }

        private int _sessionNum;
        public int SessionNum { get => _sessionNum; set { _sessionNum = value; OnPropertyChanged(); } }

        private string _sessionType = "Unknown";
        public string SessionType { get => _sessionType; set { _sessionType = value; OnPropertyChanged(); } }

        private int _gear;
        public int Gear { get => _gear; set { _gear = value; OnPropertyChanged(); } }

        private float _speed;
        public float Speed { get => _speed; set { _speed = value; OnPropertyChanged(); } }

        public void UpdateFromTelemetry(SVappsLABSnapshot s)
        {
            Position = s.PlayerCarIdx;
            SessionTime = s.SessionTime;
            SessionNum = s.SessionNum;
            SessionType = s.SessionType;
            Gear = s.Gear;
            Speed = s.Speed;
        }
    }
}