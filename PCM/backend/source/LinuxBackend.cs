// SPDX-License-Identifier: GPL-3.0-only
// Linux serial frontend for the unmodified PCM Hammer 2.0.0 protocol library.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Newtonsoft.Json;
using PcmHacking;

public sealed class LinuxSerialPort : IPort
{
    private readonly string name;
    private int fd=-1;
    private int timeout=1000;
    private readonly object gate = new object();
    private readonly Queue<byte> received = new Queue<byte>();
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
            if (received.Count > 0)
            {
                int buffered = Math.Min(count, received.Count);
                for (int index = 0; index < buffered; index++)
                    bytes[offset + index] = received.Dequeue();
                return Task.FromResult(buffered);
            }
            if(count==0 || !Wait(1,timeout)) return Task.FromResult(0);
            var buffer=new byte[count];
            int got=read(fd,buffer,(UIntPtr)count).ToInt32();
            if(got<0) {if(Marshal.GetLastWin32Error()==11) return Task.FromResult(0);throw Error("Serial receive");}
            Buffer.BlockCopy(buffer,0,bytes,offset,got);
            return Task.FromResult(got);
        }
    }
    public Task ChangeBaudRate(int baud) { lock(gate) { Configure(baud); } return Task.CompletedTask; }
    public Task DiscardBuffers()
    {
        lock (gate)
        {
            if (tcflush(fd, 2) != 0) throw Error("Serial flush");
            received.Clear();
        }
        return Task.CompletedTask;
    }
    public Task<int> GetReceiveQueueSize()
    {
        lock (gate)
        {
            int available;
            if (ioctl(fd, 0x541b, out available) != 0) throw Error("Serial queue");
            while (available > 0)
            {
                // Drain the Linux TTY while upstream waits for a complete DVI frame.
                // Its 4 KiB input queue is smaller than a 4096-byte kernel payload plus framing.
                var buffer = new byte[Math.Min(available, 8192)];
                int count = read(fd, buffer, (UIntPtr)buffer.Length).ToInt32();
                if (count < 0)
                {
                    if (Marshal.GetLastWin32Error() == 11) break;
                    throw Error("Serial receive queue");
                }
                if (count == 0) break;
                for (int index = 0; index < count; index++)
                    received.Enqueue(buffer[index]);
                if (received.Count > 1024 * 1024)
                    throw new IOException("The serial receive queue exceeded 1 MiB.");
                if (ioctl(fd, 0x541b, out available) != 0) throw Error("Serial queue");
            }
            return Task.FromResult(received.Count);
        }
    }
    public void SetTimeout(int milliseconds) {timeout=Math.Max(1,milliseconds);}
    public void Dispose() { lock(gate) { if(fd>=0) {close(fd);fd=-1;} received.Clear(); } }
}

public sealed class JsonLogger : ILogger
{
    public bool Debug;
    static readonly object outputGate = new object();
    public static void Emit(object obj) { lock(outputGate) { Console.WriteLine(JsonConvert.SerializeObject(obj)); Console.Out.Flush(); } }
    public void AddUserMessage(string message) { Emit(new {type="message", message}); }
    public void AddDebugMessage(string message) { if(Debug) Emit(new {type="debug", message}); }
    public void StatusUpdateActivity(string message) { Emit(new {type="activity", message}); }
    public void StatusUpdateTimeRemaining(string message) { Emit(new {type="remaining", message}); }
    public void StatusUpdatePercentDone(string message) { Emit(new {type="percent", message}); }
    public void StatusUpdateRetryCount(string message) { Emit(new {type="retries", message}); }
    public void StatusUpdateProgressBar(double completed, bool visible) { }
    public void StatusUpdateKbps(string message) { Emit(new {type="speed", message}); }
    public void StatusUpdateReset() { }
}

public static class LinuxBackend
{
    static readonly HashSet<string> Operations = new HashSet<string> { "devices", "identify", "read", "verify", "test-write", "write-calibration", "write-parameters", "write-os", "write-full", "recover-p59", "log", "codes", "clear-codes", "validate", "profiles", "self-test", "serial-test" };
    static readonly HashSet<string> Options = new HashSet<string> { "--port", "--adapter", "--file", "--backup", "--profile", "--output", "--duration", "--confirm", "--debug", "--no4x", "--noninteractive" };
    public static int Main(string[] args)
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        try { return Run(args).GetAwaiter().GetResult(); }
        catch(Exception e) { JsonLogger.Emit(new {type="error", message=e.Message}); return 1; }
    }
    static string Get(Dictionary<string,string> options, string key, string fallback=null) { string value; return options.TryGetValue(key,out value) ? value : fallback; }
    static async Task<int> Run(string[] args)
    {
        if(args.Length == 0 || args[0] == "--help")
        {
            Console.WriteLine("WrongWay native Linux PCM backend 0.2.0\n" + string.Join(" | ", Operations) + "\nOptions: --port /dev/ttyUSB0 --adapter OBDX|ELM|AVT --file BIN --backup STOCK --profile XML --output CSV --duration SECONDS --confirm --debug --no4x");
            return 0;
        }
        string op=args[0];
        if(!Operations.Contains(op)) throw new ArgumentException("Unknown operation: " + op);
        var opts=new Dictionary<string,string>();
        for(int i=1;i<args.Length;i++)
        {
            string key=args[i];
            if(!Options.Contains(key) || opts.ContainsKey(key)) throw new ArgumentException("Unknown or duplicate option: " + key);
            if(new[]{"--confirm","--debug","--no4x","--noninteractive"}.Contains(key)) opts.Add(key,"true");
            else { if(++i >= args.Length || args[i].StartsWith("--")) throw new ArgumentException("Missing value for " + key); opts.Add(key,args[i]); }
        }
        var logger=new JsonLogger { Debug=opts.ContainsKey("--debug") };
        string root=AppDomain.CurrentDomain.BaseDirectory;
        string file=Get(opts,"--file");
        bool mutates=op.StartsWith("write-") || op=="recover-p59" || op=="clear-codes";
        if(mutates && !opts.ContainsKey("--confirm")) throw new ArgumentException("This operation requires explicit --confirm.");
        if(op=="devices")
        {
            var ports=Directory.GetFiles("/dev").Where(p=>p.StartsWith("/dev/ttyUSB") || p.StartsWith("/dev/ttyACM") || p.StartsWith("/dev/rfcomm")).OrderBy(p=>p).ToArray();
            JsonLogger.Emit(new {type="devices", ports});
            return 0;
        }
        if(op=="self-test")
        {
            var db=new ParameterDatabase(root); db.LoadDatabase();
            var profile=new LogProfileReader(db,12587603,logger).Read(Path.Combine(root,"Basics.LogProfile"));
            if(profile.IsEmpty) throw new Exception("Bundled logging profile did not load.");
            var ownProfile=new LogProfileReader(db,12587603,logger).Read(Path.Combine(root,"WrongWayP59.LogProfile"));
            if(ownProfile.Columns.Count()!=8) throw new Exception("WrongWay P59 logging profile did not load completely.");
            JsonLogger.Emit(new {type="result", success=true, message="Native Linux backend dependencies, PCM protocol, logging database and profile loaded.", columns=profile.Columns.Count()});
            return 0;
        }
        if(op=="profiles")
        {
            JsonLogger.Emit(new {type="profiles", profiles=Directory.GetFiles(root,"*.LogProfile")});
            return 0;
        }
        bool needsImage=op=="validate" || op=="verify" || op=="test-write" || (mutates && op!="clear-codes");
        FileValidator validator=null;
        if(needsImage)
        {
            if(file==null) throw new ArgumentException("Choose a BIN with --file.");
            var image=File.ReadAllBytes(file);
            validator=new FileValidator(image,logger);
            PcmType kind=validator.GetFileType();
            if(kind!=PcmType.P01 && kind!=PcmType.P59) throw new Exception("This build supports P01/P59 images only.");
            if(!validator.IsValid()) throw new Exception("BIN checksum or layout validation failed.");
            if(op=="validate") { JsonLogger.Emit(new {type="result", success=true, controller=kind.ToString(), osid=validator.GetOsidFromImage()}); return 0; }
            if(mutates)
            {
                string backup=Get(opts,"--backup");
                if(backup==null || Path.GetFullPath(backup)==Path.GetFullPath(file)) throw new Exception("Choose a separate original PCM backup with --backup.");
                var saved=new FileValidator(File.ReadAllBytes(backup),logger);
                if(!saved.IsValid() || saved.GetFileType()!=kind) throw new Exception("Original backup is invalid or belongs to another controller family.");
                if((op=="write-calibration" || op=="write-parameters") && saved.GetOsidFromImage()!=validator.GetOsidFromImage()) throw new Exception("Calibration/parameter writes require matching backup and tune operating systems.");
            }
        }
        string portName=Get(opts,"--port");
        if(string.IsNullOrWhiteSpace(portName) || !portName.StartsWith("/dev/")) throw new ArgumentException("Select an absolute Linux device such as /dev/ttyUSB0.");
        if(op=="serial-test")
        {
            using(var p=new LinuxSerialPort(portName))
            {
                await p.OpenAsync(new SerialPortConfiguration { BaudRate=115200,Timeout=1000 });
                await p.Send(new byte[]{0x12,0x34,0x56});
                byte[] data=new byte[3]; int n=0;
                while(n<3) { int got=await p.Receive(data,n,3-n); if(got==0) throw new TimeoutException("No serial loopback response."); n+=got; }
                if(!data.SequenceEqual(new byte[]{0x65,0x43,0x21})) throw new Exception("Incorrect serial loopback response.");
                await p.Send(new byte[]{0x52,0x42});
                const int frameSize = 16384;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (await p.GetReceiveQueueSize() < frameSize && DateTime.UtcNow < deadline)
                    await Task.Delay(1);
                if (await p.GetReceiveQueueSize() != frameSize)
                    throw new TimeoutException("Large serial frame did not arrive intact.");
                data = new byte[frameSize];
                n = 0;
                while (n < frameSize)
                {
                    int got = await p.Receive(data, n, frameSize - n);
                    if (got == 0) throw new TimeoutException("Large serial frame stopped arriving.");
                    n += got;
                }
                for (int index = 0; index < frameSize; index++)
                    if (data[index] != (byte)(index % 251)) throw new IOException("Large serial frame byte order failed.");
                JsonLogger.Emit(new {type="result", success=true, message="Serial open/send/receive/dispose and 16 KiB queued frame passed."});
                return 0;
            }
        }
        string output=Get(opts,"--output");
        if((op=="read" || op=="log") && (string.IsNullOrWhiteSpace(output) || File.Exists(output))) throw new ArgumentException("Choose a new output filename with --output.");
        if(op=="recover-p59" && validator.GetFileType()!=PcmType.P59) throw new ArgumentException("P59 recovery requires a P59 image.");
        using(var port=new LinuxSerialPort(portName))
        {
            Device device;
            string adapter=Get(opts,"--adapter","OBDX").ToUpperInvariant();
            switch(adapter)
            {
                case "OBDX": device=new OBDXProDevice(port,logger); break;
                case "ELM": device=new ElmDevice(port,logger); break;
                case "AVT": device=new AvtDevice(port,logger); break;
                default: throw new ArgumentException("Adapter must be OBDX, ELM or AVT.");
            }
            var protocol=new Protocol();
            using(var vehicle=new Vehicle(device,protocol,logger,new ToolPresentNotifier(device,protocol,logger),root))
            using(var cts=new CancellationTokenSource())
            {
                Console.CancelKeyPress+=(s,e)=>{e.Cancel=true;cts.Cancel();};
                var answers=new BlockingCollection<string>();
                var inputReader=Task.Run(()=>{try {string line;while((line=Console.ReadLine())!=null) {if(line=="cancel") cts.Cancel();else answers.Add(line);}} finally {answers.CompleteAdding();}});
                var init=vehicle.ResetConnection();
                if(await Task.WhenAny(init,Task.Delay(15000))!=init) throw new TimeoutException("Adapter initialization timed out.");
                if(!await init) throw new Exception("Unable to initialize selected adapter.");
                vehicle.Enable4xReadWrite=!opts.ContainsKey("--no4x");
                logger.AddUserMessage("Connected: " + vehicle.DeviceDescription);
                Func<string,string,Task> alert=(msg,title)=>{logger.AddUserMessage(title + ": " + msg);return Task.CompletedTask;};
                Func<string,string,Task<bool>> prompt=(msg,title)=>
                {
                    if(opts.ContainsKey("--noninteractive")) return Task.FromResult(false);
                    JsonLogger.Emit(new {type="prompt", title, message=msg});
                    return Task.FromResult(string.Equals(answers.Take(cts.Token),"yes",StringComparison.OrdinalIgnoreCase));
                };
                bool success=false;
                if(op=="identify") success=await Properties(vehicle,logger,cts.Token);
                else if(op=="read")
                {
                    PcmType readType = await ReadController(vehicle, logger, cts.Token);
                    var manager=new ReadManager(logger,vehicle,a=>{a();return Task.CompletedTask;},()=>Task.FromResult<string>(null),()=>Task.FromResult(0u),alert,prompt,cts.Token);
                    success=await manager.Read(output, readType);
                    if(success && File.Exists(output)) success=new FileValidator(File.ReadAllBytes(output),logger).IsValid();
                    else success=false;
                }
                else if(op=="log") success=await Log(vehicle,logger,root,opts,cts.Token);
                else if(op=="codes") success=await Codes(vehicle,logger,cts.Token);
                else if(op=="clear-codes") {await vehicle.ClearTroubleCodes();logger.AddUserMessage("Clear request sent. Read codes again to confirm the PCM response.");success=true;}
                else
                {
                    WriteType kind=WriteType.Compare;
                    switch(op)
                    {
                        case "test-write":kind=WriteType.TestWrite;break;
                        case "write-calibration":kind=WriteType.Calibration;break;
                        case "write-parameters":kind=WriteType.Parameters;break;
                        case "write-os":kind=WriteType.OsPlusCalibrationPlusBoot;break;
                        case "write-full": case "recover-p59":kind=WriteType.Full;break;
                    }
                    bool? hardwareWrite = null;
                    if (op == "write-os" || op == "test-write")
                        hardwareWrite = await WriteKnownP59(vehicle, logger, kind, file, Get(opts,"--backup"), validator, cts.Token);
                    if (hardwareWrite.HasValue)
                        success = hardwareWrite.Value;
                    else
                    {
                        var manager=new WriteManager(logger,vehicle,kind,alert,prompt,cts.Token);
                        success=await manager.Write(file,op=="recover-p59" ? PcmType.P59 : PcmType.Undefined);
                    }
                }
                JsonLogger.Emit(new {type="result", success, operation=op});
                vehicle.ShutdownSignalSource.Cancel();
                GC.KeepAlive(inputReader);
                return success ? 0 : 2;
            }
        }
    }
    static async Task<PcmType> ReadController(Vehicle vehicle, JsonLogger logger, CancellationToken token)
    {
        var os = await vehicle.QueryOperatingSystemId(token);
        if (os.Status != ResponseStatus.Success && !token.IsCancellationRequested)
        {
            logger.AddUserMessage("OS query failed; exiting any previous read kernel before retrying identification.");
            await vehicle.ExitKernel();
            os = await vehicle.QueryOperatingSystemId(token);
        }
        if (os.Status != ResponseStatus.Success)
            throw new IOException("Cannot identify the PCM OS before reading.");
        if (new OSIDInfo(os.Value).IsSupported)
            return PcmType.Undefined;

        var hardware = await vehicle.QueryHardwareId();
        if (hardware.Status != ResponseStatus.Success || hardware.Value != 12589162)
            throw new IOException("PCM OS " + os.Value + " is unrecognized and its hardware is not in the P59 read fallback list.");
        logger.AddUserMessage("PCM OS " + os.Value + " is absent from the bundled OS lookup. Hardware ID 12589162 identifies a P59; using its 1 MiB read protocol.");
        return PcmType.P59;
    }
    static async Task<bool?> WriteKnownP59(Vehicle vehicle, JsonLogger logger, WriteType kind, string file, string backup, FileValidator target, CancellationToken token)
    {
        var os = await vehicle.QueryOperatingSystemId(token);
        if (os.Status != ResponseStatus.Success)
            throw new IOException("Cannot identify the PCM before an OS write or connection test.");
        if (new OSIDInfo(os.Value).IsSupported)
            return null; // Keep the normal upstream compatibility path for recognized operating systems.

        var hardware = await vehicle.QueryHardwareId();
        if (hardware.Status != ResponseStatus.Success || hardware.Value != 12589162 ||
            target.GetFileType() != PcmType.P59 ||
            new OSIDInfo(target.GetOsidFromImage()).HardwareType != PcmType.P59)
            throw new IOException("The unrecognized PCM OS requires verified P59 hardware and a supported P59 target image.");
        if (string.IsNullOrWhiteSpace(backup) || Path.GetFullPath(backup) == Path.GetFullPath(file))
            throw new IOException("Choose a separate original PCM backup with --backup.");
        var saved = new FileValidator(File.ReadAllBytes(backup), logger);
        if (!saved.IsValid() || saved.GetFileType() != PcmType.P59 || saved.GetOsidFromImage() != os.Value)
            throw new IOException("The P59 backup must validate and match the connected PCM's current OS.");

        // The OS catalog is incomplete; identify this controller using its independent hardware ID.
        // Keep upstream flash-chip size, kernel OS-response and per-range CRC verification intact.
        // Only OS+calibration (which preserves parameter blocks) and simulated test writes use this path.
        var info = new OSIDInfo(PcmType.P59);
        logger.AddUserMessage("Verified P59 hardware 12589162 and original OS " + os.Value + " backup. Using the P59 kernel for " + kind + ".");
        await vehicle.SuppressChatter();
        if (!await vehicle.UnlockEcu(info.KeyAlgorithm))
        {
            logger.AddUserMessage("Unlock was not successful.");
            return false;
        }
        logger.AddUserMessage("Unlock succeeded.");
        var writer = new CKernelWriter(vehicle, info, new Protocol(), kind, logger);
        return await writer.Write(File.ReadAllBytes(file), 0, target, true, token);
    }
    static async Task<bool> Properties(Vehicle vehicle,JsonLogger logger,CancellationToken token)
    {
        var vin=await vehicle.QueryVin();
        var os=await vehicle.QueryOperatingSystemId(token);
        var cal=await vehicle.QueryCalibrationId();
        var hardware=await vehicle.QueryHardwareId();
        var serial=await vehicle.QuerySerial();
        var volts=await vehicle.QueryVoltage();
        bool success=vin.Status==ResponseStatus.Success && os.Status==ResponseStatus.Success;
        JsonLogger.Emit(new {type="properties", vin=vin.Status==ResponseStatus.Success ? vin.Value : "Unavailable",osid=os.Status==ResponseStatus.Success ? os.Value.ToString() : "Unavailable", calibration=cal.Status==ResponseStatus.Success ? cal.Value.ToString() : "Unavailable",hardware=hardware.Status==ResponseStatus.Success ? hardware.Value.ToString() : "Unavailable",serial=serial.Status==ResponseStatus.Success ? serial.Value : "Unavailable",voltage=volts.Status==ResponseStatus.Success ? volts.Value : "Unavailable"});
        return success;
    }
    static string Csv(string s) {return "\"" + (s ?? "").Replace("\"","\"\"") + "\"";}
    static async Task<bool> Log(Vehicle vehicle,JsonLogger ui,string root,Dictionary<string,string> opts,CancellationToken token)
    {
        var os=await vehicle.QueryOperatingSystemId(token);
        if(os.Status!=ResponseStatus.Success) throw new Exception("Cannot load logging profile without the PCM OS ID.");
        var db=new ParameterDatabase(root);db.LoadDatabase();
        var profile=new LogProfileReader(db,os.Value,ui).Read(Get(opts,"--profile",Path.Combine(root,"Basics.LogProfile")));
        if(profile.IsEmpty) throw new Exception("Selected logging profile contains no supported parameters.");
        double duration=double.Parse(Get(opts,"--duration","0"),CultureInfo.InvariantCulture);
        if(duration<0 || double.IsNaN(duration) || double.IsInfinity(duration)) throw new ArgumentException("Invalid log duration.");
        using(var can=new CanLogger(db,ui))
        {
            await can.SetPort(null);
            var log=vehicle.CreateLogger(os.Value,can,profile.Columns,ui);
            if(!await log.StartLogging()) throw new Exception("PCM did not accept logging configuration.");
            var nativeHeader=log.GetColumnNames().ToArray();
            int acIndex=Array.FindIndex(nativeHeader,s=>s.Contains("A/C High Side Pressure Sensor") && s.Contains("(Volts)"));
            var header=new[]{"Time (s)"}.Concat(nativeHeader).Concat(acIndex>=0 ? new[]{"7101.10","AFR"} : new string[0]).ToArray();
            JsonLogger.Emit(new {type="log_header",columns=header});
            using(var writer=new StreamWriter(new FileStream(Get(opts,"--output"),FileMode.CreateNew,FileAccess.Write)))
            {
                writer.WriteLine(string.Join(",",header.Select(Csv)));writer.Flush();
                var started=DateTime.UtcNow;int misses=0;
                try
                {
                    while(!token.IsCancellationRequested && (duration==0 || (DateTime.UtcNow-started).TotalSeconds<duration))
                    {
                        var row=await log.GetNextRow();
                        if(row==null) {if(++misses>=5) throw new IOException("No live data received after five attempts.");continue;}
                        misses=0;
                        var nativeValues=row.ToArray();
                        var extras=new List<string>();
                        if(acIndex>=0)
                        {
                            double voltage;
                            string input=nativeValues[acIndex];
                            if(!double.TryParse(input,NumberStyles.Float,CultureInfo.InvariantCulture,out voltage) || double.IsNaN(voltage) || double.IsInfinity(voltage)) throw new IOException("A/C voltage conversion failed.");
                            extras.Add(input);extras.Add(((voltage/.5)+9.57).ToString("F4",CultureInfo.InvariantCulture));
                        }
                        var values=new[]{(DateTime.UtcNow-started).TotalSeconds.ToString("F3",CultureInfo.InvariantCulture)}.Concat(nativeValues).Concat(extras).ToArray();
                        writer.WriteLine(string.Join(",",values.Select(Csv)));writer.Flush();
                        JsonLogger.Emit(new {type="log_row",values});
                    }
                }
                finally {await vehicle.ResetConnection();}
            }
        }
        return true;
    }
    static async Task<bool> Codes(Vehicle vehicle,JsonLogger logger,CancellationToken token)
    {
        await vehicle.SendMessage(new Message(new byte[]{0x68,0x6A,0xF0,0x03}));
        var codes=new HashSet<string>(); bool responded=false;
        for(int i=0;i<12 && !token.IsCancellationRequested;i++)
        {
            var msg=await vehicle.ReceiveMessage();
            byte[] bytes=msg==null ? null : msg.GetBytes();
            if(bytes==null || bytes.Length<4 || bytes[3]!=0x43) continue;
            responded=true;
            for(int j=4;j+1<bytes.Length;j+=2)
            {
                int a=bytes[j],b=bytes[j+1];if(a==0 && b==0) continue;
                codes.Add("PCBU"[a>>6] + ((a>>4)&3).ToString() + (a&15).ToString("X") + b.ToString("X2"));
            }
        }
        JsonLogger.Emit(new {type="codes",codes=codes.OrderBy(c=>c).ToArray(),responded});
        return responded;
    }
}
