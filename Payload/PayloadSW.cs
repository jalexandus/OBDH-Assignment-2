using Common;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Timers;
using static System.Collections.Specialized.BitVector32;

namespace Payload; // For logging;  https://learn.microsoft.com/en-us/dotnet/core/extensions/logging?tabs=command-line
                   // https://learn.microsoft.com/en-us/answers/questions/1377949/logging-in-c-to-a-text-file

internal class Payload
{
    private struct Parameters
    {
        // ---- System Time ----
        long unix_time;              // [s] Unix timestamp (UTC)

        // ---- Thermal ----
        float payload_temperature;       // [°C]

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

    }

    private static IPEndPoint ipEndPoint;

    private static ushort recieveSequenceCount = 0;
    private static ushort transmitSequenceCount = 0;

    private static ushort actionCount = 0; // For tracking actions logged

    public enum Mode
    {
        SAFE = 0,
        INERTIAL = 1,
    };

    private static BlockingCollection<Report> TransmitQueue = new BlockingCollection<Report>(new ConcurrentQueue<Report>(), 100); // Maximum 100 command packets queue
    private static BlockingCollection<Request> RecieveQueue = new BlockingCollection<Request>(new ConcurrentQueue<Request>(), 100); // Maximum 100 command packets queue

    static Mode currentMode = Mode.SAFE;
    static bool modeFlag = false;

    // static long unix_time = 0; // [s] Unix timestamp (UTC)

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("██████████████████████████████████████████████████████████████████");
        Console.WriteLine("████████████████████████ ~ Payload ~ █████████████████████████████");
        Console.WriteLine("██████████████████████████████████████████████████████████████████");

        // Get the localhost ip address
        var hostName = Dns.GetHostName();
        IPHostEntry localhost = Dns.GetHostEntry(hostName);

        // Server IP address is the localipaddress
        IPAddress serverIpAddress = localhost.AddressList[0];

        Console.WriteLine($"Server IP address: {serverIpAddress.ToString()}"); // print the server ip address

        ipEndPoint = new(serverIpAddress, 12_000);

        // Start the communication task
        var cts = new CancellationTokenSource();
        var commTask = CommunicationSession(cts.Token);

        // command interpreter 
        // Loops through the queue of received commands and executes the ones that are due.
        while (!cts.IsCancellationRequested)
        {
            Request nextRequest = RecieveQueue.Take(cts.Token);
            RequestHandlerStatus(nextRequest, cts.Token);
            if (modeFlag)
            {
                RequestHandler(nextRequest, cts.Token);
            }
            else
            {
                continue;
            }
        }
        await commTask; // Pause execution

        // Exit
        Console.Write("Press any key to exit");
        Console.ReadKey();
        return 0;
    }

    private static bool RequestHandlerStatus(Request request, CancellationToken cancelToken)
    {
        // Check if payload is in safemode
        if (modeFlag == false && (request.ServiceType == 8 && request.ServiceSubtype == 1))
        {
            modeFlag = true; // Accept the possibility to switch from SAFE mode
        }
        return modeFlag; 
    }

    private static void RequestHandler(Request request, CancellationToken cancelToken)
    {
        switch ((request.ServiceType, request.ServiceSubtype))
        {
            case (2,1):
                string message = Encoding.UTF8.GetString(request.Data, 0, request.Nbytes);
                Console.WriteLine("Recieved string:" + message);
                break;
            // Mode management
            case (8,1):
                Mode newMode = (Mode)request.Data[0];
                ModeSwitch(newMode);
                break;
            case (8,2):
                    break;
            case (8,3):
                executeAction(request);
                break;
            default:
                // TransmitQueue.Add(InvalidCommandReport(), cancelToken);
                return;
        }
        // TransmitQueue.Add(CompletedCommandReport(), cancelToken);
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
                RecieveQueue.Add(recievedRequest, cancelToken);
                //TransmitQueue.Add(AcknowledgeReport(), cancelToken); // ADD NEW ACTION REPORT

            }
        }, cancelToken);

        // Empty transmit packet queue
        var sendTask = Task.Run(async () =>
        {
            while (!cancelToken.IsCancellationRequested)
            {
                Report nextReport = TransmitQueue.Take(cancelToken);
                await handler.SendAsync(nextReport.Serialize(), 0);

                Console.WriteLine($"Sent acknowledgment: ");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine(nextReport.ToString());
                Console.ForegroundColor = ConsoleColor.White;
            }
        }, cancelToken);

        await recieveTask;
        await sendTask;

        listener.Shutdown(SocketShutdown.Both);
    }

    private static void executeAction(Request request)
    {
        Random random = new Random();
        int minValue = 5;
        int maxValue = 15;
        int randomNumber = random.Next(minValue, maxValue);
        LoggingHandler(randomNumber, request);
    }

    private static void LoggingHandler(int actionTime, Request request)
    {
        string logFilePath = Path.Combine(AppContext.BaseDirectory, "logfile.txt");

        try
        {
            // Append the message with a timestamp
            using (StreamWriter sw = new StreamWriter(logFilePath, append: true))
            {
                DateTimeOffset unixTime = DateTimeOffset.FromUnixTimeSeconds(request.TimeStamp);
                sw.WriteLine(unixTime.ToString()+$": " + $"Time spent taking image: " + actionTime.ToString() + $" seconds");
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[LOGGING ERROR]: {ex.Message}");
            Console.ResetColor();
        }

    }

    private static bool ModeSwitch(Mode newMode)
    {
        switch (newMode)
        {
            case Mode.SAFE:
                Console.WriteLine("Payload OFF");
                currentMode = newMode;
                modeFlag = false;

                return modeFlag;

            case Mode.INERTIAL:
                Console.WriteLine("Payload ON");
                currentMode = newMode;
                modeFlag = true;

                return modeFlag;
            default:
                return false;
        }
        
    }

}
