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
                      bool shareToNetwork = false)
    {
        _deviceState = new(manufacturerName, modelName, deviceVersion, manufacturerInfo, serialNumber, deviceName);
        _gvspTransmitter = new(_deviceState, IPAddress.Parse(ip));
        _gvcpServer = new GVCPServer(IPAddress.Parse(ip), _deviceState, _gvspTransmitter, shareToNetwork);
    }

    public Task Start(bool shareToNetwork = false)
    {
        return _gvcpServer.Start();
    }
}