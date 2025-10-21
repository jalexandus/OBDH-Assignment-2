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
        byte APID;
        // Selct sink
        switch (input.args[0])
        {
            case "obc":
                APID = 0;
                break;
            case "payload":
                APID = 1;
                break;
            default:
                Console.WriteLine($"'{input.args[0]}' is not a recognized application ID.");
                return;
        }

        switch (input.args[1])
        {
            case "send":
                SendString(APID, DateTime.UtcNow, input.args[2]);
                return;
            case "update-OBT":
                if (input.args[2] == "now") UpdateOBT(APID, DateTime.UtcNow);
                else
                {
                    try
                    {
                        string timeString = "";
                        for (int i = 2; i < input.args.Length; i++)
                        {
                            timeString += input.args[i] + " ";
                        }
                        CultureInfo culture = CultureInfo.CreateSpecificCulture("en-US");
                        UpdateOBT(APID, DateTime.Parse(input.args[2], culture, DateTimeStyles.AssumeLocal));
                    }
                    catch (Exception e)
                    {
                        throw;
                    }                    
                }                
                return;
            default:
                Console.WriteLine($"'{input.args[1]}' is not a recognized command.");
                return;
        }        
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
    private static void SendString(byte applicationID, DateTime utcTime, string message)
    {
        // Convert to Unix time in seconds
        long unixSeconds = new DateTimeOffset(utcTime).ToUnixTimeSeconds();


        // Set service and subservice type
        const byte serviceType = 2;
        const byte serviceSubtype = 1;

        // Encode message data
        byte[] data = Encoding.UTF8.GetBytes(message);
        Request TX_Pckt = new Request(unixSeconds, applicationID, transmitSequenceCount++, serviceType, serviceSubtype, data);
        OutgoingQueue.Add(TX_Pckt);
    }
    private static void UpdateOBT(byte applicationID, DateTime utcTime)
    {
        // Set service and subservice type
        const byte serviceType = 9;
        const byte serviceSubtype = 4;

        long unixSeconds = new DateTimeOffset(utcTime).ToUnixTimeSeconds();

        byte[] data = BitConverter.GetBytes(unixSeconds);
        Request TX_Pckt = new Request(unixSeconds, applicationID, transmitSequenceCount++, serviceType, serviceSubtype, data);
        OutgoingQueue.Add(TX_Pckt);
    }
}