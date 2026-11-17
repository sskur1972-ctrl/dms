//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* URS-7.28 password policy, driven by system_config (security.password.*):
 * minLength, requireUpper, requireLower, requireDigit, requireSymbol.
 */
using DMS.Model.Common;

namespace DMS.Web.CommonUtils
{
    public static class PasswordPolicy
    {
        /// <summary>Returns null when the password is acceptable, otherwise the reason.</summary>
        public static string? Validate(string? password, IEnumerable<AppSettings>? settings)
        {
            var list = settings?.ToList() ?? new List<AppSettings>();
            string Get(string key, string fallback) => list.FirstOrDefault(s => s.key == key)?.value is { Length: > 0 } v ? v : fallback;
            bool On(string key) => Get(key, "true").Equals("true", StringComparison.OrdinalIgnoreCase);

            var minLength = int.TryParse(Get("security.password.minLength", "8"), out var n) ? n : 8;
            if (string.IsNullOrEmpty(password) || password.Length < minLength)
                return $"Password must be at least {minLength} characters.";
            if (On("security.password.requireUpper") && !password.Any(char.IsUpper))
                return "Password must contain an upper-case letter.";
            if (On("security.password.requireLower") && !password.Any(char.IsLower))
                return "Password must contain a lower-case letter.";
            if (On("security.password.requireDigit") && !password.Any(char.IsDigit))
                return "Password must contain a digit.";
            if (On("security.password.requireSymbol") && password.All(char.IsLetterOrDigit))
                return "Password must contain a special character.";
            return null;
        }
    }
}
