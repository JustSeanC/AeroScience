using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Desktop;

// The same profile store/API contract; no browser or independent Windows preference file.
public sealed class PresentationPanel : FlowLayoutPanel
{
    private sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
    private readonly PreferenceStore store;
    private readonly ComboBox theme = new() { Width = 380, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox language = new() { Width = 380, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label feedback = new() { AutoSize = true, MaximumSize = new(660, 0) };
    private PresentationPreferences saved;
    public bool HasChanges => (theme.SelectedItem as Choice)?.Id != saved.Theme || (language.SelectedItem as Choice)?.Id != saved.Locale;
    public PresentationPanel(PreferenceStore store, Action changed, bool showSave = true)
    {
        this.store = store; saved = store.Presentation(); AutoSize = true; FlowDirection = FlowDirection.TopDown; WrapContents = false;
        WindowsPresentation.SetLocale(saved.Locale);
        AddText(WindowsPresentation.Text("settings.presentation")); AddText(WindowsPresentation.Text("settings.hostScope"));
        foreach (var item in new[] { new Choice("auto", WindowsPresentation.Text("settings.themeAuto")), new Choice("light", WindowsPresentation.Text("settings.themeLight")), new Choice("dark", WindowsPresentation.Text("settings.themeDark")) }) theme.Items.Add(item);
        foreach (var locale in PresentationText.Locales) language.Items.Add(new Choice(locale.Id, locale.Review == "candidate" ? WindowsPresentation.Text("localization.candidateOption", new Dictionary<string, object> { ["name"] = locale.Name }) : locale.Name));
        AddText(WindowsPresentation.Text("settings.hostTheme")); theme.AccessibleName = WindowsPresentation.Text("settings.hostTheme"); Controls.Add(theme);
        AddText(WindowsPresentation.Text("settings.hostLanguage")); language.AccessibleName = WindowsPresentation.Text("settings.hostLanguage"); Controls.Add(language);
        SelectSaved(); AddText(WindowsPresentation.Text("settings.hostNativeNotice")); Controls.Add(feedback);
        if (showSave)
        {
            var apply = new Button { Text = WindowsPresentation.Text("settings.hostSave"), AutoSize = true };
            apply.Click += (_, _) => { try { Save(); changed(); feedback.Text = WindowsPresentation.Text("windows.language.reopen"); }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException) { feedback.Text = error.Message; } };
            Controls.Add(apply);
        }
        var reload = new Button { Text = WindowsPresentation.Text("settings.hostReload"), AutoSize = true };
        reload.Click += (_, _) => { try { saved = store.Presentation(); SelectSaved(); feedback.Text = WindowsPresentation.Text("settings.hostScope"); } catch (Exception e) { feedback.Text = e.Message; } }; Controls.Add(reload);
    }
    private void AddText(string text) => Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new(660, 0) });
    private void SelectSaved()
    {
        theme.SelectedItem = theme.Items.Cast<Choice>().Single(c => c.Id == saved.Theme);
        language.SelectedItem = language.Items.Cast<Choice>().Single(c => c.Id == saved.Locale);
    }
    public void Save()
    {
        if (Change() is { } change) saved = store.SetPresentation(change);
        WindowsPresentation.SetLocale(saved.Locale);
    }
    public PresentationChange? Change() => HasChanges ? new(saved.Revision, ((Choice)theme.SelectedItem!).Id, ((Choice)language.SelectedItem!).Id) : null;
}

internal static class WindowsPresentation
{
    public static string Locale { get; private set; } = "en-US";
    public static void SetLocale(string? locale) => Locale = PresentationText.EffectiveLocale(locale);
    public static string Text(string key, IReadOnlyDictionary<string, object>? values = null) => PresentationText.Text(key, values, Locale);
    public static string Format(FormattableString value) => value.ToString(System.Globalization.CultureInfo.GetCultureInfo(Locale));
    public static bool Dark(string theme)
    {
        if (theme != "auto") return theme == "dark";
        try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0; }
        catch(Exception error)when(error is UnauthorizedAccessException or System.Security.SecurityException){return false;}
    }
    public static void Apply(Control root, bool dark)
    {
        // Windows High Contrast remains owned by the OS, including focus/disabled states.
        var highContrast = SystemInformation.HighContrast;
        var background = highContrast ? SystemColors.Control : dark ? Color.FromArgb(18, 28, 39) : Color.FromArgb(241, 245, 249);
        var foreground = highContrast ? SystemColors.ControlText : dark ? Color.FromArgb(233, 239, 244) : Color.FromArgb(24, 40, 56);
        void Visit(Control node)
        {
            if (node is Form or Panel or Label or Button or CheckBox or TabPage or TabControl or TextBoxBase or ComboBox or NumericUpDown)
            { node.BackColor = background; node.ForeColor = foreground; if (node is Button button) button.UseVisualStyleBackColor = highContrast; }
            foreach (Control child in node.Controls) Visit(child);
        }
        Visit(root);
    }
}
