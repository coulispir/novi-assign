using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace Core.Service.Strategies;

/// <summary>
/// Converts text to <typeparamref name="TEnum"/> by member name only, ignoring case. The default
/// <see cref="EnumConverter"/> also accepts numbers, including ones no member has, and comma-separated lists.
/// </summary>
public sealed class StrictEnumConverter<TEnum> : EnumConverter
    where TEnum : struct, Enum
{
    public StrictEnumConverter() : base(typeof(TEnum))
    {
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string text)
            return base.ConvertFrom(context, culture, value);

        var name = Enum.GetNames<TEnum>().FirstOrDefault(n => string.Equals(n, text.Trim(), StringComparison.OrdinalIgnoreCase));

        // Model binding reports a FormatException as "The value '...' is not valid for <parameter>."
        return name is null
            ? throw new FormatException($"'{text}' is not a valid {typeof(TEnum).Name}. Allowed values: {string.Join(", ", Enum.GetNames<TEnum>())}.")
            : Enum.Parse<TEnum>(name);
    }
}
