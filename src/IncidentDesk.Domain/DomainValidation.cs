namespace IncidentDesk.Domain;

internal static class DomainValidation
{
    internal static string RequiredText(string? value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException("invalid_input", $"{field} must not be blank.", field);
        }

        if (value.Contains('\0'))
        {
            throw new DomainException("invalid_input", $"{field} must not contain null characters.", field);
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength)
        {
            throw new DomainException(
                "invalid_input",
                $"{field} must be {maximumLength} characters or fewer.",
                field);
        }

        return trimmed;
    }

    internal static void NonEmptyId(Guid value, string field)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("invalid_input", $"{field} must be a non-empty identifier.", field);
        }
    }

    internal static void DefinedEnum<T>(T value, string field) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new DomainException("invalid_input", $"{field} has an unsupported value.", field);
        }
    }
}
