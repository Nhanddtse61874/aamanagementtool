namespace TimesheetApp.Services;

// P19: runtime light/dark theme switch. Apply() swaps the palette ResourceDictionary in the app's
// merged dictionaries; because views + Theme.xaml styles reference palette keys via DynamicResource,
// the whole UI re-resolves live (no restart).
public interface IThemeService
{
    bool IsDark { get; }
    void Apply(bool dark);
}
