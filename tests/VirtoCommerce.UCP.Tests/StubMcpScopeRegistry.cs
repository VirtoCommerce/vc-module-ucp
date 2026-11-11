using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core;

namespace VirtoCommerce.UCP.Tests;

// The MCP module's scope registry with the ucp scope switched on or off (MCP:Scopes:ucp:Enabled), served at /ucp/mcp
// and at /mcp of the host store.example.
internal sealed class StubMcpScopeRegistry : IMcpScopeRegistry
{
    public const string ScopeHost = "store.example";

    private readonly McpScope _ucpScope;

    public StubMcpScopeRegistry(bool ucpEnabled = true)
    {
        _ucpScope = ucpEnabled ? new McpScope { Name = ModuleConstants.McpScopeName } : null;
    }

    public IReadOnlyCollection<McpScope> Scopes => _ucpScope == null ? [] : [_ucpScope];

    public McpScope GetScope(string name)
    {
        return string.Equals(name, _ucpScope?.Name, StringComparison.OrdinalIgnoreCase) ? _ucpScope : null;
    }

    public McpScope FindScopeByPath(PathString path)
    {
        return _ucpScope != null && path.StartsWithSegments(_ucpScope.Path, StringComparison.OrdinalIgnoreCase) ? _ucpScope : null;
    }

    public McpScope FindScopeByHost(HostString host)
    {
        return string.Equals(host.Host, ScopeHost, StringComparison.OrdinalIgnoreCase) ? _ucpScope : null;
    }

    public McpScope FindScope(HostString host, PathString path)
    {
        return string.Equals(path.Value?.TrimEnd('/'), McpScope.HostEndpointPath, StringComparison.OrdinalIgnoreCase)
            ? FindScopeByHost(host)
            : FindScopeByPath(path);
    }
}
