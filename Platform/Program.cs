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

namespace Program;



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

    private static IPEndPoint ipEndPoint;
    private static IPEndPoint ipEndPoint2;

    private static ushort recieveSequenceCount = 0; // MCS -> OBC
    private static ushort transmitSequenceCount = 0; // OBC -> MCS

    private static ushort recieveSequenceCount2 = 0; // Payload -> OBC
    private static ushort transmitSequenceCount2 = 0; // OBC -> Payload

    private static BlockingCollection<Report> TransmitQueue = new BlockingCollection<Report>(new ConcurrentQueue<Report>(), 100); // Maximum 100 command packets queue
    private static BlockingCollection<Request> RecieveQueue = new BlockingCollection<Request>(new ConcurrentQueue<Request>(), 100); // Maximum 100 command packets queue

    private static BlockingCollection<Request> TransmitQueue2 = new BlockingCollection<Request>(new ConcurrentQueue<Request>(), 100); // Maximum 100 command packets queue
    private static BlockingCollection<Report> RecieveQueue2 = new BlockingCollection<Report>(new ConcurrentQueue<Report>(), 100); // Maximum 100 command packets queue

    static long unix_time = 0;              // [s] Unix timestamp (UTC)

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

        ipEndPoint = new(serverIpAddress, 11_000); 
        ipEndPoint2 = new(serverIpAddress, 12_000); // For payload

        // Start the communication task with MCS
        var cts = new CancellationTokenSource();
        var commTask = CommunicationSession(cts.Token);

        // Start the communcation task with Payload
        var cts2 = new CancellationTokenSource();
        var commTask2 = PayloadCommunicationSession(serverIpAddress, cts2.Token);

        // command interpreter 
        // Loops through the queue of received commands and executes the ones that are due.
        while (!cts.IsCancellationRequested)
        {
            Request nextRequest = RecieveQueue.Take(cts.Token);
            RequestHandler(nextRequest, cts.Token);
        }
        await commTask; // Pause execution

        while (!cts2.IsCancellationRequested)
        {
            Request nextRequest2 = TransmitQueue2.Take(cts2.Token);
            PayloadRequestHandler(nextRequest2, cts2.Token);
        }
        await commTask2; // Pause execution

        // Exit
        Console.Write("Press any key to exit");
        Console.ReadKey();
        return 0;
    }

    private static void RequestHandler(Request request, CancellationToken cancelToken)
    {
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

private static async Task CommunicationSession(CancellationToken cancelToken)
    {
        // Start server

        // Command input loop
        using Socket listener = new(
        ipEndPoint.AddressFamily,
        SocketType.Stream,
        ProtocolType.Tcp);

        listener.Bind(ipEndPoint);
        listener.Listen(100);

        var handler = await listener.AcceptAsync();


        // Fill recieved command queue
        var recieveTask = Task.Run(async () =>
        {
            while (!cancelToken.IsCancellationRequested)
            {
                // Receive message.
                var buffer = new byte[1_024];
                await handler.ReceiveAsync(buffer, SocketFlags.None);
                Request recievedRequest = new Request(buffer);

                // Verify command isnt outdated
                var deltaTime = GetCurrentTime() - DateTimeOffset.FromUnixTimeSeconds(recievedRequest.TimeStamp).ToUnixTimeSeconds();

                if (deltaTime < +1 || true) // accept t+1 second overdueness
                {
                    RecieveQueue.Add(recievedRequest, cancelToken);
                    TransmitQueue.Add(AcknowledgeReport(), cancelToken);
                    TransmitQueue2.Add(recievedRequest, cancelToken); // Forward to OBC-Payload transmit queue (just to check if message is forwarded)
                }
                else {
                    TransmitQueue.Add(InvalidCommandReport(), cancelToken);
                }

            }
        }, cancelToken);

        // Empty transmit packet queue
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

    private static void SendString(DateTime utcTime, string message)
    {
        // Convert to Unix time in seconds
        long unixSeconds = new DateTimeOffset(utcTime).ToUnixTimeSeconds();


        // Set service and subservice type
        byte serviceType = 2;
        byte serviceSubtype = 1;

        // Encode message data
        byte[] data = Encoding.UTF8.GetBytes(message);
        Request TX_Pckt = new Request(unixSeconds, transmitSequenceCount2++, serviceType, serviceSubtype, data);
        TransmitQueue2.Add(TX_Pckt);
    }

    private static void CommandHandlerPayload(string message)
    {
        switch (message)
        {
            case "send":
                SendString(DateTime.UtcNow, message);
                return;
        }
        Console.WriteLine("ERROR: Not a recognized command.");
    }

    private static void PayloadRequestHandler(Request request, CancellationToken cancelToken)
    {
        switch (request.ServiceType)
        {
            case 2:
                string message = Encoding.UTF8.GetString(request.Data, 0, request.Nbytes);
                Console.WriteLine("Recieved string to forward to payload:" + message);
                CommandHandlerPayload(message); // Forward to payload request handler 
                break;
            case 9:
                if (request.ServiceSubtype == 4)
                {
                    SetCurrentTime(BitConverter.ToInt64(request.Data));
                    break;
                }
                else return;

            default:
                RecieveQueue2.Add(InvalidCommandReport(), cancelToken);
                return;
        }
        RecieveQueue2.Add(CompletedCommandReport(), cancelToken);
    }

    // Open up communication with payload server
    private static async Task PayloadCommunicationSession(IPAddress serverIpAddress, CancellationToken cancelToken)
    {
        // Payload server port
        IPEndPoint ipEndPoint2 = new(serverIpAddress, 12000);

        // Open client socket
        using Socket client = new(
            ipEndPoint2.AddressFamily,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        try
        {
            await client.ConnectAsync(ipEndPoint2);
            Console.WriteLine($"[OBC->Payload] Connected from {client.LocalEndPoint} to {client.RemoteEndPoint}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[OBC->Payload] Connection failed: {e.Message}");
            return;
        }

        // --- SEND LOOP ---
         var sendTask = Task.Run(async () =>
        {
            while (!cancelToken.IsCancellationRequested)
            {
                // Send next payload command
                Request nextPayloadCommand = TransmitQueue2.Take(cancelToken);
                byte[] messageBytes = nextPayloadCommand.Serialize();
                await client.SendAsync(messageBytes, SocketFlags.None);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[OBC->Payload] TX: {nextPayloadCommand}");
                Console.ResetColor();
            }
        }, cancelToken);

        // --- RECEIVE LOOP ---
        // var receiveTask = Task.Run(async () =>
        // {
        //  while (!cancelToken.IsCancellationRequested)
        //  {
        //    var buffer = new byte[1024];
        //    int received = await client.ReceiveAsync(buffer, SocketFlags.None);

        //     if (received == 0)
        //    {
        //         Console.WriteLine("[OBC→Payload] Payload disconnected.");
        //         break;
        //      }

        // Convert raw bytes into telemetry/ack report
        //      Report payloadReport = new Report(buffer);
        //      PayloadIncomingQueue.Add(payloadReport, cancelToken);

        //      Console.ForegroundColor = ConsoleColor.Blue;
        //      Console.WriteLine($"[OBC→Payload RX]: {payloadReport}");
        //       Console.ResetColor();
        //   }
        //}, cancelToken);

        //await Task.WhenAll(receiveTask, sendTask);
        await Task.WhenAll(sendTask);
        client.Shutdown(SocketShutdown.Both);
    }

    private static Report AcknowledgeReport()
    {
        // Create packet with service/subservice: Successful acceptance verification
        return new Report(GetCurrentTime(), transmitSequenceCount++, 1, 1, Array.Empty<byte>() );
    }
    private static Report InvalidCommandReport()
    {
        // Create packet with service/subservice: Failed start of execution
        return new Report(GetCurrentTime(), transmitSequenceCount++, 1, 4, Array.Empty<byte>());
    }
    private static Report CompletedCommandReport()
    {
        // Create packet with service/subservice: Failed start of execution
        return new Report(GetCurrentTime(), transmitSequenceCount++, 1, 4, Array.Empty<byte>());
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
}

