using System;

namespace Core.Service.Exceptions;

// No exchange rate is known for one side of a conversion, so it cannot be computed
public class UnsupportedCurrencyException : Exception
{
    public UnsupportedCurrencyException(string message) : base(message) { }
}
