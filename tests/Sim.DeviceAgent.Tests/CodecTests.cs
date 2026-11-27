using System.Text;
using System.Text.RegularExpressions;
using Sim.DeviceAgent.Codecs;
using Sim.DeviceAgent.Configuration;
using Sim.DeviceAgent.Transports;
using Xunit;

namespace Sim.DeviceAgent.Tests;

public sealed class CodecTests
{
    [Fact]
    public void DelimiterDecoder_SplitsAcrossChunks_AndParsesNamedGroups()
    {
        var codec = new DelimiterCodec(
            "\r\n",
            Encoding.ASCII,
            new Regex(@"ST,(?<state>GS|US),(?<value>\d+\.\d+)(?<unit>kg|g)", RegexOptions.CultureInvariant),
            filter: null,
            1024);
        var decoder = codec.CreateDecoder();

        Assert.Empty(decoder.Push(Encoding.ASCII.GetBytes("ST,GS,12")));

        var frames = decoder.Push(Encoding.ASCII.GetBytes(".345kg\r\nST,US,1.000kg\r\n"));

        Assert.Equal(2, frames.Count);
        Assert.Equal("ST,GS,12.345kg", frames[0].Text);
        Assert.Equal(12.345m, frames[0].Parsed!.Value.GetProperty("value").GetDecimal());
        Assert.Equal("kg", frames[0].Parsed!.Value.GetProperty("unit").GetString());
        Assert.Equal("US", frames[1].Parsed!.Value.GetProperty("state").GetString());
    }

    [Fact]
    public void DelimiterDecoder_ReportsOversizedFrame()
    {
        var codec = new DelimiterCodec("\r\n", Encoding.ASCII, parser: null, filter: null, maxFrameBytes: 8);
        var decoder = codec.CreateDecoder();

        var frame = Assert.Single(decoder.Push(Encoding.ASCII.GetBytes("123456789")));

        Assert.Equal("frame_too_large", frame.ParseError);
        Assert.Equal(8, frame.Raw.Length);
    }

    [Fact]
    public void DelimiterDecoder_ReportsRegexMismatchWithoutDroppingFrame()
    {
        var codec = new DelimiterCodec(
            "\r\n",
            Encoding.ASCII,
            new Regex(@"^RESULT,(?<force>\d+\.\d+)N$", RegexOptions.CultureInvariant),
            filter: null,
            1024);
        var decoder = codec.CreateDecoder();

        var frame = Assert.Single(decoder.Push(Encoding.ASCII.GetBytes("NOISE\r\n")));

        Assert.Equal("regex_no_match", frame.ParseError);
        Assert.Equal("NOISE", frame.Text);
        Assert.Null(frame.Parsed);
    }

    [Fact]
    public void DelimiterDecoder_FilterDropsFramesThatDoNotMatch()
    {
        var codec = new DelimiterCodec(
            "\r\n",
            Encoding.ASCII,
            new Regex(@"^ST,(?<value>\d+\.\d+)kg$", RegexOptions.CultureInvariant),
            new Regex("^ST,", RegexOptions.CultureInvariant),
            1024);
        var decoder = codec.CreateDecoder();

        var frames = decoder.Push(Encoding.ASCII.GetBytes("ST,12.345kg\r\nNOISE\r\n"));

        var frame = Assert.Single(frames);
        Assert.Equal("ST,12.345kg", frame.Text);
        Assert.Equal(12.345m, frame.Parsed!.Value.GetProperty("value").GetDecimal());
    }

    [Fact]
    public void DelimiterDecoder_FilterKeepsFrameWhenParseDoesNotMatch()
    {
        var codec = new DelimiterCodec(
            "\r\n",
            Encoding.ASCII,
            new Regex(@"^RESULT,(?<force>\d+\.\d+)N$", RegexOptions.CultureInvariant),
            new Regex("^RESULT,", RegexOptions.CultureInvariant),
            1024);
        var decoder = codec.CreateDecoder();

        var frame = Assert.Single(decoder.Push(Encoding.ASCII.GetBytes("RESULT,BAD\r\n")));

        Assert.Equal("regex_no_match", frame.ParseError);
    }

    [Fact]
    public void DelimiterDecoder_NullFilterAllowsAllFrames_AndFilterAppliesToFlush()
    {
        var unfiltered = new DelimiterCodec(
                "\r\n",
                Encoding.ASCII,
                parser: null,
                filter: null,
                maxFrameBytes: 1024)
            .CreateDecoder();
        Assert.Equal(2, unfiltered.Push(Encoding.ASCII.GetBytes("A\r\nB\r\n")).Count);

        var filtered = new DelimiterCodec(
                "\r\n",
                Encoding.ASCII,
                parser: null,
                new Regex("^A", RegexOptions.CultureInvariant),
                maxFrameBytes: 1024)
            .CreateDecoder();
        Assert.Empty(filtered.Push(Encoding.ASCII.GetBytes("NOISE")));
        Assert.Empty(filtered.Flush());
        Assert.Empty(filtered.Push(Encoding.ASCII.GetBytes("A1")));
        Assert.Single(filtered.Flush());
    }

    [Fact]
    public void CodecFactory_IgnoreCase_AppliesToParseAndFilter()
    {
        var config = new CodecConfig
        {
            Type = "delimiter",
            Delimiter = "\n",
            Parse = new RegexParseOptions
            {
                Pattern = @"^(?<state>ST|US)\s+(?<value>[+-]?\d+(?:\.\d+)?)\s*(?<unit>kg|g)\s*(?<weightType>nt|gs)$",
                IgnoreCase = true
            },
            Filter = new RegexParseOptions
            {
                Pattern = "^ST\\s",
                IgnoreCase = true
            }
        };
        var decoder = CodecFactory.Create(config, "serial").CreateDecoder();

        var frame = Assert.Single(decoder.Push(Encoding.ASCII.GetBytes("st 12.3 KG NT\n")));

        Assert.Equal("st", frame.Parsed!.Value.GetProperty("state").GetString());
        Assert.Equal(12.3m, frame.Parsed!.Value.GetProperty("value").GetDecimal());
        Assert.Equal("KG", frame.Parsed!.Value.GetProperty("unit").GetString());
    }

    [Fact]
    public void CodecFactory_WithoutIgnoreCase_IsCaseSensitive()
    {
        var config = new CodecConfig
        {
            Type = "delimiter",
            Delimiter = "\n",
            Filter = new RegexParseOptions { Pattern = "^ST " }
        };
        var decoder = CodecFactory.Create(config, "serial").CreateDecoder();

        Assert.Empty(decoder.Push(Encoding.ASCII.GetBytes("st 12.3 kg\n")));
        Assert.Single(decoder.Push(Encoding.ASCII.GetBytes("ST 12.3 kg\n")));
    }

    [Fact]
    public async Task CsvCodec_MapsColumnsAndCoercesValues()
    {
        var codec = new CsvCodec(
            Encoding.UTF8,
            ',',
            hasHeader: true,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sampleId"] = "Sample",
                ["force"] = "ForceN"
            });

        const string csv = "Sample,ForceN,Note\n\"A,1\",12.5,ok\nB,8,ok\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var frames = new List<DecodedFrame>();
        await foreach (var frame in codec.DecodeFileAsync(
                           stream,
                           new FileMetadata("pull.csv", csv.Length, "hash", DateTimeOffset.UtcNow)))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);
        Assert.Equal("A,1", frames[0].Parsed!.Value.GetProperty("sampleId").GetString());
        Assert.Equal(12.5m, frames[0].Parsed!.Value.GetProperty("force").GetDecimal());
        Assert.Equal("B", frames[1].Parsed!.Value.GetProperty("sampleId").GetString());
    }

    [Fact]
    public void CsvLineParser_HandlesQuotedCommasAndEscapedQuotes()
    {
        var fields = CsvLineParser.ParseLine("\"A,1\",\"say \"\"hi\"\"\",plain", ',');

        Assert.Equal(3, fields.Count);
        Assert.Equal("A,1", fields[0]);
        Assert.Equal("say \"hi\"", fields[1]);
        Assert.Equal("plain", fields[2]);
    }
}
