using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace 叶片缺陷审核.Models;

public class DocumentInfo : ReactiveObject
{
    [Reactive] public string FilePath { get; set; } = string.Empty;
    [Reactive] public string FileName { get; set; } = string.Empty;
    [Reactive] public string Content { get; set; } = string.Empty;
    [Reactive] public ObservableCollection<DocumentElement> Elements { get; set; } = new();
    [Reactive] public ObservableCollection<Bitmap> PngImages { get; set; } = new();

    private long _fileSize;
    public long FileSize
    {
        get => _fileSize;
        set
        {
            this.RaiseAndSetIfChanged(ref _fileSize, value);
            this.RaisePropertyChanged(nameof(FileSizeText));
        }
    }

    public string FileSizeText
    {
        get
        {
            if (FileSize < 1024)
                return $"{FileSize} B";
            if (FileSize < 1024 * 1024)
                return $"{FileSize / 1024.0:F2} KB";
            return $"{FileSize / (1024.0 * 1024.0):F2} MB";
        }
    }

    public bool ShowTextContent => Elements.Count == 0;
    public bool ShowElements => Elements.Count > 0;
}

