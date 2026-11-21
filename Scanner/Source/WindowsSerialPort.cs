// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Ports;
using PcmHacking;

namespace WrongWay;

internal sealed class WindowsSerialPort(string name) : IPort
{
    private readonly SerialPort port = new(name, 115200, Parity.None, 8, StopBits.One)
    {
        Handshake = Handshake.None,
        WriteTimeout = 5000
    };

    public override string ToString() => port.PortName;

    public Task OpenAsync(PortConfiguration configuration)
    {
        var settings = (SerialPortConfiguration)configuration;
        if (settings.DataReceived != null)
            throw new NotSupportedException("Callback serial mode is unavailable.");

        port.Close();
        port.BaudRate = settings.BaudRate;
        port.ReadTimeout = settings.Timeout > 0 ? settings.Timeout : 1000;
        port.Open();
        return Task.CompletedTask;
    }

    public Task Send(byte[] bytes)
    {
        port.Write(bytes, 0, bytes.Length);
        return Task.CompletedTask;
    }

    public Task<int> Receive(byte[] bytes, int offset, int count)
    {
        if (count == 0)
            return Task.FromResult(0);

        try
        {
            return Task.FromResult(port.Read(bytes, offset, count));
        }
        catch (TimeoutException)
        {
            return Task.FromResult(0);
        }
    }

    public Task ChangeBaudRate(int baud)
    {
        port.BaudRate = baud;
        return Task.CompletedTask;
    }

    public Task DiscardBuffers()
    {
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        return Task.CompletedTask;
    }

    public Task<int> GetReceiveQueueSize() => Task.FromResult(port.BytesToRead);

    public void SetTimeout(int milliseconds) => port.ReadTimeout = Math.Max(1, milliseconds);

    public void Dispose() => port.Dispose();
}
