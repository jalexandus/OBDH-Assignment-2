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
    private static System.Timers.Timer cyclicHKClocl;  
    private static List<Common.Parameter> HousekeepingParameters = new List<Common.Parameter>
    {
        // ---- System Time ----
        new Common.Parameter((byte)0x00, (long)0), // unix_time [s] Unix timestamp (UTC)

        // ---- Power ----
        new Common.Parameter((byte)0x01, (float)12.1f), // bus_voltage [V] Main power bus voltage
        new Common.Parameter((byte)0x02, (float)0.0f),  // bus_current [A] Total current draw
        new Common.Parameter((byte)0x03, (float)0.0f),  // battery_voltage [V]
        new Common.Parameter((byte)0x04, (float)0.0f),  // battery_current [A]
        new Common.Parameter((byte)0x05, (float)0.0f),  // battery_temperature [°C]

        // ---- Thermal ----
        new Common.Parameter((byte)0x06, (float)0.0f),  // obc_temperature [°C]
        new Common.Parameter((byte)0x07, (float)0.0f),  // payload_temperature [°C]
        new Common.Parameter((byte)0x08, (float)0.0f),  // eps_temperature [°C]

        // ---- Communication ----
        new Common.Parameter((byte)0x09, (ushort)0),   // uplink_count [#] Commands received
        new Common.Parameter((byte)0x0A, (ushort)0),   // downlink_count [#] Packets transmitted

        // ---- System Health ----
        new Common.Parameter((byte)0x0B, (long)0),     // uptime [s] Time since boot

        // ---- Payload ----
        new Common.Parameter((byte)0x0C, (byte)0),     // payload_mode Current payload mode/state

        // ---- ADCS ----
        new Common.Parameter((byte)0x0D, (byte)0),     // adcs_mode
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

        switch ((request.ServiceType, request.ServiceSubtype))
        {
            // Recieving a string 
            case (2,1):
                string message = Encoding.UTF8.GetString(request.Data, 0, request.Nbytes);
                Console.WriteLine("Recieved string: " + message);
                // CommandHandlerPayload(message); // Forward to payload request handler 
                break;

            // Housekeeping
            case (3,5):
                if (BitConverter.ToBoolean(request.Data, 0)) StartPeriodicTelemetry(5000, cancelToken);
                // ELSE Turn off cyclic housekeeping
                break;

            // Time services 
            case (9,4):
                long newTime = BitConverter.ToInt64(request.Data);
                DateTimeOffset OBT = DateTimeOffset.FromUnixTimeSeconds(newTime);
                Console.WriteLine($"Set OBT to: {OBT.ToString()}");
                SetCurrentTime(newTime);
                break;

            // Scheduling commands
            case (11, 4):
                Request timeScheduledRequest = new Request(request.Data); // De-serialize the scheduler packet payload data
                ScheduleQueue.Enqueue(timeScheduledRequest, timeScheduledRequest.TimeStamp); // Enqueue the time-scheduled command based on the timestamp
                Console.WriteLine($"Scheduled TC: {timeScheduledRequest.ToString()}");
                break;

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
        UpdateHousekeepingParameters();
        byte[] data = SerializeParameterList();
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
        // Create a timer with a one second interval.
        onboardClock = new System.Timers.Timer(1000);
        // Create a timer with a ten second interval.
        cyclicHKClocl = new System.Timers.Timer(10000);
        // Hook up the Elapsed event for the timer. 
        onboardClock.Elapsed += (Object source, ElapsedEventArgs e) => { 
            System.Threading.Interlocked.Increment(ref unix_time);
            System.Threading.Interlocked.Increment(ref boot_time);

        };
        onboardClock.AutoReset = true;
        onboardClock.Enabled = true;
    }

    private static byte[] SerializeParameterList()
    {
        List<byte> buffer = new List<byte>();

        foreach (Common.Parameter param in HousekeepingParameters)
        {
            buffer.AddRange(param.Serialize());
        }

        return buffer.ToArray();
    }

    private static void StartPeriodicTelemetry(CancellationToken clt)
    {
        // Hook up the Elapsed event for the timer. 
        onboardClock.Elapsed += (Object source, ElapsedEventArgs e) => { TransmitQueue.Add(TelemetryReport(), clt); };
        onboardClock.AutoReset = true;
        onboardClock.Enabled = true;
    }
    private static void StartPeriodicTelemetry(CancellationToken clt)
    {
        // Hook up the Elapsed event for the timer. 
        onboardClock.Elapsed += (Object source, ElapsedEventArgs e) => { TransmitQueue.Add(TelemetryReport(), clt); };
        onboardClock.AutoReset = true;
        onboardClock.Enabled = true;
    }

    private static void UpdateHousekeepingParameters()
    {
        // ---- System Time ----
        HousekeepingParameters[0].Value = GetCurrentTime(); // unix_time [s]

        // ---- Power ----
        HousekeepingParameters[1].Value = 3.305f;  // bus_voltage [V]
        HousekeepingParameters[2].Value = 23.01f;  // bus_current [A]
        HousekeepingParameters[3].Value = 11.7f;   // battery_voltage [V]
        HousekeepingParameters[4].Value = 8.2f;    // battery_current [A]
        HousekeepingParameters[5].Value = 17.9f;   // battery_temperature [°C]

        // ---- Thermal ----
        HousekeepingParameters[6].Value = 63.7f;   // obc_temperature [°C]
        HousekeepingParameters[7].Value = 11.1f;   // payload_temperature [°C]
        HousekeepingParameters[8].Value = 0.0f;    // eps_temperature [°C]

        // ---- Communication ----
        HousekeepingParameters[9].Value = recieveSequenceCount; // uplink_count [#]
        HousekeepingParameters[10].Value = transmitSequenceCount; // downlink_count [#]

        // ---- System Health ----
        HousekeepingParameters[11].Value = (long) boot_time;        // uptime [s]

        // ---- Payload ----
        HousekeepingParameters[12].Value = (byte)0;   // payload_mode

        // ---- ADCS ----
        HousekeepingParameters[13].Value = (byte)0;   // adcs_mode
    }
}

