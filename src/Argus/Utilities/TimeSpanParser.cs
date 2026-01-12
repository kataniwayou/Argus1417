using System.Text.RegularExpressions;

namespace Argus.Utilities;

/// <summary>
/// Utility for parsing time duration strings with units.
/// Supports: s (seconds), m (minutes), h (hours), d (days)
/// Examples: "120s", "4m", "8h", "3d"
/// </summary>
public static class TimeSpanParser
{
    private static readonly Regex TimeSpanRegex = new(@"^(\d+)(s|m|h|d)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse a time duration string into seconds.
    /// </summary>
    /// <param name="input">Time string (e.g., "120s", "4m", "8h", "3d")</param>
    /// <returns>Duration in seconds</returns>
    /// <exception cref="ArgumentException">If the format is invalid</exception>
    public static int ParseToSeconds(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Time duration string cannot be null or empty", nameof(input));
        }

        var match = TimeSpanRegex.Match(input.Trim());
        if (!match.Success)
        {
            throw new ArgumentException(
                $"Invalid time duration format: '{input}'. Expected format: <number><unit> where unit is s, m, h, or d (e.g., '120s', '4m', '8h', '3d')",
                nameof(input));
        }

        if (!int.TryParse(match.Groups[1].Value, out var value))
        {
            throw new ArgumentException($"Invalid numeric value in time duration: '{input}'", nameof(input));
        }

        if (value < 0)
        {
            throw new ArgumentException($"Time duration value cannot be negative: '{input}'", nameof(input));
        }

        var unit = match.Groups[2].Value.ToLowerInvariant();
        var seconds = unit switch
        {
            "s" => value,
            "m" => value * 60,
            "h" => value * 3600,
            "d" => value * 86400,
            _ => throw new ArgumentException($"Unknown time unit: '{unit}'", nameof(input))
        };

        return seconds;
    }

    /// <summary>
    /// Try to parse a time duration string into seconds.
    /// </summary>
    /// <param name="input">Time string (e.g., "120s", "4m", "8h", "3d")</param>
    /// <param name="seconds">Output: duration in seconds if successful</param>
    /// <returns>True if parsing succeeded, false otherwise</returns>
    public static bool TryParseToSeconds(string? input, out int seconds)
    {
        seconds = 0;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            seconds = ParseToSeconds(input);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parse a time duration string into a TimeSpan.
    /// </summary>
    /// <param name="input">Time string (e.g., "120s", "4m", "8h", "3d")</param>
    /// <returns>TimeSpan representing the duration</returns>
    public static TimeSpan ParseToTimeSpan(string input)
    {
        var seconds = ParseToSeconds(input);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Try to parse a time duration string into a TimeSpan.
    /// </summary>
    /// <param name="input">Time string (e.g., "120s", "4m", "8h", "3d")</param>
    /// <param name="timeSpan">Output: TimeSpan if successful</param>
    /// <returns>True if parsing succeeded, false otherwise</returns>
    public static bool TryParseToTimeSpan(string? input, out TimeSpan timeSpan)
    {
        timeSpan = TimeSpan.Zero;

        if (TryParseToSeconds(input, out var seconds))
        {
            timeSpan = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Format seconds into a human-readable time duration string.
    /// Uses the most appropriate unit (d, h, m, or s).
    /// </summary>
    /// <param name="seconds">Duration in seconds</param>
    /// <returns>Formatted string (e.g., "3d", "8h", "4m", "120s")</returns>
    public static string FormatSeconds(int seconds)
    {
        if (seconds < 0)
        {
            throw new ArgumentException("Seconds cannot be negative", nameof(seconds));
        }

        // Use the largest unit that divides evenly
        if (seconds % 86400 == 0 && seconds >= 86400)
        {
            return $"{seconds / 86400}d";
        }
        if (seconds % 3600 == 0 && seconds >= 3600)
        {
            return $"{seconds / 3600}h";
        }
        if (seconds % 60 == 0 && seconds >= 60)
        {
            return $"{seconds / 60}m";
        }
        return $"{seconds}s";
    }
}

