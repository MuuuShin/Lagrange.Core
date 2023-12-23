using Lagrange.Core.Common;
using Lagrange.Core.Internal.Event;
using Lagrange.Core.Internal.Event.Action;
using Lagrange.Core.Internal.Packets.Service.Oidb;
using Lagrange.Core.Internal.Packets.Service.Oidb.Request;
using Lagrange.Core.Internal.Packets.Service.Oidb.Response;
using Lagrange.Core.Utility.Extension;
using Lagrange.Core.Utility.Binary;
using ProtoBuf;

namespace Lagrange.Core.Internal.Service.Action;

[EventSubscribe(typeof(GroupFSDownloadEvent))]
[Service("OidbSvcTrpcTcp.0x6d6_2")]
internal class GroupFSDownloadService : BaseService<GroupFSDownloadEvent>
{
    protected override bool Build(GroupFSDownloadEvent input, BotKeystore keystore, BotAppInfo appInfo, BotDeviceInfo device,
        out BinaryPacket output, out List<BinaryPacket>? extraPackets)
    {
        var packet = new OidbSvcTrpcTcpBase<OidbSvcTrpcTcp0x6D6_2>(new OidbSvcTrpcTcp0x6D6_2
        {
            Download = new OidbSvcTrpcTcp0x6D6_2Download
            {
                GroupUin = input.GroupUin,
                AppId = 7,
                BusId = 102,
                FileId = input.FileId
            }
        }, false, true);

        output = packet.Serialize();
        extraPackets = null;
        return true;
    }

    protected override bool Parse(byte[] input, BotKeystore keystore, BotAppInfo appInfo, BotDeviceInfo device,
        out GroupFSDownloadEvent output, out List<ProtocolEvent>? extraEvents)
    {
        var packet = Serializer.Deserialize<OidbSvcTrpcTcpResponse<OidbSvcTrpcTco0x6D6Response>>(input.AsSpan());
        var download = packet.Body.Download;

        string url = $"https://{download.DownloadIp}:443/ftn_handler/{download.DownloadUrl.Hex(true)}/?fname=";

        output = GroupFSDownloadEvent.Result((int)packet.ErrorCode, url);
        extraEvents = null;
        return true;
    }
}