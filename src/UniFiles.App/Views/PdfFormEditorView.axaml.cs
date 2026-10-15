using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using UniFiles.App.Controls;
using UniFiles.App.Services;
using UniFiles.App.ViewModels;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools.Pdf;

namespace UniFiles.App.Views;

/// <summary>
/// Lays a control over each field of the current page (text box, check box, radio buttons, list)
/// and, in "Add fields" mode, lets the user draw, move and resize new fields with the mouse.
/// </summary>
public partial class PdfFormEditorView : UserControl
{
    private const double MinimumSize = 10;
    private const double HandleSize = 10;

    private PdfFormEditorViewModel? _viewModel;
    private Rectangle? _drawing;
    private Point _start;

    // Moving or resizing an added field.
    private (FormFieldViewModel Field, Control Frame, Point Grab, Rect Original, bool Resize)? _dragging;

    public PdfFormEditorView()
    {
        InitializeComponent();
        Overlay.PointerPressed += OnOverlayPressed;
        Overlay.PointerMoved += OnOverlayMoved;
        Overlay.PointerReleased += OnOverlayReleased;
        FieldName.LostFocus += (_, _) => CommitName();
        FieldName.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                CommitName();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.LayoutChanged -= OnLayoutChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        _viewModel = DataContext as PdfFormEditorViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LayoutChanged += OnLayoutChanged;
            _viewModel.PropertyChanged += OnViewModelChanged;
            ApplyZoom();
            Build();
        }
    }

    private bool _fitted;
    private bool _wasAdding;

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        // First page shown, or the side panel appearing/disappearing: fit the page's width.
        if (_viewModel is { } vm && (!_fitted || vm.IsAdding != _wasAdding))
        {
            _fitted = true;
            _wasAdding = vm.IsAdding;
            // After the panel's layout pass, so the available width is right.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (PageScroller.Bounds.Width > 0)
                    vm.Zoom = Math.Clamp((PageScroller.Bounds.Width - 48) / vm.PageWidth, 0.5, 1);
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        Build();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfFormEditorViewModel.Zoom))
            ApplyZoom();
        else if (e.PropertyName == nameof(PdfFormEditorViewModel.SelectedField))
            Build();
    }

    private void ApplyZoom()
    {
        if (_viewModel is not null)
            Zoomer.LayoutTransform = new ScaleTransform(_viewModel.Zoom, _viewModel.Zoom);
    }

    private void OnThumbnailPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: FormPageViewModel page })
            _viewModel?.SelectPageCommand.Execute(page);
    }

    private void CommitName()
    {
        if (_viewModel?.SelectedField is { } field && FieldName.Text != field.Name && _viewModel.RenameSelected(FieldName.Text ?? ""))
            Build();
    }

    // ---- Overlay --------------------------------------------------------------------------------

    private static IBrush Brush(string key) => ModuleInfo.Resource<IBrush>(key) ?? Brushes.Gray;

    /// <summary>Recreates the controls of the current page, in reading order (top to bottom, left to right).</summary>
    private void Build()
    {
        Overlay.Children.Clear();
        if (_viewModel is not { } vm)
            return;

        var widgets = vm.Fields
            .SelectMany(field => field.Widgets.Where(w => w.Page == vm.CurrentPage).Select(widget => (Field: field, Widget: widget, Rect: vm.ToDisplay(widget.Page, widget.Rect))))
            .OrderBy(w => Math.Round(w.Rect.Top / 8))
            .ThenBy(w => w.Rect.Left)
            .ToList();

        foreach (var (field, widget, rect) in widgets)
        {
            var control = vm.IsAdding ? Frame(vm, field, rect) : Editor(field, widget, rect);
            Canvas.SetLeft(control, rect.X);
            Canvas.SetTop(control, rect.Y);
            control.Width = Math.Max(rect.Width, 4);
            control.Height = Math.Max(rect.Height, 4);
            Overlay.Children.Add(control);
        }
    }

    /// <summary>Fill mode: a control to type or pick the value.</summary>
    private static Control Editor(FormFieldViewModel field, Core.Tools.Pdf.FormWidget widget, Rect rect)
    {
        var ink = Brush("InkBrush");
        var tint = new SolidColorBrush(Color.FromArgb(0x33, 0x6E, 0x8B, 0xFF));
        var fontSize = field.FontSize > 0
            ? field.FontSize * PdfFormEditorViewModel.Scale
            : field.Kind == FormFieldKind.MultilineText ? 10 * PdfFormEditorViewModel.Scale : Math.Clamp(rect.Height * 0.62, 6, 12 * PdfFormEditorViewModel.Scale);

        Control control;
        switch (field.Kind)
        {
            case FormFieldKind.Text or FormFieldKind.MultilineText:
                var box = new TextBox
                {
                    AcceptsReturn = field.Kind == FormFieldKind.MultilineText,
                    TextWrapping = field.Kind == FormFieldKind.MultilineText ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    VerticalContentAlignment = field.Kind == FormFieldKind.MultilineText ? VerticalAlignment.Top : VerticalAlignment.Center,
                    FontSize = fontSize,
                    MinHeight = 0,
                    MinWidth = 0,
                    Padding = new Thickness(3, 1),
                    CornerRadius = new CornerRadius(2),
                    Foreground = ink,
                    CaretBrush = ink,
                    Background = tint,
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x6E, 0x8B, 0xFF)),
                    IsReadOnly = field.ReadOnly,
                };
                box.Resources["TextControlBackgroundFocused"] = Brushes.White;
                box.Resources["TextControlBackgroundPointerOver"] = tint;
                box.Resources["TextControlForegroundFocused"] = ink;
                box.Resources["TextControlForegroundPointerOver"] = ink;
                box.Bind(TextBox.TextProperty, new Binding(nameof(FormFieldViewModel.Text)) { Source = field, Mode = BindingMode.TwoWay });
                control = box;
                break;

            case FormFieldKind.CheckBox or FormFieldKind.Radio:
                var toggle = new ToggleButton
                {
                    MinHeight = 0,
                    MinWidth = 0,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(field.Kind == FormFieldKind.Radio ? 100 : 2),
                    Background = tint,
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x6E, 0x8B, 0xFF)),
                    IsEnabled = !field.ReadOnly,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Content = new Icon { Size = Math.Max(8, Math.Min(rect.Width, rect.Height) * 0.7), Data = ModuleInfo.Resource<Geometry>("IconCheck"), Foreground = ink },
                };
                if (field.Kind == FormFieldKind.CheckBox)
                {
                    toggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(FormFieldViewModel.IsChecked)) { Source = field, Mode = BindingMode.TwoWay });
                }
                else
                {
                    var state = widget.State;
                    void Sync() => toggle.IsChecked = state is not null && field.RadioValue == state;
                    Sync();
                    field.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(FormFieldViewModel.RadioValue))
                            Sync();
                    };
                    toggle.Click += (_, _) => field.RadioValue = state;
                }
                // Checked: show the mark only.
                ((Icon)toggle.Content).Bind(IsVisibleProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = toggle });
                control = toggle;
                break;

            case FormFieldKind.Choice:
                var combo = new ComboBox
                {
                    ItemsSource = field.Options,
                    MinHeight = 0,
                    MinWidth = 0,
                    FontSize = fontSize,
                    Padding = new Thickness(4, 0),
                    Background = tint,
                    Foreground = ink,
                    IsEnabled = !field.ReadOnly,
                };
                combo.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(FormFieldViewModel.Text)) { Source = field, Mode = BindingMode.TwoWay });
                control = combo;
                break;

            default:
                // Signatures and other fields UniFiles doesn't fill: shown, not editable.
                control = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)),
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                };
                ToolTip.SetTip(control, Strings.Form_Unsupported);
                break;
        }

        AutomationProperties.SetName(control, field.Name);
        ToolTip.SetTip(control, field.Required ? $"{field.Name} *" : field.Name);
        return control;
    }

    /// <summary>Add mode: existing fields are outlined; added ones can be selected, moved and resized.</summary>
    private Control Frame(PdfFormEditorViewModel vm, FormFieldViewModel field, Rect rect)
    {
        var selected = vm.SelectedField == field;
        var lemon = Brush("LemonBrush");
        var frame = new Panel
        {
            Background = field.IsAdded ? new SolidColorBrush(Color.FromArgb(0x40, 0xD7, 0xFF, 0x3D)) : new SolidColorBrush(Color.FromArgb(0x22, 0x6E, 0x8B, 0xFF)),
            Cursor = field.IsAdded ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default,
            Tag = field,
        };
        frame.Children.Add(new Border
        {
            BorderBrush = field.IsAdded ? (selected ? Brush("InkBrush") : lemon) : new SolidColorBrush(Color.FromArgb(0x80, 0x6E, 0x8B, 0xFF)),
            BorderThickness = new Thickness(selected ? 2 : 1),
        });
        frame.Children.Add(new TextBlock
        {
            Text = field.Name,
            FontSize = 10,
            Foreground = Brush("InkBrush"),
            Margin = new Thickness(3, 1),
            IsHitTestVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (field.IsAdded && selected)
        {
            frame.Children.Add(new Border
            {
                Width = HandleSize,
                Height = HandleSize,
                Background = Brush("InkBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = new Cursor(StandardCursorType.BottomRightCorner),
                Tag = "resize",
            });
        }
        AutomationProperties.SetName(frame, field.Name);
        return frame;
    }

    // ---- Drawing, moving, resizing (add mode) --------------------------------------------------

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is not { IsAdding: true } vm || !e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed)
            return;
        var point = e.GetPosition(Overlay);

        // On an added field: select it, and move or resize it.
        var hit = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Parent == Overlay);
        if (hit?.Tag is FormFieldViewModel { IsAdded: true } field)
        {
            var resize = (e.Source as Control)?.Tag as string == "resize";
            vm.SelectedField = field;
            var frame = Overlay.Children.FirstOrDefault(c => c.Tag == field) ?? hit;
            _dragging = (field, frame, point, new Rect(Canvas.GetLeft(frame), Canvas.GetTop(frame), frame.Width, frame.Height), resize);
            e.Pointer.Capture(Overlay);
            e.Handled = true;
            return;
        }
        if (hit?.Tag is FormFieldViewModel)
            return;

        // Elsewhere: draw a new field.
        vm.SelectedField = null;
        _start = point;
        _drawing = new Rectangle
        {
            Stroke = Brush("InkBrush"),
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xD7, 0xFF, 0x3D)),
        };
        Canvas.SetLeft(_drawing, point.X);
        Canvas.SetTop(_drawing, point.Y);
        Overlay.Children.Add(_drawing);
        e.Pointer.Capture(Overlay);
        e.Handled = true;
    }

    private void OnOverlayMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(Overlay);
        if (_drawing is not null)
        {
            var rect = new Rect(_start, point).Normalize();
            Canvas.SetLeft(_drawing, rect.X);
            Canvas.SetTop(_drawing, rect.Y);
            _drawing.Width = rect.Width;
            _drawing.Height = rect.Height;
        }
        else if (_dragging is { } drag)
        {
            var delta = point - drag.Grab;
            var rect = drag.Resize
                ? new Rect(drag.Original.X, drag.Original.Y, Math.Max(MinimumSize, drag.Original.Width + delta.X), Math.Max(MinimumSize, drag.Original.Height + delta.Y))
                : drag.Original.Translate(delta);
            Canvas.SetLeft(drag.Frame, rect.X);
            Canvas.SetTop(drag.Frame, rect.Y);
            drag.Frame.Width = rect.Width;
            drag.Frame.Height = rect.Height;
        }
    }

    private void OnOverlayReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_viewModel is not { } vm)
            return;
        if (_drawing is not null)
        {
            var rect = new Rect(Canvas.GetLeft(_drawing), Canvas.GetTop(_drawing), _drawing.Width, _drawing.Height);
            Overlay.Children.Remove(_drawing);
            _drawing = null;
            if (rect.Width >= MinimumSize && rect.Height >= MinimumSize)
                vm.AddField(rect);
        }
        else if (_dragging is { } drag)
        {
            _dragging = null;
            vm.MoveField(drag.Field, new Rect(Canvas.GetLeft(drag.Frame), Canvas.GetTop(drag.Frame), drag.Frame.Width, drag.Frame.Height));
            Build();
        }
    }
}
