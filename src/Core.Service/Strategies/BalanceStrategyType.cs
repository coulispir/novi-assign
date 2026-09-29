using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Core.Service.Strategies;

/// <summary>
/// The balance adjustment strategies clients can choose. The member names are the public contract
/// (<c>?strategy=AddFundsStrategy</c>), so never rename them; the numeric values carry no meaning.
/// </summary>
// Serialised as names, so the OpenAPI document lists them as a string enum rather than integers
[JsonConverter(typeof(JsonStringEnumConverter<BalanceStrategyType>))]
// Bound from the query string by name only: the default converter would also accept "0" or "42"
[TypeConverter(typeof(StrictEnumConverter<BalanceStrategyType>))]
public enum BalanceStrategyType
{
    AddFundsStrategy,
    SubtractFundsStrategy,
    ForceSubtractFundsStrategy,
}
