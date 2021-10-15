#if NET45
using System;

namespace ClickHouse.Ado
{
    /// <summary>
    /// Extension for <see cref="DateTimeOffset"/>.
    /// </summary>
    public static class DateTimeOffsetExtensions
    {
        // Number of days in a non-leap year
        private const int DaysPerYear = 365;
        // Number of days in 4 years
        private const int DaysPer4Years = DaysPerYear * 4 + 1;       // 1461
        // Number of days in 100 years
        private const int DaysPer100Years = DaysPer4Years * 25 - 1;  // 36524
        // Number of days in 400 years
        private const int DaysPer400Years = DaysPer100Years * 4 + 1; // 146097

        // Number of days from 1/1/0001 to 12/31/1969
        internal const int DaysTo1970 = DaysPer400Years * 4 + DaysPer100Years * 3 + DaysPer4Years * 17 + DaysPerYear; // 719,162

        /// <summary>
        /// Convert to Unix time ms.
        /// </summary>
        /// <param name="dateTimeOffset">Date time offset.</param>
        /// <returns>Milliseconds from 1970.</returns>
        public static long ToUnixTimeMilliseconds(this DateTimeOffset dateTimeOffset)
        {
            long milliseconds = dateTimeOffset.UtcDateTime.Ticks / TimeSpan.TicksPerMillisecond;
            return milliseconds - TimeSpan.TicksPerDay * DaysTo1970 / TimeSpan.TicksPerMillisecond;
        }
    }
}
#endif