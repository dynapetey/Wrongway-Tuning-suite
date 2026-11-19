// SPDX-License-Identifier: GPL-3.0-only
// Run with Mono after compiling against backend/wrongway-pcm.exe and PcmLibrary.dll.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using PcmHacking;

sealed class DelayedPort : IPort
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Queue<byte> queue = new Queue<byte>();
    int released;
    public Task<int> GetReceiveQueueSize()
    {
        while (released < 2 && clock.ElapsedMilliseconds >= 1500 * (released + 1))
        {
            byte[] frame = { 8, 15, 0x6C, 0xF0, 0x10, 0x7D, 2, 1, 0, 0,
                (byte)(15 - released), 0, 0, 0x12, 0x34, 0x56, 0x78, 0 };
            byte checksum = 0;
            for (int i = 0; i < frame.Length - 1; i++) checksum += frame[i];
            frame[frame.Length - 1] = (byte)~checksum;
            foreach (byte value in frame) queue.Enqueue(value);
            released++;
        }
        return Task.FromResult(queue.Count);
    }
    public Task<int> Receive(byte[] bytes, int offset, int count)
    {
        int length = Math.Min(count, queue.Count);
        for (int i = 0; i < length; i++) bytes[offset + i] = queue.Dequeue();
        return Task.FromResult(length);
    }
    public Task Send(byte[] bytes) { throw new Exception("Receive must not retransmit a pending CRC query."); }
    public Task OpenAsync(PortConfiguration configuration) { return Task.CompletedTask; }
    public Task ChangeBaudRate(int baud) { return Task.CompletedTask; }
    public Task DiscardBuffers() { queue.Clear(); return Task.CompletedTask; }
    public void SetTimeout(int milliseconds) { }
    public void Dispose() { }
}

class ObdxTimeoutCheck
{
    static int Main()
    {
        using (var port = new DelayedPort())
        using (var device = new LinuxObdxProDevice(port, new JsonLogger()))
        {
            device.SetTimeout(TimeoutScenario.ReadCrc).Wait();
            for (uint address = 0xF0000; address >= 0xE0000; address -= 0x10000)
            {
                Message message = device.ReceiveMessage().GetAwaiter().GetResult();
                if (message == null) throw new Exception("CRC response timed out before delayed frame arrived.");
                byte[] bytes = message.GetBytes();
                if (bytes.Length != 15 || bytes[8] != address >> 16 ||
                    bytes[11] != 0x12 || bytes[12] != 0x34 || bytes[13] != 0x56 || bytes[14] != 0x78)
                    throw new Exception("Delayed CRC frame was not parsed correctly.");
            }
            Console.WriteLine("PASS: consecutive delayed OBDX CRC replies without retransmission.");
        }
        return 0;
    }
}
