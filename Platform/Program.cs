using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Common;

namespace Program;

internal class PlatformOBC
{
    static async Task<int> Main(string[] args)
    {
        const string configFilePath = "config.txt";
        string serverIpAddressString;
        ushort recieveSequenceCount = 0;
        ushort transmitSequenceCount = 0;


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

        // Start server

        // Command input loop
        using Socket listener = new(
        ipEndPoint.AddressFamily,
        SocketType.Stream,
        ProtocolType.Tcp);

        listener.Bind(ipEndPoint);
        listener.Listen(100);

        var handler = await listener.AcceptAsync();
        while (true)
        {
            // Receive message.
            var buffer = new byte[1_024];
            await handler.ReceiveAsync(buffer, SocketFlags.None); 
            Packet response = new Packet(buffer);

            if (response.SequenceControl == recieveSequenceCount || true)
            {
                recieveSequenceCount++;
                Console.WriteLine($"Received command: ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(response.ToString());
                Console.ForegroundColor = ConsoleColor.White;

                Packet ack = AcknowledgePacket();
                await handler.SendAsync(ack.Serialize(), 0);

                Console.WriteLine($"Sent acknowledgment: ");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine(ack.ToString());
                Console.ForegroundColor = ConsoleColor.White;
            }
            else if(response.SequenceControl >= recieveSequenceCount + 1) // +1 to check overflow
            {
                // This means we missed a packed from the client
                // IMPLEMENT: Ask to send previous packet
                Console.WriteLine($"Sequence count is out of order: expected {recieveSequenceCount}, recieved {response.SequenceControl}");
            }
        }

        // Exit
        Console.Write("Press any key to exit");
        Console.ReadKey();
        return 0;

        Packet AcknowledgePacket()
        {
            DateTime utcTime = DateTime.UtcNow;
            long unixSeconds = new DateTimeOffset(utcTime).ToUnixTimeSeconds();
            Console.WriteLine(transmitSequenceCount.ToString());
            return new Packet(unixSeconds, transmitSequenceCount++, 1, 1, Array.Empty<byte>() );
        }
    }

}

