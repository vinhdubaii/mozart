using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MozartBrowser.Models
{
    public enum DownloadState
    {
        InProgress,
        Completed,
        Failed,
        Cancelled
    }

    public class DownloadItem : INotifyPropertyChanged
    {
        /// <summary>Stable id for referencing this download from the downloads.html bridge (open/show-in-folder/remove-from-list).</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public DateTime StartTime { get; set; } = DateTime.Now;

        public ulong TotalBytes { get; set; }

        private ulong _receivedBytes;
        public ulong ReceivedBytes
        {
            get => _receivedBytes;
            set { _receivedBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressPercent)); }
        }

        public double ProgressPercent =>
            TotalBytes == 0 ? 0 : Math.Min(100.0, (ReceivedBytes / (double)TotalBytes) * 100.0);

        private DownloadState _state = DownloadState.InProgress;
        public DownloadState State
        {
            get => _state;
            set { _state = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
