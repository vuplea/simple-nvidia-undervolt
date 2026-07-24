using System.Globalization;

namespace SimpleNvidiaUndervolt;

/// <summary>Small argv parser for this CLI. Keeping argv as plain tokens lets elevation, shortcuts and
/// the logon task pass the same command line through without a dependency on a parser library, while
/// validation still stays strict: unknown, duplicated and unconsumed arguments fail early.</summary>
internal static class Args
{
    public static readonly Options Global = Options.Empty
        .WithBare("--silent");

    /// <summary>How a flag consumes the token after it.</summary>
    private enum Kind
    {
        Bare,          // never takes a value
        Value,         // requires a value (which may itself start with '-', e.g. a negative number)
        OptionalValue, // takes the next token unless it starts with '-'
    }

    /// <summary>A command's accepted options: each flag mapped to whether it takes a value. Commands
    /// extend <see cref="Global"/> instead of passing raw flag arrays around.</summary>
    public sealed class Options
    {
        public static readonly Options Empty = new(new Dictionary<string, Kind>(StringComparer.Ordinal));

        private readonly Dictionary<string, Kind> _flags;

        private Options(Dictionary<string, Kind> flags) => _flags = flags;

        public Options WithBare(params string[] flags) => With(Kind.Bare, flags);

        public Options WithValue(params string[] flags) => With(Kind.Value, flags);

        public Options WithOptionalValue(params string[] flags) => With(Kind.OptionalValue, flags);

        private Options With(Kind kind, string[] flags)
        {
            var extended = new Dictionary<string, Kind>(_flags, StringComparer.Ordinal);
            foreach (string flag in flags)
            {
                extended.Add(flag, kind);
            }

            return new(extended);
        }

        public Parsed Parse(string[] args) => Args.Parse(args, _flags, allowPositionals: false);

        public string[] Positionals(string[] args)
            => Args.Parse(args, _flags, allowPositionals: true).Positionals;
    }

    /// <summary>The parsed token stream: each given flag mapped to its value (null for a bare flag, or
    /// for an optional value that wasn't given), plus any positional tokens.</summary>
    public sealed class Parsed
    {
        private readonly IReadOnlyDictionary<string, string?> _options;

        internal Parsed(IReadOnlyDictionary<string, string?> options, string[] positionals)
        {
            _options = options;
            Positionals = positionals;
        }

        public string[] Positionals { get; }

        /// <summary>Whether the flag was given (with or without a value).</summary>
        public bool Has(string flag) => _options.ContainsKey(flag);

        /// <summary>The flag's value, or null when the flag is absent or was given without one.</summary>
        public string? Value(string flag) => _options.TryGetValue(flag, out string? value) ? value : null;

        /// <summary>The flag's value as a number, or null when the flag is absent. Parses invariantly: a
        /// value like "2.5" must mean two-and-a-half on every locale, not 25 as a comma-decimal culture
        /// (ro-RO/de-DE) would read it - a tuning command can't depend on the machine's regional
        /// settings. Rejects non-finite values (NaN/Infinity) so no downstream range check has to reason
        /// about what they truncate to.</summary>
        public double? Number(string flag)
        {
            if (Value(flag) is not { } raw)
            {
                return null;
            }

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value))
            {
                throw new CliError($"{flag} requires a numeric value.");
            }

            return value;
        }

        /// <summary>The flag's value as an absolute file path, or null when the flag is absent.
        /// Resolved against the current directory here, at parse time (which the elevation relay
        /// preserves), so every later use and error message names one unambiguous file.</summary>
        public string? FilePath(string flag)
        {
            if (Value(flag) is not { } raw)
            {
                return null;
            }

            try
            {
                return System.IO.Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException
                                           or NotSupportedException or IOException)
            {
                throw new CliError($"{flag}: '{raw}' isn't a usable path ({ex.Message}).");
            }
        }

        /// <summary>Like <see cref="Number"/>, but for a count: a fraction is rejected rather than
        /// rounded, so "--cap-points 2.7" fails instead of silently applying a band the user didn't
        /// ask for.</summary>
        public int? Integer(string flag)
        {
            if (Value(flag) is not { } raw)
            {
                return null;
            }

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                throw new CliError($"{flag} requires an integer value.");
            }

            return value;
        }
    }

    /// <summary>Parses and validates every token after the command word: each flag must be known and
    /// given at most once, a flag with a mandatory value must have one, and any token not consumed as a
    /// flag or a flag's value is rejected. On a tool that writes to hardware, a typo -
    /// <c>--no-persit</c>, <c>no-persist</c> without the dashes, a duplicated flag whose second value
    /// would be silently ignored - must fail rather than quietly change the run.</summary>
    private static Parsed Parse(string[] args, IReadOnlyDictionary<string, Kind> flags,
        bool allowPositionals)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (!flags.TryGetValue(arg, out Kind kind))
            {
                if (allowPositionals && !IsLongOption(arg))
                {
                    positionals.Add(arg);
                    continue;
                }

                throw new CliError(arg.StartsWith('-')
                    ? $"Unknown option '{arg}'. Run 'simple-nvidia-undervolt --help' for the supported options."
                    : $"Unexpected argument '{arg}'. Run 'simple-nvidia-undervolt --help' for usage.");
            }

            if (options.ContainsKey(arg))
            {
                throw new CliError($"'{arg}' is given more than once.");
            }

            if (kind == Kind.Value)
            {
                // The value may itself start with '-' (a negative number), but is never another known
                // flag: a swallowed flag is a missing value, not an intended one - and the guarantee
                // that a token spelled like a flag always acts as that flag is what keeps the raw
                // pre-parse --silent scan exact (see InteractiveOutput.Install).
                if (i + 1 >= args.Length || flags.ContainsKey(args[i + 1]))
                {
                    throw new CliError($"{arg} requires a value.");
                }

                options[arg] = args[++i];
            }
            else if (kind == Kind.OptionalValue && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                options[arg] = args[++i];
            }
            else
            {
                options[arg] = null;
            }
        }

        return new Parsed(options, positionals.ToArray());
    }

    private static bool IsLongOption(string arg)
        => arg.StartsWith("--", StringComparison.Ordinal);
}
