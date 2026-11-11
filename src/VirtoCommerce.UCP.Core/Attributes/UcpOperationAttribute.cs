using System;

namespace VirtoCommerce.UCP.Core.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class UcpOperationAttribute : Attribute
{
    public UcpOperationAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    public bool IsXApiBacked { get; set; }
}
