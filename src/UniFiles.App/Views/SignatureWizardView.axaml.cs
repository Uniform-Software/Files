using Avalonia.Controls;
using Avalonia.Threading;
using UniFiles.App.ViewModels;

namespace UniFiles.App.Views;

public partial class SignatureWizardView : UserControl
{
    public SignatureWizardView()
    {
        InitializeComponent();
        Pad.Changed += (_, _) =>
        {
            if (DataContext is SignatureWizardViewModel vm)
                vm.Drawing = Pad.IsEmpty ? null : Pad.ToHandwriting();
        };
        ClearPad.Click += (_, _) =>
        {
            Pad.Load(null);
            (DataContext as SignatureWizardViewModel)?.ClearHandwriting();
        };
        // The field of each step takes the focus, so typing starts right away.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not SignatureWizardViewModel vm)
                return;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SignatureWizardViewModel.Step))
                    Dispatcher.UIThread.Post(() => (vm.Step == 1 ? FirstName : vm.Step == 3 ? EmailBox : (Control)Pad).Focus(), DispatcherPriority.Background);
            };
            Pad.Load(vm.Drawing);
            Dispatcher.UIThread.Post(() => FirstName.Focus(), DispatcherPriority.Background);
        };
    }
}
