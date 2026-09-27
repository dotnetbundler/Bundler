namespace DotNet.Bundler.Rpm;

/// <summary>
/// Parses rpm dependency clauses of the form <c>name</c> or
/// <c>name &lt;op&gt; evr</c> — op one of <c>&lt;</c>, <c>&lt;=</c>,
/// <c>=</c>, <c>&gt;=</c>, <c>&gt;</c> — into the name/flags/version triples
/// used by the REQUIRE*/PROVIDE*/CONFLICT*/OBSOLETE*/weak-dep tag families.
/// The syntax is passed through verbatim beyond the operator check; validity
/// of names and EVRs is left to the caller (roadmap decision: pass-through).
/// </summary>
internal static class RpmDependency
{
    internal const int Less = 2;      // RPMSENSE_LESS
    internal const int Greater = 4;   // RPMSENSE_GREATER
    internal const int Equal = 8;     // RPMSENSE_EQUAL
    internal const int Rpmlib = 16777216; // RPMSENSE_RPMLIB

    internal readonly struct Parsed
    {
        internal Parsed(string name, int flags, string version)
        {
            Name = name;
            Flags = flags;
            Version = version;
        }

        internal string Name { get; }
        internal int Flags { get; }
        internal string Version { get; }
    }

    internal static Parsed Parse(string clause)
    {
        var tokens = clause.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1)
        {
            ValidateName(tokens[0], clause);
            return new Parsed(tokens[0], 0, "");
        }
        if (tokens.Length == 3)
        {
            var flags = tokens[1] switch
            {
                "<" => Less,
                "<=" => Less | Equal,
                "=" => Equal,
                ">=" => Greater | Equal,
                ">" => Greater,
                _ => throw new ArgumentException(
                    $"'{tokens[1]}' is not a valid rpm dependency operator in '{clause}'" +
                    " (<, <=, =, >=, >).")
            };
            ValidateName(tokens[0], clause);
            return new Parsed(tokens[0], flags, tokens[2]);
        }
        throw new ArgumentException(
            $"'{clause}' is not a valid rpm dependency clause ('name' or 'name <op> evr').");
    }

    private static void ValidateName(string name, string clause)
    {
        if (name.Length == 0 || name == "(" || name == ")")
        {
            throw new ArgumentException(
                $"'{clause}' is not a valid rpm dependency clause (empty capability name).");
        }
    }

    /// <summary>
    /// Appends the name/flags/version tag triple for a caller-supplied clause
    /// list; emits nothing when the list is empty.
    /// </summary>
    internal static void Emit(
        List<RpmHeaderWriter.Entry> entries,
        IReadOnlyList<string>? clauses,
        int nameTag, int flagsTag, int versionTag)
    {
        if (clauses is null || clauses.Count == 0)
        {
            return;
        }
        var parsed = clauses.Select(Parse).ToArray();
        entries.Add(RpmHeaderWriter.Strings(nameTag, parsed.Select(p => p.Name).ToArray()));
        entries.Add(RpmHeaderWriter.Int32s(flagsTag, parsed.Select(p => p.Flags).ToArray()));
        entries.Add(RpmHeaderWriter.Strings(versionTag, parsed.Select(p => p.Version).ToArray()));
    }
}
