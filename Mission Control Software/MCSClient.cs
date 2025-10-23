using Common;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Wasm;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static Mission_Control_Software.MCSCLient;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Mission_Control_Software;
internal class MCSCLient
{
    private static ushort recieveSequenceCount = 0;
    private static ushort transmitSequenceCount = 0;

    private static BlockingCollection<Request> OutgoingQueue = new BlockingCollection<Request>(new ConcurrentQueue<Request>(), 100); // Maximum 100 command packets queue
    private static BlockingCollection<Report> IncomingQueue = new BlockingCollection<Report>(new ConcurrentQueue<Report>(), 100); // Maximum 100 recieved telemetry packets in queue

    public enum Mode
        {
          SAFE = 0,
          INERTIAL = 1,
        };
    static async Task<int> Main(string[] args)
    {
        const string configFilePath = "config.txt";
        string serverIpAddressString;
        
        Console.WriteLine("██████████████████████████████████████████████████████████████████");
        Console.WriteLine("████████████████ ~ LTU Mission Control software ~ ████████████████");
        Console.WriteLine("██████████████████████████████████████████████████████████████████");

        // Get configuration settings
        try
        {
            //Pass the file path and file name to the StreamReader constructor
            StreamReader sr = new StreamReader(configFilePath);
            //Read the first line of text
            serverIpAddressString = sr.ReadLine();
            Console.WriteLine("Config.txt read successfully");
            //close the file
            sr.Close();
        }
        catch (Exception e)
        {
            Console.WriteLine("Exception: " + e.Message);
            return 0;
        }
        // Get the localhost ip address
        var hostName = Dns.GetHostName();
        IPHostEntry localhost = Dns.GetHostEntry(hostName);
        
        // This is the IP address of the local machine
        IPAddress localIpAddress = localhost.AddressList[0];

        // Server IP address
        IPAddress serverIpAddress;
        if (serverIpAddressString == "localhost")
        {
            serverIpAddress = localIpAddress;
        }
        else
        {
            serverIpAddress = IPAddress.Parse(serverIpAddressString);
        }
            
        Console.WriteLine("Client IP address: " + localIpAddress.ToString()); // print the local ip address
        Console.WriteLine("Server IP address: " + serverIpAddress.ToString()); // print the server ip address

        Console.WriteLine("Press any key to try establish connection.");
        Console.ReadKey();

        // Start the communcation task
        var cts = new CancellationTokenSource();
        var commTask = CommunicationSession(localIpAddress, cts.Token);

        Request TX_Pckt;
        // Command input loop
        while (true)
        {
            string firstCommand;
            MCSCommand cmd = new MCSCommand(Console.ReadLine());
            if (cmd.args.Count == 0) continue;
            else firstCommand = cmd.args.Peek();

                
            if (firstCommand == "exit")
            {
                cts.Cancel(); // Cancel the communcation task
                continue;
            }
            else
            {
                try
                {
                    TX_Pckt = CommandHandler(cmd);
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception.Message);
                    continue;
                }

            }
            OutgoingQueue.Add(TX_Pckt);
            transmitSequenceCount++;
        }
        await commTask;
        return 0;

    }
    private static Request CommandHandler(MCSCommand input)
    {
        if (input.args == null || input.args.Count < 2)
            throw new ArgumentException("Not enough arguments for command.");

        // Select destination (APID) 
        byte APID;
        string destination = input.args.Pop().ToLowerInvariant();

        switch (destination)
        {
            case "obc":
                APID = 0;
                break;
            case "payload":
                APID = 1;
                break;
            default:
                throw new Exception($"'{destination}' is not a recognized application ID.");
        }

        // Select command 
        if (input.args.Count == 0)
            throw new Exception("Missing command after destination application.");

        string command = input.args.Pop().ToLowerInvariant();
        Request TX_Pckt;

        switch (command)
        {
            case "send":
                {
                    string message = string.Join(" ",input.args); // Combine remaining args
                    message.Trim();
                    TX_Pckt = SendString(APID, message);
                    break;
                }

            case "update-obt":
                {
                    if (input.args.Count == 0)
                        throw new Exception("Missing time argument for update-obt.");

                    string arg = input.args.Peek().ToLowerInvariant();
                    if (arg == "now")
                    {
                        TX_Pckt = UpdateOBT(APID, DateTime.UtcNow);
                    }
                    else
                    {
                        // Reconstruct timestamp string
                        string timeString = (input.args.Count > 0 ? string.Join(" ", input.args.ToArray()) : " ");
                        var culture = CultureInfo.CreateSpecificCulture("en-US");
                        DateTime newOBT = DateTime.Parse(timeString, culture, DateTimeStyles.AssumeLocal);
                        TX_Pckt = UpdateOBT(APID, newOBT);
                    }
                    break;
                }

            case "schedule":
                {
                    if (input.args.Count < 2)
                        throw new Exception("schedule requires <Application> <Command> (<Arguments>)");

                    Request request = CommandHandler(input);
                    // Extract schedule time
                    

                    Console.WriteLine($"Input time to schedule comman for: ");

                    var culture = CultureInfo.CreateSpecificCulture("en-US");
                    DateTime scheduleTime = DateTime.Parse(Console.ReadLine(), culture, DateTimeStyles.AssumeLocal);

                    TX_Pckt = Schedule(APID, scheduleTime, request);
                    break;
                }

            case "mode":
                {
                    string action = input.args.Pop().ToLowerInvariant();
                    string? modeString = input.args.Pop();
                    if (!Enum.TryParse<Mode>(modeString, true, out Mode parsedMode))
                    {
                        throw new Exception("Mode not found. Possible modes: safe, inertial.");
                    }
                    byte mode = (byte)parsedMode.GetHashCode();

                    if (action == "set")
                    {
                        // Send mode change request
                        TX_Pckt = setMode(APID, mode);
                        break;
                    }
                    else if (action == "get")
                    {
                        // Send mode get request
                        TX_Pckt = getMode(APID, mode);
                        break;
                    }
                    else
                    {
                        throw new Exception("Action not found. Possible actions: set, get.");
                    }
                    
                }
            case "take-image":
                {
                    byte action = 1;
                    TX_Pckt = payloadAction(APID, action);
                    break;
                }

            default:
                throw new Exception($"'{command}' is not a recognized command.");
        }

        return TX_Pckt;
    }
    private static async Task CommunicationSession(IPAddress localIpAddress, CancellationToken cancelToken)
    {
        IPEndPoint ipEndPoint = new(localIpAddress, 11_000);

        // Open client socket 

        using Socket client = new(
            ipEndPoint.AddressFamily,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        try
        {
            await client.ConnectAsync(ipEndPoint);
            Console.WriteLine($"Connected from {client.LocalEndPoint} to {client.RemoteEndPoint}");
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
                //Console.WriteLine($"DEBUG: Compare {new Request(messageBytes).ToString()}");
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
                LoggingHandler(telemetry);

                recieveSequenceCount++;
                Report incomingReport = new Report(buffer);
                InterpretReport(incomingReport);
            }
        }, cancelToken);

        await receiveTask;
        await sendTask;

        // Close socket
        client.Shutdown(SocketShutdown.Both);
    }
    private static void InterpretReport(Report report)
    {
        string source;
        switch (report.ApplicationID)
        {
            case 0:
                source = "OBC";
                break;
            case 1:
                source = "PL";
                break;

            default:
                source = "unkown";
                break;
        }
        // Interpret based on Service Subtype (PUS Service 1)
        switch (report.ServiceSubtype)
        {
            case 1:
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[RX {source}] ACK: Command accepted successfully.");
                break;

            case 2:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[RX {source}] NACK: Command rejected or invalid.");
                break;

            case 3:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[RX {source}] INFO: Command execution started.");
                break;

            case 4:
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[RX {source}] DONE: Command execution completed successfully.");
                break;

            default:
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[RX {source}] Unknown verification report (Subtype {report.ServiceSubtype}).");
                break;
        }

        Console.ForegroundColor = ConsoleColor.White;
    }
    private static Request SendString(byte applicationID, string message)
    {
        // Convert to Unix time in seconds
        long unixSeconds = new DateTimeOffset(DateTime.Now).ToUnixTimeSeconds();


        // Set service and subservice type
        const byte serviceType = 2;
        const byte serviceSubtype = 1;

        // Encode message data
        byte[] data = Encoding.UTF8.GetBytes(message);
        return new Request(unixSeconds, applicationID, transmitSequenceCount, serviceType, serviceSubtype, data);
    }
    private static Request UpdateOBT(byte applicationID, DateTime newOBT)
    {
        // Set service and subservice type
        const byte serviceType = 9;
        const byte serviceSubtype = 4;

        long unixSeconds = new DateTimeOffset(newOBT).ToUnixTimeSeconds();

        byte[] data = BitConverter.GetBytes(unixSeconds);
        return new Request(unixSeconds, applicationID, transmitSequenceCount, serviceType, serviceSubtype, data);
    }

    //  Insert activities into the time-based schedule
    private static Request Schedule(byte applicationID, DateTime scheduleTime, Request payloadPacket) // 
    {
        // Set service and subservice type
        const byte serviceType = 11;
        const byte serviceSubtype = 4;

        // Convert current time to Unix time in seconds
        long unixSecondsCurrent = new DateTimeOffset(DateTime.Now).ToUnixTimeSeconds();

        long unixSecondsSchedule = new DateTimeOffset(scheduleTime).ToUnixTimeSeconds();

        payloadPacket.TimeStamp = unixSecondsSchedule;

        return new Request(unixSecondsCurrent, applicationID, transmitSequenceCount, serviceType, serviceSubtype, payloadPacket.Serialize());
    }

    // Set mode
    private static Request setMode(byte applicationID, byte mode)
    {
        // Convert to Unix time in seconds
        long unixSeconds = new DateTimeOffset(DateTime.Now).ToUnixTimeSeconds();

        byte[] modeBytes = new byte[] {mode};

        // Set service and subservice type
        const byte serviceType = 8;
        const byte serviceSubtype = 1;

        // Encode message data;
        return new Request(unixSeconds, applicationID, transmitSequenceCount, serviceType, serviceSubtype, modeBytes);
    }

    // Retrieve current mode
    private static Request getMode(byte applicationID, byte mode)
    {
        // Convert to Unix time in seconds
        long unixSeconds = new DateTimeOffset(DateTime.Now).ToUnixTimeSeconds();

        byte[] modeBytes = new byte[] {mode};

        // Set service and subservice type
        const byte serviceType = 8;
        const byte serviceSubtype = 2;

        // Encode message data;
        return new Request(unixSeconds, applicationID, transmitSequenceCount, serviceType, serviceSubtype, modeBytes);
    }

    private static Request payloadAction(byte applicationID, byte action)
    {
        // Convert to Unix time in seconds
        long unixSeconds = new DateTimeOffset(DateTime.Now).ToUnixTimeSeconds();

        byte[] actionBytes = new byte[] { action };

        // Set service and subservice type
        const byte serviceType = 8;
        const byte serviceSubtype = 3;

        // Encode message data;
        return new Request(unixSeconds, applicationID, transmitSequenceCount, serviceType, serviceSubtype, actionBytes);
    }

    private static void LoggingHandler(Report report)
    {
        string logFilePath = Path.Combine(AppContext.BaseDirectory, "housekeeping.txt");

        try
        {
            // Append the message with a timestamp
            using (StreamWriter sw = new StreamWriter(logFilePath, append: true))
            {
                sw.WriteLine(report.ToString());
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[LOGGING ERROR]: {ex.Message}");
            Console.ResetColor();
        }

    }
}