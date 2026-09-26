using System.Windows;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

namespace ConnectionChecker;

public static class ValidationHelpers
{
    /// <summary>
    /// Restricts a TextBox to digits only: blocks typed non-digits (including
    /// '-'), the space key, and pastes containing anything but digits.
    /// </summary>
    public static void MakeNumeric(TextBox box)
    {
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        box.PreviewKeyDown += (_, e) => e.Handled = e.Key == Key.Space;
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            var text = e.DataObject.GetData(DataFormats.Text) as string;
            if (text == null || !text.All(char.IsAsciiDigit))
                e.CancelCommand();
        });
    }

    /// <summary>Opens the native colour picker seeded with the given hex; returns "#RRGGBB" or null on cancel.</summary>
    public static string? PickColor(string currentHex)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (ParseColor(currentHex) is { } seed)
            dialog.Color = System.Drawing.Color.FromArgb(seed.R, seed.G, seed.B);
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"
            : null;
    }

    /// <summary>Parses "#RRGGBB" (or any WPF colour string); null when invalid.</summary>
    public static System.Windows.Media.Color? ParseColor(string text)
    {
        try
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(text.Trim())
                is System.Windows.Media.Color c)
                return c;
        }
        catch
        {
            // fall through
        }
        return null;
    }

    /// <summary>
    /// Normalizes an HTTP path (and optional query): blank -> "/", adds the
    /// leading '/' if missing. Returns null when it contains whitespace.
    /// </summary>
    public static string? NormalizePath(string? input)
    {
        var path = (input ?? "").Trim();
        if (path.Any(char.IsWhiteSpace)) return null;
        return path.StartsWith('/') ? path : "/" + path;
    }

    /// <summary>
    /// Normalizes a domain name to look up: trims, drops a trailing dot, and
    /// converts international names to their ASCII (punycode) form. Returns
    /// null unless every label is 1-63 letters/digits/'-'/'_' and the whole
    /// name fits DNS's 253-character limit. IP literals are rejected: they
    /// aren't names to look up.
    /// </summary>
    public static string? NormalizeDnsName(string? input)
    {
        var name = (input ?? "").Trim().TrimEnd('.');
        if (name.Length == 0) return null;
        try
        {
            name = new System.Globalization.IdnMapping().GetAscii(name).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (name.Length > 253 || System.Net.IPAddress.TryParse(name, out _)) return null;
        return name.Split('.').All(label => label.Length is >= 1 and <= 63 &&
                                            label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            ? name
            : null;
    }

    /// <summary>
    /// Normalizes and validates a host/IP. Extracts the host from a pasted URL
    /// ("https://example.com/x" -> "example.com"). Returns null when invalid.
    /// </summary>
    public static string? NormalizeHost(string input)
    {
        var address = input.Trim();
        if (address.Length == 0) return null;

        // Derived-address tokens ({gateway}, {dns}) resolve at check time.
        if (Services.NetworkTokens.IsToken(address)) return address.ToLowerInvariant();

        // Someone pasted a full URL — take just the host part.
        if (address.Contains("://") &&
            Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            address = uri.Host;
        }

        if (address.Any(char.IsWhiteSpace)) return null;

        // IPv6 literals in their standard form: "[2606:4700::1111]" (as a URL
        // gives it) loses the brackets, an empty "%" scope is dropped, and a
        // real scope ("fe80::1%8", needed for link-local) is kept.
        if (address.Contains(':'))
        {
            return System.Net.IPAddress.TryParse(address, out var ip) &&
                   ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? ip.ToString()
                : null; // a colon anywhere else can't be a valid host
        }

        // Accepts DNS names, IPv4, and IPv6. Underscore hostnames (seen in the
        // wild on Windows networks) fail CheckHostName, so allow those too.
        var kind = Uri.CheckHostName(address);
        if (kind != UriHostNameType.Unknown) return address;
        return address.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            ? address
            : null;
    }
}
