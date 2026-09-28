using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Data.Services;

internal static class UcpInventoryErrorNormalizer
{
    public static IList<UcpError> ReadErrors(JsonElement cart, IDictionary<string, object> command = null)
    {
        var result = new List<UcpError>();
        if (cart.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        var items = cart.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList()
            : [];
        AddErrors(result, cart, default, items, command);
        foreach (var item in items)
        {
            AddErrors(result, item, item, items, command);
        }

        return result;
    }

    public static IList<UcpError> ReadMutationErrors(JsonElement cart, IDictionary<string, object> command)
    {
        var errors = ReadErrors(cart, command);
        var productId = ReadCommandString(command, "productId");
        var lineItemId = ReadCommandString(command, "lineItemId");
        if (productId == null && lineItemId == null)
        {
            return errors;
        }

        // Existing errors on other items must not prevent a multi-item repair.
        // The final cart is validated separately before reporting success.
        return errors.Where(error => lineItemId != null
            ? !error.Details.TryGetValue("line_item_id", out var line) || lineItemId == line as string
            : !error.Details.TryGetValue("product_id", out var product) || productId == product as string).ToList();
    }

    public static void ThrowIfInvalid(UcpCart cart, string correlationId)
    {
        if (cart.InventoryErrors?.Count > 0)
        {
            var first = cart.InventoryErrors[0];
            var error = new UcpError
            {
                Code = first.Code,
                Message = "The requested operation could not be fulfilled. " + first.Message
                    + " The cart may still contain unfulfillable quantities. Review and correct it before checkout.",
                CorrelationId = correlationId,
                Details = new Dictionary<string, object>(first.Details)
                {
                    ["cart_id"] = cart.Id,
                    ["operation_rejected"] = true,
                    // XCart mutations are sequential; this is not a promise of rollback.
                    ["errors"] = cart.InventoryErrors,
                },
            };
            throw new UcpException(error.Code, error.Message, 409) { Error = error };
        }
    }

    private static void AddErrors(List<UcpError> result, JsonElement owner, JsonElement item,
        IList<JsonElement> items, IDictionary<string, object> command)
    {
        if (!owner.TryGetProperty("validationErrors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var error in errors.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
        {
            var sourceCode = ReadString(error, "errorCode");
            if (sourceCode is not ("PRODUCT_FFC_QTY" or "PRODUCT_QTY_CHANGED" or "PRODUCT_QTY_INSUFFICIENT" or "PRODUCT_MIN_QTY_NOT_AVAILABLE"))
            {
                continue;
            }

            result.Add(NormalizeError(error, sourceCode, item, items, command));
        }
    }

    private static UcpError NormalizeError(JsonElement error, string sourceCode, JsonElement item,
        IList<JsonElement> items, IDictionary<string, object> command)
    {
        var identity = ReadIdentity(error, item, items, command);
        var available = ReadParameter(error, "availableQty", "availQty");
        var requested = ReadParameter(error, "qty", "new_qty")
            ?? (identity.MatchesCommand ? ReadCommandQuantity(command) : null)
            ?? ReadQuantity(identity.Item);
        var code = (sourceCode, available) switch
        {
            (_, 0) => ModuleConstants.ErrorCodes.OutOfStock,
            ("PRODUCT_MIN_QTY_NOT_AVAILABLE", _) => ModuleConstants.ErrorCodes.InventoryUnavailable,
            _ => ModuleConstants.ErrorCodes.InsufficientStock,
        };
        var details = new Dictionary<string, object>
        {
            ["retryable"] = code == ModuleConstants.ErrorCodes.InsufficientStock && available > 0,
        };
        AddDetail(details, "product_id", identity.ProductId);
        AddDetail(details, "line_item_id", identity.LineItemId);
        AddDetail(details, "requested_quantity", requested);
        AddDetail(details, "available_quantity", available);
        return new UcpError
        {
            Code = code,
            Message = CreateMessage(code, available),
            Details = details,
        };
    }

    private static (string ProductId, string LineItemId, JsonElement Item, bool MatchesCommand) ReadIdentity(
        JsonElement error, JsonElement item, IList<JsonElement> items, IDictionary<string, object> command)
    {
        var objectId = ReadString(error, "objectId");
        var objectType = ReadString(error, "objectType");
        var isProduct = objectType is "CatalogProduct" or "CartProduct";
        var commandProduct = ReadCommandString(command, "productId");
        var commandLine = ReadCommandString(command, "lineItemId");
        var relatedItem = item.ValueKind == JsonValueKind.Object ? item
            : FindRelatedItem(items, objectId, objectType, isProduct, commandLine);
        var productId = ReadString(relatedItem, "productId") ?? (isProduct ? objectId : null);
        var lineItemId = ReadString(relatedItem, "id") ?? (objectType == "LineItem" ? objectId : null);
        var matchesCommand = MatchesCommand(productId, lineItemId, objectId, item, command);
        if (matchesCommand)
        {
            productId ??= commandProduct;
            lineItemId ??= commandLine;
        }
        return (productId, lineItemId, relatedItem, matchesCommand);
    }

    private static bool MatchesCommand(string productId, string lineItemId, string objectId, JsonElement item,
        IDictionary<string, object> command)
    {
        if (productId != null && productId == ReadCommandString(command, "productId"))
        {
            return true;
        }
        if (lineItemId != null && lineItemId == ReadCommandString(command, "lineItemId"))
        {
            return true;
        }
        return objectId == null && item.ValueKind != JsonValueKind.Object;
    }

    private static JsonElement FindRelatedItem(IList<JsonElement> items, string objectId, string objectType, bool isProduct, string commandLine)
    {
        if (objectId == null)
        {
            return default;
        }
        if (objectType == "LineItem")
        {
            return items.FirstOrDefault(x => ReadString(x, "id") == objectId);
        }
        if (!isProduct)
        {
            return default;
        }
        var candidates = items.Where(x => ReadString(x, "productId") == objectId).ToList();
        return candidates.Count == 1 ? candidates[0]
            : candidates.FirstOrDefault(x => commandLine != null && ReadString(x, "id") == commandLine);
    }

    private static string CreateMessage(string code, long? available)
    {
        if (code == ModuleConstants.ErrorCodes.OutOfStock)
        {
            return "This product is out of stock.";
        }
        if (code == ModuleConstants.ErrorCodes.InsufficientStock && available.HasValue)
        {
            if (available == 1)
            {
                return "Only 1 unit of this product is currently available.";
            }
            return $"Only {available.Value.ToString(CultureInfo.InvariantCulture)} units of this product are currently available.";
        }
        if (code == ModuleConstants.ErrorCodes.InventoryUnavailable)
        {
            return "The minimum order quantity cannot be fulfilled with the current stock.";
        }
        return "The requested quantity is not available. Refresh the cart and choose an available quantity before continuing to checkout.";
    }

    private static long? ReadCommandQuantity(IDictionary<string, object> command)
    {
        return command?.TryGetValue("quantity", out var quantity) == true && quantity is int value ? value : null;
    }

    private static long? ReadParameter(JsonElement error, params string[] keys)
    {
        if (error.TryGetProperty("errorParameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var parameter in parameters.EnumerateArray())
            {
                if (keys.Contains(ReadString(parameter, "key")) &&
                    long.TryParse(ReadString(parameter, "value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
                {
                    return value;
                }
            }
        }
        return null;
    }

    private static long? ReadQuantity(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("quantity", out var quantity) ||
            quantity.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        return quantity.TryGetInt64(out var value) ? value : null;
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
    }

    private static string ReadCommandString(IDictionary<string, object> command, string name)
    {
        return command?.TryGetValue(name, out var value) == true ? value as string : null;
    }

    private static void AddDetail(Dictionary<string, object> details, string name, object value)
    {
        if (value != null)
        {
            details[name] = value;
        }
    }
}
