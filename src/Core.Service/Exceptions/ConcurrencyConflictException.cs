using System;

namespace Core.Service.Exceptions;

public class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message) : base(message) { }
}
