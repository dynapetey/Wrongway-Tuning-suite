// SPDX-License-Identifier: GPL-3.0-only
namespace WrongWay;

sealed record VcmControlResult(uint OsId, string Reply, string? ReleaseReply);

sealed partial class HammerdroidProbe
{
    public static Task<VcmControlResult> RunVcmControl(string device, VcmControlAction action,
        Action<string> status, CancellationToken cancellation)
    {
        if (!VcmTelemetryControls.AvailableLive(action))
            throw new ArgumentException("This control only has an offline frame preview.", nameof(action));

        cancellation.ThrowIfCancellationRequested();
        // Keep the transport available to release an override even after Stop is pressed.
        return WithAdapter(device, status, CancellationToken.None, async (client, _) =>
        {
            cancellation.ThrowIfCancellationRequested();
            await client.EnableVpw();
            uint os = await client.ReadPcmOsId(status);
            cancellation.ThrowIfCancellationRequested();

            bool clearCodes = action == VcmControlAction.ClearTroubleCodes;
            bool releaseOnly = action == VcmControlAction.ReleaseAllControls;
            if (!clearCodes && os is not (12587603 or 12587811))
                throw new IOException($"Fan relay control frames are unavailable for PCM OS {os}.");
            if (!releaseOnly)
            {
                await client.RequireEngineStopped();
                cancellation.ThrowIfCancellationRequested();
            }

            status($"Sending {action} to PCM OS {os}…");
            if (clearCodes || releaseOnly)
                return new VcmControlResult(os, await client.ExchangeControl(action), null);

            bool accepted = false;
            string reply;
            string releaseReply;
            try
            {
                reply = await client.ExchangeControl(action);
                accepted = true;
                status("PCM accepted the fan command. Returning to automatic control in two seconds…");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            }
            finally
            {
                status("Returning fan relays to automatic PCM control…");
                try { releaseReply = await client.ExchangeControl(VcmControlAction.ReleaseAllControls); }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    throw new IOException((accepted ? "Fan command accepted. " : "Fan command outcome uncertain. ") +
                        "The PCM did not confirm automatic control. Use Return fans to PCM or cycle the ignition. " + ex.Message, ex);
                }
            }
            return new VcmControlResult(os, reply, releaseReply);
        });
    }

    private async Task RequireEngineStopped()
    {
        byte[]? rpm = await ReadSaeValue(0x0C, 2);
        byte[]? speed = await ReadSaeValue(0x0D, 1);
        if (rpm is null || speed is null)
            throw new IOException("The PCM must report engine RPM and vehicle speed before this operation.");
        if (rpm[5] != 0 || rpm[6] != 0 || speed[5] != 0)
            throw new IOException("Park the vehicle and switch the engine off. Leave the ignition on.");
    }

    private Task<byte[]?> ReadSaeValue(byte pid, int bytes)
    {
        int length = 5 + bytes;
        return ReadPidReply([0x68, 0x6A, 0xF0, 0x01, pid],
            reply => reply[0] is 0x48 or 0x68 && reply[3] == 0x41 && reply[4] == pid &&
                (reply.Length == length || reply.Length == length + 1 && HasVpwCrc(reply)), 750);
    }

    private async Task<string> ExchangeControl(VcmControlAction action)
    {
        byte[] request = VcmTelemetryControls.BuildVpwCommand(new(action));
        await serial.Send(Encode(0x10, request));
        long deadline = Environment.TickCount64 + 1500;
        while (true)
        {
            var (command, reply) = await Packet(deadline);
            if (command is not (0x08 or 0x09) || reply.Length < 4 || reply[2] != 0x10)
                continue;
            bool functional = action == VcmControlAction.ClearTroubleCodes;
            bool addressed = functional
                ? reply[0] is 0x48 or 0x68 && reply[1] is 0x6B or 0xF0
                : reply[0] == 0x6C && reply[1] == 0xF0;
            if (!addressed)
                continue;

            string hex = Convert.ToHexString(reply);
            if (reply[3] == 0x7F && reply.Length >= 6 && reply[4] == request[3])
            {
                if (reply[5] == 0x78)
                    continue;
                throw new IOException($"PCM rejected {action} with status 0x{reply[5]:X2} ({hex}).");
            }
            if (functional && reply[3] == 0x44 &&
                (reply.Length == 4 || reply.Length == 5 && HasVpwCrc(reply)))
                return hex;
            if (!functional && reply[3] == 0xEE && reply.Length >= 6 && reply[4] == request[4] &&
                (reply.Length == 6 || reply.Length == 7 && HasVpwCrc(reply)))
            {
                if (reply[5] == 0xE1)
                    return hex;
                throw new IOException($"PCM declined {action} with status 0x{reply[5]:X2} ({hex}).");
            }
        }
    }
}
