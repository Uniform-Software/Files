using Avalonia.Controls;
using UniFiles.App.ViewModels;

namespace UniFiles.App.Views;

public partial class SignatureDetailView : UserControl
{
    public SignatureDetailView()
    {
        InitializeComponent();
        // Each finished stroke is saved.
        Pad.Changed += (_, _) => (DataContext as SignatureDetailViewModel)?.SaveDrawing(Pad.ToHandwriting());
        ClearPad.Click += (_, _) => Pad.Load(null);
    }
}
