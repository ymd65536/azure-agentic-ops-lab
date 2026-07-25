namespace AzureAgenticOps.Safety;

/// <summary>
/// Validates idempotency keys supplied with remediation actions. Keys must be
/// non-empty, bounded in length, and restricted to a safe character set so they
/// can be stored and compared reliably across components.
/// </summary>
public static class IdempotencyKeyValidator
{
    /// <summary>The maximum allowed idempotency key length.</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Validates an idempotency key.
    /// </summary>
    /// <param name="idempotencyKey">The key to validate.</param>
    /// <param name="failureReason">The reason validation failed, when it fails.</param>
    /// <returns><see langword="true"/> when the key is valid.</returns>
    public static bool IsValid(string? idempotencyKey, out string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            failureReason = "Idempotency key must not be null, empty, or whitespace.";
            return false;
        }

        if (idempotencyKey.Length > MaxLength)
        {
            failureReason = $"Idempotency key must not exceed {MaxLength} characters.";
            return false;
        }

        foreach (char character in idempotencyKey)
        {
            bool allowed = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.';
            if (!allowed)
            {
                failureReason = "Idempotency key may contain only ASCII letters, digits, '-', '_', ':' and '.'.";
                return false;
            }
        }

        failureReason = null;
        return true;
    }
}
