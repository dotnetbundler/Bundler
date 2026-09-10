namespace Bundler.Core.Validation;

public sealed record ValidationIssue(string Path, string Message);
