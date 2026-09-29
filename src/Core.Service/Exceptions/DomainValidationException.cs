using System;

namespace Core.Service.Exceptions;

// A request that breaks a validation rule (bad amount, currency or strategy). Kept apart from ArgumentException so that
// guard clauses catching programming errors are not reported to clients as their fault.
public class DomainValidationException : Exception
{
    public DomainValidationException(string message) : base(message) { }
}
