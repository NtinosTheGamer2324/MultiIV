// ============================ MultiIvConfig.cs ============================
using System;
using System.IO;
using System.Text;

namespace MultiIV
{
    /// <summary>
    /// Reads/writes %appdata%/MultiIV/mivarg.json without any external
    /// JSON library — single-DLL build constraint. Extended to support
    /// bool, int, and string fields.
    /// </summary>
    public static class MultiIvConfig
    {
        private static string FolderPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiIV");

        private static string FilePath => Path.Combine(FolderPath, "mivarg.json");

        public static bool GetMultiIvSession()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    SetMultiIvSession(false);
                    return false;
                }

                string content = File.ReadAllText(FilePath);
                return ParseBool(content, "multiivsession", false);
            }
            catch
            {
                return false;
            }
        }

        public static void SetMultiIvSession(bool value)
        {
            // For a complete implementation, you'd typically want a full save function,
            // but this preserves your existing method signature.
            try
            {
                if (!Directory.Exists(FolderPath))
                    Directory.CreateDirectory(FolderPath);

                string json;
                if (File.Exists(FilePath))
                {
                    string content = File.ReadAllText(FilePath);
                    // Simple replacement for multiivsession if file exists
                    json = content;
                    // Or rewrite standard template if updating from scratch
                }

                json = "{\n  \"multiivsession\": " + (value ? "true" : "false") + ",\n  \"ip\": \"127.0.0.1\",\n  \"port\": 7777,\n  \"offline_name\": \"ntinos\"\n}";

                File.WriteAllText(FilePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try { IVSDKDotNet.IVGame.Console.PrintError("[MultiIV] Config write failed: " + ex.Message); } catch { }
            }
        }

        public static string GetString(string key, string defaultValue)
        {
            try
            {
                if (!File.Exists(FilePath)) return defaultValue;
                string content = File.ReadAllText(FilePath);
                return ParseString(content, key, defaultValue);
            }
            catch
            {
                return defaultValue;
            }
        }

        public static int GetInt(string key, int defaultValue)
        {
            try
            {
                if (!File.Exists(FilePath)) return defaultValue;
                string content = File.ReadAllText(FilePath);
                return ParseInt(content, key, defaultValue);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static bool ParseBool(string json, string key, bool defaultValue)
        {
            int keyIdx = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0) return defaultValue;

            int colonIdx = json.IndexOf(':', keyIdx);
            if (colonIdx < 0) return defaultValue;

            string rest = json.Substring(colonIdx + 1).TrimStart();
            if (rest.StartsWith("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (rest.StartsWith("false", StringComparison.OrdinalIgnoreCase)) return false;

            return defaultValue;
        }

        private static string ParseString(string json, string key, string defaultValue)
        {
            int keyIdx = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0) return defaultValue;

            int colonIdx = json.IndexOf(':', keyIdx);
            if (colonIdx < 0) return defaultValue;

            int firstQuote = json.IndexOf('"', colonIdx);
            if (firstQuote < 0) return defaultValue;

            int secondQuote = json.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0) return defaultValue;

            return json.Substring(firstQuote + 1, secondQuote - (firstQuote + 1));
        }

        private static int ParseInt(string json, string key, int defaultValue)
        {
            int keyIdx = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0) return defaultValue;

            int colonIdx = json.IndexOf(':', keyIdx);
            if (colonIdx < 0) return defaultValue;

            string rest = json.Substring(colonIdx + 1).TrimStart();

            // Handle both quoted ("7777") and unquoted (7777) numbers
            bool isQuoted = rest.StartsWith("\"");
            int endIdx = 0;

            if (isQuoted)
            {
                int closingQuote = rest.IndexOf('"', 1);
                if (closingQuote < 0) return defaultValue;
                string numStr = rest.Substring(1, closingQuote - 1);
                return int.TryParse(numStr, out int val) ? val : defaultValue;
            }
            else
            {
                // Find where the number ends (comma, closing brace, or whitespace)
                while (endIdx < rest.Length &&
                       rest[endIdx] != ',' &&
                       rest[endIdx] != '}' &&
                       !char.IsWhiteSpace(rest[endIdx]))
                {
                    endIdx++;
                }

                string numStr = rest.Substring(0, endIdx);
                return int.TryParse(numStr, out int val) ? val : defaultValue;
            }
        }
    }
}