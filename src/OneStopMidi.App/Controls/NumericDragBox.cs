using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace OneStopMidi.App.Controls;

/// <summary>
/// Compact numeric value: drag up/down with the mouse to change it, mouse wheel steps by 1, double-click to type
/// a value (Enter commits, Esc cancels), Shift+click resets to <see cref="DefaultValue"/>.
/// </summary>
public sealed class NumericDragBox : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumericDragBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumericDragBox)d).UpdateText(),
            (d, v) => Math.Clamp((int)v, ((NumericDragBox)d).Minimum, ((NumericDragBox)d).Maximum)));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(int), typeof(NumericDragBox), new PropertyMetadata(0));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(NumericDragBox), new PropertyMetadata(127));
    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(nameof(DefaultValue), typeof(int), typeof(NumericDragBox), new PropertyMetadata(0));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int DefaultValue { get => (int)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }

    private readonly Border _border;
    private readonly TextBlock _text;
    private readonly TextBox _editor;
    private Point _dragStart;
    private int _dragStartValue;
    private bool _dragging;

    static NumericDragBox()
    {
        FocusableProperty.OverrideMetadata(typeof(NumericDragBox), new FrameworkPropertyMetadata(false));
    }

    public NumericDragBox()
    {
        _text = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas"),
        };
        _text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        _editor = new TextBox
        {
            Visibility = Visibility.Collapsed,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
        };
        _editor.KeyDown += Editor_KeyDown;
        _editor.LostKeyboardFocus += (_, _) => CommitEdit();
        var grid = new Grid();
        grid.Children.Add(_text);
        grid.Children.Add(_editor);
        _border = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x50)),
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1E)),
            Padding = new Thickness(2, 1, 2, 1),
            Cursor = Cursors.SizeNS,
        };
        AddVisualChild(_border);
        AddLogicalChild(_border);
        ToolTip = "Drag up/down to change, double-click to type, Shift+click to reset";
        UpdateText();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _border;

    protected override Size MeasureOverride(Size constraint)
    {
        _border.Measure(constraint);
        return _border.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        _border.Arrange(new Rect(arrangeBounds));
        return arrangeBounds;
    }

    private void UpdateText() => _text.Text = Value.ToString(CultureInfo.InvariantCulture);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_editor.Visibility == Visibility.Visible) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Value = DefaultValue;
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            BeginEdit();
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragStartValue = Value;
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || !IsMouseCaptured) return;
        var dy = _dragStart.Y - e.GetPosition(this).Y;
        Value = Math.Clamp(_dragStartValue + (int)Math.Round(dy / 2.0), Minimum, Maximum);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Value = Math.Clamp(Value + Math.Sign(e.Delta), Minimum, Maximum);
        e.Handled = true;
    }

    private void BeginEdit()
    {
        _editor.Text = Value.ToString(CultureInfo.InvariantCulture);
        _editor.Visibility = Visibility.Visible;
        _text.Visibility = Visibility.Hidden;
        _editor.Focus();
        _editor.SelectAll();
    }

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndEdit();
            e.Handled = true;
        }
    }

    private void CommitEdit()
    {
        if (_editor.Visibility != Visibility.Visible) return;
        if (int.TryParse(_editor.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            Value = Math.Clamp(v, Minimum, Maximum);
        EndEdit();
    }

    private void EndEdit()
    {
        _editor.Visibility = Visibility.Collapsed;
        _text.Visibility = Visibility.Visible;
        // Hand keyboard focus back to the window so shortcuts work again.
        if (_editor.IsKeyboardFocusWithin && Window.GetWindow(this) is { } w) Keyboard.Focus(w);
    }
}
