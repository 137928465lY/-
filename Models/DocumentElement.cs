using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace 叶片缺陷审核.Models;

public class DocumentElement : ReactiveObject
{
    [Reactive] public string Text { get; set; } = string.Empty;
    [Reactive] public Bitmap? Image { get; set; }
    [Reactive] public bool IsImage { get; set; }
    [Reactive] public bool IsTable { get; set; }
    [Reactive] public bool IsFooter { get; set; }
    [Reactive] public bool IsHeader { get; set; }
    [Reactive] public ObservableCollection<ObservableCollection<CellContent>> TableData { get; set; } = new();
}

