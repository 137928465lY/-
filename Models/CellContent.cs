using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace 叶片缺陷审核.Models;

public class CellContent : ReactiveObject
{
    [Reactive] public string Text { get; set; } = string.Empty;
    [Reactive] public ObservableCollection<Bitmap> Images { get; set; } = new();
    public bool HasImages => Images.Any();
}

