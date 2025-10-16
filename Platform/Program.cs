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

namespace Program;

struct Telemetry
{
    public DateTime time;
}
struct Parameters
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
    byte payload_status;         // Bit field for payload subsystems
    float payload_voltage;        // [V]
    float payload_current;        // [A]
}
enum ADCSmode{
    TARGET,
    NADIR,
    INERTIAL
}

internal class PlatformOBC
{
    static async Task<int> Main(string[] args)
    {
        const string configFilePath = "config.txt";
        string serverIpAddressString;
        ushort recieveSequenceCount = 0;
        ushort transmitSequenceCount = 0;


        BlockingCollection<Packet> TransmitQueue = new BlockingCollection<Packet>(new ConcurrentQueue<Packet>(), 100); // Maximum 100 command packets queue
        BlockingCollection<Packet> RecieveQueue = new BlockingCollection<Packet>(new ConcurrentQueue<Packet>(), 100); // Maximum 100 command packets queue

        Console.WriteLine("██████████████████████████████████████████████████████████████████");
        Console.WriteLine("████████████████████████ ~ Platform OBC ~ ████████████████████████");
        Console.WriteLine("██████████████████████████████████████████████████████████████████");

        // Get the localhost ip address
        var hostName = Dns.GetHostName();
        IPHostEntry localhost = Dns.GetHostEntry(hostName);

        // Server IP address is the localipaddress
        IPAddress serverIpAddress = localhost.AddressList[0];

        Console.WriteLine($"Server IP address: {serverIpAddress.ToString()}"); // print the server ip address

        IPEndPoint ipEndPoint = new(serverIpAddress, 11_000);

        // Start the communcation task
        var cts = new CancellationTokenSource();
        var commTask = CommunicationSession(cts.Token);

        // command interpreter 
        // Loops through the queue of received commands and executes the ones that are due.
        while (!cts.IsCancellationRequested)
        {
            Packet nextPacket = RecieveQueue.Take(cts.Token);
            CommandHandler(nextPacket, cts.Token);
        }
        await commTask; // Pause execution

        // Exit
        Console.Write("Press any key to exit");
        Console.ReadKey();
        return 0;

        void CommandHandler(Packet packet, CancellationToken cancelToken)
        {
            switch (packet.ServiceType) 
            { 
                case 2:
                string message = Encoding.UTF8.GetString(packet.Data, 0, packet.Nbytes);
                Console.WriteLine("Recieved string:" + message);
                break;

            default:
                TransmitQueue.Add(InvalidCommandReport(), cancelToken);
                return;
            }
            TransmitQueue.Add(CompletedCommandReport(), cancelToken);
        }

        async Task CommunicationSession(CancellationToken cancelToken)
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
                    Packet recievedPacket = new Packet(buffer);

                    // Verify command isnt outdated
                    var deltaTime = GetCurrentTime() - DateTimeOffset.FromUnixTimeSeconds(recievedPacket.TimeStamp).ToUnixTimeSeconds();

                    if (deltaTime < +1) // accept t+1 second overdueness
                    {
                        RecieveQueue.Add(recievedPacket, cancelToken);
                        TransmitQueue.Add(AcknowledgeReport(), cancelToken);
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
                    Packet nextPacket = TransmitQueue.Take(cancelToken);
                    await handler.SendAsync(nextPacket.Serialize(), 0);

                    Console.WriteLine($"Sent acknowledgment: ");
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Console.WriteLine(nextPacket.ToString());
                    Console.ForegroundColor = ConsoleColor.White;
                }
            }, cancelToken);

            await recieveTask;
            await sendTask;

            listener.Shutdown(SocketShutdown.Both);
        }

        Packet AcknowledgeReport()
        {
            // Create packet with service/subservice: Successful acceptance verification
            return new Packet(GetCurrentTime(), transmitSequenceCount++, 1, 1, Array.Empty<byte>() );
        }
        Packet InvalidCommandReport()
        {
            // Create packet with service/subservice: Failed start of execution
            return new Packet(GetCurrentTime(), transmitSequenceCount++, 1, 4, Array.Empty<byte>());
        }
        Packet CompletedCommandReport()
        {
            // Create packet with service/subservice: Failed start of execution
            return new Packet(GetCurrentTime(), transmitSequenceCount++, 1, 4, Array.Empty<byte>());
        }

        // Returns the OBC time in unix seconds 
        long GetCurrentTime()
        {
            DateTime utcTime = DateTime.UtcNow;
            return new DateTimeOffset(utcTime).ToUnixTimeSeconds();
        }

    }

}

