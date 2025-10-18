using Common;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Wasm;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Mission_Control_Software;
internal class MCSCLient
{
    private static ushort recieveSequenceCount = 0;
    private static ushort transmitSequenceCount = 0;

    private static BlockingCollection<Request> OutgoingQueue = new BlockingCollection<Request>(new ConcurrentQueue<Request>(), 100); // Maximum 100 command packets queue
    private static BlockingCollection<Report> IncomingQueue = new BlockingCollection<Report>(new ConcurrentQueue<Report>(), 100); // Maximum 100 recieved telemetry packets in queue

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

        // Command input loop
        while (true)
        {
            string input = Console.ReadLine();
            if (input != null)
            {
                MCSCommand cmd = new MCSCommand(input);
                if (cmd.args[0] == "exit")
                {
                    cts.Cancel(); // Cancel the communcation task
                    break;
                }
                else
                {
                    CommandHandler(cmd);
                }
            }
             
        }
        await commTask;
        return 0;

    }
    private static void CommandHandler(MCSCommand input)
    {
        switch (input.args[0])
        {
            case "send":
                SendString(DateTime.UtcNow, input.args[1]);
                return;
            case "time":
                if (input.args[1] == "now") UpdateOBT(DateTime.UtcNow);
                else
                {
                    try
                    {
                        CultureInfo culture = CultureInfo.CreateSpecificCulture("en-US");
                        UpdateOBT(DateTime.Parse(input.args[1], culture, DateTimeStyles.AssumeLocal));
                    }
                    catch (Exception e)
                    {
                        throw;
                    }
                    
                }
                
                return;
            default:
                break;
        }
        Console.WriteLine("ERROR: Not a recognized command.");
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
                Console.WriteLine($"[TX]: {nextRequest.ToString()}");
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
                Console.WriteLine($"[RX]: {telemetry.ToString()}");
                Console.ForegroundColor = ConsoleColor.White;
            }
        }, cancelToken);

        await receiveTask;
        await sendTask;

        // Close socket
        client.Shutdown(SocketShutdown.Both);
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
        Request TX_Pckt = new Request(unixSeconds, transmitSequenceCount++, serviceType, serviceSubtype, data);
        OutgoingQueue.Add(TX_Pckt);
    }
    private static void UpdateOBT(DateTime utcTime)
    {
        // Set service and subservice type
        byte serviceType = 9;
        byte serviceSubtype = 4;

        long unixSeconds = new DateTimeOffset(utcTime).ToUnixTimeSeconds();

        byte[] data = BitConverter.GetBytes(unixSeconds);
        Request TX_Pckt = new Request(unixSeconds, transmitSequenceCount++, serviceType, serviceSubtype, data);
        OutgoingQueue.Add(TX_Pckt);
    }
}