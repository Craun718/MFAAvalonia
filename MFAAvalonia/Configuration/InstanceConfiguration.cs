using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace MFAAvalonia.Configuration;

public sealed class InstanceConfiguration
{
    private readonly string _instanceId;
    private Dictionary<string, object> _config;
    private const int MaxReportedDeserializationFailures = 1024;
    private static readonly object DeserializationFailureLock = new();
    private static readonly HashSet<DeserializationFailureFingerprint> ReportedDeserializationFailures = new();
    private static readonly Queue<DeserializationFailureFingerprint> DeserializationFailureOrder = new();
    private volatile bool _isDeleted;

    internal static string InstancesDir => AppPaths.InstancesDirectory;

    public InstanceConfiguration(string instanceId)
    {
        _instanceId = instanceId;
        _config = LoadInstanceConfig();
    }

    /// <summary>
    /// 获取实例配置文件路径：config/instances/{id}.json
    /// </summary>
    public string GetConfigFilePath() =>
        Path.Combine(InstancesDir, $"{_instanceId}.json");

    public bool ConfigFileExists()
        => File.Exists(GetConfigFilePath());

    public bool HasLocalConfigData()
        => _config.Count > 0;

    /// <summary>
    /// 加载实例独立配置文件
    /// </summary>
    private Dictionary<string, object> LoadInstanceConfig()
    {
        if (!Directory.Exists(InstancesDir))
            Directory.CreateDirectory(InstancesDir);

        var filePath = GetConfigFilePath();
        if (!File.Exists(filePath))
            return new Dictionary<string, object>();

        return JsonHelper.LoadJson(filePath, new Dictionary<string, object>());
    }

    /// <summary>
    /// 保存实例配置到独立文件
    /// </summary>
    private void SaveInstanceConfig()
    {
        if (_isDeleted) return;

        if (!Directory.Exists(InstancesDir))
            Directory.CreateDirectory(InstancesDir);

        JsonHelper.SaveJson(
            GetConfigFilePath(),
            _config,
            new MaaInterfaceSelectAdvancedConverter(false),
            new MaaInterfaceSelectOptionConverter(false));
    }

    private MFAConfiguration GlobalConfig => ConfigurationManager.Current;

    private bool ShouldPersistFallbackValue()
    {
        if (_isDeleted)
            return false;

        if (_instanceId != "default")
            return true;

        return File.Exists(GetConfigFilePath());
    }

    private void PersistFallbackValue(string key, object? value)
    {
        if (!ShouldPersistFallbackValue())
            return;

        SetValue(key, value);
    }

    public bool ContainsKey(string key)
        => _config.ContainsKey(key) || GlobalConfig.ContainsKey(key);

    public void SetValue(string key, object? value)
    {
        if (value == null || _isDeleted) return;
        _config[key] = value;
        SaveInstanceConfig();
    }

    public void RemoveValue(string key)
    {
        if (_isDeleted) return;
        if (!_config.Remove(key)) return;
        SaveInstanceConfig();
    }

    public T GetValue<T>(string key, T defaultValue)
    {
        // 优先从实例独立配置读取
        if (_config.TryGetValue(key, out var data))
        {
            var result = ConvertValue<T>(data);
            if (result != null) return result;
        }

        // 回退：从全局 config.json 的旧 scoped key 读取（兼容过渡）
        var scopedKey = $"Instance.{_instanceId}.{key}";
        if (GlobalConfig.ContainsKey(scopedKey))
        {
            var value = GlobalConfig.GetValue<T>(scopedKey, defaultValue);
            // 迁移到实例文件；临时 default 实例不落盘，避免重建 default.json
            PersistFallbackValue(key, value);
            return value;
        }

        // 回退：从 default 实例的 scoped key 读取
        if (_instanceId != "default")
        {
            var defaultScopedKey = $"Instance.default.{key}";
            if (GlobalConfig.ContainsKey(defaultScopedKey))
            {
                var value = GlobalConfig.GetValue<T>(defaultScopedKey, defaultValue);
                PersistFallbackValue(key, value);
                return value;
            }
        }

        // 回退：从全局 config.json 的无前缀 key 读取（最早版本兼容）
        if (GlobalConfig.ContainsKey(key))
        {
            var value = GlobalConfig.GetValue<T>(key, defaultValue);
            PersistFallbackValue(key, value);
            return value;
        }

        return defaultValue;
    }

    public T GetValue<T>(string key, T defaultValue, List<T> whitelist)
    {
        var value = GetValue(key, defaultValue);
        return whitelist.Contains(value) ? value : defaultValue;
    }

    public T GetValue<T>(string key, T defaultValue, Dictionary<object, T> options)
    {
        if (_config.TryGetValue(key, out var data))
        {
            if (options != null && options.TryGetValue(data, out var result))
                return result;
            var converted = ConvertValue<T>(data);
            if (converted != null) return converted;
        }

        // 回退到全局配置
        var scopedKey = $"Instance.{_instanceId}.{key}";
        if (GlobalConfig.ContainsKey(scopedKey))
        {
            var value = GlobalConfig.GetValue<T>(scopedKey, defaultValue, options);
            PersistFallbackValue(key, value);
            return value;
        }

        if (_instanceId != "default")
        {
            var defaultScopedKey = $"Instance.default.{key}";
            if (GlobalConfig.ContainsKey(defaultScopedKey))
            {
                var value = GlobalConfig.GetValue<T>(defaultScopedKey, defaultValue, options);
                PersistFallbackValue(key, value);
                return value;
            }
        }

        if (GlobalConfig.ContainsKey(key))
        {
            var value = GlobalConfig.GetValue<T>(key, defaultValue, options);
            PersistFallbackValue(key, value);
            return value;
        }

        return defaultValue;
    }

    public T GetValue<T>(string key, T defaultValue, T? noValue = default, params JsonConverter[] valueConverters)
    {
        if (_config.TryGetValue(key, out var data))
        {
            try
            {
                var settings = new JsonSerializerSettings();
                foreach (var converter in valueConverters)
                    settings.Converters.Add(converter);
                var result = JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(data), settings) ?? defaultValue;
                if (result != null && !result.Equals(noValue))
                    return result;
            }
            catch (Exception e)
            {
                LoggerHelper.Error($"类型转换失败: {e.Message}");
            }
        }

        // 回退到全局配置
        var scopedKey = $"Instance.{_instanceId}.{key}";
        if (GlobalConfig.ContainsKey(scopedKey))
        {
            var value = GlobalConfig.GetValue<T>(scopedKey, defaultValue, noValue, valueConverters);
            PersistFallbackValue(key, value);
            return value;
        }

        if (_instanceId != "default")
        {
            var defaultScopedKey = $"Instance.default.{key}";
            if (GlobalConfig.ContainsKey(defaultScopedKey))
            {
                var value = GlobalConfig.GetValue<T>(defaultScopedKey, defaultValue, noValue, valueConverters);
                PersistFallbackValue(key, value);
                return value;
            }
        }

        if (GlobalConfig.ContainsKey(key))
        {
            var value = GlobalConfig.GetValue<T>(key, defaultValue, noValue, valueConverters);
            PersistFallbackValue(key, value);
            return value;
        }

        return defaultValue;
    }

    public T GetValue<T>(string key, T defaultValue, List<T>? noValue = null, params JsonConverter[] valueConverters)
    {
        if (_config.TryGetValue(key, out var data))
        {
            try
            {
                var settings = new JsonSerializerSettings();
                foreach (var converter in valueConverters)
                    settings.Converters.Add(converter);
                var result = JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(data), settings) ?? defaultValue;
                if (noValue == null || !noValue.Contains(result))
                    return result;
            }
            catch (Exception e)
            {
                LoggerHelper.Error($"类型转换失败: {e.Message}");
            }
        }

        // 回退到全局配置
        var scopedKey = $"Instance.{_instanceId}.{key}";
        if (GlobalConfig.ContainsKey(scopedKey))
        {
            var value = GlobalConfig.GetValue<T>(scopedKey, defaultValue, noValue, valueConverters);
            PersistFallbackValue(key, value);
            return value;
        }

        if (_instanceId != "default")
        {
            var defaultScopedKey = $"Instance.default.{key}";
            if (GlobalConfig.ContainsKey(defaultScopedKey))
            {
                var value = GlobalConfig.GetValue<T>(defaultScopedKey, defaultValue, noValue, valueConverters);
                PersistFallbackValue(key, value);
                return value;
            }
        }

        if (GlobalConfig.ContainsKey(key))
        {
            var value = GlobalConfig.GetValue<T>(key, defaultValue, noValue, valueConverters);
            PersistFallbackValue(key, value);
            return value;
        }

        return defaultValue;
    }

    public bool TryGetValue<T>(string key, out T output, params JsonConverter[] valueConverters)
    {
        if (TryGetLocalValue(key, out output, valueConverters))
            return true;

        // 回退到全局配置
        var scopedKey = $"Instance.{_instanceId}.{key}";
        if (GlobalConfig.TryGetValue(scopedKey, out output, valueConverters))
        {
            PersistFallbackValue(key, output);
            return true;
        }

        if (_instanceId != "default")
        {
            var defaultScopedKey = $"Instance.default.{key}";
            if (GlobalConfig.TryGetValue(defaultScopedKey, out output, valueConverters))
            {
                PersistFallbackValue(key, output);
                return true;
            }
        }

        if (GlobalConfig.TryGetValue(key, out output, valueConverters))
        {
            PersistFallbackValue(key, output);
            return true;
        }

        output = default!;
        return false;
    }

    /// <summary>
    /// 仅从当前实例配置读取指定值；读取失败时记录诊断，不回退或迁移其他配置。
    /// </summary>
    public bool TryGetLocalValue<T>(string key, out T output, params JsonConverter[] valueConverters)
    {
        if (_config.TryGetValue(key, out var data))
        {
            string? serializedData = null;
            try
            {
                var settings = new JsonSerializerSettings();
                foreach (var converter in valueConverters)
                    settings.Converters.Add(converter);
                serializedData = JsonConvert.SerializeObject(data);
                output = JsonConvert.DeserializeObject<T>(serializedData, settings) ?? default!;
                return true;
            }
            catch (Exception ex)
            {
                var converterSignature = string.Join("\0", Array.ConvertAll(valueConverters,
                    converter => converter is null
                        ? "<null>"
                        : converter.GetType().AssemblyQualifiedName ?? converter.GetType().FullName ?? converter.GetType().Name));
                var targetType = typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name;
                var failureContent = serializedData ??
                    $"serialization-failure\0{(data is null ? 0 : RuntimeHelpers.GetHashCode(data))}\0{ex.GetType().AssemblyQualifiedName}";
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(failureContent)));
                var fingerprint = new DeserializationFailureFingerprint(
                    _instanceId, key, contentHash, targetType, converterSignature);
                if (TryRegisterDeserializationFailure(fingerprint))
                {
                    var exceptionSummary = GetRedactedExceptionSummary(ex, serializedData, key);
                    LoggerHelper.Error($"实例配置反序列化失败：实例={SanitizeLogField(_instanceId)}，配置项={SanitizeLogField(key)}，目标类型={SanitizeLogField(typeof(T).FullName ?? typeof(T).Name)}，配置文件={SanitizeLogField(GetConfigFilePath())}，异常类型={SanitizeLogField(ex.GetType().FullName ?? ex.GetType().Name)}，异常摘要={SanitizeLogField(exceptionSummary)}");
                }
            }
        }

        output = default!;
        return false;
    }

    private static bool TryRegisterDeserializationFailure(DeserializationFailureFingerprint fingerprint)
    {
        lock (DeserializationFailureLock)
        {
            if (!ReportedDeserializationFailures.Add(fingerprint))
                return false;

            DeserializationFailureOrder.Enqueue(fingerprint);
            while (DeserializationFailureOrder.Count > MaxReportedDeserializationFailures)
                ReportedDeserializationFailures.Remove(DeserializationFailureOrder.Dequeue());

            return true;
        }
    }

    private readonly record struct DeserializationFailureFingerprint(
        string InstanceId, string Key, string ContentHash, string TargetType, string ConverterSignature);

    private static string GetRedactedExceptionSummary(Exception exception, string? serializedData, string key)
    {
#if DEBUG
        return exception.Message;
#else
        if (serializedData is null)
            return "异常消息因无法检查配置值而省略";

        try
        {
            var configValue = JToken.Parse(serializedData);
            var sensitiveValues = new HashSet<string>(StringComparer.Ordinal);
            CollectSensitiveValues(configValue, IsSensitiveFieldName(key), sensitiveValues);

            var summary = exception.Message;
            foreach (var sensitiveValue in sensitiveValues.OrderByDescending(value => value.Length))
            {
                summary = summary.Replace(sensitiveValue, "[REDACTED]", StringComparison.Ordinal);
                var escapedValue = JsonConvert.ToString(sensitiveValue).Trim('"');
                if (!string.Equals(escapedValue, sensitiveValue, StringComparison.Ordinal))
                    summary = summary.Replace(escapedValue, "[REDACTED]", StringComparison.Ordinal);
            }

            return summary;
        }
        catch
        {
            return "异常消息因无法检查配置值而省略";
        }
#endif
    }

    private static void CollectSensitiveValues(JToken token, bool inheritedSensitive, HashSet<string> values)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties())
                    CollectSensitiveValues(property.Value, inheritedSensitive || IsSensitiveFieldName(property.Name), values);
                break;
            case JArray array:
                foreach (var item in array)
                    CollectSensitiveValues(item, inheritedSensitive, values);
                break;
            case JValue value when inheritedSensitive && value.Type != JTokenType.Null:
                var text = Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(text))
                    values.Add(text);
                break;
        }
    }

    private static bool IsSensitiveFieldName(string fieldName)
    {
        var normalized = fieldName.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("passwd", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("accesskey", StringComparison.Ordinal)
            || normalized.Contains("privatekey", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal);
    }

    private static string SanitizeLogField(string value)
    {
        const int maxLength = 512;
        var sanitized = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value)
        {
            if (char.IsControl(character))
                continue;

            if (sanitized.Length == maxLength)
                break;

            sanitized.Append(character);
        }

        return sanitized.ToString();
    }

    /// <summary>
    /// 从实例配置文件中快速读取指定 key 的字符串值（不创建完整实例）
    /// </summary>
    public static string ReadValueFromFile(string instanceId, string key, string defaultValue = "")
    {
        var filePath = Path.Combine(InstancesDir, $"{instanceId}.json");
        if (!File.Exists(filePath)) return defaultValue;

        try
        {
            var data = JsonHelper.LoadJson(filePath, new Dictionary<string, object>());
            if (data.TryGetValue(key, out var value))
            {
                if (value is string str)
                    return str;

                if (value is JValue jValue)
                    return jValue.ToString();

                if (value != null)
                    return Convert.ToString(value) ?? defaultValue;
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[实例加载] 读取实例名称失败: {filePath}", ex);
        }

        return defaultValue;
    }

    /// <summary>
    /// 从磁盘重新加载配置（迁移后调用，刷新内存中的过期数据）
    /// </summary>
    public void ReloadFromDisk()
    {
        _config = LoadInstanceConfig();
    }

    /// <summary>
    /// 将当前实例的配置复制到新实例的配置文件（排除实例名称），
    /// 需在新实例创建前调用，确保构造函数能读到完整配置
    /// </summary>
    public void CopyToNewInstance(string targetInstanceId)
    {
        if (!Directory.Exists(InstancesDir))
            Directory.CreateDirectory(InstancesDir);

        var data = new Dictionary<string, object>(_config);
        data.Remove(ConfigurationKeys.InstanceName);

        JsonHelper.SaveJson(
            Path.Combine(InstancesDir, $"{targetInstanceId}.json"),
            data,
            new MaaInterfaceSelectAdvancedConverter(false),
            new MaaInterfaceSelectOptionConverter(false));
    }

    /// <summary>
    /// 批量设置配置（用于迁移，只保存一次）
    /// </summary>
    public void SetValues(Dictionary<string, object> values)
    {
        foreach (var kvp in values)
        {
            _config[kvp.Key] = kvp.Value;
        }
        SaveInstanceConfig();
    }

    /// <summary>
    /// 删除实例配置文件
    /// </summary>
    public void DeleteConfigFile()
    {
        _isDeleted = true;
        var filePath = GetConfigFilePath();
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    /// <summary>
    /// 反序列化时使用的自定义转换器（与 SaveInstanceConfig 保持一致，确保读写对称）
    /// </summary>
    private static readonly JsonSerializerSettings DeserializeSettings = new()
    {
        Converters =
        {
            new MaaInterfaceSelectAdvancedConverter(false),
            new MaaInterfaceSelectOptionConverter(false),
        }
    };

    private static T? ConvertValue<T>(object data)
    {
        try
        {
            if (data is long longValue && typeof(T) == typeof(int))
                return (T)(object)Convert.ToInt32(longValue);

            if (data is T t)
                return t;

            if (data is JArray jArray)
                return JsonConvert.DeserializeObject<T>(jArray.ToString(), DeserializeSettings);

            if (data is JObject jObject)
                return JsonConvert.DeserializeObject<T>(jObject.ToString(), DeserializeSettings);
        }
        catch (Exception e)
        {
            LoggerHelper.Error("在进行类型转换时发生错误!", e);
        }

        return default;
    }
}
