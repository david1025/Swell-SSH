using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace SwellSSH.Models
{
    public class ThemeViewModel : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public Brush BgBrush { get; set; } = null!;
        public Brush FgBrush { get; set; } = null!;
        public Brush Accent1Brush { get; set; } = null!;
        public Brush Accent2Brush { get; set; } = null!;
        public Brush Accent3Brush { get; set; } = null!;
        
        private bool _isSelected;
        public bool IsSelected 
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class SnippetViewModel
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
    }
}
