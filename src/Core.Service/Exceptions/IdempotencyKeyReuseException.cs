using System;

namespace Core.Service.Exceptions;

public class IdempotencyKeyReuseException : Exception
{
    public IdempotencyKeyReuseException(string message) : base(message) { }
}
