using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VirtoCommerce.Platform.Core.Settings;

namespace VirtoCommerce.UCP.Tests;

internal sealed class TestSettingsManager : ISettingsManager
{
    private readonly Dictionary<string, object> _values;

    public TestSettingsManager(params (string Name, object Value)[] values)
    {
        _values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            _values[name] = value;
        }
    }

    public List<string> ReadNames { get; } = [];

    public IEnumerable<SettingDescriptor> AllRegisteredSettings => [];

    public Task<ObjectSettingEntry> GetObjectSettingAsync(string name, string objectType = null, string objectId = null)
    {
        ReadNames.Add(name);
        _values.TryGetValue(name, out var value);

        return Task.FromResult(new ObjectSettingEntry { Name = name, Value = value });
    }

    public Task<IEnumerable<ObjectSettingEntry>> GetObjectSettingsAsync(IEnumerable<string> names, string objectType = null, string objectId = null)
    {
        throw new NotSupportedException();
    }

    public Task SaveObjectSettingsAsync(IEnumerable<ObjectSettingEntry> objectSettings)
    {
        throw new NotSupportedException();
    }

    public Task RemoveObjectSettingsAsync(IEnumerable<ObjectSettingEntry> objectSettings)
    {
        throw new NotSupportedException();
    }

    public void RegisterSettings(IEnumerable<SettingDescriptor> settings, string moduleId = null)
    {
        throw new NotSupportedException();
    }

    public void RegisterSettingsForType(IEnumerable<SettingDescriptor> settings, string typeName)
    {
        throw new NotSupportedException();
    }

    public IEnumerable<SettingDescriptor> GetSettingsForType(string typeName)
    {
        throw new NotSupportedException();
    }

    public IDictionary<string, string[]> GetSettingTypeAssignments()
    {
        throw new NotSupportedException();
    }
}
