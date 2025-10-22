using Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Timers;
using static PayloadSW.PlatformOBC;

namespace PayloadSW;

internal class PlatformOBC
{
    private static System.Timers.Timer onboardClock;

    public struct Parameter
    {
        public object Value { get; set; }
        public Type Type { get; }
        public string Label { get; }

        public Parameter(string label, object value)
        {
            Label = label;
            Value = value;
            Type = value?.GetType() ?? typeof(object);
        }
    }

    private static readonly List<Parameter> HousekeepingParameters = new List<Parameter>
{
    // ---- System Time ----
    new Parameter("unix_time [s] Unix timestamp (UTC)", (long)0),

    // ---- Power ----
    new Parameter("bus_voltage [V] Main power bus voltage", (float)12.1f),
    new Parameter("bus_current [A] Total current draw", (float)0.0f),
    new Parameter("battery_voltage [V]", (float)0.0f),
    new Parameter("battery_current [A]", (float)0.0f),
    new Parameter("battery_temperature [°C]", (float)0.0f),

    // ---- Thermal ----
    new Parameter("obc_temperature [°C]", (float)0.0f),
    new Parameter("payload_temperature [°C]", (float)0.0f),
    new Parameter("eps_temperature [°C]", (float)0.0f),

    // ---- Communication ----
    new Parameter("uplink_count [#] Commands received", (ushort)0),
    new Parameter("downlink_count [#] Packets transmitted", (ushort)0),

    // ---- System Health ----
    new Parameter("uptime [s] Time since boot", (uint)0),

    // ---- Payload ----
    new Parameter("payload_mode Current payload mode/state", (byte)0),

    // ---- ADCS ----
    new Parameter("adcs_mode", (byte)0),
};

    private static IPEndPoint? ipEndPointSpaceLink;
    private static IPEndPoint? ipEndPointBusController;

    private static ushort recieveSequenceCount = 0;     // MCS -> OBC
    private static ushort transmitSequenceCount = 0;    // OBC -> MCS

    private static BlockingCollection<Report> TransmitQueue = new BlockingCollection<Report>(new ConcurrentQueue<Report>(), 100); // Maximum 100 command packets queue
    private static BlockingCollection<Request> RecieveQueue = new BlockingCollection<Request>(new ConcurrentQueue<Request>(), 100); // Maximum 100 command packets queue

    private static BlockingCollection<Request> MainBusOutgoingQueue = new BlockingCollection<Request>(new ConcurrentQueue<Request>(), 100); // Maximum 100 command packets queue
    private static BlockingCollection<Report> MainBusIncommingQueue = new BlockingCollection<Report>(new ConcurrentQueue<Report>(), 100); // Maximum 100 command packets queue

    private static PriorityQueue<Request, long> ScheduleQueue = new PriorityQueue<Request, long>();

    static long unix_time = 0; // [s] Unix timestamp (UTC)
    static long boot_time = 0; // [s] Unix timestamp (UTC)

    static async Task<int> Main(string[] args)
    {
        // Start counting the on-board time
        StartClock();

        Console.WriteLine("██████████████████████████████████████████████████████████████████");
        Console.WriteLine("████████████████████████ ~ Platform OBC ~ ████████████████████████");
        Console.WriteLine("██████████████████████████████████████████████████████████████████");

        // Get the localhost ip address
        var hostName = Dns.GetHostName();
        IPHostEntry localhost = Dns.GetHostEntry(hostName);

        // Server IP address is the localipaddress
        IPAddress serverIpAddress = localhost.AddressList[0];

        Console.WriteLine($"Server IP address: {serverIpAddress.ToString()}"); // print the server ip address

        ipEndPointSpaceLink = new(serverIpAddress, 11_000); 
        ipEndPointBusController = new(serverIpAddress, 12_000); // Main spacecraft bus

        // Start the communication task with MCS
        var cts = new CancellationTokenSource();
        var commTask = CommunicationSession(cts.Token);

        // Start the communcation task with Payload
        var busCommTask = MainBusCommunicationSession(cts.Token);

        // command interpreter 
        // Loops through the queue of received commands and executes the ones that are due.
        while (!cts.IsCancellationRequested)
        {
            // Check the scheduling queue
            if(RecieveQueue.Count() > 0)
            {
                Request nextRequest = RecieveQueue.Take(cts.Token);
                RequestHandler(nextRequest, cts.Token);
            }
            if (ScheduleQueue.TryPeek(out Request nextScheduled, out long priority))
            {
                if (GetCurrentTime() >= priority)
                {
                    Console.WriteLine($"Current time {GetCurrentTime()}, scheduled time: {priority}");
                    ScheduleQueue.Dequeue();
                    Console.WriteLine($"Executing scheduled: {nextScheduled.ToString()}");
                    RequestHandler(nextScheduled, cts.Token);
                }
            }

        }
        await commTask; await busCommTask; // Pause execution

        // Exit
        Console.Write("Press any key to exit");
        Console.ReadKey();
        return 0;
    }

    private static void RequestHandler(Request request, CancellationToken cancelToken)
    {
        // -------------------- Request routing --------------------

        switch (request.ApplicationID)
        {
            // Application: OBSW
            case 0: 
                break;

            // Application: PayloadSW
            case 1: 
                Console.WriteLine("Forwarding request to payload");
                MainBusOutgoingQueue.Add(request, cancelToken); // Forward to OBC-Payload transmit queue
                return;
        }

        // -------------------- Service/subservice --------------------

        switch (request.ServiceType)
        {
            // Recieving a string 
            case 2:
                string message = Encoding.UTF8.GetString(request.Data, 0, request.Nbytes);
                Console.WriteLine("Recieved string: " + message);
                // CommandHandlerPayload(message); // Forward to payload request handler 
                break;

            // Housekeeping
            case 3:
                switch (request.ServiceSubtype)
                {
                    // Start periodic housekeeping
                    
                    case 5:
                        if(BitConverter.ToBoolean(request.Data,0)) StartPeriodicTelemetry(1000, cancelToken);
                         // ELSE Turn off cyclic housekeeping
                         break;
                    default:
                        TransmitQueue.Add(InvalidCommandReport(), cancelToken);
                        break;
                }
                break;

            // Time services 
            case 9: 
                if (request.ServiceSubtype == 4) // Set OBT
                {
                    long newTime = BitConverter.ToInt64(request.Data);
                    DateTimeOffset OBT = DateTimeOffset.FromUnixTimeSeconds(newTime);
                    Console.WriteLine($"Set OBT to: {OBT.ToString()}");
                    SetCurrentTime(newTime);
                    break;
                }
                else TransmitQueue.Add(InvalidCommandReport(), cancelToken);
                return;

            // Scheduling commands
            case 11:
                if (request.ServiceSubtype == 4)
                {
                    Request timeScheduledRequest = new Request(request.Data); // De-serialize the scheduler packet payload data
                    ScheduleQueue.Enqueue(timeScheduledRequest, timeScheduledRequest.TimeStamp); // Enqueue the time-scheduled command based on the timestamp
                    Console.WriteLine($"Scheduled TC: {timeScheduledRequest.ToString()}");
                    break;
                }
                else TransmitQueue.Add(InvalidCommandReport(), cancelToken);
                return;

            // Default send InvalidCommandReport
            default:
                TransmitQueue.Add(InvalidCommandReport(), cancelToken);
                return;
        }
        TransmitQueue.Add(CompletedCommandReport(), cancelToken);
    }

    private static void RoutingHandler(Request request)
    {

    }

    private static async Task CommunicationSession(CancellationToken cancelToken)
    {
        // Start server

        // Command input loop
        using Socket listener = new(
        ipEndPointSpaceLink.AddressFamily,
        SocketType.Stream,
        ProtocolType.Tcp);

        listener.Bind(ipEndPointSpaceLink);
        listener.Listen(100);

        var handler = await listener.AcceptAsync();

        // Fill recieved request queue
        var recieveTask = Task.Run(async () =>
        {
            while (!cancelToken.IsCancellationRequested)
            {
                // Receive message.
                var buffer = new byte[1_024];
                await handler.ReceiveAsync(buffer, SocketFlags.None);
                Request recievedRequest = new Request(buffer);

                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"[RX]: {recievedRequest.ToString()}");
                Console.ForegroundColor = ConsoleColor.White;

                // Check sequence count
                if (recievedRequest.SequenceControl == recieveSequenceCount)
                {
                    recieveSequenceCount++;

                    RecieveQueue.Add(recievedRequest, cancelToken);
                    TransmitQueue.Add(AcknowledgeReport(), cancelToken);
                }
                else {
                    TransmitQueue.Add(InvalidCommandReport(), cancelToken);
                }

            }       
        }, cancelToken);

        // Empty transmit report queue
        var sendTask = Task.Run(async () =>
        {
            while (!cancelToken.IsCancellationRequested)
            {
                Report nextReport = TransmitQueue.Take(cancelToken);                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[TX]: {nextReport.ToString()}");
                Console.ForegroundColor = ConsoleColor.White;

                await handler.SendAsync(nextReport.Serialize(), 0);
                transmitSequenceCount++;
            }
        }, cancelToken);

        await recieveTask;
        await sendTask;

        listener.Shutdown(SocketShutdown.Both);
    }
    private static async Task MainBusCommunicationSession(CancellationToken cancelToken)
    {

        // Open client socket 
        using Socket client = new(
            ipEndPointBusController.AddressFamily,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        int timeout = 10;
        for (int i = 0; i < timeout; i++)
        {
            try
            {
                await client.ConnectAsync(ipEndPointBusController);
                Console.WriteLine($"Connected main bus from {client.LocalEndPoint} to {client.RemoteEndPoint}");
                break;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Processing failed: {exception.Message}");
            }
            Thread.Sleep(100);
        }


        
        // Empty outgoing command queue
        var sendTask = Task.Run(async () =>
        {
            while (!cancelToken.IsCancellationRequested)
            {
                // Send next packet in queue
                Request nextRequest = MainBusOutgoingQueue.Take(cancelToken);
                byte[] messageBytes = nextRequest.Serialize();
                await client.SendAsync(messageBytes, SocketFlags.None);
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[OBC -> PL]: {nextRequest.ToString()}");
                Console.ForegroundColor = ConsoleColor.White;
            }
        }, cancelToken);

        var receiveTask = Task.Run(async () =>
        {
            while (!cancelToken.IsCancellationRequested)
            {
                // Receive telemetry.
                var buffer = new byte[1_024];
                await client.ReceiveAsync(buffer, SocketFlags.None);
                Report payloadReport = new Report(buffer);
                TransmitQueue.Add(payloadReport);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[PL -> OBC]: {payloadReport.ToString()}");
                Console.ForegroundColor = ConsoleColor.Cyan;
            }
        }, cancelToken);

        await receiveTask;
        await sendTask;

        // Close socket
        client.Shutdown(SocketShutdown.Both);
    }

    // --------------------- Reports -----------------------
    private static Report AcknowledgeReport()
    {
        // Create packet with service/subservice: Successful acceptance verification
        return new Report(GetCurrentTime(), 0, transmitSequenceCount, 1, 1, Array.Empty<byte>() );
    }
    private static Report InvalidCommandReport()
    {
        // Create packet with service/subservice: Failed acceptance verification report
        return new Report(GetCurrentTime(), 0, transmitSequenceCount, 1, 2, Array.Empty<byte>());
    }
    private static Report CompletedCommandReport()
    {
        // Create packet with service/subservice: Failed start of execution
        return new Report(GetCurrentTime(), 0, transmitSequenceCount, 1, 4, Array.Empty<byte>());
    }

    private static Report TelemetryReport()
    {
        byte[] data = SerializeParameterList(HousekeepingParameters);
        // Create packet with service/subservice: housekeeping parameter report
        return new Report(GetCurrentTime(), 0, transmitSequenceCount, 3, 25, data);
    }

    // ----------------- On-board functions -----------------

    // Returns the OBC time in unix seconds 
    private static long GetCurrentTime() => Interlocked.Read(ref unix_time);
    private static void SetCurrentTime(long new_unix_time) => Interlocked.Exchange(ref unix_time, new_unix_time);

    // Based on the following example: https://learn.microsoft.com/en-us/dotnet/api/system.timers.timer?view=net-9.0
    private static void StartClock()
    {
        // Create a timer with a two second interval.
        onboardClock = new System.Timers.Timer(1000);
        // Hook up the Elapsed event for the timer. 
        onboardClock.Elapsed += (Object source, ElapsedEventArgs e) => { 
            System.Threading.Interlocked.Increment(ref unix_time);
            System.Threading.Interlocked.Increment(ref boot_time);

        };
        onboardClock.AutoReset = true;
        onboardClock.Enabled = true;
    }

    private static byte[] SerializeParameterList(List<Parameter> parameters )
    {
        List<byte> buffer = new List<byte>();

        foreach (Parameter param in parameters)
        {
            object val = param.Value;
            byte[] parameterBytes;

            switch (val)
            {
                case byte b:
                    parameterBytes = new[] { b };
                    break;
                case bool flag:
                    parameterBytes = BitConverter.GetBytes(flag);
                    break;
                case short s:
                    parameterBytes = BitConverter.GetBytes(s);
                    break;
                case ushort us:
                    parameterBytes = BitConverter.GetBytes(us);
                    break;
                case int i:
                    parameterBytes = BitConverter.GetBytes(i);
                    break;
                case uint ui:
                    parameterBytes = BitConverter.GetBytes(ui);
                    break;
                case long l:
                    parameterBytes = BitConverter.GetBytes(l);
                    break;
                case ulong ul:
                    parameterBytes = BitConverter.GetBytes(ul);
                    break;
                case float f:
                    parameterBytes = BitConverter.GetBytes(f);
                    break;
                case double d:
                    parameterBytes = BitConverter.GetBytes(d);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported parameter type: {val.GetType().Name}");
            }

            buffer.AddRange(parameterBytes);
        }

        return buffer.ToArray();
    }

    private static void StartPeriodicTelemetry(int period, CancellationToken clt)
    {
        // Create a timer with a two second interval.
        onboardClock = new System.Timers.Timer(period);
        // Hook up the Elapsed event for the timer. 
        onboardClock.Elapsed += (Object source, ElapsedEventArgs e) => { TransmitQueue.Add(TelemetryReport(), clt); };
        onboardClock.AutoReset = true;
        onboardClock.Enabled = true;
    }

    private static void UpdateHousekeepingParameters()
    {
        // ---- System Time ----
        HousekeepingParameters[0].Value = GetCurrentTime(); // unix_time

        // ---- Power ----
        HousekeepingParameters[1].Value = /* bus_voltage */ 3.305f;
        HousekeepingParameters[2].Value = /* bus_current */ 23.01f;
        HousekeepingParameters[3].Value = /* battery_voltage */ 11.7f;
        HousekeepingParameters[4].Value = /* battery_current */ 8.2f;
        HousekeepingParameters[5].Value = /* battery_temperature */ 17.9f;

        // ---- Thermal ----
        HousekeepingParameters[6].Value = /* obc_temperature */ 63.7f;
        HousekeepingParameters[7].Value = /* payload_temperature */ 11.1f;
        HousekeepingParameters[8].Value = /* eps_temperature */ 0.0f;

        // ---- Communication ----
        HousekeepingParameters[9].Value = /* uplink_count */ 0;
        HousekeepingParameters[10].Value = /* downlink_count */ 0;
        HousekeepingParameters[11].Value = /* last_command_status */ (byte)0;
        HousekeepingParameters[12].Value = /* comm_status */ (byte)0;

        // ---- System Health ----
        HousekeepingParameters[13].Value = /* uptime */ 0u;
        HousekeepingParameters[14].Value = /* reset_count */ (ushort)0;
        HousekeepingParameters[15].Value = /* last_reset_reason */ (byte)0;

        // ---- Payload ----
        HousekeepingParameters[16].Value = /* payload_mode */ (byte)0;
        HousekeepingParameters[17].Value = /* payload_status */ false;

        // ---- ADCS ----
        HousekeepingParameters[18].Value = /* adcs_mode */ (byte)0;
    }
    private static void EventTelemetry()
    {

    }
}

