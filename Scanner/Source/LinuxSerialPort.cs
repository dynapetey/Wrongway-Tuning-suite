// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using PcmHacking;

namespace WrongWay;

public sealed class LinuxSerialPort : IPort
{
    private readonly string name;
    private int fd=-1;
    private int timeout=1000;
    private readonly object gate = new object();
    [StructLayout(LayoutKind.Sequential)] struct Termios {public uint input,output,control,local;public byte line;[MarshalAs(UnmanagedType.ByValArray,SizeConst=32)]public byte[] cc;public uint inputSpeed,outputSpeed;}
    [StructLayout(LayoutKind.Sequential)] struct PollFd {public int fd;public short events,revents;}
    [DllImport("libc",SetLastError=true)] static extern int open(string path,int flags);
    [DllImport("libc",SetLastError=true)] static extern int close(int descriptor);
    [DllImport("libc",SetLastError=true)] static extern int tcgetattr(int descriptor,out Termios termios);
    [DllImport("libc",SetLastError=true)] static extern int tcsetattr(int descriptor,int action,ref Termios termios);
    [DllImport("libc")] static extern void cfmakeraw(ref Termios termios);
    [DllImport("libc",SetLastError=true)] static extern int cfsetispeed(ref Termios termios,uint speed);
    [DllImport("libc",SetLastError=true)] static extern int cfsetospeed(ref Termios termios,uint speed);
    [DllImport("libc",SetLastError=true)] static extern int tcflush(int descriptor,int selector);
    [DllImport("libc",SetLastError=true)] static extern int ioctl(int descriptor,uint request,out int value);
    [DllImport("libc",SetLastError=true)] static extern int poll(ref PollFd descriptors,uint count,int milliseconds);
    [DllImport("libc",SetLastError=true)] static extern IntPtr read(int descriptor,byte[] bytes,UIntPtr count);
    [DllImport("libc",SetLastError=true)] static extern IntPtr write(int descriptor,byte[] bytes,UIntPtr count);
    static IOException Error(string operation) {return new IOException(operation + ": " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);}
    void Configure(int baud)
    {
        Termios t;
        if(tcgetattr(fd,out t)!=0) throw Error("Read serial settings");
        cfmakeraw(ref t);
        t.control=(t.control | 0x800u | 0x80u) & ~0x80000000u; // CLOCAL, CREAD, no RTS/CTS
        uint speed;
        switch(baud) {case 9600:speed=13;break;case 19200:speed=14;break;case 38400:speed=15;break;case 57600:speed=4097;break;case 115200:speed=4098;break;case 230400:speed=4099;break;case 500000:speed=4101;break;case 1000000:speed=4104;break;default:throw new NotSupportedException("Unsupported serial baud: " + baud);}
        if(cfsetispeed(ref t,speed)!=0 || cfsetospeed(ref t,speed)!=0 || tcsetattr(fd,0,ref t)!=0) throw Error("Set serial settings");
    }
    bool Wait(short events,int milliseconds)
    {
        var p=new PollFd {fd=fd,events=events};
        int result=poll(ref p,1,milliseconds);
        if(result<0) {if(Marshal.GetLastWin32Error()==4) return false;throw Error("Serial poll");}
        if((p.revents & (8|16|32))!=0) throw new IOException("Serial device disconnected or failed.");
        return result>0 && (p.revents & events)!=0;
    }
    public LinuxSerialPort(string name) { this.name = name; }
    public override string ToString() {return name;}
    public Task OpenAsync(PortConfiguration configuration)
    {
        var c = (SerialPortConfiguration)configuration;
        if (c.DataReceived != null) throw new NotSupportedException("Callback serial mode is not used by this Linux backend.");
        Dispose();
        fd=open(name,2|0x100|0x800); // O_RDWR | O_NOCTTY | O_NONBLOCK
        if(fd<0) throw Error("Open " + name);
        try {Configure(c.BaudRate);timeout=c.Timeout>0 ? c.Timeout : 1000;}
        catch {Dispose();throw;}
        return Task.CompletedTask;
    }
    public Task Send(byte[] bytes)
    {
        lock(gate)
        {
            int offset=0;
            while(offset<bytes.Length)
            {
                if(!Wait(4,5000)) throw new TimeoutException("Serial send timed out.");
                byte[] part=new byte[bytes.Length-offset];Buffer.BlockCopy(bytes,offset,part,0,part.Length);
                int sent=write(fd,part,(UIntPtr)part.Length).ToInt32();
                if(sent<=0) throw Error("Serial send");offset+=sent;
            }
        }
        return Task.CompletedTask;
    }
    public Task<int> Receive(byte[] bytes, int offset, int count)
    {
        lock(gate)
        {
            if(count==0 || !Wait(1,timeout)) return Task.FromResult(0);
            var buffer=new byte[count];
            int got=read(fd,buffer,(UIntPtr)count).ToInt32();
            if(got<0) {if(Marshal.GetLastWin32Error()==11) return Task.FromResult(0);throw Error("Serial receive");}
            Buffer.BlockCopy(buffer,0,bytes,offset,got);
            return Task.FromResult(got);
        }
    }
    public Task ChangeBaudRate(int baud) { lock(gate) { Configure(baud); } return Task.CompletedTask; }
    public Task DiscardBuffers() { lock(gate) {if(tcflush(fd,2)!=0) throw Error("Serial flush");} return Task.CompletedTask; }
    public Task<int> GetReceiveQueueSize() { lock(gate) {int size;if(ioctl(fd,0x541b,out size)!=0) throw Error("Serial queue");return Task.FromResult(size);} }
    public void SetTimeout(int milliseconds) {timeout=Math.Max(1,milliseconds);}
    public void Dispose() { lock(gate) { if(fd>=0) {close(fd);fd=-1;} } }
}

