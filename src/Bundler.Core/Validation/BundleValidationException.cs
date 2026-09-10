namespace Bundler.Core.Validation;

public sealed class BundleValidationException : Exception
{
    public BundleValidationException(IReadOnlyList<ValidationIssue> issues)
        : base($"Bundle configuration contains {issues.Count} validation error(s).")
    {
        Issues = issues;
    }

    public IReadOnlyList<ValidationIssue> Issues { get; }
}
