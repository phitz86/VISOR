using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Change notification for the view models the windows bind to. <see cref="Set{T}"/> notifies
    /// only when the value really changes.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }
    }
}
