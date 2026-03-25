using System.Windows.Automation;
using Microsoft.Extensions.Logging;

namespace BigBro.Collector.Collectors;

/// <summary>
/// Reads browser URL from the address bar using UI Automation.
/// Best-effort — falls back gracefully if the automation tree is unavailable.
/// </summary>
internal sealed class BrowserUrlReader
{
    private readonly ILogger _logger;
    private const int TimeoutMs = 500;

    public BrowserUrlReader(ILogger logger)
    {
        _logger = logger;
    }

    public string? TryGetUrl(IntPtr hwnd, string processName)
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            if (element is null) return null;

            // Different browsers expose the URL bar differently in the automation tree.
            // Common approach: find a control with ControlType.Edit that has a ValuePattern.
            // Chrome/Edge: The address bar is typically accessible via automation ID or name.

            return processName.ToLowerInvariant() switch
            {
                "chrome" or "msedge" or "brave" or "vivaldi" => ReadChromiumUrl(element),
                "firefox" => ReadFirefoxUrl(element),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read browser URL for {Process}.", processName);
            return null;
        }
    }

    private string? ReadChromiumUrl(AutomationElement window)
    {
        // Chromium browsers: the address bar is typically a ControlType.Edit element
        // named "Address and search bar" or with AutomationId "addressEditBox".
        // Strategy: find the first Edit control in the toolbar area with a ValuePattern.
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));

        var addressBar = window.FindFirst(TreeScope.Descendants, condition);
        if (addressBar is null) return null;

        if (addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern))
        {
            var value = ((ValuePattern)pattern).Current.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private string? ReadFirefoxUrl(AutomationElement window)
    {
        // Firefox: the URL bar has AutomationId = "urlbar-input" in recent versions.
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"),
            new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));

        var addressBar = window.FindFirst(TreeScope.Descendants, condition);
        if (addressBar is null)
        {
            // Fallback: search for any edit control with a ValuePattern
            return ReadChromiumUrl(window);
        }

        if (addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern))
        {
            var value = ((ValuePattern)pattern).Current.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
