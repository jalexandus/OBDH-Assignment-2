using Common;
using System;
using System.Collections.Concurrent;
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
using System.Collections.Generic;

namespace PayloadSW;



internal class PlatformOBC
{
    private static System.Timers.Timer onboardClock;

    private struct Parameters
    {
        // ---- System Time ----
        long unix_time;              // [s] Unix timestamp (UTC)

        // ---- Power ----
        float bus_voltage;               // [V] Main power bus voltage
        float bus_current;               // [A] Total current draw
        float battery_voltage;           // [V]
        float battery_current;           // [A]
        float battery_temperature;       // [°C]

        // ---- Thermal ----
        float obc_temperature;           // [°C]
        float payload_temperature;       // [°C]
        float eps_temperature;           // [°C]

        // ---- Communication ----
        UInt16 uplink_count;           // [#] Commands received
        UInt16 downlink_count;         // [#] Packets transmitted
        byte last_command_status;    // 0 = OK, 1 = ERR, 2 = UNKNOWN
        byte comm_status;            // Bit flags (bit0: TX on, bit1: RX on, etc.)

        // ---- System Health ----
        UInt32 uptime;                 // [s] Time since boot
        UInt16 reset_count;            // [#] Number of system resets
        byte last_reset_reason;      // Code: 0=power, 1=watchdog, 2=manual, etc.

        // ---- Payload ----
        byte payload_mode;           // Current payload mode/state
        bool payload_status;         // Bit field for payload subsystems

        // ---- ADCS ----
        byte adcs_mode;
    }

    private static IPEndPoint ipEndPointSpaceLink;
    private static IPEndPoint ipEndPointBusController;

    private static ushort recieveSequenceCount = 0; // MCS -> OBC
    private static ushort transmitSequenceCount = 0; // OBC -> MCS

    private static BlockingCollection<Report> TransmitQueue = new BlockingCollection<Report>(new ConcurrentQueue<Report>(), 100); // Maximum 100 command packets queue
    private static BlockingCollection<Request> RecieveQueue = new BlockingCollection<Request>(new ConcurrentQueue<Request>(), 100); // Maximum 100 command packets queue


    private static BlockingCollection<Request> MainBusTransmitQueue = new BlockingCollection<Request>(new ConcurrentQueue<Request>(), 100); // Maximum 100 command packets queue
    private static BlockingCollection<Report> MainBusRecieveQueue = new BlockingCollection<Report>(new ConcurrentQueue<Report>(), 100); // Maximum 100 command packets queue

    static long unix_time = 0; // [s] Unix timestamp (UTC)

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
            Request nextRequest = RecieveQueue.Take(cts.Token);
            RequestHandler(nextRequest, cts.Token);
        }
        await commTask; await busCommTask; // Pause execution

        /*
        while (!cts2.IsCancellationRequested)
        {
            Request nextRequest2 = TransmitQueue2.Take(cts2.Token);
            PayloadRequestHandler(nextRequest2, cts2.Token);
        }
        await busCommTask; // Pause execution
        */

        // Exit
        Console.Write("Press any key to exit");
        Console.ReadKey();
        return 0;
    }

    private static void RequestHandler(Request request, CancellationToken cancelToken)
    {
        switch (request.ApplicationID)
        {
            case 0: // Application: OBC
                break;
            case 1: // Application: Payload
                MainBusTransmitQueue.Add(recievedRequest, cancelToken); // Forward to OBC-Payload transmit queue (just to check if message is forwarded)
                break;
        }
        switch (request.ServiceType)
        {
            case 2:
                string message = Encoding.UTF8.GetString(request.Data, 0, request.Nbytes);
                Console.WriteLine("Recieved string:" + message);
                // CommandHandlerPayload(message); // Forward to payload request handler 
                break;
            case 9:
                if (request.ServiceSubtype == 4)
                {
                    SetCurrentTime(BitConverter.ToInt64(request.Data));
                    break;
                }
                else return;

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

        PriorityQueue<Request, short> ScheduleQueue = new PriorityQueue<Request, short>();

        // Fill recieved request queue
        var recieveTask = Task.Run(async () =>
        {
            while (!cancelToken.IsCancellationRequested)
            {
                // Receive message.
                var buffer = new byte[1_024];
                await handler.ReceiveAsync(buffer, SocketFlags.None);
                Request recievedRequest = new Request(buffer);

                if (recievedRequest.SequenceControl == recieveSequenceCount++)
                {
                    RecieveQueue.Add(recievedRequest, cancelToken);
                    TransmitQueue.Add(AcknowledgeReport(), cancelToken);
                    if(recievedRequest.ServiceType == )
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
                await handler.SendAsync(nextReport.Serialize(), 0);

                Console.WriteLine($"[OBC -> MCS] Sent acknowledgment: ");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine(nextReport.ToString());
                Console.ForegroundColor = ConsoleColor.White;
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
        try
        {
            await client.ConnectAsync(ipEndPointBusController);
            Console.WriteLine($"Connected main bus from {client.LocalEndPoint} to {client.RemoteEndPoint}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Processing failed: {e.Message}");
            return;
        }


        // Empty outgoing command queue
        var sendTask = Task.Run(async () =>
        {
            while (!cancelToken.IsCancellationRequested)
            {
                // Send next packet in queue
                Request nextRequest = OutgoingQueue.Take(cancelToken);
                byte[] messageBytes = nextRequest.Serialize();
                await client.SendAsync(messageBytes, SocketFlags.None);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[MCS -> OBC] TX: {nextRequest.ToString()}");
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
                Report telemetry = new Report(buffer);

                recieveSequenceCount++;
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"[MCS <- OBC] RX: {telemetry.ToString()}");
                Console.ForegroundColor = ConsoleColor.White;
            }
        }, cancelToken);

        await receiveTask;
        await sendTask;

        // Close socket
        client.Shutdown(SocketShutdown.Both);
    }

    private static Report AcknowledgeReport()
    {
        // Create packet with service/subservice: Successful acceptance verification
        return new Report(GetCurrentTime(), 0, transmitSequenceCount++, 1, 1, Array.Empty<byte>() );
    }
    private static Report InvalidCommandReport()
    {
        // Create packet with service/subservice: Failed start of execution
        return new Report(GetCurrentTime(), 0, transmitSequenceCount++, 1, 4, Array.Empty<byte>());
    }
    private static Report CompletedCommandReport()
    {
        // Create packet with service/subservice: Failed start of execution
        return new Report(GetCurrentTime(), 0, transmitSequenceCount++, 1, 4, Array.Empty<byte>());
    }

    // Returns the OBC time in unix seconds 
    private static long GetCurrentTime() => Interlocked.Read(ref unix_time);
    private static void SetCurrentTime(long new_unix_time) => Interlocked.Exchange(ref unix_time, new_unix_time);

    // Based on the following example: https://learn.microsoft.com/en-us/dotnet/api/system.timers.timer?view=net-9.0
    private static void StartClock()
    {
        // Create a timer with a two second interval.
        onboardClock = new System.Timers.Timer(1000);
        // Hook up the Elapsed event for the timer. 
        onboardClock.Elapsed += (Object source, ElapsedEventArgs e) => { System.Threading.Interlocked.Increment(ref unix_time);};
        onboardClock.AutoReset = true;
        onboardClock.Enabled = true;
    }

    private static void StartPeriodicTelemetry(int period)
    {
        // Create a timer with a two second interval.
        onboardClock = new System.Timers.Timer(period);
        // Hook up the Elapsed event for the timer. 
        onboardClock.Elapsed += (Object source, ElapsedEventArgs e) => { EventTelemetry(); };
        onboardClock.AutoReset = true;
        onboardClock.Enabled = true;
    }
    private static void EventTelemetry()
    {

    }
    /*
    private static void Schedule()
    {

        // Verify command isnt outdated
        var deltaTime = GetCurrentTime() - DateTimeOffset.FromUnixTimeSeconds(recievedRequest.TimeStamp).ToUnixTimeSeconds();

        if (deltaTime < +1 || true) // accept t+1 second overdueness
    }
    */
}

