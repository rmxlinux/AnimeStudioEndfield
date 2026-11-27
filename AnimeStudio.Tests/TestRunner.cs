using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AnimeStudio.Tests
{
    internal sealed class TestRunner
    {
        private readonly List<(string Name, Exception Error)> _failures = new();
        private int _count;

        public void Add(string name, Action body)
        {
            _count++;
            try
            {
                body();
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                _failures.Add((name, ex));
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine("        " + ex.Message.Replace("\r", string.Empty).Replace("\n", "\n        "));
                if (Environment.GetEnvironmentVariable("ANIMESTUDIO_TEST_TRACE") == "1")
                {
                    Console.WriteLine("        " + (ex.StackTrace ?? string.Empty).Replace("\r", string.Empty).Replace("\n", "\n        "));
                }
            }
        }

        public int Complete()
        {
            Console.WriteLine();
            Console.WriteLine($"{_count - _failures.Count}/{_count} tests passed.");
            if (_failures.Count == 0)
            {
                return 0;
            }

            Console.WriteLine($"{_failures.Count} failed:");
            foreach (var (name, error) in _failures)
            {
                Console.WriteLine($" - {name}: {error.GetType().Name}: {error.Message}");
            }

            return 1;
        }
    }

    internal static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("assert failed: " + message);
            }
        }

        public static void False(bool condition, string message) => True(!condition, message);

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception($"{message}: expected <{expected}>, got <{actual}>");
            }
        }

        public static void NotNull(object value, string message)
        {
            if (value is null)
            {
                throw new Exception(message + ": value is null");
            }
        }

        public static void BytesEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string message)
        {
            if (expected.SequenceEqual(actual))
            {
                return;
            }

            var index = 0;
            var limit = Math.Min(expected.Length, actual.Length);
            while (index < limit && expected[index] == actual[index])
            {
                index++;
            }

            throw new Exception(
                $"{message}: lengths {expected.Length}/{actual.Length}, first mismatch at {index} " +
                $"(expected {Describe(expected, index)}, got {Describe(actual, index)})");
        }

        public static TException Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new Exception($"{message}: expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}");
            }

            throw new Exception($"{message}: expected {typeof(TException).Name}, but nothing was thrown");
        }

        public static void Contains(string haystack, string needle, string message)
        {
            if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
            {
                throw new Exception($"{message}: expected text containing '{needle}', got '{haystack}'");
            }
        }

        private static string Describe(ReadOnlySpan<byte> buffer, int index)
        {
            if (index >= buffer.Length)
            {
                return "<end of buffer>";
            }

            var end = Math.Min(buffer.Length, index + 4);
            var builder = new StringBuilder();
            for (var i = index; i < end; i++)
            {
                if (builder.Length != 0)
                {
                    builder.Append(' ');
                }

                builder.Append(buffer[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }

    internal static class Hex
    {
        public static string ToHex(ReadOnlySpan<byte> data) => Convert.ToHexString(data);

        public static byte[] FromHex(string hex) => Convert.FromHexString(hex);

        public static byte[] DecodeBase64(string text) => Convert.FromBase64String(text);
    }
}
