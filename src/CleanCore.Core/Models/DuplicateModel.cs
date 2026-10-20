using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanCore.Core.Models;

public class DuplicateItem : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelectedForDeletion;

    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime? LastModified { get; set; }
    public bool IsOriginalPreserved { get; set; }

    public bool IsSelectedForDeletion
    {
        get => _isSelectedForDeletion;
        set
        {
            if (_isSelectedForDeletion != value)
            {
                _isSelectedForDeletion = value;
                OnPropertyChanged();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? SelectionChanged;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    public string SizeDisplay => DriveModel.FormatBytes(SizeBytes);
    public string ModifiedDisplay => LastModified?.ToString("yyyy-MM-dd HH:mm") ?? "-";
}

public class DuplicateGroup
{
    public string GroupId { get; set; } = Guid.NewGuid().ToString("N");
    public string Sha256Hash { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public List<DuplicateItem> Items { get; set; } = new();

    public int TotalCount => Items.Count;
    public long ReclaimableBytes => TotalCount > 1 
        ? Items.Where(i => i.IsSelectedForDeletion).Sum(i => i.SizeBytes) 
        : 0;

    public string TotalSizeDisplay => DriveModel.FormatBytes(FileSizeBytes * TotalCount);
    public string SingleSizeDisplay => DriveModel.FormatBytes(FileSizeBytes);
    public string ReclaimableDisplay => DriveModel.FormatBytes(ReclaimableBytes);
    public string SummaryText => $"{TotalCount} copies ({SingleSizeDisplay} each)";
}
