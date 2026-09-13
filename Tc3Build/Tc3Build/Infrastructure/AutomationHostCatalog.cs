namespace Tc3Build.Infrastructure;

internal sealed record AutomationHostDefinition(
    string Key,
    string DisplayName,
    string ProgId,
    bool IsVisualStudio);

internal static class AutomationHostCatalog
{
    // Newest Visual Studio first, then TwinCAT XAE Shell as fallback.
    public static IReadOnlyList<AutomationHostDefinition> All { get; } =
    [
        new("vs2026", "Visual Studio 2026", "VisualStudio.DTE.18.0", IsVisualStudio: true),
        new("vs2022", "Visual Studio 2022", "VisualStudio.DTE.17.0", IsVisualStudio: true),
        new("vs2019", "Visual Studio 2019", "VisualStudio.DTE.16.0", IsVisualStudio: true),
        new("xae2022", "TwinCAT XAE Shell (VS2022)", "TcXaeShell.DTE.17.0", IsVisualStudio: false),
        new("xae2019", "TwinCAT XAE Shell (VS2019)", "TcXaeShell.DTE.16.0", IsVisualStudio: false),
        new("xae2017", "TwinCAT XAE Shell (VS2017)", "TcXaeShell.DTE.15.0", IsVisualStudio: false),
        new("xae", "TwinCAT XAE Shell", "TcXaeShell.DTE", IsVisualStudio: false)
    ];

    public static IReadOnlyList<AutomationHostDefinition> Resolve(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection) ||
            string.Equals(selection, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return All;
        }

        var selected = All
            .Where(host => string.Equals(host.Key, selection, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(host.ProgId, selection, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selected.Length > 0)
            return selected;

        var supported = string.Join(", ", All.Select(host => host.Key));
        throw new InvalidOperationException(
            $"Unknown automation host '{selection}'. Use 'auto' or one of: {supported}. " +
            "The COM ProgID can also be supplied directly.");
    }

    public static bool IsAutomaticSelection(string? selection) =>
        string.IsNullOrWhiteSpace(selection) ||
        string.Equals(selection, "auto", StringComparison.OrdinalIgnoreCase);
}
