using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Alethic.Auth0.Operator.Options
{

    /// <summary>
    /// A TypeConverter that parses Go/Kubernetes-style duration strings (e.g., "10m", "1h30m", "300s")
    /// into TimeSpan values. Also supports standard TimeSpan formats as a fallback.
    /// </summary>
    public partial class GoStyleDurationTypeConverter : TypeConverter
    {

        // Pattern matches Go-style durations: optional hours, minutes, seconds, milliseconds
        // Examples: "10m", "1h30m", "300s", "1h", "500ms", "1h30m45s"
        [GeneratedRegex(@"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?(?:(\d+)ms)?$", RegexOptions.Compiled)]
        private static partial Regex GoStyleDurationPattern();

        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        {
            return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
        }

        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        {
            return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
        }

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if (value is string stringValue)
            {
                return ParseDuration(stringValue);
            }

            return base.ConvertFrom(context, culture, value);
        }

        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is TimeSpan timeSpan)
            {
                return FormatDuration(timeSpan);
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }

        /// <summary>
        /// Parses a duration string in either Go-style format (e.g., "10m", "1h30m") 
        /// or standard TimeSpan format (e.g., "00:10:00").
        /// </summary>
        public static TimeSpan ParseDuration(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new FormatException("Duration string cannot be null or empty.");
            }

            input = input.Trim();

            // Try standard TimeSpan format first (e.g., "00:10:00", "1.02:30:00")
            if (TimeSpan.TryParse(input, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }

            // Try Go-style duration format
            var match = GoStyleDurationPattern().Match(input);
            if (match.Success && match.Length > 0 && match.Value == input)
            {
                // At least one component must be present
                if (!match.Groups[1].Success && !match.Groups[2].Success && 
                    !match.Groups[3].Success && !match.Groups[4].Success)
                {
                    throw new FormatException($"Unable to parse duration: '{input}'");
                }

                int hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                int minutes = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                int seconds = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                int milliseconds = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0;

                return new TimeSpan(days: 0, hours, minutes, seconds, milliseconds);
            }

            throw new FormatException($"Unable to parse duration: '{input}'. Expected formats: Go-style (e.g., '10m', '1h30m', '300s') or TimeSpan (e.g., '00:10:00').");
        }

        /// <summary>
        /// Formats a TimeSpan as a Go-style duration string.
        /// </summary>
        public static string FormatDuration(TimeSpan timeSpan)
        {
            if (timeSpan == TimeSpan.Zero)
            {
                return "0s";
            }

            var parts = new System.Text.StringBuilder();
            var totalHours = (int)timeSpan.TotalHours;
            var minutes = timeSpan.Minutes;
            var seconds = timeSpan.Seconds;
            var milliseconds = timeSpan.Milliseconds;

            if (totalHours > 0)
            {
                parts.Append(totalHours);
                parts.Append('h');
            }

            if (minutes > 0)
            {
                parts.Append(minutes);
                parts.Append('m');
            }

            if (seconds > 0)
            {
                parts.Append(seconds);
                parts.Append('s');
            }

            if (milliseconds > 0)
            {
                parts.Append(milliseconds);
                parts.Append("ms");
            }

            return parts.Length > 0 ? parts.ToString() : "0s";
        }

        /// <summary>
        /// Registers this converter globally for TimeSpan types.
        /// Call this early in application startup before configuration binding.
        /// </summary>
        public static void Register()
        {
            TypeDescriptor.AddAttributes(typeof(TimeSpan), new TypeConverterAttribute(typeof(GoStyleDurationTypeConverter)));
        }

    }

}
