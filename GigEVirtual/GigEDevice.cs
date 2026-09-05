// --------------------------------------------------------------------------------
// GigEDevice.cs
//
// what every emulated device has in common: the register state, a control
// server, a stream transmitter and somewhere to get frames from. a device
// subclass supplies its own registers, its own description and its own
// behaviour on top.
// --------------------------------------------------------------------------------

using System.Net;

namespace GigEVirtual;

public abstract class GigEDevice
{
    private protected readonly DeviceState State;
    private protected readonly GVSPTransmitter Transmitter;

    private readonly GVCPServer _server;

    // a subclass builds its DeviceState and defines its own registers first, then
    // hands both here along with a description of where its geometry lives.
    private protected GigEDevice(string ip,
                                 DeviceState state,
                                 StreamSettings settings,
                                 string? imagePath,
                                 bool shareToNetwork)
    {
        State = state;

        IPAddress address = IPAddress.Parse(ip);

        Transmitter = new GVSPTransmitter(state, address, new ImageSource(imagePath), settings);
        _server = new GVCPServer(address, state, shareToNetwork);

        // the same three seams for every device, so a device only has to wire up
        // what is actually specific to it
        state.OnControlChannelClosed(() => Transmitter.StopAcquisition());
        state.OnFireTestPacket(Transmitter.SendTestPacket);
        state.OnPacketResend(Transmitter.Resend);
        state.StreamingCheck(() => Transmitter.IsStreaming);
    }

    // tells the device to drop one stream packet in every n on the way out. no
    // real camera has this knob, which is the point: a lossy link is the thing
    // an application most needs to be tested against and the hardest to arrange.
    public int DropOneIn
    {
        get => Transmitter.DropOneIn;
        set => Transmitter.DropOneIn = value;
    }

    public Task Start() => _server.Start();
}
