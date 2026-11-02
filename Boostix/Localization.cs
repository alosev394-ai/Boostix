using System;
using System.Globalization;

namespace Boostix
{
    /// <summary>
    /// Interface language. The Russian text in the code is the source; the
    /// English table in LocalizationEnglish.cs translates it. A text without a
    /// translation is shown in Russian rather than not at all.
    /// </summary>
    internal static partial class L
    {
        internal const string Russian = "ru";
        internal const string English = "en";
        private static bool english;

        internal static string Language
        {
            get { return english ? English : Russian; }
        }

        internal static void SetLanguage(string language)
        {
            english = string.Equals(language, English, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Russian when either the Windows display language or the regional
        /// format belongs to the Russian-speaking audience: many of them run
        /// English Windows with Russian formats. English everywhere else.
        /// </summary>
        internal static string SystemDefault()
        {
            return SystemDefault(
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                CultureInfo.CurrentCulture.TwoLetterISOLanguageName);
        }

        internal static string SystemDefault(string displayLanguage, string formatLanguage)
        {
            return IsRussianAudience(displayLanguage) || IsRussianAudience(formatLanguage)
                ? Russian
                : English;
        }

        private static bool IsRussianAudience(string code)
        {
            return code == "ru" || code == "uk" || code == "be" || code == "kk";
        }

        /// <summary>
        /// A language passed as --lang=ru or --lang=en, or null.
        /// </summary>
        internal static string FromArguments(string[] arguments)
        {
            foreach (string argument in arguments ?? new string[0])
            {
                if (argument != null && argument.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = argument.Substring("--lang=".Length).Trim();
                    if (string.Equals(value, English, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, Russian, StringComparison.OrdinalIgnoreCase))
                    {
                        return value.ToLowerInvariant();
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Culture for dates with month names. Numbers keep the regional
        /// format, but "31 окт" in an English interface reads as a bug, so
        /// English uses English month names whatever the regional format.
        /// </summary>
        internal static CultureInfo DateCulture
        {
            get
            {
                CultureInfo current = CultureInfo.CurrentCulture;
                if (!english || current.TwoLetterISOLanguageName == "en")
                {
                    return current;
                }
                return CultureInfo.GetCultureInfo("en-GB");
            }
        }

        internal static string T(string russian)
        {
            string translated;
            return english && russian != null && EnglishText.TryGetValue(russian, out translated)
                ? translated
                : russian;
        }
    }
}
