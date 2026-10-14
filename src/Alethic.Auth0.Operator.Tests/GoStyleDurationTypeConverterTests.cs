using System;

using Alethic.Auth0.Operator.Options;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Alethic.Auth0.Operator.Tests
{

    [TestClass]
    public class GoStyleDurationTypeConverterTests
    {

        [TestMethod]
        [DataRow("10m", 0, 10, 0)]
        [DataRow("5m", 0, 5, 0)]
        [DataRow("1h", 1, 0, 0)]
        [DataRow("2h30m", 2, 30, 0)]
        [DataRow("1h30m45s", 1, 30, 45)]
        [DataRow("300s", 0, 5, 0)]
        [DataRow("90s", 0, 1, 30)]
        [DataRow("30s", 0, 0, 30)]
        [DataRow("1h1m1s", 1, 1, 1)]
        public void ParseDuration_GoStyle_ReturnsCorrectTimeSpan(string input, int expectedHours, int expectedMinutes, int expectedSeconds)
        {
            var result = GoStyleDurationTypeConverter.ParseDuration(input);

            Assert.AreEqual(expectedHours, result.Hours);
            Assert.AreEqual(expectedMinutes, result.Minutes);
            Assert.AreEqual(expectedSeconds, result.Seconds);
        }

        [TestMethod]
        [DataRow("500ms", 500)]
        [DataRow("1s500ms", 1500)]
        [DataRow("1m500ms", 60500)]
        public void ParseDuration_WithMilliseconds_ReturnsCorrectTimeSpan(string input, int expectedTotalMilliseconds)
        {
            var result = GoStyleDurationTypeConverter.ParseDuration(input);

            Assert.AreEqual(expectedTotalMilliseconds, (int)result.TotalMilliseconds);
        }

        [TestMethod]
        [DataRow("00:10:00", 0, 10, 0)]
        [DataRow("01:30:00", 1, 30, 0)]
        [DataRow("00:05:30", 0, 5, 30)]
        [DataRow("1:00:00:00", 24, 0, 0)] // 1 day
        public void ParseDuration_StandardTimeSpanFormat_ReturnsCorrectTimeSpan(string input, int expectedHours, int expectedMinutes, int expectedSeconds)
        {
            var result = GoStyleDurationTypeConverter.ParseDuration(input);

            Assert.AreEqual(expectedHours, (int)result.TotalHours);
            Assert.AreEqual(expectedMinutes, result.Minutes);
            Assert.AreEqual(expectedSeconds, result.Seconds);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow(null)]
        public void ParseDuration_EmptyOrNull_ThrowsFormatException(string? input)
        {
            Assert.ThrowsException<FormatException>(() => GoStyleDurationTypeConverter.ParseDuration(input!));
        }

        [TestMethod]
        [DataRow("invalid")]
        [DataRow("10x")]
        [DataRow("abc123")]
        [DataRow("10 minutes")]
        [DataRow("m10")]
        public void ParseDuration_InvalidFormat_ThrowsFormatException(string input)
        {
            Assert.ThrowsException<FormatException>(() => GoStyleDurationTypeConverter.ParseDuration(input));
        }

        [TestMethod]
        public void FormatDuration_Zero_Returns0s()
        {
            var result = GoStyleDurationTypeConverter.FormatDuration(TimeSpan.Zero);

            Assert.AreEqual("0s", result);
        }

        [TestMethod]
        [DataRow(0, 10, 0, 0, "10m")]
        [DataRow(1, 0, 0, 0, "1h")]
        [DataRow(1, 30, 0, 0, "1h30m")]
        [DataRow(0, 0, 45, 0, "45s")]
        [DataRow(2, 15, 30, 0, "2h15m30s")]
        [DataRow(0, 0, 0, 500, "500ms")]
        [DataRow(0, 0, 1, 500, "1s500ms")]
        public void FormatDuration_ReturnsGoStyleFormat(int hours, int minutes, int seconds, int milliseconds, string expected)
        {
            var timeSpan = new TimeSpan(0, hours, minutes, seconds, milliseconds);

            var result = GoStyleDurationTypeConverter.FormatDuration(timeSpan);

            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        public void RoundTrip_ParseThenFormat_PreservesValue()
        {
            var inputs = new[] { "10m", "1h30m", "45s", "2h15m30s", "500ms" };

            foreach (var input in inputs)
            {
                var parsed = GoStyleDurationTypeConverter.ParseDuration(input);
                var formatted = GoStyleDurationTypeConverter.FormatDuration(parsed);
                var reparsed = GoStyleDurationTypeConverter.ParseDuration(formatted);

                Assert.AreEqual(parsed, reparsed, $"Round-trip failed for '{input}'");
            }
        }

        [TestMethod]
        public void TypeConverter_CanConvertFromString()
        {
            var converter = new GoStyleDurationTypeConverter();

            Assert.IsTrue(converter.CanConvertFrom(null, typeof(string)));
        }

        [TestMethod]
        public void TypeConverter_CanConvertToString()
        {
            var converter = new GoStyleDurationTypeConverter();

            Assert.IsTrue(converter.CanConvertTo(null, typeof(string)));
        }

        [TestMethod]
        public void TypeConverter_ConvertFrom_ParsesGoStyle()
        {
            var converter = new GoStyleDurationTypeConverter();

            var result = converter.ConvertFrom(null, null, "10m");

            Assert.IsInstanceOfType(result, typeof(TimeSpan));
            Assert.AreEqual(TimeSpan.FromMinutes(10), result);
        }

        [TestMethod]
        public void TypeConverter_ConvertTo_FormatsAsGoStyle()
        {
            var converter = new GoStyleDurationTypeConverter();
            var timeSpan = TimeSpan.FromMinutes(10);

            var result = converter.ConvertTo(null, null, timeSpan, typeof(string));

            Assert.AreEqual("10m", result);
        }

    }

}
