using System.Net;
using System.Numerics;
using System.Text.Json.Nodes;
using AnyCam.Core.Media;
using AnyCam.Core.Protocol;
using AnyCam.Core.Transport;
using AnyCam.Core.VirtualCamera;

namespace AnyCam.Tests;

public class PacketHeaderTests
{
    [Fact]
    public void RoundTripsAllFields()
    {
        var header = new PacketHeader(PacketType.Video, (byte)(VideoFlags.Keyframe | VideoFlags.ParameterSets), 123456, -42);
        var buffer = new byte[ProtocolConstants.HeaderSize];
        header.Write(buffer);

        Assert.Equal(new byte[] { 0x10, 0x03, 0, 0, 0x40, 0xE2, 0x01, 0x00 }, buffer[..8]);
        Assert.Equal(header, PacketHeader.Read(buffer));
    }

    [Fact]
    public void RejectsOversizedPayloads()
    {
        var buffer = new byte[ProtocolConstants.HeaderSize];
        new PacketHeader(PacketType.Control, 0, 0, 0).Write(buffer);
        BitConverter.GetBytes((uint)(ProtocolConstants.MaxPayloadSize + 1)).CopyTo(buffer, 4);
        Assert.Throws<InvalidDataException>(() => PacketHeader.Read(buffer));
    }
}

public class MessageTests
{
    [Fact]
    public void ParsesCapsWithCamelCaseNames()
    {
        var json = JsonNode.Parse("""
            {"t":"caps","cameras":[{"id":"a","name":"Back Ultra Wide","position":"back","lens":"ultraWide","zoomFactor":0.5,
             "formats":[{"width":1920,"height":1080,"maxFps":60,"hdr":true}],"minISO":32,"maxISO":3072,"maxWBGain":4.0}],
             "codecs":["h264","hevc"],"audio":true,"photo":true}
            """)!.AsObject();

        Assert.Equal("caps", json.MessageType());
        var caps = json.As<CameraCaps>()!;
        var camera = Assert.Single(caps.Cameras);
        Assert.Equal(0.5, camera.ZoomFactor);
        Assert.Equal(3072, camera.MaxISO);
        Assert.Equal(4.0, camera.MaxWBGain);
        Assert.Equal("0.5×", camera.ShortLabel);
        Assert.Equal(60, camera.Formats[0].MaxFps);
        Assert.Contains("hevc", caps.Codecs);
    }

    [Fact]
    public void BuildsConfigureWithOnlySetFields()
    {
        var message = HostMessages.Configure(new StreamConfig { Width = 1920, Height = 1080, Codec = "hevc" });
        Assert.Equal("configure", message.MessageType());
        Assert.Equal(1920, message["width"]!.GetValue<int>());
        Assert.False(message.ContainsKey("fps"));
        Assert.False(message.ContainsKey("camera"));
    }

    [Fact]
    public void ClampsFocusCoordinates()
    {
        var message = HostMessages.Focus(1.5, -0.2);
        Assert.Equal(1.0, message["x"]!.GetValue<double>());
        Assert.Equal(0.0, message["y"]!.GetValue<double>());
    }
}

public class PlistTests
{
    [Fact]
    public void RoundTripsUsbmuxMessages()
    {
        var xml = Plist.Serialize(new Dictionary<string, object>
        {
            ["MessageType"] = "Connect",
            ["DeviceID"] = 7,
            ["PortNumber"] = 24760,
            ["Flag"] = true,
        });
        var parsed = Plist.ParseDictionary(xml);
        Assert.Equal("Connect", parsed["MessageType"]);
        Assert.Equal(7L, parsed["DeviceID"]);
        Assert.Equal(24760L, parsed["PortNumber"]);
        Assert.Equal(true, parsed["Flag"]);
    }

    [Fact]
    public void ParsesDeviceList()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>DeviceList</key><array>
              <dict><key>DeviceID</key><integer>3</integer><key>MessageType</key><string>Attached</string>
                <key>Properties</key><dict><key>ConnectionType</key><string>USB</string><key>DeviceID</key><integer>3</integer>
                <key>ProductID</key><integer>4776</integer><key>SerialNumber</key><string>00008110-000A1B2C3D4E</string></dict>
              </dict></array></dict></plist>
            """;
        var parsed = Plist.ParseDictionary(xml);
        var device = (Dictionary<string, object>)((List<object>)parsed["DeviceList"])[0];
        var properties = (Dictionary<string, object>)device["Properties"];
        Assert.Equal("USB", properties["ConnectionType"]);
        Assert.Equal(4776L, properties["ProductID"]);
    }
}

public class MdnsTests
{
    [Fact]
    public void BuildsPtrQueryForService()
    {
        var query = MdnsBrowser.BuildQuery(0x1234);
        Assert.Equal(0x12, query[0]);
        Assert.Equal(0x34, query[1]);
        Assert.Equal(1, query[5]); // one question
        Assert.Equal("_anycam", System.Text.Encoding.ASCII.GetString(query, 13, 7));
        Assert.Equal(new byte[] { 0x00, 12, 0x80, 0x01 }, query[^4..]); // PTR, QU, IN
    }

    [Fact]
    public void ParsesResponseWithCompression()
    {
        var response = BuildResponse();
        var cameras = MdnsBrowser.ParseResponse(response, IPAddress.Parse("10.0.0.99"));
        var camera = Assert.Single(cameras);
        Assert.Equal("Kalyan's iPhone", camera.DisplayName);
        Assert.Equal(IPAddress.Parse("192.168.1.23"), camera.Address);
        Assert.Equal(47200, camera.Port);
        Assert.Equal("abc", camera.Id);
    }

    private static byte[] BuildResponse()
    {
        var bytes = new List<byte>();
        void U16(int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); }
        void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
        void Name(params string[] labels)
        {
            foreach (var label in labels)
            {
                var b = System.Text.Encoding.UTF8.GetBytes(label);
                bytes.Add((byte)b.Length);
                bytes.AddRange(b);
            }
            bytes.Add(0);
        }

        U16(0); U16(0x8400); U16(0); U16(1); U16(0); U16(3);

        // PTR _anycam._tcp.local -> iPhone._anycam._tcp.local
        var serviceOffset = bytes.Count;
        Name("_anycam", "_tcp", "local");
        U16(12); U16(1); U32(120);
        var instanceLabel = System.Text.Encoding.UTF8.GetBytes("iPhone");
        U16(1 + instanceLabel.Length + 2);
        var instanceOffset = bytes.Count;
        bytes.Add((byte)instanceLabel.Length);
        bytes.AddRange(instanceLabel);
        bytes.Add(0xC0); bytes.Add((byte)serviceOffset); // compressed pointer to service name

        // SRV iPhone._anycam._tcp.local -> host.local:47200
        bytes.Add(0xC0); bytes.Add((byte)instanceOffset);
        U16(33); U16(0x8001); U32(120);
        var hostLabel = System.Text.Encoding.UTF8.GetBytes("host");
        var localLabel = System.Text.Encoding.UTF8.GetBytes("local");
        U16(6 + 1 + hostLabel.Length + 1 + localLabel.Length + 1);
        U16(0); U16(0); U16(47200);
        var hostOffset = bytes.Count;
        Name("host", "local");

        // TXT
        bytes.Add(0xC0); bytes.Add((byte)instanceOffset);
        U16(16); U16(0x8001); U32(120);
        var entries = new[] { "v=1", "id=abc", "name=Kalyan's iPhone" };
        U16(entries.Sum(e => System.Text.Encoding.UTF8.GetByteCount(e) + 1));
        foreach (var entry in entries)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(entry);
            bytes.Add((byte)b.Length);
            bytes.AddRange(b);
        }

        // A host.local
        bytes.Add(0xC0); bytes.Add((byte)hostOffset);
        U16(1); U16(0x8001); U32(120); U16(4);
        bytes.AddRange(new byte[] { 192, 168, 1, 23 });
        return [.. bytes];
    }
}

public class AnnexBTests
{
    private static readonly byte[] H264KeyframeAccessUnit =
    [
        0, 0, 0, 1, 0x67, 0x64, 0x00, 0x28,  // SPS
        0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0x80,  // PPS
        0, 0, 1, 0x65, 0x88, 0x84, 0x00,     // IDR slice (3-byte start code)
    ];

    [Fact]
    public void SplitsNalUnitsWithMixedStartCodes()
    {
        var units = AnnexB.SplitNalUnits(H264KeyframeAccessUnit);
        Assert.Equal(3, units.Count);
        Assert.Equal(0x67, H264KeyframeAccessUnit[units[0].Offset]);
        Assert.Equal(0x65, H264KeyframeAccessUnit[units[2].Offset]);
    }

    [Fact]
    public void DetectsKeyframesAndExtractsParameterSets()
    {
        Assert.True(AnnexB.ContainsKeyframe(H264KeyframeAccessUnit, hevc: false));
        var sets = AnnexB.ExtractParameterSets(H264KeyframeAccessUnit, hevc: false);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0x64, 0x00, 0x28, 0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0x80 }, sets);
        Assert.False(AnnexB.ContainsKeyframe([0, 0, 0, 1, 0x41, 0x9A], hevc: false));
    }

    [Fact]
    public void RecognizesHevcIrapAndParameterSets()
    {
        // VPS (32), IDR_W_RADL (19)
        byte[] hevc = [0, 0, 0, 1, 0x40, 0x01, 0x0C, 0, 0, 0, 1, 0x26, 0x01, 0xAF];
        Assert.True(AnnexB.ContainsKeyframe(hevc, hevc: true));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x40, 0x01, 0x0C }, AnnexB.ExtractParameterSets(hevc, hevc: true));
    }
}

public class FramingTests
{
    private static Vector2 Map(Matrix3x2 m, float u, float v) => Vector2.Transform(new Vector2(u, v), m);

    [Fact]
    public void IdentityWhenAspectsMatch()
    {
        var m = FrameProcessor.FitMapping(1920, 1080, 1920, 1080, fill: true, 1, 0, 0, false, false, 0);
        Assert.Equal(new Vector2(0, 0), Map(m, 0, 0));
        Assert.Equal(new Vector2(1, 1), Map(m, 1, 1));
    }

    [Fact]
    public void FillCropsTopAndBottomOfTallerSource()
    {
        // 4:3 source into 16:9 output: only the middle 75% of the height is used.
        var m = FrameProcessor.FitMapping(1920, 1080, 1440, 1080, fill: true, 1, 0, 0, false, false, 0);
        var top = Map(m, 0.5f, 0);
        Assert.Equal(0.125, top.Y, 3);
        Assert.Equal(0.5, top.X, 3);
    }

    [Fact]
    public void FitLetterboxesSides()
    {
        var m = FrameProcessor.FitMapping(1920, 1080, 1080, 1920, fill: false, 1, 0, 0, false, false, 0);
        Assert.True(Map(m, 0, 0.5f).X < 0); // outside the source: black bars
        Assert.Equal(0.5, Map(m, 0.5f, 0.5f).X, 3);
    }

    [Fact]
    public void MirrorAndRotationCompose()
    {
        var mirrored = FrameProcessor.FitMapping(100, 100, 100, 100, true, 1, 0, 0, mirror: true, false, 0);
        Assert.Equal(new Vector2(1, 0), Map(mirrored, 0, 0));

        // 90° clockwise: top-left of the output shows the bottom-left of the source.
        var rotated = FrameProcessor.FitMapping(100, 100, 100, 100, true, 1, 0, 0, false, false, 90);
        Assert.Equal(new Vector2(0, 1), Map(rotated, 0, 0));
    }

    [Fact]
    public void ZoomAndPanStayInsideTheSource()
    {
        var m = FrameProcessor.FitMapping(1920, 1080, 3840, 2160, true, zoom: 2, panX: 1, panY: -1, false, false, 0);
        Assert.Equal(1.0, Map(m, 1, 0.5f).X, 3);
        Assert.Equal(0.0, Map(m, 0.5f, 0).Y, 3);
    }
}

public class CubeLutTests
{
    [Fact]
    public void ParsesTwoPointIdentity()
    {
        var lines = new[]
        {
            "# comment", "TITLE \"Identity\"", "LUT_3D_SIZE 2",
            "0 0 0", "1 0 0", "0 1 0", "1 1 0", "0 0 1", "1 0 1", "0 1 1", "1 1 1",
        };
        var lut = CubeLut.Parse(lines);
        Assert.Equal("Identity", lut.Name);
        Assert.Equal(2, lut.Size);
        Assert.Equal(1f, lut.Data[1 * 4 + 0]);  // second entry: red changes fastest
        Assert.Equal(1f, lut.Data[7 * 4 + 2]);
    }

    [Fact]
    public void RejectsIncompleteTables() =>
        Assert.Throws<FormatException>(() => CubeLut.Parse(["LUT_3D_SIZE 2", "0 0 0"]));
}

public class VirtualCameraLayoutTests
{
    [Fact]
    public void MatchesTheNativeHeader()
    {
        // Values from windows/vcam/include/AnyCamVCamShared.h
        Assert.Equal(4096, VirtualCameraOutput.HeaderSize);
        Assert.Equal(3840 * 2160 * 3 / 2, VirtualCameraOutput.SlotSize);
        Assert.Equal(256, VirtualCameraOutput.OffSlots);
        Assert.Equal(64, VirtualCameraOutput.SlotInfoSize);
        Assert.Equal(140, VirtualCameraOutput.OffLatestSlot);
        Assert.Equal(60, VirtualCameraOutput.OffReadingSlot);
        Assert.True(VirtualCameraOutput.OffSlots + VirtualCameraOutput.SlotCount * VirtualCameraOutput.SlotInfoSize <= VirtualCameraOutput.HeaderSize);
    }
}
