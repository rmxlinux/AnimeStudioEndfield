using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;

namespace AnimeStudio
{
    public sealed record EndfieldResourceKeyRequest(
        string AppCode,
        string Platform,
        string GameVersion,
        string PackageVersion,
        string RandStr);

    public static class EndfieldResourceKeyClient
    {
        private const string Endpoint = "https://launcher.hypergryph.com/api/game/get_latest_resources";
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        private static readonly object Sync = new();
        private static EndfieldResourceKeyRequest _request;
        private static Action<string> _saveCache;
        private static Dictionary<string, string> _cachedValues = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastAttemptUtc = DateTime.MinValue;
        private static string _lastConfigurationSignature;

        public static void Configure(
            EndfieldResourceKeyRequest request,
            string cachedValuesJson,
            Action<string> saveCache)
        {
            lock (Sync)
            {
                _request = request;
                _saveCache = saveCache;
                _lastAttemptUtc = DateTime.MinValue;
                _lastConfigurationSignature = null;
                _cachedValues = DeserializeCache(cachedValuesJson);
                EndfieldBundleKeyStore.ReplaceKeys(DecodeCachedValues(_cachedValues));
            }
        }

        public static void RefreshIfDue()
        {
            lock (Sync)
            {
                if (_request == null || DateTime.UtcNow - _lastAttemptUtc < RefreshInterval)
                {
                    return;
                }

                RefreshLocked();
            }
        }

        public static bool RefreshNow()
        {
            lock (Sync)
            {
                if (_request == null)
                {
                    Logger.ErrorSilently("Endfield resource keys are not configured.");
                    return false;
                }

                return RefreshLocked();
            }
        }

        private static bool RefreshLocked()
        {
            _lastAttemptUtc = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(_request.AppCode)
                || string.IsNullOrWhiteSpace(_request.Platform)
                || string.IsNullOrWhiteSpace(_request.GameVersion)
                || string.IsNullOrWhiteSpace(_request.PackageVersion)
                || string.IsNullOrWhiteSpace(_request.RandStr))
            {
                LogConfigurationErrorOnce("One or more required request parameters are empty.");
                return false;
            }

            try
            {
                var url = BuildUrl(_request);
                using var response = HttpClient.GetAsync(url).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var root = JObject.Parse(json);
                if (root["resource_keys"] is not JArray entries)
                {
                    Logger.ErrorSilently("Resource response has no resource_keys array.");
                    return false;
                }

                if (entries.Count == 0)
                {
                    Logger.ErrorSilently("Resource response resource_keys array is empty.");
                    return false;
                }

                var updated = new Dictionary<string, string>(_cachedValues, StringComparer.OrdinalIgnoreCase);
                var decodedKeys = new Dictionary<uint, byte[]>();

                foreach (var entry in entries)
                {
                    if (entry is not JObject resourceKey)
                    {
                        Logger.ErrorSilently("Skipping a malformed resource_keys entry.");
                        continue;
                    }

                    var idText = resourceKey["key"]?.Value<string>();
                    var encryptedValue = resourceKey["value"]?.Value<string>();
                    if (!TryParseKeyId(idText, out var id))
                    {
                        Logger.ErrorSilently($"Skipping resource key with invalid id '{idText}'.");
                        continue;
                    }

                    string error = null;
                    if (string.IsNullOrWhiteSpace(encryptedValue)
                        || !EndfieldBundleCrypto.TryDecodeResourceKeyValue(encryptedValue, out var key, out error))
                    {
                        Logger.ErrorSilently($"Could not decode resource key 0x{id:X8}: {error ?? "value is empty"}.");
                        continue;
                    }

                    decodedKeys[id] = key;
                    updated[id.ToString("X8", CultureInfo.InvariantCulture)] = encryptedValue;
                }

                if (decodedKeys.Count == 0)
                {
                    Logger.ErrorSilently("Resource response contained no usable bundle keys.");
                    return false;
                }

                _cachedValues = updated;
                EndfieldBundleKeyStore.AddOrUpdateKeys(decodedKeys);
                _saveCache?.Invoke(JsonConvert.SerializeObject(_cachedValues));
                Logger.InfoSilently($"Loaded {decodedKeys.Count} Endfield bundle resource keys.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.ErrorSilently("Could not refresh Endfield resource keys.", ex);
                return false;
            }
        }

        private static void LogConfigurationErrorOnce(string message)
        {
            var signature = string.Join("|", _request.AppCode, _request.Platform, _request.GameVersion,
                _request.PackageVersion, _request.RandStr, message);
            if (_lastConfigurationSignature == signature)
            {
                return;
            }

            _lastConfigurationSignature = signature;
            Logger.ErrorSilently($"Endfield resource key request configuration is incomplete. {message}");
        }

        private static string BuildUrl(EndfieldResourceKeyRequest request)
        {
            return Endpoint
                + "?appcode=" + Uri.EscapeDataString(request.AppCode.Trim())
                + "&platform=" + Uri.EscapeDataString(request.Platform.Trim())
                + "&game_version=" + Uri.EscapeDataString(request.GameVersion.Trim())
                + "&version=" + Uri.EscapeDataString(request.PackageVersion.Trim())
                + "&rand_str=" + Uri.EscapeDataString(request.RandStr.Trim());
        }

        private static bool TryParseKeyId(string idText, out uint id)
        {
            id = 0;
            if (string.IsNullOrWhiteSpace(idText))
            {
                return false;
            }

            var value = idText.Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = value[2..];
            }

            return uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
        }

        private static Dictionary<string, string> DeserializeCache(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException ex)
            {
                Logger.ErrorSilently("Saved Endfield resource key cache is invalid.", ex);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static Dictionary<uint, byte[]> DecodeCachedValues(Dictionary<string, string> values)
        {
            var keys = new Dictionary<uint, byte[]>();
            foreach (var entry in values)
            {
                if (!TryParseKeyId(entry.Key, out var id))
                {
                    Logger.ErrorSilently($"Skipping cached resource key with invalid id '{entry.Key}'.");
                    continue;
                }

                if (!EndfieldBundleCrypto.TryDecodeResourceKeyValue(entry.Value, out var key, out var error))
                {
                    Logger.ErrorSilently($"Could not decode cached resource key 0x{id:X8}: {error}.");
                    continue;
                }

                keys[id] = key;
            }

            return keys;
        }
    }
}
