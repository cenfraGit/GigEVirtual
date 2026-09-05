// --------------------------------------------------------------------------------
// GigECamera.cs
//
// uses GVCPServer and GVSPTransmitter to emulate a gigevision camera.
// --------------------------------------------------------------------------------

using System.Net;

namespace GigEVirtual;

public class GigECamera
{
    DeviceState _deviceState;
    GVCPServer _gvcpServer;
    GVSPTransmitter _gvspTransmitter;

    public GigECamera(string ip,
                      string manufacturerName = "fromVirtual",
                      string modelName = "modelVirtual",
                      string deviceVersion = "1.0",
                      string manufacturerInfo = "C# cam",
                      string serialNumber = "S0001",
                      string deviceName = "virtualCam",
                      bool shareToNetwork = false,
                      string? imagePath = null)
    {
        _deviceState = new(manufacturerName, modelName, deviceVersion, manufacturerInfo, serialNumber, deviceName);
        _gvspTransmitter = new(_deviceState, IPAddress.Parse(ip), new ImageSource(imagePath));
        _gvcpServer = new GVCPServer(IPAddress.Parse(ip), _deviceState, shareToNetwork);

        _deviceState.OnWrite(0xA00C, (_, _) => _gvspTransmitter.StartAcquisition());
        _deviceState.OnWrite(0xA010, (_, _) => _gvspTransmitter.StopAcquisition());

        // losing the control channel, by release or by heartbeat, aborts streaming
        _deviceState.OnControlChannelClosed(() => _gvspTransmitter.StopAcquisition());

        // an application probes the mtu by asking for test packets through SCPS0
        _deviceState.OnFireTestPacket(_gvspTransmitter.SendTestPacket);
    }

    public Task Start()
    {
        return _gvcpServer.Start();
    }
}