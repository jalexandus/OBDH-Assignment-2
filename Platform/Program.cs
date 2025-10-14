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
        ushort currentSequenceCount = 0;


        Console.WriteLine("██████████████████████████████████████████████████████████████████");
        Console.WriteLine("████████████████████████ ~ Platform OBC ~ ████████████████████████");
        Console.WriteLine("██████████████████████████████████████████████████████████████████");

        // Get the localhost ip address
        var hostName = Dns.GetHostName();
        IPHostEntry localhost = Dns.GetHostEntry(hostName);

        // Server IP address is the localipaddress
        IPAddress serverIpAddress = localhost.AddressList[0];

        Console.WriteLine($"Client IP address: {serverIpAddress.ToString()}"); // print the server ip address

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

            if (response.SequenceControl == currentSequenceCount)
            {
                currentSequenceCount++;
                Console.WriteLine($"Socket server received message: \"{response.ToString()}\"");
                byte[] ackBytes = AcknowledgePacket();
                await handler.SendAsync(ackBytes, 0);
                Console.WriteLine($"Socket server sent acknowledgment");
            }
            else if(response.SequenceControl > currentSequenceCount)
            {
                // IMPLEMENT: Ask to send previous packet
                Console.WriteLine($"Sequence count is out of order: expected {currentSequenceCount}, recieved {response.SequenceControl}");
            }
        }

        // Exit
        Console.Write("Press any key to exit");
        Console.ReadKey();
        return 0;

        byte[] AcknowledgePacket()
        {
            Packet ack = new Packet(currentSequenceCount, 1, 1, Array.Empty<byte>() );
            return ack.Serialize();
        }
    }

}

