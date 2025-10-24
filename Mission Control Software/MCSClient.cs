using Common;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

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

        serverIpAddress = serverIpAddressString == "localhost" ? localIpAddress : IPAddress.Parse(serverIpAddressString);

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
                break;
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
            throw new Exception("Missing <command> after destination application.");

        var culture = CultureInfo.CreateSpecificCulture("en-US");
        string command = input.args.Pop().ToLowerInvariant();
        Request TX_Pckt = null!;

        switch (command)
        {
            case "send":
                {
                    string message = string.Join(" ", input.args); // Combine remaining args
                    message.Trim();
                    TX_Pckt = SendStringRequest(APID, message);
                    break;
                }

            case "obt" when APID == 0:
                {
                    if (!input.args.TryPop(out string? action)) throw new Exception("Action not found. Possible actions: set, get.");
                    action.ToLowerInvariant();

                    if (action == "set")
                    {
                        if (input.args.Count == 0)
                            throw new Exception("Missing <UTC time> argument for update-obt.");

                        string arg = input.args.Peek().ToLowerInvariant();
                        DateTime newOBT;
                        if (arg == "now")
                        {
                            newOBT = DateTime.UtcNow;
                        }
                        else
                        {
                            // Reconstruct timestamp string
                            string timeString = (input.args.Count > 0 ? string.Join(" ", input.args.ToArray()) : " ");
                            newOBT = DateTime.Parse(timeString, culture, DateTimeStyles.AssumeUniversal);
                        }
                        TX_Pckt = SetOBTRequest(APID, newOBT);
                    }
                    else if (action == "get")
                    {
                        // Send mode get request
                        TX_Pckt = GetOBTRequest(APID);
                    }
                    break;
                }

            case "schedule":
                {
                    if (input.args.Count < 2)
                        throw new Exception("schedule requires: <Application> <Command> (<Arguments>)");

                    Request request = CommandHandler(input);

                    // Extract schedule time                    
                    Console.WriteLine($"Input time to schedule command for: ");

                    DateTime scheduleTime = DateTime.Parse(Console.ReadLine(), culture, DateTimeStyles.AssumeUniversal);

                    TX_Pckt = ScheduleRequest(APID, scheduleTime, request);
                    break;
                }

            case "hk":
                {
                    if (input.args.Count < 1)
                        throw new Exception("hk requires <Application> <Command> (<ON/OFF>");

                    string stateString = input.args.Pop().ToUpperInvariant();
                    bool state;

                    switch (stateString)
                    {
                        case "OFF":
                            state = false;
                            break;

                        case "ON":
                            state = true;
                            break;
                        default:
                            throw new Exception($"Input is either 'ON' or 'OFF'");
                    }
                    TX_Pckt = CyclicHKEnableRequest(APID, state);
                    break;
                }

            case "mode":
                {
                    string action = input.args.Pop().ToLowerInvariant();

                    if (action == "set")
                    {
                        string? modeString = input.args.Pop();
                        if (!Enum.TryParse<Mode>(modeString, true, out Mode parsedMode))
                        {
                            throw new Exception("Mode not found. Possible modes: safe, inertial.");
                        }
                        byte mode = (byte)parsedMode.GetHashCode();
                        // Send mode change request
                        TX_Pckt = SetModeRequest(APID, mode);
                    }
                    else if (action == "get")
                    {
                        // Send mode get request
                        TX_Pckt = GetModeRequest(APID);
                    }
                    else
                    {
                        throw new Exception("Action not found. Possible actions: set, get.");
                    }
                    break;
                }


            case "take-image":
                {
                    byte action = 1;
                    TX_Pckt = PayloadActionRequest(APID, action);
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
        int serviceType = report.ServiceType;
        int subtype = report.ServiceSubtype;
        switch ((serviceType, subtype))
        {
            case (1, 1):
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[RX {source}] ACK: Command received successfully.");
                break;

            case (1, 2):
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[RX {source}] NACK: Command rejected or invalid.");
                break;

            case (1, 3):
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[RX {source}] INFO: Command execution started.");
                break;

            case (1, 4):
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[RX {source}] DONE: Command execution completed successfully.");
                break;
            case (1, 5):
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[RX {source}] Failed routing verification report");
                break;
            case (3, 25):
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"[RX {source}] Housekeeping parameters report");
                PrintParameters(report);
                break;
            case (9, 6):
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"[RX {source}] On-board time report");
                Console.WriteLine("On-board time:" + PrintUnixTime(report.TimeStamp));
                break;
            default:
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[RX {source}] Unknown report (Type {serviceType}, Subtype {subtype}).");
                break;
        }

        Console.ForegroundColor = ConsoleColor.White;
    }
    private static Request SendStringRequest(byte applicationID, string message)
    {
        // Encode message data
        byte[] data = Encoding.UTF8.GetBytes(message);
        return new Request(GetUnixTime(), applicationID, transmitSequenceCount, 2, 1, data);
    }
    private static Request CyclicHKEnableRequest(byte applicationID, bool enable)
    {
        return new Request(GetUnixTime(), applicationID, transmitSequenceCount, 3, 5, BitConverter.GetBytes(enable));
    }
    private static Request SetOBTRequest(byte applicationID, DateTime newOBT)
    {
        byte[] data = BitConverter.GetBytes(GetUnixTime(newOBT));
        return new Request(GetUnixTime(), applicationID, transmitSequenceCount, 9, 4, data);
    }
    private static Request GetOBTRequest(byte applicationID)
    {
        return new Request(GetUnixTime(), applicationID, transmitSequenceCount, 9, 5, Array.Empty<byte>());
    }
    //  Insert activities into the time-based schedule
    private static Request ScheduleRequest(byte applicationID, DateTime scheduleTime, Request payloadPacket) // 
    {
        payloadPacket.TimeStamp = GetUnixTime(scheduleTime);

        var now = GetUnixTime();

        if (payloadPacket.TimeStamp <= now)
        {
            throw new Exception("the requested scheduling time is in the past.");
        }

        return new Request(now, applicationID, transmitSequenceCount, 11, 4, payloadPacket.Serialize());
    }

    private static Request GetModeRequest(byte applicationID)
    {
        return new Request(GetUnixTime(), applicationID, transmitSequenceCount, 8, 2, Array.Empty<byte>());
    }
    private static long GetUnixTime()
    {
        return new DateTimeOffset(DateTime.Now).ToUnixTimeSeconds();
    }
    private static long GetUnixTime(DateTime time)
    {
        return new DateTimeOffset(time).ToUnixTimeSeconds();
    }
    // Set mode
    private static Request SetModeRequest(byte applicationID, byte mode)
    {
        return new Request(GetUnixTime(), applicationID, transmitSequenceCount, 8, 1, new byte[] { mode });
    }

    // Retrieve current mode


    private static Request PayloadActionRequest(byte applicationID, byte action)
    {
        byte[] actionBytes = new byte[] { action };

        // Encode message data;
        return new Request(GetUnixTime(), applicationID, transmitSequenceCount, 8, 3, actionBytes);
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

    private static void PrintParameters(Report report)
    {
        int dataLength = report.Data.Length;
        int startIndex = 0;
        // Mapping of ParameterID to label and unit
        Dictionary<byte, (string Label, string Unit)> paramInfo = new()
        {
            { 0x00, ("unix_time", "s") },
            { 0x01, ("bus_voltage", "V") },
            { 0x02, ("bus_current", "A") },
            { 0x03, ("battery_voltage", "V") },
            { 0x04, ("battery_current", "A") },
            { 0x05, ("battery_temperature", "°C") },
            { 0x06, ("obc_temperature", "°C") },
            { 0x07, ("payload_temperature", "°C") },
            { 0x08, ("eps_temperature", "°C") },
            { 0x09, ("uplink_count", "#") },
            { 0x0A, ("downlink_count", "#") },
            { 0x0B, ("uptime", "s") },
            { 0x0C, ("payload_mode", "") },
            { 0x0D, ("adcs_mode", "") }
        };

        // Read all parameters from data
        while (startIndex < dataLength)
        {
            Common.Parameter param = new Common.Parameter(report.Data, startIndex);
            startIndex += param.Length;

            if (!paramInfo.TryGetValue(param.ID, out var info))
                info = ($"ParameterID:{param.ID}", ""); // fallback if unknown

            Console.WriteLine($"   {info.Label}: {param.Value} {info.Unit}");
        }
    }
    private static string PrintUnixTime(long unix)
    {
        DateTimeOffset unixTime = DateTimeOffset.FromUnixTimeSeconds(unix);
        return unixTime.ToUniversalTime().ToString();
    }
}