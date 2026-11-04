using System;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core;

namespace VirtoCommerce.UCP.Web.Mcp;

// Keeps the UCP messages[] body of a refused /ucp/mcp request. Declared with the ucp scope (WithChallengeWriter), so it
// writes the responses of that scope only.
public class UcpMcpChallengeWriter : McpChallengeWriter
{
    private const string LinkBuyerIdentityMessage = "Platform OAuth user identity is required to link the buyer account.";

    protected override object GetChallengeBody(McpChallenge challenge)
    {
        if (challenge == null)
        {
            return base.GetChallengeBody(challenge);
        }

        var message = string.IsNullOrEmpty(challenge.Error) &&
            string.Equals(challenge.ToolName, ModuleConstants.McpTools.LinkBuyerIdentity, StringComparison.Ordinal)
                ? LinkBuyerIdentityMessage
                : challenge.ErrorDescription;

        return CreateBody(ModuleConstants.ErrorCodes.IdentityRequired, message);
    }

    protected override object GetForbiddenBody(McpScope scope, string message)
    {
        return CreateBody(ModuleConstants.ErrorCodes.IdentityRequired, message);
    }

    private static object CreateBody(string code, string message)
    {
        return new
        {
            messages = new[]
            {
                new
                {
                    type = "error",
                    code,
                    content = message,
                    severity = "requires_buyer_review",
                },
            },
        };
    }
}
