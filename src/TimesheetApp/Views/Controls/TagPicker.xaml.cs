using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TimesheetApp.Views.Controls;

// P13 W2: tag multi-select dropdown. ItemsSource = ObservableCollection<TagPickVm>.
// Each TagPickVm: bool IsChecked (settable, two-way) + Tag { Text, Icon, Color }.
// The control owns its own CollectionViewSource so it never mutates a shared default view.
public partial class TagPicker : UserControl
{
    // ---- ItemsSource DP -------------------------------------------------

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(TagPicker),
            new PropertyMetadata(null, OnItemsSourceChanged));

    /// <summary>
    /// Bind to ObservableCollection&lt;TagPickVm&gt; from the editor VM.
    /// Each element must expose: bool IsChecked (get/set) + Tag { Text, Icon, Color }.
    /// </summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (TagPicker)d;
        picker.RebuildView();
    }

    // ---- HeaderText (computed read-only DP exposed for XAML binding) -----

    private static readonly DependencyPropertyKey HeaderTextPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HeaderText),
            typeof(string),
            typeof(TagPicker),
            new PropertyMetadata("Tags ▾"));

    public static readonly DependencyProperty HeaderTextProperty = HeaderTextPropertyKey.DependencyProperty;

    public string HeaderText
    {
        get => (string)GetValue(HeaderTextProperty);
        private set => SetValue(HeaderTextPropertyKey, value);
    }

    // ---- CollectionViewSource (private, owned by this control) -----------

    private readonly CollectionViewSource _cvs = new();

    public TagPicker()
    {
        InitializeComponent();
        // Wire the ItemsControl to our own CVS view — never to a shared default view.
        TagItemsControl.ItemsSource = _cvs.View;
    }

    private void RebuildView()
    {
        // Detach change listeners from the old source.
        if (_cvs.Source is INotifyCollectionChanged oldCol)
            oldCol.CollectionChanged -= OnCollectionChanged;

        _cvs.Source = ItemsSource;
        // Re-point the ItemsControl at the (now-live) view: when the ctor ran, _cvs.Source was still null
        // so _cvs.View was null — without this the picker stayed permanently empty.
        TagItemsControl.ItemsSource = _cvs.View;

        // Re-apply type-to-filter text after source swap.
        ApplyFilter(FilterBox?.Text);

        // Subscribe to collection changes so the header text stays in sync.
        if (_cvs.Source is INotifyCollectionChanged newCol)
            newCol.CollectionChanged += OnCollectionChanged;

        // Subscribe to per-item IsChecked changes via the underlying list.
        SubscribeToItemChanges();
        UpdateHeader();
    }

    // ---- Type-to-filter -------------------------------------------------

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter(((TextBox)sender).Text);

    private void ApplyFilter(string? text)
    {
        if (_cvs.View is null) return;
        if (string.IsNullOrWhiteSpace(text))
            _cvs.View.Filter = null;
        else
        {
            var query = text.Trim();
            _cvs.View.Filter = item =>
            {
                // Access Tag.Text via reflection to keep the control decoupled from TagPickVm.
                var tagProp = item?.GetType().GetProperty("Tag");
                var tagObj = tagProp?.GetValue(item);
                var textProp = tagObj?.GetType().GetProperty("Text");
                var tagText = textProp?.GetValue(tagObj)?.ToString() ?? string.Empty;
                return tagText.Contains(query, StringComparison.OrdinalIgnoreCase);
            };
        }
    }

    // ---- Header text: "Tags (N) ▾" where N = checked count ---------------

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        SubscribeToItemChanges();
        UpdateHeader();
    }

    private void SubscribeToItemChanges()
    {
        if (ItemsSource is null) return;
        foreach (var item in ItemsSource)
        {
            if (item is INotifyPropertyChanged npc)
            {
                npc.PropertyChanged -= OnItemPropertyChanged;
                npc.PropertyChanged += OnItemPropertyChanged;
            }
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "IsChecked")
            UpdateHeader();
    }

    private void UpdateHeader()
    {
        if (ItemsSource is null)
        {
            HeaderText = "Tags ▾";
            return;
        }

        var checkedCount = 0;
        foreach (var item in ItemsSource)
        {
            var prop = item?.GetType().GetProperty("IsChecked");
            if (prop?.GetValue(item) is true) checkedCount++;
        }

        HeaderText = $"Tags ({checkedCount}) ▾";
    }
}
