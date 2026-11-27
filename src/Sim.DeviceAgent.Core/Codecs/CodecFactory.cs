using System.Text.RegularExpressions;
using Sim.DeviceAgent.Configuration;

namespace Sim.DeviceAgent.Codecs;

public static class CodecFactory
{
    public static IDeviceCodec Create(CodecConfig config, string transportType)
    {
        var encoding = EncodingResolver.Resolve(config.Encoding);

        return config.Type.ToLowerInvariant() switch
        {
            "delimiter" => CreateDelimiter(config, encoding),
            "passthrough" => new PassthroughCodec(encoding),
            "csv" => CreateCsv(config, encoding, transportType),
            _ => throw new NotSupportedException($"Unsupported codec type '{config.Type}'.")
        };
    }

    private static IDeviceCodec CreateDelimiter(CodecConfig config, System.Text.Encoding encoding)
        => new DelimiterCodec(
            config.Delimiter,
            encoding,
            CompileRegex(config.Parse?.Pattern, config.Parse?.IgnoreCase ?? false),
            CompileRegex(config.Filter?.Pattern, config.Filter?.IgnoreCase ?? false),
            config.MaxFrameBytes);

    private static Regex? CompileRegex(string? pattern, bool ignoreCase)
        => string.IsNullOrWhiteSpace(pattern)
            ? null
            : new Regex(
                pattern,
                RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                TimeSpan.FromMilliseconds(250));

    private static IDeviceCodec CreateCsv(CodecConfig config, System.Text.Encoding encoding, string transportType)
    {
        if (!string.Equals(transportType, "file", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("CSV codec is supported only for file devices.");
        }

        if (config.CsvDelimiter.Length != 1)
        {
            throw new ArgumentException("CSV delimiter must be exactly one character.", nameof(config));
        }

        return new CsvCodec(
            encoding,
            config.CsvDelimiter[0],
            config.HasHeader,
            config.Columns);
    }
}
