using System.Buffers.Binary;
using System.Net.Sockets;

namespace AzlDl.Core.Gate;

// Reply to cs_10800: the version strings for every asset category, plus the game server address.
public sealed record GateResponse(
    IReadOnlyList<string> Versions,
    IReadOnlyList<string> CdnList,
    string Url,
    string GatewayIp,
    int GatewayPort);

// Asks the region's gate server for the current asset versions. The gate speaks raw TCP: a 7-byte header
// (2-byte length of what follows the length field, a zero byte, 2-byte command id, 2-byte index), then a protobuf body.
public static class GateClient
{
    private const int VersionRequest = 10800;
    private const int VersionReply = 10801;
    private const int HeaderLength = 7;

    // Values the game client sends; state 21 asks for the version list.
    private const int RequestState = 21;
    private const string RequestPlatform = "0";

    public static async Task<GateResponse> QueryVersionsAsync(
        ClientRegion region,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));

        using var client = new TcpClient();
        await client.ConnectAsync(region.GateHost, region.GatePort, timer.Token);
        var stream = client.GetStream();

        await stream.WriteAsync(BuildRequest(), timer.Token);

        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, timer.Token);
        if (header[2] != 0)
        {
            throw new InvalidDataException("Gate reply has an invalid header.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(header) - (HeaderLength - 2);
        var command = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3));
        if (command != VersionReply)
        {
            throw new InvalidDataException($"Gate answered with command {command}, expected {VersionReply}.");
        }

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, timer.Token);
        return ParseReply(payload);
    }

    private static byte[] BuildRequest()
    {
        using var body = new MemoryStream();
        ProtoWire.WriteVarintField(body, 1, RequestState);
        ProtoWire.WriteStringField(body, 2, RequestPlatform);

        var frame = new byte[HeaderLength + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)(body.Length + HeaderLength - 2));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), VersionRequest);
        body.ToArray().CopyTo(frame, HeaderLength);
        return frame;
    }

    // sc_10801: 1 gateway_ip, 2 gateway_port, 3 url, 4 version (repeated), 10 cdn_list (repeated).
    private static GateResponse ParseReply(byte[] payload)
    {
        var versions = new List<string>();
        var cdns = new List<string>();
        var url = "";
        var ip = "";
        var port = 0;

        foreach (var field in ProtoWire.ReadFields(payload))
        {
            switch (field.Number)
            {
                case 1: ip = field.AsString(); break;
                case 2: port = (int)(field.Value ?? 0); break;
                case 3: url = field.AsString(); break;
                case 4: versions.Add(field.AsString()); break;
                case 10: cdns.Add(field.AsString()); break;
            }
        }

        return new GateResponse(versions, cdns, url, ip, port);
    }
}
