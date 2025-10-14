using Common;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Mission_Control_Software;
internal class MCSCLient
{ 

    static async Task<int> Main(string[] args)
    {
        const string configFilePath = "config.txt";
        string serverIpAddressString;
        IPEndPoint ipEndPoint;
        ushort currentSequenceCount = 0;

        

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

        ipEndPoint = new(localIpAddress, 11_000);

        

        // Command input loop
        while (true)
        {
            string input = Console.ReadLine();
            if (input != null)
            {
                MCSCommand cmd = new MCSCommand(input);
                if (cmd.args[0] == "exit")
                {
                    break;
                }
                else
                {
                    await CommandHandler(cmd);
                }
            }          

        }
        return 0;

        async Task<int> CommandHandler(MCSCommand input)
        {
            switch (input.args[0])
            {
                case "send":
                    return await SendString(input.args[1]);
                default:
                    Console.WriteLine("ERROR: Not a recognized command.");
                    return 0;
            }
        }
        async Task<int> SendString(string message)
        {
            ushort sequenceControl = currentSequenceCount++;
            byte serviceType = 2;
            byte serviceSubtype = 1;
            byte[] data = Encoding.UTF8.GetBytes(message);
            Packet TX_Pckt = new Packet(sequenceControl, serviceType, serviceSubtype, data);

            return await SendPacket(TX_Pckt);
        }

        async Task<int> SendPacket(Packet pckt)
        {
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
                return 1;
            }

            int maxAttempts= 10;
            for(int i = 0; i< maxAttempts; i++)
            {
                // Send message.
                byte[] messageBytes = pckt.Serialize();
                _ = await client.SendAsync(messageBytes, SocketFlags.None);

                // Receive ack.
                var buffer = new byte[1_024];
                var received = await client.ReceiveAsync(buffer, SocketFlags.None);
                var response = new Packet(buffer);

                if (response.ServiceType == 1 && response.ServiceSubtype == 1)
                {
                    Console.WriteLine(
                        $"Socket client received acknowledgment");
                    break;
                }
            }

            client.Shutdown(SocketShutdown.Both);
            return 1;
        }
        
    }
}